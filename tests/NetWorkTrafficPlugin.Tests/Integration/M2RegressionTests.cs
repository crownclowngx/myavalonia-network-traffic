using System.Text.Json;
using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Features.Analysis;
using NetWorkTrafficPlugin.Features.Capture;
using NetWorkTrafficPlugin.Infrastructure.Analysis;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Infrastructure.Content;
using NetWorkTrafficPlugin.Infrastructure.Rules;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Integration;

public sealed class M2RegressionTests
{
    [Fact] public void R01PredecessorScenarioSetsAndBudgetsRemain()
    {
        // 这里只验证继承契约，旧业务是否实际通过由完整M2入口检查同轮TRX和业务证据。
        // 不能把本方法通过当作143项旧场景全部执行的替代品。
        var counts = new List<int>();
        foreach (var (file, expected) in new[] { ("m0-test-map.json", 50), ("m1-test-map.json", 53), ("ux-test-map.json", 40) })
        {
            using var map = JsonDocument.Parse(File.ReadAllText(Path.Combine(ProbeProcess.Repository, "tools", file)));
            var ids = map.RootElement.GetProperty("scenarios").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToArray();
            Assert.Equal(expected, ids.Length); Assert.Equal(expected, ids.Distinct().Count()); counts.Add(ids.Length);
        }
        using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(ProbeProcess.Repository, "tools/m1-resource-profile.json")));
        Assert.Equal(256L << 20, profile.RootElement.GetProperty("maxPrivateDeltaBytes").GetInt64());
        Assert.Equal(10000, profile.RootElement.GetProperty("maxStopMilliseconds").GetInt32());
        M2Fixture.Evidence("regression", "R01", new { counts, inherited = counts.Sum(), privateBytes = 256L << 20, stopMilliseconds = 10000 });
    }
    [Fact] public async Task R02AnalysisKeepsCaptureContentAndReadingState()
    {
        var directory = TemporaryArtifacts.Create();
        try
        {
            await using var f = new M1SessionFixture();
            var row = await f.AddAsync("原始响应正文"u8.ToArray(), uri: "https://example.test/?user=u");
            var store = new CaptureSessionStore(); store.Publish(f.Session);
            await using var archive = new FileCaptureArchiveStore();
            using var capture = new CaptureWorkspace(store, archive, new BoundedImageDecoder(store.ContentBudget));
            await capture.RefreshAsync(); capture.Selected = Assert.Single(capture.Rows); await capture.DetailTask;
            capture.Follow = false; var selected = capture.Selected.RecordId; var before = capture.BodyText;
            var resources = new AnalysisResources();
            using (var page = new AnalysisWorkspace(new(resources), new FileRuleLibraryStore(directory)))
            {
                page.Editor.Text = M2Fixture.Program(); page.SetSource(new CaptureAnalysisSource(f.Session, store.ContentBudget));
                await page.ApplyAsync(); await page.PreviewAsync(); await page.SaveAsync();
                Assert.Equal(selected, capture.Selected.RecordId); Assert.False(capture.Follow); Assert.Equal(before, capture.BodyText);
                Assert.True(f.Session.Snapshot().IsRunning);
            }
            Assert.Equal(0, resources.Memory.Reserved);
            Assert.True(await capture.OpenAnalyzedRecordAsync(f.Session, row.Id)); Assert.Equal(before, capture.BodyText);
            var unchangedBody = before == capture.BodyText;
            capture.Dispose(); Assert.Equal(0, store.ContentBudget.Reserved);
            M2Fixture.Evidence("regression", "R02", new { beforeId = selected, afterId = row.Id, unchangedBody,
                running = f.Session.Snapshot().IsRunning, analysisAfter = resources.Memory.Reserved, contentAfter = store.ContentBudget.Reserved });
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }
}
