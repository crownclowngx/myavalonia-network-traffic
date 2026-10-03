using System.Text;
using System.Text.Json;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Rewrite;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Rewrite;

public sealed class RewriteExamplesTests
{
    private static string Example(string name) => File.ReadAllText(Path.Combine(ProbeProcess.Repository, "docs/examples/m3", name));
    [Fact]
    public void 固定样例和Profile与生产常量一致()
    {
        Assert.True(RewriteCompiler.Compile(Example("route-and-json.dsl")).Success);
        Assert.True(RewriteCompiler.Compile(Example("original-only.dsl")).Success);
        Assert.Equal("RW007", Assert.Single(RewriteCompiler.Compile(Example("invalid-conflict.dsl")).Diagnostics).Code);
        using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(ProbeProcess.Repository, "tools/m3-profile.json")));
        Assert.Equal("M3-foundation", profile.RootElement.GetProperty("scope").GetString());
        Assert.False(profile.RootElement.GetProperty("runtimeIntegrationEnabled").GetBoolean());
        var budgets = profile.RootElement.GetProperty("budgets");
        var expected = new Dictionary<string, long>
        {
            ["rules"] = RewriteLimits.Rules, ["actionsPerRule"] = RewriteLimits.ActionsPerRule, ["actions"] = RewriteLimits.Actions,
            ["headerBytes"] = RewriteLimits.HeaderBytes, ["headerLines"] = RewriteLimits.HeaderLines,
            ["bodyBytes"] = RewriteLimits.BodyBytes, ["bodySlots"] = RewriteLimits.BodySlots, ["bodyReservationBytes"] = RewriteLimits.BodyReservationBytes,
            ["bodyMilliseconds"] = RewriteLimits.BodyMilliseconds, ["metadataMilliseconds"] = RewriteLimits.MetadataMilliseconds,
            ["operations"] = RewriteLimits.Operations, ["uriCharacters"] = RewriteLimits.UriCharacters, ["traceBytes"] = RewriteLimits.TraceBytes,
            ["libraryBytes"] = RewriteLibraryValidation.MaximumBytes, ["revisions"] = RewriteLibraryValidation.MaximumRevisions,
            ["configurations"] = RewriteLibraryValidation.MaximumConfigurations
        };
        Assert.Equal(expected.Count, budgets.EnumerateObject().Count());
        foreach (var item in expected) Assert.Equal(item.Value, budgets.GetProperty(item.Key).GetInt64());
    }

    private sealed class Body : IRewriteBodyReader
    {
        public ValueTask<byte[]> ReadAsync(CancellationToken token) => ValueTask.FromResult(Encoding.UTF8.GetBytes("{\"context\":{\"environment\":\"production\"},\"debugToken\":\"synthetic\"}"));
    }
    [Fact]
    public async Task 专项设计样例经实际规划得到预期候选且不拥有网络端口()
    {
        var program = RewriteCompiler.Compile(Example("route-and-json.dsl")).Program!;
        var input = RewriteInput.Create("POST", "https://api.example.test/v1/orders?legacy=old&signed=%2f+",
            [new("X-Caller", "tester-a"), new("Content-Type", "application/json")]);
        using var fields = new RewriteFieldSource(input, new Body(), new(new ContentMemoryBudget()));
        var result = await RewritePlanner.PlanAsync(program, input, fields);
        Assert.False(result.Blocked, result.Message);
        Assert.Equal("https://stage.example.test:8443/v2/orders?signed=%2f+&debug=1", result.Plan!.Uri);
        Assert.Contains(new HeaderField("Host", "stage.example.test:8443"), result.Plan.Headers);
        using var body = JsonDocument.Parse(result.Plan.Body!);
        Assert.Equal("stage", body.RootElement.GetProperty("context").GetProperty("environment").GetString());
        Assert.False(body.RootElement.TryGetProperty("debugToken", out _));
    }
}
