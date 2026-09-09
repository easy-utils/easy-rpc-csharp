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
    }

    public interface Transport
    {
        Task<Response> Send(Request req);
        Task<IAsyncEnumerable<byte[]>> OpenStream(Request req);
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
            HttpVersionPolicy policy = HttpVersionPolicy.RequestVersionOrLower)
        {
            _client = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
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
            return msg;
        }

        public async Task<Response> Send(Request req)
        {
            var msg = BuildMessage(req, false);
            var resp = await _client.SendAsync(msg);
            var body = await resp.Content.ReadAsByteArrayAsync();
            var s = (int)resp.StatusCode;
            return new Response { Status = s, Body = body, Error = s >= 300 ? new RpcError(Protocol.ConnectFromStatus(s), System.Text.Encoding.UTF8.GetString(body)) : null };
        }
        public async Task<IAsyncEnumerable<byte[]>> OpenStream(Request req)
        {
            var msg = BuildMessage(req, true);
            var resp = await _client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
            var stream = await resp.Content.ReadAsStreamAsync();
            return DeFrame(stream);
        }
        private static async IAsyncEnumerable<byte[]> DeFrame(Stream s)
        {
            var acc = new MemoryStream();
            var buf = new byte[8192];
            while (true)
            {
                int n = await s.ReadAsync(buf, 0, buf.Length);
                if (n == 0) break;
                acc.Write(buf, 0, n);
                while (true)
                {
                    long avail = acc.Length - acc.Position;
                    if (avail < 5) break;
                    var hdr = new byte[5];
                    acc.Read(hdr, 0, 5);
                    int len = (hdr[1] << 24) | (hdr[2] << 16) | (hdr[3] << 8) | hdr[4];
                    if (acc.Length - acc.Position < len) break;
                    var payload = new byte[len];
                    acc.Read(payload, 0, len);
                    yield return payload;
                    if ((hdr[0] & Protocol.EndStream) != 0) yield break;
                }
                acc.Position = 0;
                // compact remaining
                var remaining = acc.ToArray();
                acc.SetLength(0);
                acc.Write(remaining, 0, remaining.Length);
            }
        }
    }
}


public delegate byte[] CSharpUnaryHandler(string kind, byte[] input);
public delegate void CSharpStreamHandler(string kind, byte[] input, Action<byte[]> emit);

public class CSharpServerRegistry
{
    public Dictionary<string, CSharpUnaryHandler> Unary = new();
    public Dictionary<string, CSharpStreamHandler> Stream = new();
}

public class CSharpServer
{
    private readonly HttpListener _listener = new();
    private readonly List<(string path, bool stream, string name)> _specs;
    private readonly CSharpServerRegistry _reg;
    public CSharpServer(List<(string, bool, string)> specs, CSharpServerRegistry reg) { _specs = specs; _reg = reg; }

    public void Start(int port = 18888)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(async () => { while (true) await Loop(); });
    }
    public void Stop() => _listener.Stop();

    private async Task Loop()
    {
        var ctx = await _listener.GetContextAsync();
        _ = Handle(ctx);
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            var spec = _specs.FirstOrDefault(s => s.Item1 == path);
            if (spec.Item1 == null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
            var kind = (ctx.Request.ContentType ?? "").StartsWith("application/json") ? "json" : "proto";
            using var ms = new MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms);
            var input = ms.ToArray();
            var ct = kind == "json" ? (spec.Item2 ? "application/connect+json" : "application/json") : (spec.Item2 ? "application/connect+proto" : "application/proto");
            ctx.Response.ContentType = ct;
            if (spec.Item2)
            {
                var h = _reg.Stream[spec.Item3];
                var chunks = new List<byte[]>();
                h(kind, input, b => chunks.Add(b));
                var total = 0; foreach (var c in chunks) total += 5 + c.Length;
                var outb = new byte[total]; var off = 0;
                foreach (var c in chunks) { var fr = EasyRpc.Protocol.Frame(c); Array.Copy(fr, 0, outb, off, fr.Length); off += fr.Length; }
                ctx.Response.StatusCode = 200;
                await ctx.Response.OutputStream.WriteAsync(outb);
            }
            else
            {
                var h = _reg.Unary[spec.Item3];
                try { var outb = h(kind, input); ctx.Response.StatusCode = 200; await ctx.Response.OutputStream.WriteAsync(outb); }
                catch { ctx.Response.StatusCode = 500; }
            }
        }
        catch { ctx.Response.StatusCode = 500; }
        ctx.Response.Close();
    }
}
