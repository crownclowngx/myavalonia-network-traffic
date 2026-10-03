using System.Text.Json;
using NetWorkTrafficPlugin.Application.Rewrite;
using NetWorkTrafficPlugin.Infrastructure.Rewrite;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Rewrite;

public sealed class RewriteLibraryTests
{
    private static RewriteLibrary Candidate(string value = "one")
    {
        var revision = RewriteRevision.Create(RewriteCoreTests.Program($"set header[\"X-Test\"] = \"{value}\";"));
        return RewriteLibrary.Empty.Save(revision, new(Guid.NewGuid(), "测试改写", revision.RevisionId));
    }

    [Fact]
    public async Task 修订不可变且分析库不被覆盖()
    {
        var directory = TemporaryArtifacts.Create();
        try
        {
            Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory, "rules.json"), "旧分析库不参与本次写入");
            var store = new FileRewriteLibraryStore(directory);
            var initial = await store.CommitAsync(Candidate(), 0, default);
            var loaded = await store.LoadAsync(default); Assert.Equal(initial.Revisions[0], loaded.Revisions[0]);
            var revision = RewriteRevision.Create(RewriteCoreTests.Program("remove header[\"X-Test\"];"), initial.Revisions[0].RevisionId);
            var next = initial.Save(revision, initial.Configurations[0] with { RevisionId = revision.RevisionId });
            var committed = await store.CommitAsync(next, 1, default); Assert.Equal(2, committed.Revisions.Length);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(committed with { Revisions = [revision with { ParentRevisionId = null }] }, 2, default));
            var changed = committed.Revisions[0] with { CreatedUtc = DateTimeOffset.UtcNow.AddDays(1) };
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(committed with { Revisions = [changed, revision] }, 2, default));
            Assert.Equal("旧分析库不参与本次写入", await File.ReadAllTextAsync(Path.Combine(directory, "rules.json")));
            Assert.DoesNotContain("Active", await File.ReadAllTextAsync(Path.Combine(directory, "rewrites.json")));
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }

    [Theory]
    [InlineData("written")]
    [InlineData("before-commit")]
    public async Task 提交点前故障和取消保留原文件(string stage)
    {
        var directory = TemporaryArtifacts.Create();
        try
        {
            var initial = await new FileRewriteLibraryStore(directory).CommitAsync(Candidate(), 0, default);
            var before = await File.ReadAllBytesAsync(Path.Combine(directory, "rewrites.json"));
            var next = initial.Save(Candidate("two").Revisions[0], initial.Configurations[0]);
            var failing = new FileRewriteLibraryStore(directory, s => { if (s == stage) throw new IOException("合成磁盘失败"); });
            await Assert.ThrowsAsync<IOException>(() => failing.CommitAsync(next, 1, default));
            using var cancel = new CancellationTokenSource();
            var cancelling = new FileRewriteLibraryStore(directory, s => { if (s == stage) cancel.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.CommitAsync(next, 1, cancel.Token));
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(directory, "rewrites.json")));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            using var late = new CancellationTokenSource();
            var committed = await new FileRewriteLibraryStore(directory, s => { if (s == "committed") late.Cancel(); }).CommitAsync(next, 1, late.Token);
            Assert.Equal(2, committed.Generation); Assert.Equal(2, (await new FileRewriteLibraryStore(directory).LoadAsync(default)).Generation);
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }

    [Fact]
    public async Task 多宿主代次和独占锁冲突不覆盖()
    {
        var directory = TemporaryArtifacts.Create();
        try
        {
            var a = new FileRewriteLibraryStore(directory); var b = new FileRewriteLibraryStore(directory);
            var first = await a.CommitAsync(Candidate(), 0, default);
            await Assert.ThrowsAsync<RewriteGenerationConflictException>(() => b.CommitAsync(Candidate(), 0, default));
            using (var held = new FileStream(Path.Combine(directory, "rewrites.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await Assert.ThrowsAsync<RewriteGenerationConflictException>(() => b.CommitAsync(first, 1, default));
            Assert.Equal(1, (await b.LoadAsync(default)).Generation);
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }

    [Fact]
    public async Task 损坏版本摘要引用及超限都保留原文件()
    {
        var directory = TemporaryArtifacts.Create();
        try
        {
            Directory.CreateDirectory(directory); var store = new FileRewriteLibraryStore(directory);
            var initial = Candidate(); var revision = initial.Revisions[0];
            var variants = new[]
            {
                initial with { SchemaVersion = 2 }, initial with { Revisions = [] },
                initial with { Revisions = [revision with { SourceHash = "bad" }] },
                initial with { Revisions = [revision with { LanguageVersion = 1 }] },
                initial with { Revisions = [revision with { ParentRevisionId = Guid.NewGuid() }] },
                initial with { Revisions = [revision with { ParentRevisionId = revision.RevisionId }] },
                initial with { Revisions = [revision, revision] }, initial with { Configurations = null! },
                initial with { Revisions = [null!] }
            };
            var path = Path.Combine(directory, "rewrites.json");
            foreach (var json in variants.Select(v => JsonSerializer.Serialize(v)).Append("{broken"))
            {
                await File.WriteAllTextAsync(path, json); await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
                Assert.Equal(json, await File.ReadAllTextAsync(path));
            }
            await File.WriteAllBytesAsync(path, new byte[RewriteLibraryValidation.MaximumBytes + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
            Assert.Equal(RewriteLibraryValidation.MaximumBytes + 1, new FileInfo(path).Length);
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }

    [Fact]
    public void 库容量边界和父修订环有明确诊断()
    {
        var initial = Candidate(); var revision = initial.Revisions[0];
        var full = initial with
        {
            Revisions = Enumerable.Range(0, 200).Select(_ => RewriteRevision.Create(revision.SourceText)).ToArray(),
            Configurations = []
        };
        full = full with { Configurations = Enumerable.Range(0, 50).Select(i => new RewriteConfiguration(Guid.NewGuid(), "配置" + i, full.Revisions[0].RevisionId)).ToArray() };
        RewriteLibraryValidation.Validate(full, default);
        Assert.Throws<InvalidDataException>(() => RewriteLibraryValidation.Validate(full with { Revisions = [.. full.Revisions, revision] }, default));
        Assert.Throws<InvalidDataException>(() => RewriteLibraryValidation.Validate(full with { Configurations = [.. full.Configurations, initial.Configurations[0]] }, default));
        var parent = RewriteRevision.Create(revision.SourceText, revision.RevisionId);
        Assert.Throws<InvalidDataException>(() => RewriteLibraryValidation.Validate(initial with { Revisions = [revision with { ParentRevisionId = parent.RevisionId }, parent] }, default));
    }

    [Fact]
    public async Task 保存启用停用与在途快照互相独立()
    {
        var directory = TemporaryArtifacts.Create();
        try
        {
            var store = new FileRewriteLibraryStore(directory); var saved = await store.CommitAsync(Candidate(), 0, default);
            var coordinator = new RewriteCoordinator(store); Assert.False(coordinator.Current.Enabled);
            var started = coordinator.StartRun(1, RewriteRunMode.RewriteHttp11);
            var first = await coordinator.ActivateAsync(saved.Revisions[0].RevisionId, 1, started.ActivationGeneration, default);
            ActiveRewriteSnapshot? registered = null;
            var admitted = coordinator.Admit(1, snapshot => registered = snapshot); Assert.Same(first, registered); Assert.Same(first, admitted);
            var secondRevision = Candidate("two").Revisions[0];
            await store.CommitAsync(saved.Save(secondRevision, saved.Configurations[0] with { RevisionId = secondRevision.RevisionId }), 1, default);
            Assert.Same(first, coordinator.Current); // 保存不热启用。
            var second = await coordinator.ActivateAsync(secondRevision.RevisionId, 1, first.ActivationGeneration, default);
            Assert.Same(first, admitted); Assert.NotSame(first, second);
            var disabled = coordinator.Disable(1, second.ActivationGeneration); Assert.False(disabled.Enabled);
            Assert.Equal(RewriteRunMode.RewriteHttp11, disabled.Mode); Assert.True(admitted.Enabled);
            coordinator.StopRun(1); Assert.Throws<RewriteGenerationConflictException>(() => coordinator.Admit(1, _ => { }));
            Assert.False(coordinator.StartRun(2, RewriteRunMode.RewriteHttp11).Enabled);
            Assert.False(new RewriteCoordinator(store).Current.Enabled);
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }

    [Fact]
    public async Task 普通模式旧代次未保存及取消都不能启用()
    {
        var directory = TemporaryArtifacts.Create();
        try
        {
            var store = new FileRewriteLibraryStore(directory); var saved = await store.CommitAsync(Candidate(), 0, default);
            var coordinator = new RewriteCoordinator(store); var capture = coordinator.StartRun(1, RewriteRunMode.Capture);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ActivateAsync(saved.Revisions[0].RevisionId, 1, capture.ActivationGeneration, default));
            coordinator.StopRun(1);
            Assert.Throws<InvalidOperationException>(() => coordinator.StartRun(2, RewriteRunMode.RewriteHttp11, true));
            var started = coordinator.StartRun(2, RewriteRunMode.RewriteHttp11);
            await Assert.ThrowsAsync<RewriteGenerationConflictException>(() => coordinator.ActivateAsync(saved.Revisions[0].RevisionId, 1, started.ActivationGeneration, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ActivateAsync(Guid.NewGuid(), 2, started.ActivationGeneration, default));
            var competing = Enumerable.Range(0, 2).Select(_ => coordinator.ActivateAsync(saved.Revisions[0].RevisionId, 2, started.ActivationGeneration, default)).ToArray();
            var failures = await Task.WhenAll(competing.Select(t => Record.ExceptionAsync(() => t)));
            Assert.Single(failures, e => e is null); Assert.Single(failures, e => e is RewriteGenerationConflictException);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ActivateAsync(saved.Revisions[0].RevisionId, 2, coordinator.Current.ActivationGeneration, cancel.Token));
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }

    private sealed class DelayedStore(RewriteLibrary library) : IRewriteLibraryStore
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RewriteLibrary> LoadAsync(CancellationToken token) { Entered.TrySetResult(); await Continue.Task; return library; }
        public Task<RewriteLibrary> CommitAsync(RewriteLibrary candidate, long expectedGeneration, CancellationToken token) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 迟到加载不能跨过停止或页面取消(bool cancelPage)
    {
        var library = Candidate(); var store = new DelayedStore(library); var coordinator = new RewriteCoordinator(store);
        var started = coordinator.StartRun(1, RewriteRunMode.RewriteHttp11); using var cancellation = new CancellationTokenSource();
        var activating = coordinator.ActivateAsync(library.Revisions[0].RevisionId, 1, started.ActivationGeneration, cancellation.Token);
        await store.Entered.Task;
        if (cancelPage) cancellation.Cancel(); else coordinator.StopRun(1);
        store.Continue.TrySetResult();
        var failure = await Record.ExceptionAsync(() => activating);
        Assert.True(cancelPage ? failure is OperationCanceledException : failure is RewriteGenerationConflictException);
        Assert.False(coordinator.Current.Enabled);
    }
}
