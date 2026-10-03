using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Infrastructure.Requests;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Requests;

public sealed class RequestBudgetTests
{
    [Fact]
    public void 代理调试与文件共享根预算并发预留不突破上限()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            var shared = new ManagedDiskBudget(36L << 20); // 33MiB为索引及其原子临时副本固定预留，三个根合计只剩3MiB。
            var capture = new CaptureDiskBudget(Path.Combine(root, "capture"), 10L << 20, shared: shared);
            var debug = new CaptureDiskBudget(Path.Combine(root, "debug"), 10L << 20, shared: shared);
            var results = new int[4];
            Parallel.For(0, 4, i => results[i] = (i % 2 == 0 ? capture : debug).Reserve(1048576));
            Assert.Equal(3 * 1048576, results.Sum()); Assert.Equal(36L << 20, shared.Reserved);
            Assert.Equal(0, shared.Reserve(Path.Combine(root, "assets"), 1));
            // 重开已有根不把预留当作全新空闲额度，也不再次累加相同物理占用。
            shared.Register(Path.Combine(root, "capture"), capture.Reserved); Assert.Equal(36L << 20, shared.Reserved);
            var owner = capture.Reserved > 0 ? capture : debug; owner.ReleaseReservation(1); Assert.Equal(1, shared.Reserve(Path.Combine(root, "assets"), 2));
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
    [Fact]
    public async Task 文件预算拒绝发生在复制前且失败不占用共享余额()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            var shared = new ManagedDiskBudget((33L << 20) + 2); using var bodies = new FileRequestBodyStore(root, shared);
            using var input = new MemoryStream("abc"u8.ToArray());
            await Assert.ThrowsAsync<RequestFailure>(() => bodies.ImportAsync(input, 3, default));
            Assert.Equal(0, input.Position); Assert.Equal(0, bodies.Bytes); Assert.Equal(33L << 20, shared.Reserved);
            await Assert.ThrowsAsync<RequestFailure>(() => bodies.ImportAsync(input, RequestLimits.FileBytes + 1L, default));
            Assert.Equal(0, input.Position);
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
}
