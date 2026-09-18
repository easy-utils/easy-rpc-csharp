// Error-path matrix (spec §4.2 M1–M16) + Error Details round-trip (§4.1).
// Mirrored in every language implementation; inputs are constructed directly
// against the protocol functions - no server needed.
using System;
using System.Text;
using EasyRpc;
using Xunit;

public class ErrorsMatrixTests
{
    private static readonly ErrorDetail Detail =
        new("type.googleapis.com/google.rpc.RetryInfo", new byte[] { 1, 2, 3, 250 });

    private static byte[] Enc(string s) => Encoding.UTF8.GetBytes(s);

    /** decodeEndStream now returns an EndStream record; destructure (c,m,d). */
    private static (int c, string m, System.Collections.Generic.IReadOnlyList<ErrorDetail>? d) De(byte[] payload)
    {
        var es = Protocol.DecodeEndStream(payload);
        return (es.Code, es.Message, es.Details);
    }

    [Fact] public void M1EmptyPayloadIsCleanEnd()
    {
        var (c, m, d) = De(Array.Empty<byte>());
        Assert.Equal(0, c); Assert.Equal("", m); Assert.Null(d);
    }

    [Fact] public void M2GarbageIsCleanEnd()
    {
        var (c, _, _) = De(new byte[] { 0xff, 0xfe, 0x00, 0x42 });
        Assert.Equal(0, c);
    }

    [Fact] public void M3ErrorWithoutCodeIsUnknown()
    {
        var (c, m, _) = De(Enc(@"{""error"":{}}"));
        Assert.Equal(2, c); Assert.Equal("", m);
    }

    [Fact] public void M4UnknownCodeNameIs2()
    {
        var (c, m, _) = De(Enc(@"{""error"":{""code"":""nope"",""message"":""m""}}"));
        Assert.Equal(2, c); Assert.Equal("m", m);
    }

    [Fact] public void M5UnknownFieldsIgnored()
    {
        var (c, _, _) = De(Enc(@"{""error"":{""code"":""not_found"",""message"":""m""},""x"":1}"));
        Assert.Equal(5, c);
    }

    [Fact] public void M6DetailsRoundTrip()
    {
        var payload = Protocol.EncodeEndStream(8, "rate limited", new[] { Detail });
        var (c, m, d) = De(payload);
        Assert.Equal(8, c); Assert.Equal("rate limited", m);
        Assert.Equal(Detail, Assert.Single(d!));
    }

    [Fact] public void M7MalformedDetailsEntriesSkipped()
    {
        var json = @"{""error"":{""code"":""resource_exhausted"",""details"":[" +
                   @"{""type"":""t"",""value"":""!!!""},{""value"":""x""},{""type"":""ok""}," +
                   @"{""type"":""t2"",""value"":""AQID""}]}}";
        var (_, _, d) = De(Enc(json));
        Assert.Equal(new ErrorDetail("t2", new byte[] { 1, 2, 3 }), Assert.Single(d!));
    }

    [Fact] public void M14EndMetadataIsTrailers()
    {
        var es = Protocol.DecodeEndStream(Enc(@"{""metadata"":{""x-trl"":[""v1"",""v2""]}}"));
        Assert.Equal(0, es.Code);
        Assert.Equal(new[] { "v1", "v2" }, es.Metadata["x-trl"]);
    }

    [Fact] public void M15DemuxTrailersPrefixCaseInsensitive()
    {
        var all = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>
        {
            ["content-type"] = new() { "application/proto" },
            ["Trailer-X-Trl"] = new() { "a" },
        };
        var (h, t) = Protocol.DemuxTrailers(all);
        Assert.True(h.ContainsKey("content-type"));
        Assert.Equal(new[] { "a" }, t["x-trl"]);
    }

    [Fact] public void DetailsOmittedWhenEmpty()
    {
        var text = Encoding.UTF8.GetString(Protocol.EncodeEndStream(5, "gone"));
        Assert.Equal(@"{""error"":{""code"":""not_found"",""message"":""gone""}}", text);
    }

    [Fact] public void CleanEndSerializesAsEmptyObject()
    {
        // Connect's END frame parser requires valid JSON; a clean end is `{}`.
        Assert.Equal("{}", Encoding.UTF8.GetString(Protocol.EncodeEndStream(0, "")));
    }

    [Fact] public void M11PlainTextIsNotJsonError()
    {
        var (c, _, _) = Protocol.DecodeErrorJson(Enc("busy"));
        Assert.Equal(0, c);
    }

    [Fact] public void UnaryDetailsRoundTrip()
    {
        var body = Protocol.EncodeErrorJson(8, "limited", new[] { Detail });
        var (c, m, d) = Protocol.DecodeErrorJson(body);
        Assert.Equal(8, c); Assert.Equal("limited", m);
        Assert.Equal(Detail, Assert.Single(d!));
    }

    [Fact] public void M12M13DeadlineMapsToCode4()
    {
        var (c, _, _) = Protocol.DecodeErrorJson(Protocol.EncodeErrorJson(4, "deadline exceeded"));
        Assert.Equal(4, c);
        Assert.Equal(504, Protocol.HttpStatus(4));
        Assert.Equal(4, Protocol.ConnectFromStatus(504));
    }

    [Fact] public void M8TruncatedFrameYieldsNothing()
    {
        var full = Protocol.Frame(new byte[10]);
        var cut = full[..^4];
        Assert.True(cut.Length < 5 + 10);
    }
}
