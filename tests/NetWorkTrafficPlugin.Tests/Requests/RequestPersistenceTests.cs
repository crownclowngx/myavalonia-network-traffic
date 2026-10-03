using System.Text;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Requests;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Requests;

public sealed class RequestPersistenceTests
{
    [Fact]
    public async Task 删除模板延迟回收在途文件最后租约归还才删除()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            using var bodies = new FileRequestBodyStore(Path.Combine(root, "bodies")); using var input = new MemoryStream("abc"u8.ToArray());
            var id = await bodies.ImportAsync(input, 3, default); var store = new FileRequestLibraryStore(root, bodies: bodies);
            var draft = RequestPreparationTests.Draft() with { BodyKind = RequestBodyKind.RawFile, BodyAssetId = id };
            var library = await store.CommitAsync(RequestLibrary.Empty with { Templates = [new(Guid.NewGuid(), 1, "文件", draft)] }, 0, default);
            using (var lease = await bodies.AcquireAsync(id, default))
            {
                await store.CommitAsync(library with { Templates = [] }, library.Revision, default);
                await using var stream = await lease.OpenAsync(default); Assert.Equal(3, stream.Length); Assert.Equal(3, bodies.Bytes);
            }
            Assert.Equal(0, bodies.Bytes); await Assert.ThrowsAsync<RequestFailure>(() => bodies.AcquireAsync(id, default));
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
    [Fact]
    public async Task 模板文件资产先于索引持久化并可在新容器校验恢复()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            Guid id;
            using (var bodies = new FileRequestBodyStore(Path.Combine(root, "bodies")))
            {
                using var input = new MemoryStream("完整二进制\0"u8.ToArray());
                id = await bodies.ImportAsync(input, input.Length, default);
                var store = new FileRequestLibraryStore(root, bodies: bodies);
                var draft = RequestPreparationTests.Draft() with { BodyKind = RequestBodyKind.RawFile, BodyAssetId = id };
                await store.CommitAsync(RequestLibrary.Empty with { Templates = [new(Guid.NewGuid(), 1, "文件", draft)] }, 0, default);
            }
            using var reopened = new FileRequestBodyStore(Path.Combine(root, "bodies"));
            var library = await new FileRequestLibraryStore(root, bodies: reopened).LoadAsync(default);
            Assert.Equal(id, Assert.Single(library.Templates).Draft.BodyAssetId);
            using var lease = await reopened.AcquireAsync(id, default); await using var stream = await lease.OpenAsync(default);
            using var reader = new StreamReader(stream); Assert.Equal("完整二进制\0", await reader.ReadToEndAsync());
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
    [Fact]
    public async Task 索引提交失败撤销新固定资产且旧库字节不变()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            var store = new FileRequestLibraryStore(root); var old = await store.CommitAsync(RequestLibrary.Empty, 0, default);
            var original = await File.ReadAllBytesAsync(Path.Combine(root, "requests.json"));
            using (var bodies = new FileRequestBodyStore(Path.Combine(root, "bodies")))
            {
                using var input = new MemoryStream("abc"u8.ToArray()); var id = await bodies.ImportAsync(input, 3, default);
                var failed = new FileRequestLibraryStore(root, _ => throw new IOException("synthetic"), bodies);
                var draft = RequestPreparationTests.Draft() with { BodyKind = RequestBodyKind.RawFile, BodyAssetId = id };
                await Assert.ThrowsAsync<IOException>(() => failed.CommitAsync(old with { Templates = [new(Guid.NewGuid(), 1, "文件", draft)] }, old.Revision, default));
                bodies.Remove(id); Assert.Equal(0, bodies.Bytes);
            }
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(root, "requests.json")));
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
    [Fact]
    public async Task 资产篡改长度相同仍拒绝且中途取消不留下临时文件()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            using var bodies = new FileRequestBodyStore(root); using var input = new MemoryStream("abc"u8.ToArray());
            var id = await bodies.ImportAsync(input, 3, default);
            await File.WriteAllTextAsync(Path.Combine(root, id.ToString("N") + ".body"), "xyz", new UTF8Encoding(false));
            await Assert.ThrowsAsync<IOException>(() => bodies.AcquireAsync(id, default));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bodies.ImportAsync(new MemoryStream(), 0, cancelled.Token));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
    [Theory]
    [InlineData(RequestDispatchState.NotSent)] [InlineData(RequestDispatchState.DispatchStarted)] [InlineData(RequestDispatchState.ResponseHeadersReceived)]
    public async Task 重启历史中断保留调度事实与已知状态且不重发(RequestDispatchState dispatch)
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            var store = new FileRequestHistoryStore(root); var id = Guid.NewGuid();
            var result = new RequestExecutionSnapshot(id, RequestExecutionState.Receiving, dispatch, dispatch == RequestDispatchState.ResponseHeadersReceived ? 202 : null, 7, "接收中");
            await store.WriteAsync(new(id, DateTimeOffset.UtcNow, "POST", "https://example.test/api", TrafficSource.Replay, null, null, 0, result), default);
            Assert.Equal(RequestExecutionState.Receiving, Assert.Single(await store.ReadAsync(default)).Result.State);
            var restored = Assert.Single(await new FileRequestHistoryStore(root).ReadAsync(default));
            Assert.Equal(RequestExecutionState.Interrupted, restored.Result.State); Assert.Equal(result.StatusCode, restored.Result.StatusCode);
            Assert.Equal(dispatch, restored.Result.Dispatch); Assert.Contains(dispatch == RequestDispatchState.NotSent ? "尚未发送" : "未知", restored.Result.Message);
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
    [Fact]
    public async Task 并发历史提交保留所有身份且损坏历史不被覆盖()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            var store = new FileRequestHistoryStore(root);
            RequestHistoryEntry Entry() { var id = Guid.NewGuid(); return new(id, DateTimeOffset.UtcNow, "GET", "https://example.test/", TrafficSource.Manual, null, null, 0,
                new(id, RequestExecutionState.Completed, RequestDispatchState.ResponseHeadersReceived, 200, 0, "完成")); }
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.WriteAsync(Entry(), default)));
            Assert.Equal(20, (await store.ReadAsync(default)).Count);
            var path = Path.Combine(root, "history.json"); await File.WriteAllTextAsync(path, "broken");
            await Assert.ThrowsAsync<RequestFailure>(() => store.WriteAsync(Entry(), default)); Assert.Equal("broken", await File.ReadAllTextAsync(path));
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
}
