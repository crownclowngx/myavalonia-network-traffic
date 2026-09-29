using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Proxy;

public sealed class ProxyCoordinatorTests
{
    private sealed class Runtime : IProxyRuntime
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Delay, Fail, FailDispose;
        public int Starts, Disposes;
        public async Task<ProxyRuntimeInfo> StartAsync(ProxyOptions options, CancellationToken token)
        {
            Starts++;
            Entered.SetResult();
            if (Delay) await Release.Task; // 故意不观察取消，验证真实 I/O 迟到时协调器仍能回滚。
            if (Fail) throw new IOException("合成失败");
            return new(8123, "public");
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposes++; return FailDispose ? ValueTask.FromException(new IOException("清理失败")) : ValueTask.CompletedTask; }
        public ProxyMetrics GetMetrics() => new(0, 0, 0, 0, 0, 0, []);
    }

    [Fact]
    public async Task 并发同配置仅启动一次且重复停止和释放幂等()
    {
        var runtime = new Runtime { Delay = true };
        var owner = new ProxySessionCoordinator(() => runtime);
        var a = owner.StartAsync(new());
        var b = owner.StartAsync(new());
        await runtime.Entered.Task;
        Assert.Equal(ProxyState.Starting, owner.Status.State);
        runtime.Release.SetResult();
        Assert.Equal(await a, await b);
        Assert.Equal(1, runtime.Starts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StartAsync(new(Port: 1)));
        await Task.WhenAll(owner.StopAsync(), owner.StopAsync());
        Assert.Equal(ProxyState.Stopped, owner.Status.State);
        await owner.DisposeAsync();
        owner.Dispose();
        Assert.Equal(1, runtime.Disposes);
        Assert.Equal(ProxyState.Disposed, owner.Status.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.StartAsync(new()));
    }

    [Fact]
    public async Task 停止使迟到的启动失效并清理旧实例()
    {
        var runtime = new Runtime { Delay = true };
        await using var owner = new ProxySessionCoordinator(() => runtime);
        var start = owner.StartAsync(new());
        await runtime.Entered.Task;
        var stop = owner.StopAsync();
        Assert.Equal(ProxyState.Stopping, owner.Status.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StartAsync(new()));
        runtime.Release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        await stop;
        Assert.Equal(ProxyState.Stopped, owner.Status.State);
        Assert.Equal(1, runtime.Disposes);
    }

    [Fact]
    public async Task 页面取消只退出等待不取消共享启动()
    {
        var runtime = new Runtime { Delay = true };
        await using var owner = new ProxySessionCoordinator(() => runtime);
        using var cancelled = new CancellationTokenSource();
        var first = owner.StartAsync(new(), cancelled.Token);
        await runtime.Entered.Task;
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var second = owner.StartAsync(new());
        runtime.Release.SetResult();
        await second;
        Assert.Equal(ProxyState.Running, owner.Status.State);
        var stop = owner.StopAsync(cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        await owner.StopAsync();
        Assert.Equal(ProxyState.Stopped, owner.Status.State);
    }

    [Fact]
    public async Task 预取消无副作用且启动失败回滚后可以重试()
    {
        var runtimes = new Queue<Runtime>([new() { Fail = true }, new()]);
        var creations = 0;
        await using var owner = new ProxySessionCoordinator(() => { creations++; return runtimes.Dequeue(); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => owner.StartAsync(new(), new CancellationToken(true)));
        Assert.Equal(0, creations);
        await Assert.ThrowsAsync<IOException>(() => owner.StartAsync(new()));
        Assert.Equal(ProxyState.Faulted, owner.Status.State);
        await owner.StartAsync(new());
        Assert.Equal(ProxyState.Running, owner.Status.State);
        Assert.Equal(2, creations);
    }

    [Fact]
    public async Task 清理失败不宣称停止也不允许新运行()
    {
        var runtime = new Runtime { FailDispose = true };
        var owner = new ProxySessionCoordinator(() => runtime);
        await owner.StartAsync(new());
        await Assert.ThrowsAsync<IOException>(() => owner.StopAsync());
        Assert.Equal(ProxyState.Faulted, owner.Status.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StartAsync(new()));
        await Assert.ThrowsAsync<IOException>(() => owner.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task 两个容器不共享运行且同步释放也会完成收口()
    {
        var first = new Runtime(); var second = new Runtime();
        var a = new ProxySessionCoordinator(() => first); var b = new ProxySessionCoordinator(() => second);
        await a.StartAsync(new()); await b.StartAsync(new());
        a.Dispose();
        Assert.Equal(ProxyState.Running, b.Status.State);
        Assert.Equal(0, second.Disposes);
        b.Dispose();
        Assert.Equal(1, second.Disposes);
    }

    [Fact]
    public async Task 启动不响应取消时停止有截止且迟到结果不能复活运行()
    {
        var runtime = new Runtime { Delay = true };
        var owner = new ProxySessionCoordinator(() => runtime, TimeSpan.FromMilliseconds(100));
        var start = owner.StartAsync(new()); await runtime.Entered.Task;
        await Assert.ThrowsAsync<TimeoutException>(() => owner.StopAsync());
        Assert.Equal(ProxyState.Faulted, owner.Status.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StartAsync(new()));
        runtime.Release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(ProxyState.Faulted, owner.Status.State); Assert.Equal(1, runtime.Disposes);
        await Assert.ThrowsAsync<TimeoutException>(() => owner.DisposeAsync().AsTask());
    }
}
