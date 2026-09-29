using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Features.Capture;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Infrastructure.Content;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Capture;

public sealed class M1WorkspaceTests
{
    /// <summary>故意不响应取消的读适配，模拟文件完成后已经排队的回调；断言靠身份，而不是靠 Cancel 恰好及时。</summary>
    private sealed class DelayedSession(ICaptureSession inner, Guid delayed) : ICaptureSession
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsArchive => inner.IsArchive;
        public IDisposable AcquireReadLease() => inner.AcquireReadLease();
        public CaptureSnapshot Snapshot() => inner.Snapshot();
        public async Task<TrafficRecord?> ReadAsync(Guid id, CancellationToken token)
        { if (id == delayed) { Entered.TrySetResult(); await Release.Task; } return await inner.ReadAsync(id, default); }
        public Task<TrafficSummary[]> QueryAsync(CaptureQuery q, CancellationToken t) => inner.QueryAsync(q, t);
        public Task<byte[]> ReadBodyAsync(BodyDescriptor b, int m, CancellationToken t) => inner.ReadBodyAsync(b, m, t);
        public StreamPreview? Stream(Guid id) => inner.Stream(id);
        public Task<CaptureLease> FreezeAsync(CancellationToken t) => inner.FreezeAsync(t);
    }

    [Fact]
    public async Task A迟到不覆盖B且关闭后的结果不复活预览()
    {
        await using var f = new M1SessionFixture();
        var a = await f.AddAsync("内容 A"u8.ToArray()); var b = await f.AddAsync("内容 B"u8.ToArray()); await f.StopAsync();
        var delayed = new DelayedSession(f.Session, a.Id); var store = new CaptureSessionStore(); store.Publish(delayed);
        await using var archive = new FileCaptureArchiveStore();
        using var model = new CaptureWorkspace(store, archive, new BoundedImageDecoder(store.ContentBudget));
        await model.RefreshAsync(); model.Selected = model.Rows.Single(r => r.RecordId == a.Id); var oldTask = model.DetailTask; await delayed.Entered.Task;
        model.Selected = model.Rows.Single(r => r.RecordId == b.Id);
        await model.DetailTask;
        Assert.Equal("内容 B", model.BodyText);
        delayed.Release.TrySetResult(); await oldTask;
        Assert.Equal("内容 B", model.BodyText); Assert.Equal(b.Id, model.Selected!.RecordId);
        model.Dispose(); await model.LoadDetailsAsync(a.Id, 1);
        Assert.Empty(model.BodyText); Assert.Equal(0, store.ContentBudget.Reserved);
    }

    [Fact]
    public async Task 双页面筛选和清空独立且每次批量不超过两百行()
    {
        await using var f = new M1SessionFixture();
        for (var i = 0; i < 501; i++) await f.AddAsync([], uri: $"https://example.test/{i}", mode: Application.Proxy.CaptureMode.MetadataOnly);
        var store = new CaptureSessionStore(); store.Publish(f.Session); await using var archive = new FileCaptureArchiveStore();
        using var a = new CaptureWorkspace(store, archive, new BoundedImageDecoder(store.ContentBudget));
        using var b = new CaptureWorkspace(store, archive, new BoundedImageDecoder(store.ContentBudget));
        await a.RefreshAsync(); await b.RefreshAsync(); Assert.Equal(200, a.Rows.Count); Assert.Equal(200, b.Rows.Count);
        await a.RefreshAsync(); Assert.Equal(400, a.Rows.Count); await a.RefreshAsync(); Assert.Equal(501, a.Rows.Count);
        a.Selected = a.Rows[50]; var id = a.Selected.RecordId;
        await a.RefreshAsync(); Assert.Equal(id, a.Selected!.RecordId);
        a.Keyword = "/500"; await a.RefreshAsync(); Assert.Empty(a.Rows);
        // 明确检验 UI 的 200ms 消抖；解析及磁盘竞争测试不依赖此时延。
        await Task.Delay(220); await a.RefreshAsync(); Assert.Single(a.Rows); Assert.Equal(200, b.Rows.Count);
        a.ClearViewCommand.Execute(null); await Task.Delay(220); await a.RefreshAsync(); Assert.Empty(a.Rows);
        Assert.Equal(501, f.Session.Snapshot().Total); Assert.Equal(200, b.Rows.Count);
        a.ClearFilterCommand.Execute(null); await Task.Delay(220); await a.RefreshAsync(); Assert.Equal(200, a.Rows.Count);
    }

    [Fact]
    public async Task 二十轮页面读取释放归还内容预算且不停止共享捕获()
    {
        await using var f = new M1SessionFixture(); await f.AddAsync("test"u8.ToArray());
        var store = new CaptureSessionStore(); store.Publish(f.Session); await using var archive = new FileCaptureArchiveStore();
        for (var i = 0; i < 20; i++)
        {
            var model = new CaptureWorkspace(store, archive, new BoundedImageDecoder(store.ContentBudget));
            await model.RefreshAsync(); model.Selected = model.Rows[0]; await model.DetailTask;
            Assert.Equal("test", model.BodyText); model.Dispose(); model.Dispose(); Assert.Equal(0, store.ContentBudget.Reserved);
            Assert.True(f.Session.Snapshot().IsRunning);
        }
        ProbeEvidence.Write("m1-lifetime", "pages", new { rounds = 20, reservedAfter = store.ContentBudget.Reserved, peak = store.ContentBudget.Peak, running = f.Session.Snapshot().IsRunning });
    }
}
