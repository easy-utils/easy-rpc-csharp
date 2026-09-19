using System.Collections.Generic;
using System.Threading.Tasks;
using EasyRpc;
using Easyrpc.Conformance.V1;
using Google.Protobuf;
using Xunit;

// Transport-independent JSON codec tests: encode/decode round-trips + the
// content-type mapping. Mirrors the other languages' json_codec tests.
public class JsonCodecTests
{
    [Fact]
    public void ContentKindMapping()
    {
        Assert.Equal("proto", Protocol.ContentKindOf("application/proto"));
        Assert.Equal("json", Protocol.ContentKindOf("application/json; charset=utf-8"));
        Assert.Equal("json", Protocol.ContentKindOf("application/connect+json"));
        Assert.Null(Protocol.ContentKindOf("text/plain"));
        Assert.Equal("application/json", Protocol.ContentTypeFor(false, "json"));
        Assert.Equal("application/connect+json", Protocol.ContentTypeFor(true, "json"));
    }

    [Fact]
    public void JsonStringRoundTrip()
    {
        var m = new EchoResponse { Output = "echo:hi" };
        var bytes = Protocol.EncodeMsg(m, "json");
        // protojson may add whitespace; compare semantically.
        Assert.Contains("\"output\"", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Contains("echo:hi", System.Text.Encoding.UTF8.GetString(bytes));
        var back = Protocol.DecodeMsg<EchoResponse>(bytes, "json");
        Assert.Equal("echo:hi", back.Output);
    }

    [Fact]
    public void JsonBytesAreBase64()
    {
        var m = new EchoBytesResponse { Data = ByteString.CopyFrom(new byte[] { 0, 1, 2, 0xff, 0xfe, 0x80 }) };
        var bytes = Protocol.EncodeMsg(m, "json");
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("AAEC", text); // base64 prefix
        var back = Protocol.DecodeMsg<EchoBytesResponse>(bytes, "json");
        Assert.Equal(m.Data, back.Data);
    }

    [Fact]
    public void JsonIgnoresUnknownFields()
    {
        var json = System.Text.Encoding.UTF8.GetBytes("{\"count\":42,\"unknownField\":\"x\"}");
        var m = Protocol.DecodeMsg<CountRequest>(json, "json");
        Assert.Equal(42, m.Count);
    }
}
