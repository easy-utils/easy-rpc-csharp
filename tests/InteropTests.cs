using System.Threading.Tasks;
using EasyRpc;
using Easyrpc.Conformance.V1;
using Google.Protobuf;
using Xunit;

public class InteropTests
{
    [Fact]
    public async Task EchoUnary()
    {
        var baseUrl = System.Environment.GetEnvironmentVariable("EASY_RPC_BASE") ?? "http://127.0.0.1:18888";
        var t = new HttpClientTransport(baseUrl: baseUrl);
        var c = new ConformanceServiceClient(t);
        var outM = await c.echo(new EchoRequest { Input = "hi" });
        Assert.Equal("echo:hi", outM.Output);
    }
}
