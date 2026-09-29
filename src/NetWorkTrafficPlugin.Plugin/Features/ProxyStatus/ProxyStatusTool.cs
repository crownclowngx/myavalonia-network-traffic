using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Features.ProxyStatus;

/// <summary>Tool 隐藏不改变运行寿命；它与 Document 共享唯一协调器，保留独立显示状态。</summary>
public sealed class ProxyStatusTool(ProxySessionCoordinator coordinator) : IDisposable
{
    public ProxyPanelModel Panel { get; } = new(coordinator);
    public void Dispose() => Panel.Dispose();
}
