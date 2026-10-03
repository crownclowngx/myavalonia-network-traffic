using MyAvaloniaManagement.PluginSdk;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Plugin;

/// <summary>只使用公开 SDK。Host 退出先等待代理收口，随后容器同步释放成为幂等兜底。</summary>
public sealed class NetWorkTrafficPluginLifecycle(ProxySessionCoordinator coordinator, NetWorkTrafficPlugin.Infrastructure.Capture.FileCaptureArchiveStore? archive = null,
    NetWorkTrafficPlugin.Application.Requests.RequestExecutionCoordinator? requests = null,
    NetWorkTrafficPlugin.Infrastructure.Requests.DebugCaptureStore? debug = null) : IPluginLifecycle
{
    public Task InitializeAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        // 先结束真实发送再关闭记录器，避免尾部回调写入已释放的会话；代理与主动执行可同时收口。
        await Task.WhenAll(coordinator.DisposeAsync().AsTask(), requests?.DisposeAsync().AsTask() ?? Task.CompletedTask).ConfigureAwait(false);
        if (archive is not null) await archive.DisposeAsync().ConfigureAwait(false);
        if (debug is not null) await debug.DisposeAsync().ConfigureAwait(false);
    }
}
