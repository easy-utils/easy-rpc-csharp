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
        var t = new HttpClientTransport();
        var req = new Request { Url = "http://127.0.0.1:18888/v1/echo", Body = new EchoRequest { Input = "hi" }.ToByteArray() };
        var res = await t.Send(req);
        Assert.Equal(200, res.Status);
        var outM = EchoResponse.Parser.ParseFrom(res.Body);
        Assert.Equal("echo:hi", outM.Output);
    }
}
