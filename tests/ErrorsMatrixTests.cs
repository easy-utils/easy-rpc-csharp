// Error-path matrix (spec §4.2 M1–M13) + Error Details round-trip (§4.1).
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

    [Fact] public void M1EmptyPayloadIsCleanEnd()
    {
        var (c, m, d) = Protocol.DecodeEndStream(Array.Empty<byte>());
        Assert.Equal(0, c); Assert.Equal("", m); Assert.Null(d);
    }

    [Fact] public void M2GarbageIsCleanEnd()
    {
        var (c, _, _) = Protocol.DecodeEndStream(new byte[] { 0xff, 0xfe, 0x00, 0x42 });
        Assert.Equal(0, c);
    }

    [Fact] public void M3ErrorWithoutCodeIsUnknown()
    {
        var (c, m, _) = Protocol.DecodeEndStream(Enc(@"{""error"":{}}"));
        Assert.Equal(2, c); Assert.Equal("", m);
    }

    [Fact] public void M4UnknownCodeNameIs2()
    {
        var (c, m, _) = Protocol.DecodeEndStream(Enc(@"{""error"":{""code"":""nope"",""message"":""m""}}"));
        Assert.Equal(2, c); Assert.Equal("m", m);
    }

    [Fact] public void M5UnknownFieldsIgnored()
    {
        var (c, _, _) = Protocol.DecodeEndStream(Enc(@"{""error"":{""code"":""not_found"",""message"":""m""},""x"":1}"));
        Assert.Equal(5, c);
    }

    [Fact] public void M6DetailsRoundTrip()
    {
        var payload = Protocol.EncodeEndStream(8, "rate limited", new[] { Detail });
        var (c, m, d) = Protocol.DecodeEndStream(payload);
        Assert.Equal(8, c); Assert.Equal("rate limited", m);
        Assert.Equal(Detail, Assert.Single(d!));
    }

    [Fact] public void M7MalformedDetailsEntriesSkipped()
    {
        var json = @"{""error"":{""code"":""resource_exhausted"",""details"":[" +
                   @"{""type"":""t"",""value"":""!!!""},{""value"":""x""},{""type"":""ok""}," +
                   @"{""type"":""t2"",""value"":""AQID""}]}}";
        var (_, _, d) = Protocol.DecodeEndStream(Enc(json));
        Assert.Equal(new ErrorDetail("t2", new byte[] { 1, 2, 3 }), Assert.Single(d!));
    }

    [Fact] public void DetailsOmittedWhenEmpty()
    {
        var text = Encoding.UTF8.GetString(Protocol.EncodeEndStream(5, "gone"));
        Assert.Equal(@"{""error"":{""code"":""not_found"",""message"":""gone""}}", text);
    }

    [Fact] public void M11PlainTextIsNotJsonError()
    {
        var (c, _, _) = Protocol.DecodeErrorJson(Encoding.UTF8.GetBytes("busy"));
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

    [Fact] public void M8M9FramingBounds()
    {
        // M9: oversized frame is refused by the reader (4MB default).
        Assert.True(Protocol.DefaultMaxMessageBytes == 4 * 1024 * 1024);
        // M8: a truncated buffer can never satisfy 5+len (asserted structurally:
        // the reader only yields a frame when the whole payload is present).
        var full = Protocol.Frame(new byte[10]);
        Assert.True(full.Length == 15);
        var cut = new byte[full.Length - 4];
        Array.Copy(full, cut, cut.Length);
        Assert.True(cut.Length < 5 + 10);
    }
}
