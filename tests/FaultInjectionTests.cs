// Fault injection (spec §4.2 M8/M10 + F2) at the protocol level via a local
// mock HTTP server: the client MUST error on truncated/END-less/corrupt-gzip
// bodies, reassemble split frames, and treat a garbage END payload as clean.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using EasyRpc;
using Xunit;

public class FaultInjectionTests : IDisposable
{
    public void Dispose() { }

    private static byte[] Frame(byte[] payload, bool end = false, bool compressed = false)
    {
        var flags = (byte)((end ? 0x02 : 0) | (compressed ? 0x01 : 0));
        var outB = new byte[5 + payload.Length];
        outB[0] = flags;
        outB[1] = (byte)(payload.Length >> 24);
        outB[2] = (byte)(payload.Length >> 16);
        outB[3] = (byte)(payload.Length >> 8);
        outB[4] = (byte)payload.Length;
        payload.CopyTo(outB, 5);
        return outB;
    }

    private async Task<List<byte>> Run(byte[] body, int? readChunk = null)
    {
        // HttpListener needs a fixed port; pick a free one via TcpListener.
        var port = GetFreePort();
        var l2 = new HttpListener();
        l2.Prefixes.Add($"http://127.0.0.1:{port}/");
        l2.Start();
        var serveTask = Task.Run(async () =>
        {
            var ctx = await l2.GetContextAsync();
            var ms = new MemoryStream();
            var send = body;
            ctx.Response.ContentType = "application/connect+proto";
            if (readChunk is int chunk)
            {
                // slow-write in small chunks (fault F5)
                ctx.Response.SendChunked = true;
                await ctx.Response.OutputStream.WriteAsync(send, 0, Math.Min(chunk, send.Length));
                await ctx.Response.OutputStream.FlushAsync();
                for (var i = chunk; i < send.Length; i += chunk)
                {
                    await ctx.Response.OutputStream.WriteAsync(send, i, Math.Min(chunk, send.Length - i));
                    await ctx.Response.OutputStream.FlushAsync();
                    await Task.Delay(2);
                }
            }
            else
            {
                await ms.WriteAsync(send, 0, send.Length);
                ctx.Response.ContentLength64 = send.Length;
                await ctx.Response.OutputStream.WriteAsync(ms.ToArray(), 0, (int)ms.Length);
            }
            ctx.Response.Close();
        });

        var t = new HttpClientTransport(baseUrl: $"http://127.0.0.1:{port}");
        var outB = new List<byte>();
        try
        {
            var st = await t.OpenStream(new Request { Url = "/x" });
            await foreach (var p in st) outB.Add(p[0]);
        }
        finally
        {
            // never await the serve task unconditionally: if the client threw
            // before sending, GetContextAsync never completes.
            await Task.WhenAny(serveTask, Task.Delay(2000));
            l2.Stop();
        }
        return outB;
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact] public async Task F1MidFrameTruncationErrors()
    {
        var full = Frame(new byte[8]);
        var cut = new byte[full.Length - 4];
        Array.Copy(full, cut, cut.Length);
        var body = new byte[Frame(new byte[] { 0 }).Length + cut.Length];
        Array.Copy(Frame(new byte[] { 0 }), body, 6);
        Array.Copy(cut, 0, body, 6, cut.Length);
        await Assert.ThrowsAsync<RpcError>(() => Run(body));
    }

    [Fact] public async Task F2MissingEndFrameErrors()
    {
        await Assert.ThrowsAsync<RpcError>(() => Run(Frame(new byte[] { 0 })));
    }

    [Fact] public async Task F3GarbageEndIsClean()
    {
        var body = Combine(Frame(new byte[] { 0 }), Frame(new byte[] { 0xff, 0xfe, 0x42 }, end: true));
        var got = await Run(body);
        Assert.Equal(new byte[] { 0 }, got);
    }

    [Fact] public async Task F4CorruptGzipErrorsNeverRaw()
    {
        var gz = Protocol.GzipCompress(new byte[] { 1, 2, 3 });
        gz[gz.Length / 2] ^= 0xff;
        var body = Combine(Frame(gz, compressed: true), Frame(Array.Empty<byte>(), end: true));
        await Assert.ThrowsAsync<RpcError>(() => Run(body));
    }

    [Fact] public async Task F5SplitFramesReassemble()
    {
        var body = Combine(Frame(new byte[] { 0 }), Frame(new byte[] { 1 }), Frame(Array.Empty<byte>(), end: true));
        var got = await Run(body, readChunk: 3);
        Assert.Equal(new byte[] { 0, 1 }, got);
    }

    [Fact] public async Task F6ValidGzipDecodes()
    {
        var gz = Protocol.GzipCompress(new byte[] { 7 });
        var body = Combine(Frame(gz, compressed: true), Frame(Array.Empty<byte>(), end: true));
        var got = await Run(body);
        Assert.Equal(new byte[] { 7 }, got);
    }

    private static byte[] Combine(params byte[][] parts)
    {
        var all = new MemoryStream();
        foreach (var p in parts) all.Write(p, 0, p.Length);
        return all.ToArray();
    }
}
