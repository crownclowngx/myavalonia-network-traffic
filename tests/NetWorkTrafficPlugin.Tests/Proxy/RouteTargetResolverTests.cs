using NetWorkTrafficPlugin.Application.Proxy;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Proxy;

public sealed class RouteTargetResolverTests
{
    [Fact]
    public void 精确域名大小写与条件映射保留原始编码和版本()
    {
        const string original = "https://SOURCE-A.test:123/a%2Fb?q=1&q=%2F";
        var mapping = new RouteMapping("source-a.test", "https://target-b.test:456", "B", 7);
        var result = RouteTargetResolver.Resolve(original, mapping, "B", 8888);
        Assert.Equal("https://target-b.test:456/a%2Fb?q=1&q=%2F", result.EffectiveUri);
        Assert.Equal(original, result.OriginalUri); Assert.Equal(7, result.Revision);
        Assert.False(RouteTargetResolver.Resolve(original, mapping, "A", 8888).Matched);
        Assert.False(RouteTargetResolver.Resolve("https://sub.source-a.test/", mapping, "B", 8888).Matched);
        Assert.Equal(result, RouteTargetResolver.Resolve(original, mapping, "B", 8888));
    }
    [Theory]
    [InlineData("ftp://host/")]
    [InlineData("https://user:pass@host/")]
    [InlineData("https://host/#fragment")]
    [InlineData("http://localhost:8888/")]
    [InlineData("http://127.0.0.1:8888/")]
    public void 非法目标与自代理在发送前拒绝(string target) =>
        Assert.Throws<ArgumentException>(() => RouteTargetResolver.Resolve(target, null, null, 8888));

    [Fact]
    public async Task 并发决策不共享目标且旧修订不受新配置影响()
    {
        var mapping = new RouteMapping("a.test", "https://b.test", "B", 3);
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => RouteTargetResolver.Resolve("https://a.test/x", mapping, i % 2 == 0 ? "B" : "A", 8080))));
        Assert.Equal(20, results.Count(r => r.Matched));
        var next = mapping with { Revision = 4, TargetOrigin = "https://c.test" };
        Assert.All(results, r => Assert.Equal(3, r.Revision));
        Assert.Equal("https://c.test/x", RouteTargetResolver.Resolve("https://a.test/x", next, "B", 8080).EffectiveUri);
    }
}
