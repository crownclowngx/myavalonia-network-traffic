using System.Text;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Requests;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Requests;

public sealed class RequestPreparationTests
{
    public static RequestDraft Draft(string address = "https://example.test/echo") => new(Guid.NewGuid(), 1, "POST", address, []);
    [Theory]
    [InlineData("file:///tmp/x")] [InlineData("https://user:pass@example.test/")] [InlineData("https://example.test/#part")]
    [InlineData("https://example.test/%zz")] [InlineData("https://example.test/a b")] [InlineData("https://example.test/\r\nx")]
    public async Task 无效地址在准备期被拒绝并归还额度(string input)
    {
        var memory = new ContentMemoryBudget();
        await Assert.ThrowsAsync<RequestFailure>(() => new RequestPreparation(memory).PrepareAsync(Draft(input), null, default));
        Assert.Equal(0, memory.Reserved);
    }
    [Theory]
    [InlineData("GET")] [InlineData("POST")] [InlineData("PUT")] [InlineData("PATCH")] [InlineData("DELETE")]
    [InlineData("HEAD")] [InlineData("OPTIONS")] [InlineData("CUSTOM")]
    public async Task 方法保持原值且不产生隐式正文(string method)
    {
        using var value = await new RequestPreparation(new()).PrepareAsync(Draft() with { Method = method }, null, default);
        Assert.Equal(method, value.Wire.Method); Assert.Null(value.Wire.Body);
    }
    [Theory]
    [InlineData("Host", "other")] [InlineData("Expect", "100-continue")] [InlineData("X-Test", "a\r\nb")]
    [InlineData("Content-Type", "text/plain")] [InlineData("Connection", "close")] [InlineData("Proxy-Authorization", "secret")]
    public async Task 非法或协议保护头不能进入冻结请求(string name, string value)
    {
        await Assert.ThrowsAsync<RequestFailure>(() => new RequestPreparation(new()).PrepareAsync(Draft() with { Headers = [new(name, value)] }, null, default));
    }
    [Fact]
    public async Task 变量按作用域和上下文单次替换且保留原Query()
    {
        var draft = Draft("${baseUrl}/v1/${segment}?a=1&a=%2B&q=${q}") with
        {
            Defaults = [new("baseUrl", "https://default.example.test"), new("segment", "default")],
            Variables = [new("segment", "中/文"), new("q", "+ %${segment}")],
            Headers = [new("X-One", "${segment}"), new("X-One", "$${literal}")]
        };
        var env = new RequestEnvironment(Guid.NewGuid(), 1, "测试", [new("baseUrl", "https://stage.example.test"), new("segment", "环境值")]);
        using var value = await new RequestPreparation(new()).PrepareAsync(draft, env, default);
        Assert.Equal("https://stage.example.test/v1/%E4%B8%AD%2F%E6%96%87?a=1&a=%2B&q=%2B%20%25%24%7Bsegment%7D", value.Wire.Address.AbsoluteUri);
        Assert.Equal(new[] { "中/文", "${literal}" }, value.Wire.Headers.Select(h => h.Value));
    }
    [Theory]
    [InlineData("{\"x\":1,\"x\":2}")] [InlineData("{\"x\":1,}")] [InlineData("\uFEFF{}")]
    [InlineData("{\"${key}\":1}")] [InlineData("{\"x\":${number}}")]
    public async Task JSON严格格式和插值位置被验证(string body)
    {
        await Assert.ThrowsAsync<RequestFailure>(() => new RequestPreparation(new()).PrepareAsync(Draft() with { BodyKind = RequestBodyKind.Json, Body = body }, null, default));
    }
    [Fact]
    public async Task JSON插值不注入结构数字不经浮点转换()
    {
        var draft = Draft() with { BodyKind = RequestBodyKind.Json, Body = "{\"value\":\"${v}\",\"n\":12345678901234567890123456789}", Variables = [new("v", "\"},\"pwn\":1")] };
        using var prepared = await new RequestPreparation(new()).PrepareAsync(draft, null, default);
        await using var stream = await prepared.Wire.Body!.OpenAsync(default); using var reader = new StreamReader(stream);
        using var json = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.Equal("\"},\"pwn\":1", json.RootElement.GetProperty("value").GetString());
        Assert.Equal("12345678901234567890123456789", json.RootElement.GetProperty("n").GetRawText());
    }
    [Fact]
    public async Task 敏感标记不可由普通覆盖降级且预览不输出值()
    {
        var draft = Draft("https://example.test/${token}?v=${token}") with { Defaults = [new("token", "old", true)], Variables = [new("token", "SENTINEL")] };
        using var prepared = await new RequestPreparation(new()).PrepareAsync(draft, null, default);
        Assert.True(prepared.Sensitive); Assert.DoesNotContain("SENTINEL", prepared.Preview); Assert.DoesNotContain("SENTINEL", prepared.ToString());
    }
    [Fact]
    public async Task 正文上限与未知规则拒绝不泄漏租约()
    {
        var memory = new ContentMemoryBudget(); var prepare = new RequestPreparation(memory);
        using (var prepared = await prepare.PrepareAsync(Draft() with { BodyKind = RequestBodyKind.Text, Body = new('a', RequestLimits.BodyBytes) }, null, default))
            Assert.Equal(RequestLimits.BodyBytes, prepared.Wire.Body!.Length);
        await Assert.ThrowsAsync<RequestFailure>(() => prepare.PrepareAsync(Draft() with { BodyKind = RequestBodyKind.Text, Body = new('a', RequestLimits.BodyBytes + 1) }, null, default));
        var failure = await Assert.ThrowsAsync<RequestFailure>(() => prepare.PrepareAsync(Draft() with { ApplyRules = true }, null, default));
        Assert.Equal("RQ007", failure.Code); Assert.Equal(0, memory.Reserved);
    }
    [Fact]
    public async Task 文件冻结后原文件变化不改变发送且租约防删除()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            var input = Path.Combine(root, "input.bin"); await File.WriteAllTextAsync(input, "original");
            using var store = new FileRequestBodyStore(Path.Combine(root, "bodies")); var id = await store.ImportAsync(input, default);
            await File.WriteAllTextAsync(input, "changed");
            using (var body = await store.AcquireAsync(id, default))
            {
                Assert.Throws<RequestFailure>(() => store.Remove(id)); await using var stream = await body.OpenAsync(default);
                using var reader = new StreamReader(stream); Assert.Equal("original", await reader.ReadToEndAsync());
            }
            store.Remove(id); Assert.Equal(0, store.Bytes);
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
}
