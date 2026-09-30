using System.IO.Compression;
using System.Text;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Dsl;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Dsl;

public sealed class FieldExtractionTests
{
    [Fact] public async Task E01QuerySemantics()
    {
        var f = new M2Fixture(); var r = f.Add("https://example.test/a?x&x=&x=b+c&X=大");
        Assert.Equal(ValueState.Null, (await f.Value("first(original.query[\"x\"])", r)).State);
        var all = await f.Value("all(original.query[\"x\"])", r); Assert.Equal(3, all.ItemsValue.Count); Assert.Equal("", all.ItemsValue[1].Data); Assert.Equal("b+c", all.ItemsValue[2].Data);
        Assert.Equal("大", (await f.Value("first(original.query[\"X\"])", r)).Data);
        Assert.Equal(ValueState.Missing, (await f.Value("first(original.query[\"absent\"])", r)).State);
        foreach (var bad in new[] { "%", "%Q1", "%FF", "%C0%AF" })
            Assert.Equal(ValueState.Invalid, (await f.Value("first(original.query[\"x\"])", r with { OriginalUri = "https://example.test/?x=" + bad })).State);
        M2Fixture.Evidence("grouping", "E01", new { repeated = all.ItemsValue.Count, invalid = 4 });
    }
    [Fact] public async Task E02HeadersAndCookies()
    {
        var f = new M2Fixture(); var r = f.Add(headers: [new("X-User", "a,b"), new("x-user", "second"), new("Cookie", "user=one; user=two=3")]);
        var all = await f.Value("all(original.headers[\"X-USER\"])", r); Assert.Equal(2, all.ItemsValue.Count); Assert.Equal("a,b", all.ItemsValue[0].Data);
        var cookies = await f.Value("all(original.cookies[\"user\"])", r with { ResponseHeaders = [new("Set-Cookie", "user=wrong")] });
        Assert.Equal(new[] { "one", "two=3" }, cookies.ItemsValue.Select(v => v.Data));
        Assert.Equal(ValueState.Invalid, (await f.Value("first(original.cookies[\"user\"])", r with { RequestHeaders = [new("Cookie", "broken")] })).State);
        M2Fixture.Evidence("grouping", "E02", new { headerRows = all.ItemsValue.Count, cookieRows = cookies.ItemsValue.Count });
    }
    [Fact] public async Task E03JsonPathsAndTypes()
    {
        var f = new M2Fixture(); var r = f.Add(body: """{"identity":{"user":"u1"},"business.id":2,"items":[{"id":true}],"nothing":null}""", contentType: "application/json");
        Assert.Equal("u1", (await f.Value("toString(original.json(\"$.identity.user\"))", r)).Data);
        Assert.Equal(2m, (await f.Value("toNumber(original.json(\"$[\\\"business.id\\\"]\"))", r)).Data);
        Assert.True((await f.Value("toBool(original.json(\"$.items[0].id\"))", r)).Truth);
        Assert.Equal(ValueState.Null, (await f.Value("original.json(\"$.nothing\")", r)).State);
        Assert.Equal(ValueState.Missing, (await f.Value("original.json(\"$.missing\")", r)).State);
        var duplicate = f.Add(body: """{"user":1,"user":2}""", contentType: "application/json");
        Assert.Equal(ValueState.Invalid, (await f.Value("original.json(\"$.user\")", duplicate)).State);
        Assert.False(DslCompiler.Compile(M2Fixture.Program("field user = original.json(\"$..user\");")).Success);
        M2Fixture.Evidence("grouping", "E03", new { paths = 5, duplicateRejected = true });
    }
    [Fact] public async Task E04ThreeValuedLogicAndFallback()
    {
        var f = new M2Fixture(); var r = f.Add(body: "{\"user\":", contentType: "application/json", bodyState: ValueState.NotReady);
        var unknown = "toBool(original.json(\"$.flag\"))";
        // 真值表的关键反例：未知取反仍未知，明确False/True可以决定短路结果。
        foreach (var expr in new[] { $"not ({unknown})", $"{unknown} and true", $"{unknown} or false" }) Assert.Null((await f.Value(expr, r)).Truth);
        Assert.False((await f.Value($"{unknown} and false", r)).Truth); Assert.True((await f.Value($"{unknown} or true", r)).Truth);
        Assert.Equal(ValueState.NotReady, (await f.Value("firstNonEmpty(toString(original.json(\"$.user\")), \"fallback\")", r)).State);
        Assert.Equal("fallback", (await f.Value("firstNonEmpty(null, \"\", \"fallback\")", r)).Data);
        var reads = f.BodyReads; Assert.Equal("selected", (await f.Value("if(true,\"selected\",toString(original.json(\"$.user\")))", r)).Data); Assert.Equal(reads, f.BodyReads);
        M2Fixture.Evidence("grouping", "E04", new { unknownNot = (await f.Value($"not ({unknown})", r)).Truth is null, fallbackState = (await f.Value("firstNonEmpty(toString(original.json(\"$.user\")), \"fallback\")", r)).State.ToString(), unselectedBodyReads = f.BodyReads - reads });
    }
    [Fact] public async Task E05FormAndPath()
    {
        var f = new M2Fixture(); var r = f.Add("https://example.test/actors/a%2Fb//end", "user=a+b&user=", "application/x-www-form-urlencoded");
        Assert.Equal("a b", (await f.Value("first(original.form[\"user\"])", r)).Data);
        Assert.Equal("a/b", (await f.Value("original.pathSegment(1)", r)).Data); Assert.Equal("", (await f.Value("original.pathSegment(2)", r)).Data);
        Assert.Equal(ValueState.Missing, (await f.Value("original.pathSegment(99)", r)).State);
        Assert.Equal(ValueState.Unavailable, (await f.Value("first(original.form[\"user\"])", r with { RequestBody = r.RequestBody with { ContentType = "multipart/form-data" } })).State);
        M2Fixture.Evidence("grouping", "E05", new { decoded = "a b", segment = "a/b", emptySegment = true });
    }
    [Fact] public async Task E06CompletenessAndIoFailures()
    {
        var f = new M2Fixture(); var r = f.Add(body: """{"user":"u"}""", contentType: "application/json");
        foreach (var state in new[] { CaptureState.Partial, CaptureState.MetadataOnly, CaptureState.Failed })
            Assert.Equal(ValueState.Unavailable, (await f.Value("original.json(\"$.user\")", r with { RequestBody = r.RequestBody with { Capture = state } })).State);
        var waiting = r with { RequestBody = r.RequestBody with { Transport = TransportState.Receiving } };
        Assert.Equal(ValueState.NotReady, (await f.Value("original.json(\"$.user\")", waiting)).State);
        f.IsArchive = true; Assert.Equal("DATA_ARCHIVED_INCOMPLETE", (await f.Value("original.json(\"$.user\")", waiting)).Reason);
        f.ReadFailure = new FileNotFoundException(); Assert.Equal(ValueState.Unavailable, (await f.Value("original.json(\"$.user\")", r)).State);
        Assert.Equal(0, f.Memory.Reserved);
        M2Fixture.Evidence("grouping", "E06", new { partialStates = 3, archivedState = "Unavailable", reservedAfter = f.Memory.Reserved });
    }
    [Fact] public async Task E07BodyAndJsonBudgets()
    {
        var f = new M2Fixture(); var r = f.Add(body: "{\"user\":\"u\"}", contentType: "application/json");
        Assert.Equal(ValueState.BudgetExceeded, (await f.Value("original.json(\"$.user\")", r with { RequestBody = r.RequestBody with { CommittedBytes = DslLimits.BodyBytes + 1 } })).State);
        var deep = f.Add(body: new string('[', 33) + "0" + new string(']', 33), contentType: "application/json");
        Assert.Equal(ValueState.BudgetExceeded, (await f.Value("original.json(\"$\")", deep)).State);
        foreach (var encoding in new[] { "gzip", "br", "deflate" })
        {
            using var compressed = new MemoryStream();
            using (Stream zipper = encoding switch { "gzip" => new GZipStream(compressed, CompressionMode.Compress, true), "br" => new BrotliStream(compressed, CompressionMode.Compress, true), _ => new DeflateStream(compressed, CompressionMode.Compress, true) })
                zipper.Write(Encoding.UTF8.GetBytes("{\"user\":\"compressed\"}"));
            f.Bodies[r.RequestBody.BodyId] = compressed.ToArray();
            var encoded = r with { RequestBody = r.RequestBody with { ContentEncoding = encoding, CommittedBytes = compressed.Length, ObservedBytes = compressed.Length } };
            Assert.Equal("compressed", (await f.Value("toString(original.json(\"$.user\"))", encoded)).Data);
        }
        Assert.Equal(0, f.Memory.Reserved);
        M2Fixture.Evidence("grouping", "E07", new { codecs = 3, depthExceeded = true, reservedAfter = f.Memory.Reserved });
    }
    [Fact] public async Task E08MimeAndResponseIndependence()
    {
        var f = new M2Fixture(); var r = f.Add();
        Assert.Equal(ValueState.Missing, (await f.Value("original.json(\"$.user\")", r)).State);
        r = f.Add(body: "{\"user\":\"u\"}", contentType: "application/json");
        Assert.Equal("u", (await f.Value("toString(original.json(\"$.user\"))", r with { ResponseBody = r.ResponseBody with { Transport = TransportState.Receiving } })).Data);
        Assert.Equal(ValueState.Unavailable, (await f.Value("original.json(\"$.user\")", r with { RequestBody = r.RequestBody with { ContentType = null } })).State);
        Assert.Equal(ValueState.Missing, (await f.Value("first(original.form[\"user\"])", r)).State);
        M2Fixture.Evidence("grouping", "E08", new { completedRequestUsable = true, missingMime = "Unavailable" });
    }
    [Fact] public async Task E09ConversionAndValueLimits()
    {
        var f = new M2Fixture(); var r = f.Add();
        Assert.Equal("a", (await f.Value("lower(trim(\" A \"))", r)).Data);
        foreach (var expr in new[] { "toNumber(\" 1\")", "toBool(\"True\")", "toBool(\"1\")" }) Assert.Equal(ValueState.Invalid, (await f.Value(expr, r)).State);
        Assert.False(DslCompiler.Compile(M2Fixture.Program("field user=toBool(1);")).Success);
        Assert.False(DslCompiler.Compile(M2Fixture.Program("field user=toNumber(true);")).Success);
        var oversized = r with { OriginalUri = "https://example.test/?user=" + new string('x', 4097) };
        Assert.Equal(ValueState.BudgetExceeded, (await f.Value("first(original.query[\"user\"])", oversized)).State);
        var repeated = r with { OriginalUri = "https://example.test/?" + string.Join('&', Enumerable.Repeat("user=u", 129)) };
        Assert.Equal(ValueState.BudgetExceeded, (await f.Value("all(original.query[\"user\"])", repeated)).State);
        M2Fixture.Evidence("grouping", "E09", new { conversionsRejected = 3, valueLimit = 4096, itemLimit = 128 });
    }
    [Fact] public async Task E10SharedBodyAndOrigins()
    {
        var f = new M2Fixture(); var r = f.Add(body: """{"a":"one","b":"two"}""", contentType: "application/json");
        var p = M2Fixture.Compile(M2Fixture.Program("field a=toString(original.json(\"$.a\")); field b=toString(original.json(\"$.b\")); field user=tuple(a,b);"));
        using (var reader = new NetWorkTrafficPlugin.Infrastructure.Analysis.RequestFieldReader(r, f, f.Memory, new(default)))
        {
            var e = new FieldEvaluator(p, reader, new(default), default); var value = await e.FieldAsync("user");
            Assert.Equal(new[] { "one", "two" }, value.ItemsValue.Select(v => v.Data)); Assert.Equal(2, value.Origins!.Count);
            await e.FieldAsync("user"); Assert.Equal(1, f.BodyReads);
        }
        Assert.Equal(0, f.Memory.Reserved);
        M2Fixture.Evidence("grouping", "E10", new { reads = f.BodyReads, reservedAfter = f.Memory.Reserved });
    }
    [Fact] public async Task E11MetadataGaps()
    {
        var f = new M2Fixture(); var r = f.Add(headers: [new("X-User", "prefix")]) with { MetadataPartial = true };
        foreach (var expression in new[] { "first(original.query[\"user\"])", "first(original.headers[\"X-User\"])", "original.pathSegment(0)" })
            Assert.Equal(ValueState.Unavailable, (await f.Value(expression, r)).State);
        Assert.Equal(0, f.BodyReads);
        M2Fixture.Evidence("grouping", "E11", new { unavailable = 3, bodyReads = f.BodyReads });
    }
    [Fact] public async Task E12SensitivityAndNoBodyReads()
    {
        var f = new M2Fixture(); var r = f.Add();
        var value = await f.Value("upper(first(original.query[\"user\"]))", r);
        Assert.True(value.Sensitive); Assert.Equal("••••", value.Display()); Assert.Equal("U1", value.Display(true));
        Assert.Equal("user", Assert.Single(value.Origins!).Path); Assert.Equal(0, f.BodyReads); Assert.Equal(0, f.Memory.Reserved);
        M2Fixture.Evidence("grouping", "E12", new { masked = value.Display(), bodyReads = f.BodyReads, reservedAfter = f.Memory.Reserved });
    }
}
