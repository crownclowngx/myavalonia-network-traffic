using System.Text;
using System.Text.Json;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Dsl;
using NetWorkTrafficPlugin.Application.Rewrite;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Rewrite;

/// <summary>以手工期望校验真实编译器/规划器；Fake 仅替换正文 I/O，不能替换被测规则引擎。</summary>
public sealed class RewriteCoreTests
{
    public static string Program(string actions, string condition = "true", string fields = "") =>
        $"dsl 2; {fields} rewrite \"规则\" {{ priority 10; for host \"api.test\"; when {condition}; {actions} }}";
    private sealed class Body(byte[] bytes) : IRewriteBodyReader
    {
        public int Reads;
        public ValueTask<byte[]> ReadAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Reads++; return ValueTask.FromResult(bytes.ToArray()); }
    }
    private static async Task<(RewriteResult Result, int Reads)> Execute(string source, string uri = "https://api.test/a%2fb?x=1&x=2&untouched=%2f+", string body = "{}", params HeaderField[] headers)
    {
        var compiled = RewriteCompiler.Compile(source); Assert.True(compiled.Success, string.Join(";", compiled.Diagnostics.Select(d => d.Display)));
        var reader = new Body(Encoding.UTF8.GetBytes(body)); var memory = new ContentMemoryBudget(); var resources = new RewriteResources(memory);
        using (var fields = new RewriteFieldSource(RewriteInput.Create("POST", uri, headers), reader, resources))
        {
            var result = await RewritePlanner.PlanAsync(compiled.Program!, RewriteInput.Create("POST", uri, headers), fields);
            fields.Dispose(); Assert.Equal(0, memory.Reserved); Assert.Equal(0, resources.Active); return (result, reader.Reads);
        }
    }

    [Fact]
    public void 分析与改写入口分离并保留字段绑定()
    {
        var v2 = Program("set header[\"X-Test\"] = caller;", fields: "field caller = first(original.query[\"x\"]);");
        Assert.True(RewriteCompiler.Compile(v2).Success); Assert.False(DslCompiler.Compile(v2).Success);
        var v1 = "dsl 1; field x = original.host; view \"v\" { where true; within last(1m); group by x; timeline by startedAt; }";
        Assert.True(DslCompiler.Compile(v1).Success); Assert.False(RewriteCompiler.Compile(v1).Success);
    }

    [Theory]
    [InlineData("set header[\"Host\"] = \"bad\";", "RW006")]
    [InlineData("set header[\"Content-Length\"] = \"1\";", "RW006")]
    [InlineData("set header[\"Digest\"] = \"1\";", "RW006")]
    [InlineData("set header[\"X\"] = \"a\\r\\nb\";", "RW006")]
    [InlineData("set query[\"x\"] = 1;", "RW004")]
    [InlineData("set json(\"$.a\") = [\"a\"];", "RW004")]
    [InlineData("remove json(\"$[0]\");", "RW005")]
    [InlineData("remove json(\"$\");", "RW005")]
    [InlineData("set path \"//evil.test/a\";", "RW005")]
    [InlineData("set path \"/has space\";", "RW005")]
    [InlineData("set \"path\" \"/x\";", "RW003")]
    [InlineData("set path \"/a/%2e%2E/b\";", "RW005")]
    [InlineData("route to \"https://other.test/path\";", "RW005")]
    [InlineData("route to \"file:///tmp\";", "RW005")]
    [InlineData("set header[\"X\"] = \"a\"; remove header[\"x\"];", "RW007")]
    [InlineData("set json(\"$.a\") = null; remove json(\"$.a.b\");", "RW007")]
    public void 编译拒绝非法动作及单规则冲突(string actions, string code)
    {
        var result = RewriteCompiler.Compile(Program(actions)); Assert.False(result.Success); Assert.Equal(code, result.Diagnostics[0].Code);
        Assert.InRange(result.Diagnostics[0].Span.Start, 0, Program(actions).Length);
    }

    [Theory]
    [InlineData("statusCode == 200", "", "RW003")]
    [InlineData("x == \"a\"", "field x = y; field y = x;", "DSL007")]
    [InlineData("true", "field x = readFile(\"a\");", "DSL004")]
    [InlineData("original.headers[\"x\"] == \"a\"", "", "DSL005")]
    public void 发送前禁止响应与脚本且保持依赖和类型检查(string when, string fields, string code)
    {
        var result = RewriteCompiler.Compile(Program("remove header[\"X\"];", when, fields));
        Assert.False(result.Success); Assert.Equal(code, result.Diagnostics[0].Code);
    }

    [Fact]
    public void 中文和Emoji之后的错误仍定位原始文本()
    {
        var source = Program("set header[\"X\"] = absent;", fields: "// 中文😀\n");
        var error = Assert.Single(RewriteCompiler.Compile(source).Diagnostics);
        Assert.Equal("absent", source.Substring(error.Span.Start, error.Span.Length)); Assert.Equal(2, error.Span.Line);
    }

    [Theory]
    [InlineData("priority 10", "priority 10000", "RW002")]
    [InlineData("priority 10", "priority -1", "RW002")]
    [InlineData("dsl 2", "dsl 9", "RW001")]
    public void 版本和优先级边界(string before, string after, string code)
    { Assert.Equal(code, RewriteCompiler.Compile(Program("remove header[\"x\"];").Replace(before, after)).Diagnostics[0].Code); }

    [Fact]
    public void 编译预算和取消不产生候选()
    {
        var text = Program(string.Concat(Enumerable.Range(0, 17).Select(i => $"remove header[\"X-{i}\"];")));
        Assert.Equal("RW009", RewriteCompiler.Compile(text).Diagnostics[0].Code);
        Assert.True(RewriteCompiler.Compile(Program(string.Concat(Enumerable.Range(0, 16).Select(i => $"remove header[\"X-{i}\"];")))).Success);
        Assert.Equal("DSL009", RewriteCompiler.Compile(new string(' ', DslLimits.SourceBytes + 1)).Diagnostics[0].Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => RewriteCompiler.Compile(Program("remove header[\"x\"];"), cancel.Token));
    }

    [Fact]
    public async Task 路由一次且未修改Query的转义逐字保持()
    {
        var source = Program("route to \"https://b.test:8443\";") + " rewrite \"后续\" { priority 20; for host \"api.test\"; when true; route to \"https://c.test\"; set header[\"X\"] = \"ok\"; }";
        var (result, reads) = await Execute(source);
        Assert.False(result.Blocked); Assert.Equal("https://b.test:8443/a%2fb?x=1&x=2&untouched=%2f+", result.Plan!.Uri);
        Assert.True(result.Rules[0].RouteSelected); Assert.True(result.Rules[1].RouteShadowed);
        Assert.Contains(new HeaderField("Host", "b.test:8443"), result.Plan.Headers); Assert.Equal(0, reads);
    }

    [Fact]
    public async Task 后续条件始终读原始头而非前一动作()
    {
        var source = Program("set header[\"X\"] = \"new\";") + " rewrite \"原始\" { priority 20; for host \"api.test\"; when first(original.headers[\"X\"]) == \"old\"; set header[\"Y\"] = first(original.headers[\"X\"]); }";
        var (result, _) = await Execute(source, headers: [new("X", "old")]);
        Assert.False(result.Blocked); Assert.Contains(new HeaderField("Y", "old"), result.Plan!.Headers);
    }

    [Theory]
    [InlineData("false and toString(original.json(\"$.x\")) == \"a\"", false)]
    [InlineData("not (first(original.query[\"missing\"]) == \"a\")", true)]
    [InlineData("firstNonEmpty(first(original.query[\"missing\"]), \"normal\") == \"a\"", false)]
    public async Task 三值短路不将未知当成不匹配或读无关正文(string when, bool blocked)
    {
        var (result, reads) = await Execute(Program("remove header[\"X\"];", when));
        Assert.Equal(blocked, result.Blocked); Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData("https://API.test./x", true)]
    [InlineData("https://evil-api.test/x", false)]
    [InlineData("https://api.test.evil/x", false)]
    public async Task 主机作用域按规范化精确匹配(string uri, bool matched)
    {
        var (result, _) = await Execute(Program("set header[\"X\"] = \"a\";"), uri);
        Assert.False(result.Blocked); Assert.Equal(matched, result.Plan!.Matched);
    }

    [Fact]
    public async Task 跨规则写冲突丢弃整个候选()
    {
        var source = Program("route to \"https://b.test\"; set header[\"X\"] = \"a\";") + " rewrite \"冲突\" { priority 20; for host \"api.test\"; when true; remove header[\"x\"]; }";
        var (result, _) = await Execute(source); Assert.True(result.Blocked); Assert.Null(result.Plan); Assert.Equal("RW007", result.ErrorCode);
    }

    [Fact]
    public async Task Query按键替换并保持其他原始文本()
    {
        var (result, _) = await Execute(Program("set path \"/new%2fpath\"; set query[\"x\"] = \"中 +\"; remove query[\"missing\"];"));
        Assert.False(result.Blocked);
        Assert.Equal("https://api.test/new%2fpath?x=%E4%B8%AD%20%2B&untouched=%2f+", result.Plan!.Uri);
        Assert.Equal("NoOp", result.Plan.Differences[^1].Change);
    }

    [Fact]
    public async Task 请求头按大小写不敏感替换全部且保留其他顺序()
    {
        var (result, _) = await Execute(Program("set header[\"X\"] = \"new\"; remove header[\"None\"];"), headers: [new("A", "1"), new("x", "old"), new("X", "old2"), new("Cookie", "a=1")]);
        Assert.Equal([new HeaderField("A", "1"), new("X", "new"), new("Cookie", "a=1")], result.Plan!.Headers);
        Assert.Equal("NoOp", result.Plan.Differences[^1].Change);
    }

    [Fact]
    public async Task Connection指定的动态逐跳字段不能修改()
    {
        var (result, _) = await Execute(Program("set header[\"X-Hop\"] = \"new\";"), headers: [new("Connection", "X-Hop")]);
        Assert.True(result.Blocked); Assert.Equal("RW006", result.ErrorCode);
    }

    [Fact]
    public async Task JSON修改保留原始输入并按UTF8字节生成长度()
    {
        var source = Program("set json(\"$.a.name\") = \"中文\"; set json(\"$.list[0]\") = null; remove json(\"$.remove\");");
        var original = "{\"a\":{\"name\":\"old\"},\"list\":[1],\"remove\":true,\"n\":9007199254740993}";
        var (result, reads) = await Execute(source, body: original, headers: [new("Content-Type", "application/json"), new("Content-MD5", "old")]);
        Assert.False(result.Blocked, result.Message); Assert.Equal(1, reads);
        using var json = JsonDocument.Parse(result.Plan!.Body!);
        Assert.Equal("中文", json.RootElement.GetProperty("a").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("list")[0].ValueKind);
        Assert.False(json.RootElement.TryGetProperty("remove", out _)); Assert.Equal(9007199254740993m, json.RootElement.GetProperty("n").GetDecimal());
        Assert.Contains(new HeaderField("Content-Length", result.Plan.Body!.Length.ToString()), result.Plan.Headers);
        Assert.DoesNotContain(result.Plan.Headers, h => h.Name == "Content-MD5");
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"n\":1e1000}")]
    [InlineData("[1,]")]
    [InlineData("{\"wrong\":true}")]
    public async Task JSON歧义损坏与缺失父路径均阻断(string json)
    {
        var (result, _) = await Execute(Program("set json(\"$.a.name\") = \"new\";"), body: json, headers: [new("Content-Type", "application/json")]);
        Assert.True(result.Blocked); Assert.Equal("RW008", result.ErrorCode);
    }

    [Theory]
    [InlineData("application/json", "gzip")]
    [InlineData("multipart/form-data", "identity")]
    [InlineData("application/json; charset=gbk", "identity")]
    public async Task Body依赖拒绝不支持格式而纯头规则不读取(string contentType, string encoding)
    {
        HeaderField[] headers = [new("Content-Type", contentType), new("Content-Encoding", encoding)];
        var (failed, reads) = await Execute(Program("set json(\"$.a\") = 1;"), headers: headers);
        Assert.True(failed.Blocked); Assert.Equal(0, reads);
        var (passed, plainReads) = await Execute(Program("set header[\"X\"] = \"a\";"), headers: headers);
        Assert.False(passed.Blocked); Assert.Equal(0, plainReads); Assert.Null(passed.Plan!.Body);
    }

    [Fact]
    public async Task JSON多个字段只读取一次且表单加号按空格处理()
    {
        var source = Program("set header[\"A\"] = toString(original.json(\"$.a\")); set header[\"B\"] = toString(original.json(\"$.b\"));");
        var (result, reads) = await Execute(source, body: "{\"a\":1,\"b\":true}", headers: [new("Content-Type", "application/json")]);
        Assert.False(result.Blocked); Assert.Equal(1, reads); Assert.Null(result.Plan!.Body);
        var (form, _) = await Execute(Program("set header[\"X\"] = first(original.form[\"x\"]);"), body: "x=a+b", headers: [new("Content-Type", "application/x-www-form-urlencoded")]);
        Assert.Contains(new HeaderField("X", "a b"), form.Plan!.Headers);
    }

    [Fact]
    public void 正文槽位与共享预算拒绝超额并幂等释放()
    {
        var memory = new ContentMemoryBudget(); var resources = new RewriteResources(memory);
        var leases = Enumerable.Range(0, 4).Select(_ => resources.Acquire()).ToArray();
        Assert.Equal(503, Assert.Throws<RewriteFailure>(() => resources.Acquire()).Status);
        foreach (var lease in leases) { lease.Dispose(); lease.Dispose(); }
        Assert.Equal(0, resources.Active); Assert.Equal(0, memory.Reserved);
        var limited = new RewriteResources(new ContentMemoryBudget(1024)); Assert.Throws<RewriteFailure>(() => limited.Acquire()); Assert.Equal(0, limited.Active);
    }
}
