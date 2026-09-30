using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Features.ProxyStatus;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Features.Capture;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Infrastructure.Content;
using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Rules;
using NetWorkTrafficPlugin.Infrastructure.Analysis;
using NetWorkTrafficPlugin.Infrastructure.Rules;
using NetWorkTrafficPlugin.Features.Analysis;

namespace NetWorkTrafficPlugin.Plugin;

public static class NetWorkTrafficPluginServices
{
    /// <summary>登记插件自己的业务服务；Standalone 可以复用同一个组合入口。</summary>
    public static IServiceCollection AddNetWorkTrafficPluginServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // 每插件容器一个协调器；每次运行才创建第三方代理。Tool/Document 贡献仍由 SDK 注册。
        services.TryAddSingleton<CaptureSessionStore>();
        services.TryAddSingleton(provider => new ProxySessionCoordinator(() => new TitaniumProxyRuntime(captures: provider.GetRequiredService<CaptureSessionStore>())));
        services.TryAddTransient<ProxyPanelModel>();
        services.TryAddSingleton<FileCaptureArchiveStore>();
        services.TryAddSingleton<ICaptureArchive>(p => p.GetRequiredService<FileCaptureArchiveStore>());
        services.TryAddSingleton(p => new BoundedImageDecoder(p.GetRequiredService<CaptureSessionStore>().ContentBudget));
        services.TryAddTransient<CaptureWorkspace>();
        // 规则库与额度容器共享，草稿/结果按Document独立；分析适配器只取得只读捕获端口。
        services.TryAddSingleton<AnalysisResources>();
        services.TryAddTransient<AnalysisQueryEngine>();
        services.TryAddSingleton<IRuleLibraryStore>(_ => new FileRuleLibraryStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyAvalonia", "NetworkTraffic", "Rules")));
        services.TryAddTransient<AnalysisWorkspace>();
        services.TryAddSingleton<Func<ICaptureSession, IAnalysisSource>>(provider =>
            session => new CaptureAnalysisSource(session, provider.GetRequiredService<CaptureSessionStore>().ContentBudget));
        return services;
    }
}
