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
            3 => 400, 5 => 404, 7 => 403, 8 => 429, 16 => 401, 14 => 503, _ => 500
        };
        public static int ConnectFromStatus(int status) => status switch
        {
            400 => 3, 404 => 5, 403 => 7, 401 => 16, 429 => 8, 503 => 14, _ => 13
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

    public class HttpClientTransport : Transport
    {
        private readonly HttpClient _client;
        public string Base { get; set; } = "";
        public HttpClientTransport(string baseUrl = "") { _client = new HttpClient(); Base = baseUrl; }
        private string _url(string u) => u.StartsWith("http") ? u : Base + u;
        public async Task<Response> Send(Request req)
        {
            var msg = new HttpRequestMessage(new HttpMethod(req.Method), _url(req.Url));
            if (req.Body != null) msg.Content = new ByteArrayContent(req.Body);
            msg.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");
            var resp = await _client.SendAsync(msg);
            var body = await resp.Content.ReadAsByteArrayAsync();
            var s = (int)resp.StatusCode;
            return new Response { Status = s, Body = body, Error = s >= 300 ? new RpcError(Protocol.ConnectFromStatus(s), System.Text.Encoding.UTF8.GetString(body)) : null };
        }
        public async Task<IAsyncEnumerable<byte[]>> OpenStream(Request req)
        {
            var msg = new HttpRequestMessage(new HttpMethod(req.Method), _url(req.Url));
            if (req.Body != null) msg.Content = new ByteArrayContent(req.Body);
            msg.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/connect+proto");
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
