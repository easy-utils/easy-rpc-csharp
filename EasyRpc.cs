using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using System.Net;
using System.Text;

namespace EasyRpc
{
    /// <summary>A structured error detail (spec §4.1, aligned with Connect Error
    /// Details / gRPC google.rpc status details). Type is a type URL; Value is
    /// opaque bytes (typically an encoded protobuf message).</summary>
    public sealed class ErrorDetail : IEquatable<ErrorDetail>
    {
        public string Type { get; }
        public byte[] Value { get; }
        public ErrorDetail(string type, byte[] value) { Type = type; Value = value; }
        public bool Equals(ErrorDetail? other) =>
            other is not null && other.Type == Type && other.Value.AsSpan().SequenceEqual(Value);
        public override bool Equals(object? obj) => obj is ErrorDetail d && Equals(d);
        public override int GetHashCode() => HashCode.Combine(Type, Value.Length);
        public override string ToString() => $"ErrorDetail({Type}, {Value.Length}B)";
    }

    public class RpcError : Exception
    {
        public int Code { get; }
        /// <summary>Optional structured details (spec §4.1); opaque to the wire layer.</summary>
        public IReadOnlyList<ErrorDetail>? Details { get; }
        public RpcError(int code, string message, IReadOnlyList<ErrorDetail>? details = null)
            : base($"easyrpc: code={code} {message}") { Code = code; Details = details; }
    }

    public class Request
    {
        public string Url { get; set; } = "";
        public Dictionary<string, List<string>> Headers { get; set; } = new();
        public byte[]? Body { get; set; }
        /// <summary>Local cancellation channel. HttpClient adapters honour it.</summary>
        public System.Threading.CancellationToken CancellationToken { get; set; } = default;
    }

    public class Response
    {
        public int Status { get; set; }
        public Dictionary<string, List<string>> Headers { get; set; } = new();
        public byte[] Body { get; set; } = System.Array.Empty<byte>();
        /// <summary>Unary trailing metadata (demuxed from trailer-* headers).</summary>
        public Dictionary<string, List<string>> Trailers { get; set; } = new();
        public RpcError? Error { get; set; }
    }

    public static class Protocol
    {
        public static int HttpStatus(int code) => code switch
        {
            1 => 499, 3 => 400, 4 => 504, 5 => 404, 6 => 409, 7 => 403, 8 => 429,
            9 => 400, 10 => 409, 11 => 400, 12 => 501, 14 => 503, 16 => 401, _ => 500
        };
        public static int ConnectFromStatus(int status) => status switch
        {
            400 => 3, 404 => 5, 403 => 7, 401 => 16, 429 => 8, 503 => 14,
            409 => 10, 504 => 4, 501 => 12, 499 => 1, _ => 13
        };
        public const byte EndStream = 0x02;
        public static byte[] Frame(byte[] payload, bool end = false)
        {
            var outB = new List<byte>();
            outB.Add(end ? EndStream : (byte)0);
            outB.Add((byte)(payload.Length >> 24)); outB.Add((byte)(payload.Length >> 16)); outB.Add((byte)(payload.Length >> 8)); outB.Add((byte)payload.Length);
            outB.AddRange(payload);
            return outB.ToArray();
        }

        public const string HeaderTimeout = "connect-timeout-ms";
        public const string HeaderProtocolVersion = "connect-protocol-version";
        public const string HeaderAcceptEncoding = "connect-accept-encoding";
        public const string EncodingGzip = "gzip";
        public const int CompressMinBytes = 1024;
        public const string ConnectProtocolVersion = "1";
        public const int DefaultMaxMessageBytes = 4 * 1024 * 1024;

        /// <summary>Parse the Connect timeout header into milliseconds (0 = none).</summary>
        public static int ParseTimeout(string? value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            return int.TryParse(value, out var n) && n > 0 ? n : 0;
        }

        /// <summary>Attach a deadline to a request.</summary>
        public static Request WithTimeout(Request req, int timeoutMs)
        {
            if (timeoutMs <= 0) return req;
            req.Headers[HeaderTimeout] = new List<string> { timeoutMs.ToString() };
            return req;
        }

        private static readonly Dictionary<int, string> CodeNames = new()
        {
            [0] = "ok", [1] = "canceled", [2] = "unknown", [3] = "invalid_argument",
            [4] = "deadline_exceeded", [5] = "not_found", [6] = "already_exists",
            [7] = "permission_denied", [8] = "resource_exhausted", [9] = "failed_precondition",
            [10] = "aborted", [11] = "out_of_range", [12] = "unimplemented", [13] = "internal",
            [14] = "unavailable", [15] = "data_loss", [16] = "unauthenticated",
        };

        public static string CodeToString(int code) =>
            CodeNames.TryGetValue(code, out var s) ? s : "unknown";

        public static int CodeFromString(string name)
        {
            foreach (var kv in CodeNames) if (kv.Value == name) return kv.Key;
            return 2;
        }

        /// <summary>gzip-compress (identity on failure).</summary>
        public static byte[] GzipCompress(byte[] data)
        {
            try
            {
                using var ms = new System.IO.MemoryStream();
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, true))
                    gz.Write(data, 0, data.Length);
                return ms.ToArray();
            }
            catch { return data; }
        }

        /// <summary>gzip-decompress. THROWS RpcError(13) on corrupt input
        /// (fault matrix M10): a flagged-but-corrupt gzip payload is a
        /// protocol error, never silently-yielded raw compressed bytes.</summary>
        public static byte[] GzipDecompress(byte[] data)
        {
            try
            {
                using var input = new System.IO.MemoryStream(data);
                using var gz = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var outMs = new System.IO.MemoryStream();
                gz.CopyTo(outMs);
                return outMs.ToArray();
            }
            catch (System.Exception e)
            {
                throw new RpcError(13, $"corrupt gzip frame: {e.Message}");
            }
        }

        internal static List<object> WireDetails(IReadOnlyList<ErrorDetail>? details)
        {
            var outList = new List<object>();
            if (details is null) return outList;
            foreach (var d in details)
                outList.Add(new Dictionary<string, object> { ["type"] = d.Type, ["value"] = Convert.ToBase64String(d.Value) });
            return outList;
        }

        /// <summary>Parse a JSON details array; malformed entries are skipped, never
        /// fatal (matrix M7). Returns null when absent/empty.</summary>
        internal static List<ErrorDetail>? ParseWireDetails(System.Text.Json.JsonElement? el)
        {
            if (el is null || el.Value.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var outList = new List<ErrorDetail>();
            foreach (var item in el.Value.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!item.TryGetProperty("type", out var t) || t.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                if (!item.TryGetProperty("value", out var v) || v.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var type = t.GetString() ?? "";
                var b64 = v.GetString() ?? "";
                if (type.Length == 0 || b64.Length == 0) continue;
                try { outList.Add(new ErrorDetail(type, Convert.FromBase64String(b64))); }
                catch (System.FormatException) { /* skip invalid base64 */ }
            }
            return outList.Count > 0 ? outList : null;
        }

        /// <summary>Encode a Connect unary error body {code,message[,details]}.</summary>
        public static byte[] EncodeErrorJson(int code, string message, IReadOnlyList<ErrorDetail>? details = null)
        {
            var obj = new Dictionary<string, object> { ["code"] = CodeToString(code), ["message"] = message };
            var wire = WireDetails(details);
            if (wire.Count > 0) obj["details"] = wire;
            return System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(obj));
        }

        /// <summary>Parse a Connect unary error body; (0,"",null) when not one.</summary>
        public static (int code, string message, IReadOnlyList<ErrorDetail>? details) DecodeErrorJson(byte[] body)
        {
            if (body.Length == 0) return (0, "", null);
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("code", out var c) || c.ValueKind != System.Text.Json.JsonValueKind.String)
                    return (0, "", null);
                var msg = doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String
                    ? (m.GetString() ?? "") : "";
                var details = doc.RootElement.TryGetProperty("details", out var d) ? ParseWireDetails(d) : null;
                return (CodeFromString(c.GetString() ?? "unknown"), msg, details);
            }
            catch { return (0, "", null); }
        }

        /// <summary>Encode a Connect end-stream payload; a clean end is empty.
        /// Details (spec §4.1) are included when non-empty.</summary>
        public static byte[] EncodeEndStream(int code, string message, IReadOnlyList<ErrorDetail>? details = null,
            Dictionary<string, List<string>>? metadata = null)
        {
            var obj = new Dictionary<string, object>();
            if (code != 0)
            {
                var err = new Dictionary<string, object>
                {
                    ["code"] = CodeToString(code),
                    ["message"] = message,
                };
                var wire = WireDetails(details);
                if (wire.Count > 0) err["details"] = wire;
                obj["error"] = err;
            }
            if (metadata != null)
            {
                var md = new Dictionary<string, List<string>>();
                foreach (var kv in metadata) if (kv.Value.Count > 0) md[kv.Key] = kv.Value;
                if (md.Count > 0) obj["metadata"] = md;
            }
            return System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(obj));
        }

        /// <summary>Decode a Connect end-stream payload into (code, message, details).
        /// Malformed input is a clean end (matrix M2); an error object without a
        /// code maps to 2 (M3/M4); unknown fields are ignored (M5).</summary>
        public sealed record EndStreamInfo(int Code, string Message, IReadOnlyList<ErrorDetail>? Details,
            Dictionary<string, List<string>> Metadata);

        public static EndStreamInfo DecodeEndStream(byte[] payload)
        {
            var empty = new EndStreamInfo(0, "", null, new Dictionary<string, List<string>>());
            if (payload.Length == 0) return empty;
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(payload);
                var metadata = new Dictionary<string, List<string>>();
                if (doc.RootElement.TryGetProperty("metadata", out var md) && md.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var prop in md.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                        var vs = new List<string>();
                        foreach (var el in prop.Value.EnumerateArray())
                            if (el.ValueKind == System.Text.Json.JsonValueKind.String) vs.Add(el.GetString() ?? "");
                        if (vs.Count > 0) metadata[prop.Name] = vs;
                    }
                }
                if (!doc.RootElement.TryGetProperty("error", out var e)) return empty with { Metadata = metadata };
                var code = e.TryGetProperty("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
                    ? CodeFromString(c.GetString() ?? "unknown") : 2;
                var msg = e.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String
                    ? (m.GetString() ?? "") : "";
                var details = e.TryGetProperty("details", out var d) ? ParseWireDetails(d) : null;
                return new EndStreamInfo(code, msg, details, metadata);
            }
            catch { return empty; }
        }

        /// <summary>Split headers into (headers, trailers) by the trailer- prefix.</summary>
        public static (Dictionary<string, List<string>> headers, Dictionary<string, List<string>> trailers) DemuxTrailers(
            Dictionary<string, List<string>> all)
        {
            var h = new Dictionary<string, List<string>>();
            var t = new Dictionary<string, List<string>>();
            foreach (var kv in all)
            {
                if (kv.Key.StartsWith("trailer-", System.StringComparison.OrdinalIgnoreCase))
                    t[kv.Key.Substring(8).ToLowerInvariant()] = kv.Value;
                else h[kv.Key] = kv.Value;
            }
            return (h, t);
        }

        /// <summary>Merge trailers into headers using the trailer- prefix.</summary>
        public static Dictionary<string, List<string>> MuxTrailers(
            Dictionary<string, List<string>> headers, Dictionary<string, List<string>> trailers)
        {
            var outH = new Dictionary<string, List<string>>(headers);
            foreach (var kv in trailers)
                outH["trailer-" + kv.Key.ToLowerInvariant()] = kv.Value;
            return outH;
        }
    }

    public interface Transport
    {
        Task<Response> Send(Request req);
        Task<IAsyncEnumerable<byte[]>> OpenStream(Request req);
    }

    /// <summary>A call interceptor: mutate the request (auth/metadata), impose
    /// a deadline, observe, or short-circuit. `next` performs the call.</summary>
    public interface Interceptor
    {
        Task<Response> Unary(Request req, Func<Request, Task<Response>> next);
        Task<IAsyncEnumerable<byte[]>> Stream(Request req, Func<Request, Task<IAsyncEnumerable<byte[]>>> next);
    }

    /// <summary>Apply interceptors (first = outermost) around a Transport.</summary>
    public class InterceptorTransport : Transport
    {
        private readonly IReadOnlyList<Interceptor> _ics;
        private readonly Transport _inner;
        public InterceptorTransport(IReadOnlyList<Interceptor> ics, Transport inner) { _ics = ics; _inner = inner; }

        public Task<Response> Send(Request req) => Dispatch(0, req);
        private Task<Response> Dispatch(int i, Request r) =>
            i >= _ics.Count ? _inner.Send(r) : _ics[i].Unary(r, nr => Dispatch(i + 1, nr));

        public Task<IAsyncEnumerable<byte[]>> OpenStream(Request req) => DispatchS(0, req);
        private Task<IAsyncEnumerable<byte[]>> DispatchS(int i, Request r) =>
            i >= _ics.Count ? _inner.OpenStream(r) : _ics[i].Stream(r, nr => DispatchS(i + 1, nr));
    }

    /// <summary>Attach fixed metadata to every call.</summary>
    public class MetadataInterceptor : Interceptor
    {
        private readonly Dictionary<string, List<string>> _md;
        public MetadataInterceptor(Dictionary<string, List<string>> md) { _md = md; }
        private Request Aug(Request req)
        {
            foreach (var kv in _md) if (!req.Headers.ContainsKey(kv.Key)) req.Headers[kv.Key] = kv.Value;
            return req;
        }
        public Task<Response> Unary(Request req, Func<Request, Task<Response>> next) => next(Aug(req));
        public Task<IAsyncEnumerable<byte[]>> Stream(Request req, Func<Request, Task<IAsyncEnumerable<byte[]>>> next) => next(Aug(req));
    }

    /// <summary>Attach a Connect deadline to every call.</summary>
    public class TimeoutInterceptor : Interceptor
    {
        private readonly int _ms;
        public TimeoutInterceptor(int ms) { _ms = ms; }

        private async Task<T> Run<T>(Request req, Func<Request, Task<T>> next)
        {
            if (_ms <= 0) return await next(req);
            using var cts = new System.Threading.CancellationTokenSource(_ms);
            var r = Protocol.WithTimeout(req, _ms);
            r.CancellationToken = cts.Token;
            try
            {
                return await next(r);
            }
            catch (System.OperationCanceledException)
            {
                throw new RpcError(4, "deadline exceeded");
            }
        }

        public Task<Response> Unary(Request req, Func<Request, Task<Response>> next) => Run(req, next);
        public Task<IAsyncEnumerable<byte[]>> Stream(Request req, Func<Request, Task<IAsyncEnumerable<byte[]>>> next) => Run(req, next);
    }

    /// <summary>Adapter modes for the composition root.</summary>
    public enum TransportMode { Auto, H1, H2, H3 }

    /// <summary>Options for the composition root.</summary>
    public class ConnectOptions
    {
        public string BaseUrl { get; set; } = "";
        public string Token { get; set; } = "";
        public TransportMode Mode { get; set; } = TransportMode.Auto;
        public int TimeoutMs { get; set; } = 0;
        public List<Interceptor> Interceptors { get; set; } = new();
        /// <summary>Optional pre-built adapter (e.g. one configured with a
        /// custom CA / HttpClient). When set, Mode is ignored.</summary>
        public Transport? Adapter { get; set; }
    }

    /// <summary>Composition root: pick an adapter by Mode, install the built-in
    /// metadata/deadline interceptors, then any user interceptors. Swapping Mode
    /// leaves the interceptors unchanged.</summary>
    public static class EasyRpcClient
    {
        public static Transport Connect(ConnectOptions opts)
        {
            var version = opts.Mode switch
            {
                TransportMode.H1 => new Version(1, 1),
                TransportMode.H3 => new Version(3, 0),
                _ => new Version(2, 0),
            };
            var policy = opts.Mode == TransportMode.H3
                ? HttpVersionPolicy.RequestVersionOrHigher
                : HttpVersionPolicy.RequestVersionOrLower;
            Transport inner = opts.Adapter ?? new HttpClientTransport(opts.BaseUrl, version, policy);
            var ics = new List<Interceptor>();
            if (opts.Token.Length > 0)
                ics.Add(new MetadataInterceptor(new Dictionary<string, List<string>>
                {
                    ["Authorization"] = new List<string> { $"Bearer {opts.Token}" },
                }));
            if (opts.TimeoutMs > 0) ics.Add(new TimeoutInterceptor(opts.TimeoutMs));
            ics.AddRange(opts.Interceptors);
            return ics.Count == 0 ? inner : new InterceptorTransport(ics, inner);
        }
    }

    /// <summary>
    /// Cross-platform HTTP client transport backed by System.Net.Http.HttpClient +
    /// SocketsHttpHandler. On .NET MAUI/Android/iOS the platform handler is used
    /// automatically (Android handler / NSUrlSessionHandler). Protocol coverage:
    ///   - HTTP/1.1 + HTTP/2 (over TLS, ALPN)
    ///   - HTTP/3 via .NET msquic (Linux/Windows) or the platform handler (iOS).
    /// Cleartext h2c is NOT exposed (like Go/Python/Kotlin/Dart it would need a
    /// custom connect layer); use h2 over TLS (https://) or h3.
    /// Choose the version with httpVersion + versionPolicy.
    /// </summary>
    public class HttpClientTransport : Transport
    {
        private readonly HttpClient _client;
        public string Base { get; set; } = "";
        public Version Version { get; set; } = new Version(2, 0);
        public HttpVersionPolicy VersionPolicy { get; set; } = HttpVersionPolicy.RequestVersionOrLower;

        public HttpClientTransport(string baseUrl = "", Version? version = null,
            HttpVersionPolicy policy = HttpVersionPolicy.RequestVersionOrLower,
            string? caPem = null)
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
            if (!string.IsNullOrEmpty(caPem))
            {
                // Trust the bundled self-signed agent CA (plus the system roots).
                var ca = System.Security.Cryptography.X509Certificates
                    .X509Certificate2.CreateFromPem(caPem);
                handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                {
                    if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                    using var custom = new System.Security.Cryptography.X509Certificates.X509Chain();
                    custom.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
                    custom.ChainPolicy.CustomTrustStore.Add(ca);
                    return cert != null && custom.Build(
                        new System.Security.Cryptography.X509Certificates.X509Certificate2(cert));
                };
            }
            _client = new HttpClient(handler);
            Base = baseUrl;
            if (version != null) Version = version;
            VersionPolicy = policy;
        }

        // Convenience builders: pick protocol preference.
        public static HttpClientTransport H2(string baseUrl = "") =>
            new(baseUrl, new Version(2, 0), HttpVersionPolicy.RequestVersionOrLower);
        public static HttpClientTransport H3(string baseUrl = "") =>
            new(baseUrl, new Version(3, 0), HttpVersionPolicy.RequestVersionOrHigher);
        public static HttpClientTransport H1(string baseUrl = "") =>
            new(baseUrl, new Version(1, 1), HttpVersionPolicy.RequestVersionOrLower);

        private string _url(string u) => u.StartsWith("http") ? u : Base + u;

        private HttpRequestMessage BuildMessage(Request req, bool stream)
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, _url(req.Url))
            {
                Version = Version,
                VersionPolicy = VersionPolicy,
            };
            msg.Content = new ByteArrayContent(req.Body ?? Array.Empty<byte>());
            // Caller-supplied metadata (auth/tenant/token) first — a caller
            // content-type must win over the shape default (the JSON codec
            // depends on it).
            string? callerCt = null;
            foreach (var kv in req.Headers)
            {
                if (kv.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                {
                    callerCt = kv.Value.FirstOrDefault();
                    continue;
                }
                msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
            var ct = callerCt ?? (stream ? "application/connect+proto" : "application/proto");
            msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ct);
            return msg;
        }

        public async Task<Response> Send(Request req)
        {
            var msg = BuildMessage(req, false);
            var resp = await _client.SendAsync(msg, req.CancellationToken);
            var body = await resp.Content.ReadAsByteArrayAsync();
            var s = (int)resp.StatusCode;
            var all = MapHeaders(resp);
            if (all.TryGetValue("content-encoding", out var ce) && ce.Count > 0 && ce[0] == "gzip" && body.Length > 0)
            {
                body = Protocol.GzipDecompress(body);
            }
            var (mapped, trailers) = Protocol.DemuxTrailers(all);
            RpcError? err = null;
            if (s >= 300)
            {
                err = RpcErrorFrom(resp, s, body);
                if (err == null)
                {
                    var (jc, jm, jd) = Protocol.DecodeErrorJson(body);
                    err = jc != 0 ? new RpcError(jc, jm, jd) : new RpcError(Protocol.ConnectFromStatus(s), System.Text.Encoding.UTF8.GetString(body));
                }
            }
            return new Response { Status = s, Headers = mapped, Body = body, Trailers = trailers, Error = err };
        }

        private static Dictionary<string, List<string>> MapHeaders(HttpResponseMessage resp)
        {
            var outH = new Dictionary<string, List<string>>();
            foreach (var h in resp.Headers)
            {
                outH[h.Key] = new List<string>(h.Value);
            }
            foreach (var h in resp.Content.Headers)
            {
                outH[h.Key] = new List<string>(h.Value);
            }
            return outH;
        }

        /// <summary>Reconstruct the exact RpcError from the connect-code header
        /// (the HTTP status alone is lossy). Returns null when the header is
        /// absent so the caller falls through to the Connect JSON body path.</summary>
        private static RpcError? RpcErrorFrom(HttpResponseMessage resp, int status, byte[] body)
        {
            if (resp.Headers.TryGetValues("connect-code", out var codes))
            {
                var first = System.Linq.Enumerable.FirstOrDefault(codes);
                if (first != null && int.TryParse(first, out var c))
                {
                    var msg = resp.Headers.TryGetValues("connect-error", out var es)
                        ? System.Linq.Enumerable.FirstOrDefault(es) ?? ""
                        : "";
                    // The header carries the exact code; the JSON body (when
                    // present) may still carry details - merge them.
                    var (_, _, hd) = Protocol.DecodeErrorJson(body);
                    return new RpcError(c, msg, hd);
                }
            }
            return null;
        }
        public async Task<IAsyncEnumerable<byte[]>> OpenStream(Request req)
        {
            var msg = BuildMessage(req, true);
            var resp = await _client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, req.CancellationToken);
            var stream = await resp.Content.ReadAsStreamAsync();
            return DeFrame(stream);
        }

        /// <summary>Decode length-prefixed Connect frames from a live stream and
        /// yield each payload as it arrives. The 5-byte header is not guaranteed
        /// to arrive whole, so bytes accumulate until a full frame is buffered.
        /// </summary>
        private static async IAsyncEnumerable<byte[]> DeFrame(Stream s)
        {
            var acc = new List<byte>(8192);
            int consumed = 0;
            bool sawEnd = false;
            var buf = new byte[8192];
            while (true)
            {
                int n = await s.ReadAsync(buf, 0, buf.Length);
                if (n == 0) break;
                for (int i = 0; i < n; i++) acc.Add(buf[i]);
                while (true)
                {
                    int avail = acc.Count - consumed;
                    if (avail < 5) break;
                    int len = (acc[consumed + 1] << 24) | (acc[consumed + 2] << 16) | (acc[consumed + 3] << 8) | acc[consumed + 4];
                    if (avail - 5 < len) break;
                    var payload = new byte[len];
                    for (int i = 0; i < len; i++) payload[i] = acc[consumed + 5 + i];
                    byte flags = acc[consumed];
                    consumed += 5 + len;
                    if ((flags & 0x01) != 0) payload = Protocol.GzipDecompress(payload);
                    if ((flags & Protocol.EndStream) != 0)
                    {
                        // Connect end-stream: an error and/or trailing metadata.
                        var es = Protocol.DecodeEndStream(payload);
                        if (es.Code != 0) throw new RpcError(es.Code, es.Message, es.Details);
                        sawEnd = true;
                        yield break;
                    }
                    yield return payload;
                }
                if (consumed > 0)
                {
                    acc.RemoveRange(0, consumed);
                    consumed = 0;
                }
            }
            // Fault matrix F2/M8: the Connect protocol requires every
            // server-stream to terminate with an END frame; a body that ends
            // without one (or with trailing partial bytes) was truncated.
            if (acc.Count > 0 && !sawEnd)
                throw new RpcError(13, "truncated frame at end of stream");
            if (!sawEnd)
                throw new RpcError(13, "stream ended without END frame");
        }
    }
}
