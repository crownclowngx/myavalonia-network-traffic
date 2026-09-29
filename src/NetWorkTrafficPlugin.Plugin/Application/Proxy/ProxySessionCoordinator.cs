namespace NetWorkTrafficPlugin.Application.Proxy;

/// <summary>
/// 插件容器内唯一的代理所有者。短锁只保护状态和任务身份，所有外部 I/O 在后台任务执行。
/// 单个页面令牌只取消等待；真正的运行令牌由本类持有，因此最后一页关闭不会意外停止代理。
/// </summary>
public sealed class ProxySessionCoordinator(Func<IProxyRuntime> createRuntime, TimeSpan? shutdownTimeout = null) : IDisposable, IAsyncDisposable
{
    private readonly TimeSpan _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(10);
    private readonly object _sync = new();
    private ProxyStatus _status = new(ProxyState.Stopped, 0);
    private ProxyOptions? _options;
    private IProxyRuntime? _runtime;
    private CancellationTokenSource? _runCancellation;
    private Task<ProxyRuntimeInfo>? _start;
    private Task? _stop;
    private Task? _dispose;
    private bool _closed;
    private ProxyMetrics _lastMetrics = new(0, 0, 0, 0, 0, 0, []);

    public ProxyStatus Status { get { lock (_sync) return _status; } }
    public ProxyMetrics Metrics { get { lock (_sync) return _runtime?.GetMetrics() ?? _lastMetrics; } }

    /// <summary>接纳启动后由协调器持有运行；同配置的并发调用共享一次启动。</summary>
    public Task<ProxyRuntimeInfo> StartAsync(ProxyOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_status.State == ProxyState.Stopping) throw new InvalidOperationException("代理正在停止。");
            if (_status.State is ProxyState.Starting or ProxyState.Running)
            {
                if (_options != options) throw new InvalidOperationException("请先停止代理再更改运行配置。");
                return _start!.WaitAsync(cancellationToken);
            }
            if (_runtime is not null) throw new InvalidOperationException("上次运行仍有资源未清理，不能再次启动。");
            _options = options;
            _lastMetrics = new(0, 0, 0, 0, 0, 0, []);
            _runCancellation?.Dispose();
            _runCancellation = new();
            _stop = null;
            var generation = _status.Generation + 1;
            _status = new(ProxyState.Starting, generation);
            var token = _runCancellation.Token;
            _start = Task.Run(() => StartCoreAsync(options, generation, token));
            // 即使所有调用者都取消等待，也观察共享启动异常，避免后台故障无人处理。
            _ = _start.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return _start.WaitAsync(cancellationToken);
        }
    }

    private async Task<ProxyRuntimeInfo> StartCoreAsync(ProxyOptions options, long generation, CancellationToken token)
    {
        IProxyRuntime? runtime = null;
        try
        {
            token.ThrowIfCancellationRequested();
            runtime = createRuntime();
            lock (_sync) _runtime = runtime;
            var info = await runtime.StartAsync(options, token).ConfigureAwait(false);
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (_status.Generation != generation) throw new OperationCanceledException(token);
                _status = new(ProxyState.Running, generation, info);
            }
            return info;
        }
        catch (Exception exception)
        {
            var cleaned = runtime is null;
            try
            {
                if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
                cleaned = true;
            }
            finally
            {
                lock (_sync)
                {
                    if (cleaned) _runtime = null;
                    if (_status.State != ProxyState.Stopping)
                        _status = new(ProxyState.Faulted, _status.Generation, Failure: Describe(exception));
                }
            }
            throw;
        }
    }

    /// <summary>令牌仅取消等待；停止一旦接纳，即使页面关闭也继续完成资源收口。</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync) return BeginStop().WaitAsync(cancellationToken);
    }

    private Task BeginStop()
    {
        if (_stop is not null) return _stop;
        if (_status.State is ProxyState.Stopped or ProxyState.Disposed) return Task.CompletedTask;
        _status = new(ProxyState.Stopping, _status.Generation + 1, _status.Runtime);
        // 在锁外取消：第三方取消回调可能同步执行并重新查询状态。
        return _stop = Task.Run(StopCoreAsync);
    }

    private async Task StopCoreAsync()
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _runCancellation?.Cancel();
            if (_start is not null)
            {
                try { await _start.WaitAsync(_shutdownTimeout).ConfigureAwait(false); }
                catch (TimeoutException) { throw; }
                catch { /* 启动路径已执行回滚；继续检查仍被持有的资源。 */ }
            }
            IProxyRuntime? runtime;
            lock (_sync) runtime = _runtime;
            if (runtime is not null)
            {
                // 同一截止覆盖启动回滚与释放；超时保留 Faulted 和资源身份，不能报告成功或再次占用端口。
                var remaining = _shutdownTimeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("代理退出超过截止时间。");
                await runtime.DisposeAsync().AsTask().WaitAsync(remaining).ConfigureAwait(false);
                lock (_sync) _lastMetrics = runtime.GetMetrics();
            }
            lock (_sync)
            {
                _runtime = null;
                _runCancellation?.Dispose();
                _runCancellation = null;
                _status = new(_closed ? ProxyState.Disposed : ProxyState.Stopped, _status.Generation);
            }
        }
        catch (Exception exception)
        {
            lock (_sync) _status = new(ProxyState.Faulted, _status.Generation, Failure: Describe(exception));
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_dispose is not null) return new(_dispose);
            _closed = true;
            _dispose = BeginStop();
            if (_dispose.IsCompletedSuccessfully) _status = new(ProxyState.Disposed, _status.Generation);
            return new(_dispose);
        }
    }

    // Host 正常先调用 IPluginLifecycle.ShutdownAsync；同步容器释放只是兜底，后台清理不依赖 UI continuation。
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    private static string Describe(Exception e) => e is OperationCanceledException ? "操作已取消。" : $"代理操作失败（{e.GetType().Name}）。";
}
