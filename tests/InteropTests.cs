using System.Linq;
using System.Threading.Tasks;
using EasyRpc;
using Easyrpc.Conformance.V1;
using Google.Protobuf;
using Xunit;

public class InteropTests
{
    private static string BaseUrl => System.Environment.GetEnvironmentVariable("EASY_RPC_BASE") ?? "http://127.0.0.1:18888";

    private static ConformanceServiceClient Client() => new(new HttpClientTransport(baseUrl: BaseUrl));

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
}
