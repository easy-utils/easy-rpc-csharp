using System.Linq;
using System.Threading.Tasks;
using EasyRpc;
using Easyrpc.Conformance.V1;
using Google.Protobuf;
using Xunit;

public class InteropTests
{
    private static string BaseUrl => System.Environment.GetEnvironmentVariable("EASY_RPC_BASE") ?? "http://127.0.0.1:18888";

    private static ConformanceServiceClient Client()
    {
        // Unified transport vocabulary (spec §7.1): h1 | h2 | h3 (default h1).
        var t = (System.Environment.GetEnvironmentVariable("EASY_RPC_TRANSPORT") ?? "h1").ToLowerInvariant() switch
        {
            "h2" => HttpClientTransport.H2(BaseUrl),
            "h3" => HttpClientTransport.H3(BaseUrl),
            _ => HttpClientTransport.H1(BaseUrl),
        };
        return new(t);
    }

    [Fact]
    public async Task EchoUnary()
    {
        var outM = await Client().echo(new EchoRequest { Input = "hi" });
        Assert.Equal("echo:hi", outM.Output);
    }

    [Fact]
    public async Task CountStream()
    {
        var idx = new System.Collections.Generic.List<int>();
        await foreach (var c in Client().count(new CountRequest { Count = 3 }))
            idx.Add(c.Index);
        Assert.Equal(new[] { 0, 1, 2 }, idx);
    }

    [Fact]
    public async Task FailDetailsUnaryCarriesDetails()
    {
        var ex = await Assert.ThrowsAsync<RpcError>(() => Client().failDetails(new FailDetailsRequest
        {
            Code = 8,
            Message = "limited",
            DetailType = "type.googleapis.com/google.rpc.RetryInfo",
            DetailText = "retry:5s",
        }));
        Assert.Equal(8, ex.Code);
        Assert.Contains("limited", ex.Message);
        var d = Assert.Single(ex.Details!);
        Assert.Equal("type.googleapis.com/google.rpc.RetryInfo", d.Type);
        Assert.Equal("retry:5s", System.Text.Encoding.UTF8.GetString(d.Value));
    }

    [Fact]
    public async Task StreamFailDetailsSurfacesDetails()
    {
        var seen = new System.Collections.Generic.List<int>();
        var ex = await Assert.ThrowsAsync<RpcError>(async () =>
        {
            await foreach (var c in Client().streamFailDetails(new StreamFailDetailsRequest
                     {
                         EmitBefore = 2, Code = 13, Message = "boom", DetailType = "t/stream", DetailText = "sd",
                     }))
                seen.Add(c.Index);
        });
        Assert.Equal(new[] { 0, 1 }, seen);
        Assert.Equal(13, ex.Code);
        Assert.Equal("t/stream", ex.Details!.Single().Type);
        Assert.Equal("sd", System.Text.Encoding.UTF8.GetString(ex.Details.Single().Value));
    }

    [Fact]
    public async Task UnaryTrailerSurfaces()
    {
        var c = Client();
        var outM = await c.echoTrailer(new EchoTrailerRequest { Input = "x" });
        Assert.Equal("trailer:x", outM.Output);
        Assert.Equal(new[] { "unary-x" }, c.LastTrailers["x-trl"]);
    }

    [Fact]
    public async Task UnaryErrorSurfaces()
    {
        var ex = await Assert.ThrowsAsync<RpcError>(() => Client().fail(new FailRequest { Message = "nope" }));
        Assert.Equal(3, ex.Code);
    }

    [Fact]
    public async Task EchoBytesRoundTrip()
    {
        var data = new byte[] { 0, 1, 2, 0xff, 0xfe, 0x80 };
        var res = await Client().echoBytes(new EchoBytesRequest { Data = ByteString.CopyFrom(data) });
        Assert.Equal(data, res.Data.ToByteArray());
    }

    [Fact]
    public async Task EmptyRoundTrip()
    {
        var res = await Client().empty(new EmptyRequest());
        Assert.Equal(0, res.CalculateSize());
    }

    [Fact]
    public async Task BigStreamManyFrames()
    {
        var idx = new System.Collections.Generic.List<int>();
        await foreach (var c in Client().bigStream(new BigStreamRequest { Count = 4, Size = 2048 }))
            idx.Add(c.Index);
        Assert.Equal(new[] { 0, 1, 2, 3 }, idx);
    }

    [Fact]
    public async Task SleepReturnsOk()
    {
        var res = await Client().sleep(new SleepRequest { Millis = 0 });
        Assert.True(res.Ok);
    }
}
