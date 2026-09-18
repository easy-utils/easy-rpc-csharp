// Wire golden-vector conformance (transport-independent): the protocol layer
// must reproduce easy-rpc-spec/conformance/wire-vectors.json. Frames are
// byte-exact; JSON payloads compare SEMANTICALLY (key order is not significant).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using EasyRpc;
using Xunit;

public class WireVectorsTests
{
    private static JsonElement V() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wire-vectors.json"))).RootElement;

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    private static byte[] UnHex(string s) => Convert.FromHexString(s);
    private static string Canon(byte[] b)
    {
        using var d = JsonDocument.Parse(b);
        return JsonSerializer.Serialize(d.RootElement);
    }
    private static string CanonStr(string json)
    {
        using var d = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(d.RootElement);
    }
    private static Dictionary<string, List<string>> Md(JsonElement e)
    {
        var outH = new Dictionary<string, List<string>>();
        foreach (var p in e.EnumerateObject())
            outH[p.Name] = p.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        return outH;
    }

    [Fact]
    public void Frames()
    {
        foreach (var f in V().GetProperty("frames").EnumerateArray())
        {
            var e = f.GetProperty("encode");
            var raw = Protocol.Frame(UnHex(e.GetProperty("payloadHex").GetString()!), e.GetProperty("end").GetBoolean());
            if (e.GetProperty("compressed").GetBoolean()) raw[0] |= 0x01;
            Assert.Equal(f.GetProperty("bytesHex").GetString(), Hex(raw));
        }
    }

    [Fact]
    public void EndStream()
    {
        foreach (var m in V().GetProperty("endStream").EnumerateArray())
        {
            var dec = m.GetProperty("decode");
            var es = Protocol.DecodeEndStream(UnHex(dec.GetProperty("bytesHex").GetString()!));
            Assert.Equal(m.GetProperty("code").GetInt32(), es.Code);
            Assert.Equal(m.GetProperty("message").GetString(), es.Message);
            if (m.TryGetProperty("metadata", out var md) && md.ValueKind != JsonValueKind.Null)
                Assert.Equal(Md(md), es.Metadata);
            if (m.TryGetProperty("encode", out var enc) && enc.ValueKind != JsonValueKind.Null)
            {
                var metadata = enc.TryGetProperty("metadata", out var em) && em.ValueKind != JsonValueKind.Null
                    ? Md(em) : new Dictionary<string, List<string>>();
                var got = Protocol.EncodeEndStream(enc.GetProperty("code").GetInt32(), enc.GetProperty("message").GetString()!, null, metadata);
                Assert.Equal(Canon(UnHex(m.GetProperty("bytesHex").GetString()!)), Canon(got));
            }
        }
    }

    [Fact]
    public void UnaryError()
    {
        foreach (var u in V().GetProperty("unaryError").EnumerateArray())
        {
            var enc = u.GetProperty("encode");
            List<ErrorDetail>? details = null;
            if (enc.TryGetProperty("details", out var ds) && ds.ValueKind == JsonValueKind.Array)
                details = ds.EnumerateArray().Select(d =>
                    new ErrorDetail(d.GetProperty("type").GetString()!, UnHex(d.GetProperty("valueHex").GetString()!))).ToList();
            var got = Protocol.EncodeErrorJson(enc.GetProperty("code").GetInt32(), enc.GetProperty("message").GetString()!, details);
            Assert.Equal(Canon(UnHex(u.GetProperty("bytesHex").GetString()!)), Canon(got));
        }
    }

    [Fact]
    public void Trailers()
    {
        foreach (var t in V().GetProperty("trailerHeaders").EnumerateArray())
        {
            if (t.TryGetProperty("demux", out var demux) && demux.ValueKind != JsonValueKind.Null)
            {
                var (h, tl) = Protocol.DemuxTrailers(Md(demux));
                Assert.Equal(Md(t.GetProperty("headers")), h);
                Assert.Equal(Md(t.GetProperty("trailers")), tl);
            }
            if (t.TryGetProperty("mux", out var mux) && mux.ValueKind != JsonValueKind.Null)
            {
                var got = Protocol.MuxTrailers(Md(mux.GetProperty("headers")), Md(mux.GetProperty("trailers")));
                Assert.Equal(Md(t.GetProperty("result")), got);
            }
        }
    }

    [Fact]
    public void CodeMap()
    {
        foreach (var c in V().GetProperty("codeNames").EnumerateArray())
        {
            var code = c.GetProperty("code").GetInt32();
            var name = c.GetProperty("name").GetString()!;
            Assert.Equal(name, Protocol.CodeToString(code));
            Assert.Equal(code, Protocol.CodeFromString(name));
            if (code != 0) Assert.Equal(c.GetProperty("http").GetInt32(), Protocol.HttpStatus(code));
        }
    }
}
