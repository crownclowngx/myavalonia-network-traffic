using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Capture;

/// <summary>从生产会话检验身份、筛选和额度；避免只验证 DTO 的赋值行为。</summary>
public sealed class M1SessionTests
{
    [Fact]
    public async Task 正文额度耗尽仍观察流事件且预览丢块明确标缺口()
    {
        await using var f = new M1SessionFixture(CaptureLimits.M1() with { BodyBytes = 4 });
        var entry = await f.AddAsync("data: first\n\n"u8.ToArray(), false, "text/event-stream");
        var payload = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 300).Select(i => $"data: {i}\n\n")));
        f.Recorder.Observe(entry.Response, payload); f.Session.ResponseBytes(entry.Id, payload);
        await ProxyFixture.WaitUntilAsync(() => f.Session.Stream(entry.Id)?.Total == 301);
        var stream = f.Session.Stream(entry.Id)!;
        Assert.Equal(256, stream.Events.Length); Assert.Equal(45, stream.Evicted);
        var body = (await f.Session.ReadAsync(entry.Id, default))!.ResponseBody;
        Assert.Equal(4, body.CommittedBytes); Assert.Equal(CaptureState.Partial, body.Capture);
        f.Session.ResponseBytes(entry.Id, new byte[65537]); Assert.True(f.Session.Stream(entry.Id)!.Gap);
        f.Session.ResponseBytes(entry.Id, "data: after-gap\n\n"u8); Assert.Equal(301, f.Session.Stream(entry.Id)!.Total);
        ProbeEvidence.Write("m1-stream", "independent-budget", new { stream.Total, retained = stream.Events.Length, stream.Evicted, stored = body.CommittedBytes, gap = f.Session.Stream(entry.Id)!.Gap });
    }
    [Fact]
    public async Task 一万记录淘汰不合并重复请求且活动记录与完整搜索字段保留()
    {
        await using var f = new M1SessionFixture();
        var active = await f.AddAsync([], false, mode: CaptureMode.MetadataOnly);
        var uri = "https://example.test/" + new string('x', 220) + "/needle";
        for (var i = 0; i < 10010; i++) await f.AddAsync([], uri: uri, mode: CaptureMode.MetadataOnly);
        var snapshot = f.Session.Snapshot();
        Assert.Equal(10001, snapshot.Records.Length); Assert.Equal(10, snapshot.Evicted);
        Assert.Contains(snapshot.Records, r => r.RecordId == active.Id);
        Assert.Equal(snapshot.Records.Length, snapshot.Records.Select(r => r.RecordId).Distinct().Count());
        var query = await f.Session.QueryAsync(new("NEEDLE", ["get"], 2, Source: TrafficSource.Captured), default);
        Assert.Equal(10000, query.Length); Assert.DoesNotContain("needle", query[0].Path);
        Assert.Equal(query.Select(r => r.Sequence).OrderDescending(), query.Select(r => r.Sequence));
        Assert.All(query, r => Assert.Equal(snapshot.SessionId, r.SessionId));
        using var lease = await f.Session.FreezeAsync(default);
        Assert.All(lease.Records, r => Assert.Equal(r.OriginalUri, r.EffectiveUri));
        ProbeEvidence.Write("m1-query", "retention", new { snapshot.Total, retained = snapshot.Records.Length, snapshot.Evicted, matched = query.Length, activeRetained = snapshot.Records.Any(r => r.RecordId == active.Id) });
    }

    [Fact]
    public async Task 组合筛选时间半开区间及清空游标不排除正在接收()
    {
        await using var f = new M1SessionFixture();
        var first = await f.AddAsync([]); var active = await f.AddAsync([], false, mode: CaptureMode.MetadataOnly);
        var record = (await f.Session.ReadAsync(first.Id, default))!;
        Assert.Single(await f.Session.QueryAsync(new(Methods: ["get", "post"], From: record.StartedUtc, To: record.StartedUtc.AddTicks(1)), default));
        Assert.Empty(await f.Session.QueryAsync(new(To: record.StartedUtc), default));
        Assert.Empty(await f.Session.QueryAsync(new(StatusClass: 4), default));
        Assert.Empty(await f.Session.QueryAsync(new(Methods: ["POST"]), default));
        Assert.Equal(active.Id, Assert.Single(await f.Session.QueryAsync(new(AfterSequence: 100), default)).RecordId);
        Assert.Throws<ArgumentException>(() => new CaptureQuery(From: DateTimeOffset.UtcNow, To: DateTimeOffset.MinValue).Validate());
        Assert.Throws<ArgumentException>(() => new CaptureQuery(new string('a', 1025)).Validate());
        Assert.Equal(2, f.Session.Snapshot().Total);
    }

    [Fact]
    public async Task 读取租约重复释放安全且磁盘删除失败不冒充回收()
    {
        await using var f = new M1SessionFixture(); await f.AddAsync("body"u8.ToArray()); await f.StopAsync();
        var lease = f.Session.AcquireReadLease();
        Assert.Throws<InvalidOperationException>(f.Session.DeleteStoppedSession);
        lease.Dispose(); lease.Dispose();
        var marker = Path.Combine(f.Session.DirectoryPath, ".network-traffic-session");
        using (File.Open(marker, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(f.Session.DeleteStoppedSession);
        Assert.True(Directory.Exists(f.Session.DirectoryPath));
        // 删除失败可能已删除部分普通文件，但不能对外宣布目录已回收；最后一次重试仍需所有权标记。
        f.Session.DeleteStoppedSession(); Assert.False(Directory.Exists(f.Session.DirectoryPath));
    }

    [Fact]
    public async Task 元信息到顶仍继续观察正文并披露保存范围()
    {
        await using var f = new M1SessionFixture(CaptureLimits.M1() with { DiskBytes = 1 });
        var entry = await f.AddAsync([], mode: CaptureMode.MetadataOnly);
        var record = (await f.Session.ReadAsync(entry.Id, default))!;
        Assert.True(record.MetadataPartial); Assert.Equal(CaptureState.MetadataOnly, record.ResponseBody.Capture);
        await f.StopAsync();
        Assert.Equal(1, f.Session.Snapshot().Total);
    }

    [Fact]
    public async Task 全局内容额度并发竞争及归还不出现负数或超额()
    {
        var budget = new ContentMemoryBudget();
        var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(() => budget.TryReserve(1 << 20))).ToArray();
        var leases = await Task.WhenAll(tasks);
        Assert.Equal(32, leases.Count(l => l is not null)); Assert.Equal(32L << 20, budget.Reserved);
        foreach (var lease in leases) { lease?.Dispose(); lease?.Dispose(); }
        Assert.Equal(0, budget.Reserved); Assert.Equal(32L << 20, budget.Peak);
    }

    [Fact]
    public async Task 根目录历史占用参与并发预留且不能将导出藏入工作目录()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            var session = Path.Combine(root, "old"); Directory.CreateDirectory(session);
            await File.WriteAllBytesAsync(Path.Combine(session, ".network-traffic-session"), []);
            await File.WriteAllBytesAsync(Path.Combine(session, "partial.body"), new byte[512]);
            var budget = new CaptureDiskBudget(root, 1024, 1024);
            var amounts = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => budget.Reserve(64))));
            Assert.Equal(512, amounts.Sum()); Assert.Equal(512, budget.Reserved);
            budget.ReleaseReservation(64); Assert.Equal(64, budget.Reserve(65));
            await File.WriteAllTextAsync(Path.Combine(root, ".network-traffic-root"), "V3/M1");
            await using var f = new M1SessionFixture(); await f.AddAsync("body"u8.ToArray());
            await using var store = new FileCaptureArchiveStore();
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(f.Session, Path.Combine(root, "archive"), default));
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
}
