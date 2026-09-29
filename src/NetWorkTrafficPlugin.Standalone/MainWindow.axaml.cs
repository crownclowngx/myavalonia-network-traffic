using Avalonia.Controls;
using NetWorkTrafficPlugin.Features.Main;
using MyAvaloniaManagement.PluginSdk;
using Microsoft.Extensions.DependencyInjection;
using NetWorkTrafficPlugin.Plugin;

namespace NetWorkTrafficPlugin.Standalone;

public sealed partial class MainWindow : Window
{
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private bool _closing, _closed;
    public MainWindow()
    {
        InitializeComponent();

        _services = new ServiceCollection().AddNetWorkTrafficPluginServices()
            .AddScoped<MainDocument>().AddSingleton<NetWorkTrafficPluginLifecycle>().BuildServiceProvider();
        _scope = _services.CreateScope();
        var document = _scope.ServiceProvider.GetRequiredService<MainDocument>();
        document.InitializeAsync(
            new NewDocumentActivation("NetWorkTrafficPlugin Standalone"),
            CancellationToken.None).GetAwaiter().GetResult();
        DataContext = document;
        Closing += CloseAfterShutdown;
    }

    /// <summary>窗口先阻止关闭，异步等待同一生产退出链；成功后才释放容器并再次关闭，不阻塞 UI。</summary>
    private async void CloseAfterShutdown(object? sender, WindowClosingEventArgs args)
    {
        if (_closed) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        try
        {
            await _services.GetRequiredService<NetWorkTrafficPluginLifecycle>().ShutdownAsync(CancellationToken.None);
            _scope.Dispose();
            await _services.DisposeAsync();
            _closed = true;
            Close();
        }
        catch (Exception exception) { Title = $"代理退出未完成（{exception.GetType().Name}）"; _closing = false; }
    }
}
