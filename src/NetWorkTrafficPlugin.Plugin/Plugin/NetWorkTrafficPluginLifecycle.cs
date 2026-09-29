using MyAvaloniaManagement.PluginSdk;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Plugin;

/// <summary>只使用公开 SDK。Host 退出先等待代理收口，随后容器同步释放成为幂等兜底。</summary>
public sealed class NetWorkTrafficPluginLifecycle(ProxySessionCoordinator coordinator) : IPluginLifecycle
{
    public Task InitializeAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task ShutdownAsync(CancellationToken cancellationToken) => coordinator.DisposeAsync().AsTask();
}
