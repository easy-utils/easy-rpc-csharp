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
    public class RpcError : Exception
    {
        public int Code { get; }
        public RpcError(int code, string message) : base($"easyrpc: code={code} {message}") { Code = code; }
    }

    public class Request
    {
        public string Url { get; set; } = "";
        public string Method { get; set; } = "POST";
        public Dictionary<string, List<string>> Headers { get; set; } = new();
        public byte[]? Body { get; set; }
    }

    public class Response
    {
        public int Status { get; set; }
        public Dictionary<string, List<string>> Headers { get; set; } = new();
        public byte[] Body { get; set; } = System.Array.Empty<byte>();
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

        /// <summary>Encode a Connect unary error body {code,message}.</summary>
        public static byte[] EncodeErrorJson(int code, string message)
        {
            var obj = new Dictionary<string, object> { ["code"] = CodeToString(code), ["message"] = message };
            return System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(obj));
        }

        /// <summary>Parse a Connect unary error body; (0,"") when not one.</summary>
        public static (int code, string message) DecodeErrorJson(byte[] body)
        {
            if (body.Length == 0) return (0, "");
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("code", out var c) || c.ValueKind != System.Text.Json.JsonValueKind.String)
                    return (0, "");
                var msg = doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String
                    ? (m.GetString() ?? "") : "";
                return (CodeFromString(c.GetString() ?? "unknown"), msg);
            }
            catch { return (0, ""); }
        }

        /// <summary>Encode a Connect end-stream payload; a clean end is empty.</summary>
        public static byte[] EncodeEndStream(int code, string message)
        {
            if (code == 0) return Array.Empty<byte>();
            var obj = new Dictionary<string, object>
            {
                ["error"] = new Dictionary<string, object>
                {
                    ["code"] = CodeToString(code),
                    ["message"] = message,
                },
            };
            return System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(obj));
        }

        /// <summary>Decode a Connect end-stream payload into (code, message).</summary>
        public static (int code, string message) DecodeEndStream(byte[] payload)
        {
            if (payload.Length == 0) return (0, "");
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("error", out var e)) return (0, "");
                var code = e.TryGetProperty("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
                    ? CodeFromString(c.GetString() ?? "unknown") : 2;
                var msg = e.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String
                    ? (m.GetString() ?? "") : "";
                return (code, msg);
            }
            catch { return (0, ""); }
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
        public Task<Response> Unary(Request req, Func<Request, Task<Response>> next) => next(Protocol.WithTimeout(req, _ms));
        public Task<IAsyncEnumerable<byte[]>> Stream(Request req, Func<Request, Task<IAsyncEnumerable<byte[]>>> next) => next(Protocol.WithTimeout(req, _ms));
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
            var msg = new HttpRequestMessage(new HttpMethod(req.Method), _url(req.Url))
            {
                Version = Version,
                VersionPolicy = VersionPolicy,
            };
            if (req.Body != null) msg.Content = new ByteArrayContent(req.Body);
            var ct = stream ? "application/connect+proto" : "application/proto";
            msg.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ct);
            // Caller-supplied metadata (auth/tenant/token).
            foreach (var kv in req.Headers)
            {
                if (kv.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)) continue;
                msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
            return msg;
        }

        public async Task<Response> Send(Request req)
        {
            var msg = BuildMessage(req, false);
            var resp = await _client.SendAsync(msg);
            var body = await resp.Content.ReadAsByteArrayAsync();
            var s = (int)resp.StatusCode;
            var mapped = MapHeaders(resp);
            RpcError? err = null;
            if (s >= 300)
            {
                err = RpcErrorFrom(resp, s, body);
                if (err == null)
                {
                    var (jc, jm) = Protocol.DecodeErrorJson(body);
                    err = jc != 0 ? new RpcError(jc, jm) : new RpcError(Protocol.ConnectFromStatus(s), System.Text.Encoding.UTF8.GetString(body));
                }
            }
            return new Response { Status = s, Headers = mapped, Body = body, Error = err };
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

        /// <summary>Reconstruct the exact RpcError from connect-code/connect-error
        /// (the HTTP status alone is lossy).</summary>
        private static RpcError RpcErrorFrom(HttpResponseMessage resp, int status, byte[] body)
        {
            if (resp.Headers.TryGetValues("connect-code", out var codes))
            {
                var first = System.Linq.Enumerable.FirstOrDefault(codes);
                if (first != null && int.TryParse(first, out var c))
                {
                    var msg = resp.Headers.TryGetValues("connect-error", out var es)
                        ? System.Linq.Enumerable.FirstOrDefault(es) ?? ""
                        : "";
                    return new RpcError(c, msg);
                }
            }
            return new RpcError(Protocol.ConnectFromStatus(status), System.Text.Encoding.UTF8.GetString(body));
        }
        public async Task<IAsyncEnumerable<byte[]>> OpenStream(Request req)
        {
            var msg = BuildMessage(req, true);
            var resp = await _client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
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
                    if ((flags & Protocol.EndStream) != 0)
                    {
                        // Connect end-stream: a non-empty payload is an error.
                        var (code, message) = Protocol.DecodeEndStream(payload);
                        if (code != 0) throw new RpcError(code, message);
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
        }
    }
}
