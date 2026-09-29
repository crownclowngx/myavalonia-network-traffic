using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Features.ProxyStatus;

namespace NetWorkTrafficPlugin.Plugin;

public static class NetWorkTrafficPluginServices
{
    /// <summary>登记插件自己的业务服务；Standalone 可以复用同一个组合入口。</summary>
    public static IServiceCollection AddNetWorkTrafficPluginServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // 每插件容器一个协调器；每次运行才创建第三方代理。Tool/Document 贡献仍由 SDK 注册。
        services.TryAddSingleton(_ => new ProxySessionCoordinator(() => new TitaniumProxyRuntime()));
        services.TryAddTransient<ProxyPanelModel>();
        return services;
    }
}
