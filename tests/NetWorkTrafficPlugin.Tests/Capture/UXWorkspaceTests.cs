using System.Text;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Features.Capture;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Infrastructure.Content;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Capture;

/// <summary>
/// V4 页面契约使用真实记录器和归档；时间与平台等待由明确的可控边界替代。
/// 不依赖睡眠猜测查询是否提交；迟到适配故意忽略取消，检验身份隔离本身。
/// </summary>
public sealed class UXWorkspaceTests
{
    public sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int ms = 201) => _now = _now.AddMilliseconds(ms);
    }
    private static CaptureWorkspace Page(CaptureSessionStore store, ICaptureArchive archive, Clock? clock = null) => new(store, archive, new(store.ContentBudget), clock);
    public sealed class Picker : IWorkspaceFilePicker
    {
        public readonly TaskCompletionSource<string?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkspaceFileKind Kind; public int Calls;
        public Task<string?> ChooseAsync(WorkspaceFileKind kind, string path, CancellationToken token) { Kind = kind; Calls++; return Result.Task; }
    }
    private sealed class DelayedQuery(ICaptureSession inner) : ICaptureSession
    {
        public bool Delay;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsArchive => inner.IsArchive;
        public IDisposable AcquireReadLease() => inner.AcquireReadLease();
        public CaptureSnapshot Snapshot() => inner.Snapshot();
        public Task<TrafficRecord?> ReadAsync(Guid id, CancellationToken t) => inner.ReadAsync(id, t);
        public async Task<TrafficSummary[]> QueryAsync(CaptureQuery q, CancellationToken t)
        { var result = await inner.QueryAsync(q, default); if (Delay) { Delay = false; Entered.TrySetResult(); await Release.Task; } return result; }
        public Task<byte[]> ReadBodyAsync(BodyDescriptor b, int m, CancellationToken t) => inner.ReadBodyAsync(b, m, t);
        public StreamPreview? Stream(Guid id) => inner.Stream(id);
        public Task<CaptureLease> FreezeAsync(CancellationToken t) => inner.FreezeAsync(t);
    }
    [Fact]
    public async Task 行摘要保留双向范围和成功响应下的不完整事实()
    {
        await using var f = new M1SessionFixture(CaptureLimits.M1() with { BodyBytes = 4 });
        await f.AddAsync("12345678"u8.ToArray()); await f.StopAsync();
        var row = Assert.Single(f.Session.Snapshot().Records);
        var record = (await f.Session.ReadAsync(row.RecordId, default))!;
        Assert.Equal(record.RequestBody.CommittedBytes + record.ResponseBody.CommittedBytes, row.StoredBytes);
        Assert.Equal(200, row.StatusCode); Assert.Contains("部分保存", row.CompactState);
        Assert.Equal("已存 4 B", row.CompactSize);
        foreach (var state in new[] { TransportState.Receiving, TransportState.Cancelled, TransportState.Failed })
        { var changed = row with { StatusCode = null, Transport = state, Capture = CaptureState.MetadataOnly, MetadataPartial = true }; Assert.NotEqual("0", changed.StatusText); Assert.Contains("仅元信息", changed.CompactState); Assert.Contains("元信息不全", changed.CompactState); }
        ProbeEvidence.Write("ux-interactions", "summary", new { row.ObservedBytes, row.StoredBytes, row.StatusCode, row.CompactState });
    }
    [Fact]
    public async Task 草稿组合消抖和非法条件保留最后结果且维度唯一()
    {
        await using var f = new M1SessionFixture(); await f.AddAsync([], uri: "https://example.test/a"); await f.AddAsync([], uri: "https://example.test/b");
        var store = new CaptureSessionStore(); store.Publish(f.Session); await using var archive = new FileCaptureArchiveStore(); var clock = new Clock();
        using var page = Page(store, archive, clock); await page.RefreshAsync();
        page.Keyword = "/a"; await page.RefreshAsync(); Assert.Equal(2, page.Rows.Count);
        clock.Advance(199); await page.RefreshAsync(); Assert.Equal(2, page.Rows.Count);
        page.IsComposing = true; clock.Advance(1); await page.RefreshAsync(); Assert.Equal(2, page.Rows.Count);
        page.IsComposing = false; await page.RefreshAsync(); Assert.Single(page.Rows); Assert.Equal("/a", page.Rows[0].Path);
        // 新条件会展示历史记录，不能算作暂停后新到达；仍保留用户暂停意图。
        page.Follow = false; page.Keyword = ""; clock.Advance(); await page.RefreshAsync(); Assert.Equal(0, page.ListReading.Pending); Assert.False(page.Follow);
        page.Keyword = "/a"; clock.Advance(); await page.RefreshAsync();
        page.FromText = "invalid"; clock.Advance(); await page.RefreshAsync(); Assert.Single(page.Rows); Assert.NotEmpty(page.FilterError); Assert.Single(page.Chips);
        page.FromText = ""; page.MethodFilter = " get,POST,get,, "; page.StatusIndex = 2; page.StatusCodeFilter = "200";
        Assert.Equal(0, page.StatusIndex); page.StatusIndex = 2; Assert.Empty(page.StatusCodeFilter);
        page.TransportIndex = 1; Assert.True(page.OnlyReceiving); page.TransportIndex = 2; Assert.False(page.OnlyReceiving); Assert.True(page.OnlyFailed);
        page.TransportIndex = 0; clock.Advance(); await page.RefreshAsync(); Assert.Single(page.Rows); Assert.Equal(3, page.Chips.Count);
        page.RemoveChipCommand.Execute("keyword"); clock.Advance(); await page.RefreshAsync(); Assert.Equal(2, page.Rows.Count);
        var query = new CaptureFilterInput("", "get,POST,get,,", 0, "", 0, "", "", 0).Build(); Assert.Equal(new[] { "GET", "POST" }, query.Methods);
        foreach (var code in new[] { "99", "600", "bad" }) Assert.Throws<ArgumentException>(() => new CaptureFilterInput("", "", 0, code, 0, "", "", 0).Build());
        Assert.Throws<ArgumentException>(() => new CaptureFilterInput("", "", 0, "", 0, "2026-10-02", "2026-10-01", 0).Build());
        ProbeEvidence.Write("ux-interactions", "draft", new { oldRows = 2, appliedRows = page.Rows.Count, debounceMs = 200, combinationPreserved = true, invalidPreserved = true });
    }
    [Fact]
    public async Task 迟到查询不提交集合标签或清掉新查询忙碌状态()
    {
        await using var f = new M1SessionFixture(); await f.AddAsync([], uri: "https://example.test/a"); await f.AddAsync([], uri: "https://example.test/b");
        var source = new DelayedQuery(f.Session); var store = new CaptureSessionStore(); store.Publish(source); await using var archive = new FileCaptureArchiveStore(); var clock = new Clock();
        using var page = Page(store, archive, clock); await page.RefreshAsync();
        source.Delay = true; page.Keyword = "/a"; clock.Advance(); var old = page.RefreshAsync(); await source.Entered.Task;
        page.Keyword = "/b"; source.Release.TrySetResult(); await old;
        Assert.Equal(2, page.Rows.Count); Assert.True(page.QueryPending); Assert.Empty(page.Chips);
        clock.Advance(); await page.RefreshAsync(); Assert.Single(page.Rows); Assert.Equal("/b", page.Rows[0].Path); Assert.False(page.QueryPending);
        Assert.Contains("/b", Assert.Single(page.Chips).Label);
        ProbeEvidence.Write("ux-interactions", "late-query", new { lateApplied = 0, page.QueryPending, path = page.Rows[0].Path });
    }
    [Fact]
    public void 跟随计数按当前保留集计算且两个阅读区域独立()
    {
        var list = new ReadingFollowState(); var events = new ReadingFollowState();
        list.Pause(10); list.Observe([13, 12, 11, 10]); Assert.Equal(3, list.Pending); Assert.True(events.Following);
        list.Observe([13, 12, 11, 10]); Assert.Equal(3, list.Pending); // 同一 ID 修订并不会改变序号。
        list.Observe([13, 12]); Assert.Equal(2, list.Pending); // 淘汰后只报告仍可见的新行。
        events.Pause(4); events.Observe([5, 4]); list.Resume(13); Assert.False(events.Following); Assert.Equal(1, events.Pending);
        events.Observe([5, 4]); Assert.Equal(1, events.Pending); // 半事件没有完整事件序号，不能虚增。
        list.Pause(13); list.Reset(13); Assert.Equal(0, list.Pending); Assert.False(list.Following);
        var same = Guid.NewGuid(); var older = Guid.NewGuid(); var newer = Guid.NewGuid(); var anchor = new ReadingAnchor(same, 5, -8);
        Assert.Equal(same, anchor.Resolve([(same, 5), (newer, 6)])); Assert.Equal(older, anchor.Resolve([(older, 4), (newer, 6)]));
        Assert.Equal(newer, anchor.Resolve([(newer, 6)])); Assert.Null(anchor.Resolve([])); Assert.Equal(-8, anchor.Offset);
        ProbeEvidence.Write("ux-reading", "independent", new { listPending = list.Pending, eventPending = events.Pending, listFollowing = list.Following, eventFollowing = events.Following, duplicateCount = 3, retainedCount = 2 });
    }
    [Fact]
    public async Task 万条真实摘要选择导航清空和恢复范围互不混淆()
    {
        await using var f = new M1SessionFixture();
        for (var i = 0; i < 10000; i++) await f.AddAsync([], uri: "https://example.test/" + (i % 2 == 0 ? "same" : "other"), mode: CaptureMode.MetadataOnly);
        var active = await f.AddAsync("data: one\n\n"u8.ToArray(), false, "text/event-stream");
        var store = new CaptureSessionStore(); store.Publish(f.Session); await using var archive = new FileCaptureArchiveStore(); var clock = new Clock();
        using var a = Page(store, archive, clock); using var b = Page(store, archive, clock);
        do { var before = a.Rows.Count; await a.RefreshAsync(); Assert.InRange(Math.Abs(a.Rows.Count - before), 0, 200); } while (a.QueryPending);
        await b.RefreshAsync(); Assert.Equal(10001, a.Rows.Count); Assert.Equal(200, b.Rows.Count);
        a.Selected = a.Rows[2]; var id = a.Selected.RecordId; await a.DetailTask; await a.RefreshAsync(); Assert.Equal(id, a.Selected!.RecordId); Assert.False(a.Follow);
        a.PreviousCommand.Execute(null); Assert.Equal(a.Rows[1].RecordId, a.Selected.RecordId); a.NextCommand.Execute(null); Assert.Equal(id, a.Selected.RecordId);
        a.Selected = a.Rows[0]; Assert.False(a.PreviousCommand.CanExecute(null)); a.Selected = a.Rows[^1]; Assert.False(a.NextCommand.CanExecute(null));
        a.Keyword = "/same"; clock.Advance(); do { await a.RefreshAsync(); } while (a.QueryPending); Assert.Equal(5000, a.Rows.Count);
        a.ClearViewCommand.Execute(null); clock.Advance(); do { await a.RefreshAsync(); } while (a.QueryPending); Assert.Empty(a.Rows); Assert.Null(a.Selected); Assert.Empty(a.Body.Text);
        a.ClearFilterCommand.Execute(null); clock.Advance(); await a.RefreshAsync(); Assert.Equal(active.Id, Assert.Single(a.Rows).RecordId);
        a.RemoveChipCommand.Execute("range"); clock.Advance(); do { await a.RefreshAsync(); } while (a.QueryPending); Assert.Equal(10001, a.Rows.Count);
        Assert.Equal(200, b.Rows.Count); Assert.Equal(10001, f.Session.Snapshot().Total);
        ProbeEvidence.Write("ux-interactions", "range", new { total = f.Session.Snapshot().Total, matched = 5000, restored = a.Rows.Count, otherPage = b.Rows.Count, activePreserved = true });
    }
    [Fact]
    public async Task 正文模式字段方向和无变化刷新保留当前阅读对象()
    {
        await using var f = new M1SessionFixture(); await f.AddAsync("{\"名称\":\"中文\",\"重复\":1,\"重复\":2}"u8.ToArray(), type: "application/json", uri: "https://example.test/api?token=secret"); await f.StopAsync();
        var store = new CaptureSessionStore(); store.Publish(f.Session); await using var archive = new FileCaptureArchiveStore();
        using var page = Page(store, archive); await page.RefreshAsync(); page.Selected = page.Rows[0]; await page.DetailTask;
        Assert.Contains("中文", page.Body.Text); Assert.DoesNotContain("\\u4", page.Body.Text); Assert.Empty(page.JsonNodes);
        var text = page.Body.Text; page.Body.Query = "中文"; Assert.True(page.Body.FindNext()); var match = page.Body.MatchStart;
        await page.RefreshAsync(); Assert.Same(text, page.Body.Text); Assert.Equal(match, page.Body.MatchStart);
        page.Body.Mode = 1; Assert.True(page.ShowJsonTree); Assert.False(page.ShowBodyText); Assert.Single(page.JsonNodes);
        page.Body.Mode = 2; Assert.Empty(page.JsonNodes); Assert.Equal(page.BodyText, page.Body.Text); Assert.DoesNotContain("\n", page.Body.Text);
        Assert.DoesNotContain("secret", page.DisplayAddress); Assert.DoesNotContain("secret", page.QueryText); Assert.DoesNotContain("synthetic=1", page.HeadersText);
        page.RevealSensitive = true; Assert.Contains("secret", page.DisplayAddress); Assert.Contains("a=1", page.CookiesText); Assert.Contains("b=2", page.HeadersText);
        page.RequestSelected = true; await page.DetailTask; Assert.Contains("synthetic=1", page.HeadersText); Assert.Empty(page.JsonNodes); Assert.Equal(2, page.Body.Mode);
        page.SplitRatio = double.NaN; Assert.Equal(.4, page.SplitRatio); page.SplitRatio = 9; Assert.Equal(.65, page.SplitRatio);
        page.Dispose(); Assert.Equal(0, store.ContentBudget.Reserved); Assert.Empty(page.Body.Text);
        ProbeEvidence.Write("ux-content-storage", "content", new { unicode = true, modes = 3, preservedMatch = match, reservedAfter = store.ContentBudget.Reserved, bodyWasNotRedacted = text.Contains("中文") });
    }
    [Fact]
    public void 查找只用有限当前文本且反馈代次和假时钟不相互覆盖()
    {
        var body = new BodyTextPresentation { Raw = "中文 Abc 中文 abc", Query = "中文" };
        Assert.True(body.FindNext()); Assert.Equal(0, body.MatchStart); Assert.True(body.FindNext()); Assert.Equal(7, body.MatchStart); Assert.True(body.FindNext()); Assert.Equal(0, body.MatchStart);
        body.Query = "ABC"; Assert.True(body.FindNext()); Assert.Equal(3, body.MatchStart); body.Query = new string('x', 257); Assert.False(body.FindNext());
        body.Query = ""; Assert.False(body.FindNext()); body.Clear(); body.Query = "中文"; Assert.False(body.FindNext()); Assert.Contains("有限预览", body.FindStatus);
        var feedback = new OperationFeedback(); var now = DateTimeOffset.UtcNow; var old = feedback.Begin(); var latest = feedback.Begin();
        feedback.Complete(latest, "new", now: now); feedback.Complete(old, "old failure", true); Assert.Equal("new", feedback.Text);
        feedback.Tick(now.AddMilliseconds(2999)); Assert.Equal("new", feedback.Text); feedback.Hold = true; feedback.Tick(now.AddSeconds(3)); Assert.Equal("new", feedback.Text);
        feedback.Hold = false; feedback.Tick(now.AddSeconds(6)); Assert.Empty(feedback.Text);
        feedback.Complete(feedback.Begin(), "error", true); feedback.Tick(now.AddDays(1)); Assert.Equal("error", feedback.Text);
        old = feedback.Begin(); latest = feedback.Begin(); feedback.Complete(latest, "new error", true); feedback.Complete(old, "old success"); Assert.Equal("new error", feedback.Text);
        feedback.Dismiss(); Assert.Empty(feedback.Text);
        ProbeEvidence.Write("ux-interactions", "feedback-find", new { queryLimit = 256, successMilliseconds = 3000, staleApplied = 0, wrapPosition = 0 });
    }
    [Fact]
    public async Task 选择器取消异常换源及关闭迟到均不执行文件操作()
    {
        await using var f = new M1SessionFixture(); await f.AddAsync("body"u8.ToArray()); var store = new CaptureSessionStore(); store.Publish(f.Session); await using var archive = new FileCaptureArchiveStore();
        using var page = Page(store, archive); await page.RefreshAsync(); page.Selected = page.Rows[0]; await page.DetailTask;
        var path = Path.Combine(f.Root, "must-not-exist"); page.ArchivePath = path; page.BeginFileOperation(WorkspaceFileKind.Save);
        var cancel = new Picker(); cancel.Result.SetResult(null); await page.BrowseFileAsync(cancel); Assert.False(Directory.Exists(path)); Assert.Equal(path, page.FileDialogPath);
        var fail = new Picker(); fail.Result.SetException(new NotSupportedException("选择器不可用，请输入路径。")); await page.BrowseFileAsync(fail); Assert.Contains("输入路径", page.Message);
        var late = new Picker(); var waiting = page.BrowseFileAsync(late); store.Publish(new DelayedQuery(f.Session)); late.Result.SetResult(path); await waiting;
        Assert.False(page.FileDialogVisible); Assert.False(Directory.Exists(path)); Assert.Contains("已改变", page.Message);
        page.BeginFileOperation(WorkspaceFileKind.Export); var closed = new Picker(); var closing = page.BrowseFileAsync(closed); var original = page.FileDialogPath;
        page.Dispose(); closed.Result.SetResult(path); await closing; Assert.Equal(original, page.FileDialogPath); Assert.False(File.Exists(path)); Assert.True(f.Session.Snapshot().IsRunning);
        ProbeEvidence.Write("ux-lifetime", "picker", new { cancelCalls = cancel.Calls, lateWrites = 0, disposedWrites = 0, reservedAfter = store.ContentBudget.Reserved, running = f.Session.Snapshot().IsRunning });
    }
    [Fact]
    public async Task 保存筛选外记录重开不联网且方向导出保持原字节()
    {
        await using var f = new M1SessionFixture(); await f.AddAsync("first"u8.ToArray(), uri: "https://example.test/a", requestBytes: "upload"u8.ToArray()); await f.AddAsync("second"u8.ToArray(), uri: "https://example.test/b"); await f.StopAsync();
        var store = new CaptureSessionStore(); store.Publish(f.Session); await using var archive = new FileCaptureArchiveStore(); var clock = new Clock();
        using var page = Page(store, archive, clock); await page.RefreshAsync(); page.Keyword = "/a"; clock.Advance(); await page.RefreshAsync(); Assert.Single(page.Rows);
        page.ArchivePath = Path.Combine(f.Root, "saved"); page.BeginFileOperation(WorkspaceFileKind.Save); Assert.Contains("全部记录", page.FileDialogHint); await page.ExecuteFileCommand.ExecuteAsync(null);
        var reopened = await archive.OpenAsync(page.ArchivePath, default); Assert.Equal(2, reopened.Snapshot().Records.Length); Assert.False(reopened.Snapshot().IsRunning);
        page.Selected = page.Rows[0]; await page.DetailTask; page.ExportPath = Path.Combine(f.Root, "response.bin"); page.BeginFileOperation(WorkspaceFileKind.Export); Assert.Contains("响应", page.FileDialogHint); await page.ExecuteFileCommand.ExecuteAsync(null);
        Assert.Equal("first", await File.ReadAllTextAsync(page.ExportPath)); var responseHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(page.ExportPath)));
        page.RequestSelected = true; await page.DetailTask; page.ExportPath = Path.Combine(f.Root, "request.bin"); page.BeginFileOperation(WorkspaceFileKind.Export); Assert.Contains("请求", page.FileDialogHint); await page.ExecuteFileCommand.ExecuteAsync(null); Assert.Equal("upload", await File.ReadAllTextAsync(page.ExportPath));
        page.BeginFileOperation(WorkspaceFileKind.Open); await page.ExecuteFileCommand.ExecuteAsync(null); clock.Advance(); await page.RefreshAsync(); Assert.True(page.IsReadOnly); Assert.False(page.RevealSensitive);
        page.LiveCommand.Execute(null); clock.Advance(); await page.RefreshAsync(); Assert.False(page.IsReadOnly);
        ProbeEvidence.Write("ux-content-storage", "save-range", new { matched = 1, saved = reopened.Snapshot().Records.Length, responseHash, requestBytes = new FileInfo(page.ExportPath).Length, responseBytes = 5, reopenedRunning = reopened.Snapshot().IsRunning });
    }
}
