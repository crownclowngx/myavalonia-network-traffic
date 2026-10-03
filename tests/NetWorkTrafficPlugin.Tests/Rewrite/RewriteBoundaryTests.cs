using System.Text;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Dsl;
using NetWorkTrafficPlugin.Application.Rewrite;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Rewrite;

public sealed class RewriteBoundaryTests
{
    private sealed class Body(byte[] bytes) : IRewriteBodyReader
    {
        public ValueTask<byte[]> ReadAsync(CancellationToken token) => ValueTask.FromResult(bytes.ToArray());
    }
    private static async Task<RewriteResult> Run(string actions, byte[] bytes, string uri = "https://api.test/", HeaderField[]? headers = null)
    {
        var program = RewriteCompiler.Compile(RewriteCoreTests.Program(actions)); Assert.True(program.Success);
        var input = RewriteInput.Create("POST", uri, headers ?? [new("Content-Type", "application/json")]);
        var memory = new ContentMemoryBudget(); var resources = new RewriteResources(memory);
        using var fields = new RewriteFieldSource(input, new Body(bytes), resources);
        var result = await RewritePlanner.PlanAsync(program.Program!, input, fields);
        if (fields.OriginalBody is { } original) Assert.Equal(bytes, original.ToArray());
        fields.Dispose(); Assert.Equal(0, resources.Active); Assert.Equal(0, memory.Reserved); return result;
    }

    [Fact]
    public void 规则和总动作预算在边界两侧准确生效()
    {
        static string Rules(int count, int actions) => "dsl 2; " + string.Join("\n", Enumerable.Range(0, count).Select(i =>
            $"rewrite \"r{i}\" {{ priority {i}; for host \"api.test\"; when true; " +
            string.Concat(Enumerable.Range(0, actions).Select(j => $"remove header[\"X-{i}-{j}\"];")) + " }"));
        Assert.True(RewriteCompiler.Compile(Rules(32, 1)).Success);
        Assert.Equal("RW009", RewriteCompiler.Compile(Rules(33, 1)).Diagnostics[0].Code);
        Assert.True(RewriteCompiler.Compile(Rules(8, 16)).Success);
        Assert.Equal("RW009", RewriteCompiler.Compile(Rules(9, 16)).Diagnostics[0].Code);
        Assert.Equal("RW002", RewriteCompiler.Compile(Rules(2, 1).Replace("priority 1", "priority 0")).Diagnostics[0].Code);
    }

    [Fact]
    public void 有序头在复制前校验字节和行数()
    {
        var line = new HeaderField("X", new string('a', RewriteLimits.HeaderBytes - 5));
        Assert.Single(RewriteInput.Create("GET", "https://api.test/", [line]).Headers);
        Assert.Equal(413, Assert.Throws<RewriteFailure>(() => RewriteInput.Create("GET", "https://api.test/", [line with { Value = line.Value + "a" }])).Status);
        Assert.Equal(256, RewriteInput.Create("GET", "https://api.test/", Enumerable.Repeat(new HeaderField("X", ""), 256)).Headers.Count);
        Assert.Throws<RewriteFailure>(() => RewriteInput.Create("GET", "https://api.test/", Enumerable.Repeat(new HeaderField("X", ""), 257)));
    }

    [Fact]
    public async Task JSON无变化保留原字节和摘要头()
    {
        var raw = Encoding.UTF8.GetBytes("{ \"a\" : 1, \"list\" : [true] }\n");
        var result = await Run("set json(\"$.a\") = 1; set json(\"$.list[0]\") = true; remove json(\"$.missing\");", raw,
            headers: [new("Content-Type", "application/json"), new("Digest", "original")]);
        Assert.False(result.Blocked); Assert.Null(result.Plan!.Body);
        Assert.All(result.Plan.Differences, d => Assert.Equal("NoOp", d.Change));
        Assert.Contains(new HeaderField("Digest", "original"), result.Plan.Headers);
    }

    [Fact]
    public async Task 输入和输出正文分别限额且拒绝原始歧义()
    {
        var raw = Encoding.UTF8.GetBytes("{\"a\":\"" + new string('a', RewriteLimits.BodyBytes - 8) + "\"}");
        Assert.Equal(RewriteLimits.BodyBytes, raw.Length);
        Assert.False((await Run("remove json(\"$.a\");", raw)).Blocked);
        Assert.Equal(413, (await Run("remove json(\"$.a\");", [.. raw, (byte)' '])).Status);
        Assert.Equal(413, (await Run("set json(\"$.other\") = \"more\";", raw)).Status);
        foreach (var invalid in new[] { new byte[] { 0xff }, Encoding.UTF8.GetBytes("\ufeff{}"), Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}") })
            Assert.Equal("RW008", (await Run("set json(\"$.x\") = 1;", invalid)).ErrorCode);
    }

    [Theory]
    [InlineData("set header[\"X\"] = toString(original.json(\"$.text\"));")]
    [InlineData("set query[\"x\"] = toString(original.json(\"$.text\"));")]
    public async Task 动态字符串不能膨胀候选越过头和URI上限(string action)
    {
        var raw = Encoding.UTF8.GetBytes("{\"text\":\"" + new string('x', 70000) + "\"}");
        var result = await Run(action, raw); Assert.True(result.Blocked); Assert.Null(result.Plan);
        Assert.Contains(result.ErrorCode, new[] { "RW005", "RW009" });
    }

    [Fact]
    public async Task 合法单值累积或百分号扩张也必须限制最终候选()
    {
        var raw = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { text = new string('中', 4000) });
        var uri = await Run("set query[\"x\"] = toString(original.json(\"$.text\"));", raw);
        Assert.Equal("RW005", uri.ErrorCode);
        // 单个值未超过 4096 字符，但六个中文头已经越过 UTF-8 64 KiB 总额。
        var actions = string.Concat(Enumerable.Range(0, 6).Select(i => $"set header[\"X-{i}\"] = toString(original.json(\"$.text\"));"));
        var headers = await Run(actions, raw); Assert.Equal(413, headers.Status); Assert.Equal("RW009", headers.ErrorCode);
    }

    [Theory]
    [InlineData("set path toString(original.json(\"$.text\"));", "//evil.test/a")]
    [InlineData("set header[\"X\"] = toString(original.json(\"$.text\"));", "a\r\nb")]
    public async Task 动态值在规划时复检不能绕过字面量校验(string action, string text)
    {
        var result = await Run(action, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { text })); Assert.True(result.Blocked);
    }

    [Fact]
    public async Task Query损坏只阻断实际需要改写Query的规则()
    {
        var result = await Run("set query[\"x\"] = \"ok\";", [], "https://api.test/?bad=%ZZ"); Assert.True(result.Blocked);
        var header = await Run("set header[\"X\"] = \"ok\";", [], "https://api.test/?bad=%ZZ"); Assert.False(header.Blocked);
        Assert.Equal("https://api.test/?bad=%ZZ", header.Plan!.Uri);
    }

    private sealed class PendingBody : IRewriteBodyReader
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<byte[]> ReadAsync(CancellationToken token) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return []; }
    }
    [Fact]
    public async Task 符合端口契约的读取取消会退出且租约由调用方释放()
    {
        var input = RewriteInput.Create("POST", "https://api.test/", [new("Content-Type", "application/json")]);
        var body = new PendingBody(); var memory = new ContentMemoryBudget(); var resources = new RewriteResources(memory);
        using var fields = new RewriteFieldSource(input, body, resources); using var cancel = new CancellationTokenSource();
        var running = RewritePlanner.PlanAsync(RewriteCompiler.Compile(RewriteCoreTests.Program("set json(\"$.a\") = 1;")).Program!, input, fields, cancel.Token);
        await body.Entered.Task; Assert.Equal(RewriteLimits.BodyReservationBytes, memory.Reserved); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(1)));
        fields.Dispose(); Assert.Equal(0, resources.Active); Assert.Equal(0, memory.Reserved);
        // 这里只证明应用端口契约。Titanium 真实读取未满足该取消条件，另有缺口探针，不能用此 Fake 冒充。
    }
}
