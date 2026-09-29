using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Features.Main;
using NetWorkTrafficPlugin.Features.ProxyStatus;
using NetWorkTrafficPlugin.Plugin;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Integration;

public sealed class CompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 多页面和工具共享运行且两种容器释放路径回收监听(bool asynchronous)
    {
        var services = new ServiceCollection().AddNetWorkTrafficPluginServices().AddTransient<MainDocument>()
            .AddTransient<ProxyStatusTool>().AddSingleton<NetWorkTrafficPluginLifecycle>().BuildServiceProvider();
        var owner = services.GetRequiredService<ProxySessionCoordinator>();
        Assert.Equal(ProxyState.Stopped, owner.Status.State);
        using var first = services.GetRequiredService<MainDocument>();
        using var second = services.GetRequiredService<MainDocument>();
        var directory = TemporaryArtifacts.Create();
        first.Proxy.RecordDirectory = directory;
        Assert.NotSame(first.Proxy, second.Proxy);
        await first.Proxy.StartCommand.ExecuteAsync(null);
        var port = owner.Status.Runtime!.Port;
        first.Dispose(); second.Dispose();
        Assert.Equal(ProxyState.Running, owner.Status.State);
        using (var tool = services.GetRequiredService<ProxyStatusTool>())
        {
            tool.Panel.Refresh();
            Assert.Equal($"127.0.0.1:{port}", tool.Panel.Address);
        }
        Assert.Equal(ProxyState.Running, owner.Status.State);
        using var reopened = services.GetRequiredService<ProxyStatusTool>();
        await reopened.Panel.StopCommand.ExecuteAsync(null);
        Assert.Equal(ProxyState.Stopped, owner.Status.State);
        await owner.StartAsync(new(Port: port));
        if (asynchronous)
        {
            // 与 Standalone/公开 SDK 退出顺序相同，已取消的 Host 等待令牌也不能跳过清理。
            await services.GetRequiredService<NetWorkTrafficPluginLifecycle>().ShutdownAsync(new(true));
            await services.DisposeAsync();
        }
        else services.Dispose();
        Assert.Equal(ProxyState.Disposed, owner.Status.State);
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        TemporaryArtifacts.Delete(directory);
    }

    [Fact]
    public async Task 端口冲突回滚且不会停止别人的监听()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await using var services = new ServiceCollection().AddNetWorkTrafficPluginServices().BuildServiceProvider();
        var owner = services.GetRequiredService<ProxySessionCoordinator>();
        await Assert.ThrowsAnyAsync<Exception>(() => owner.StartAsync(new(Port: port)));
        Assert.Equal(ProxyState.Faulted, owner.Status.State);
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port);
        await owner.StartAsync(new());
        Assert.Equal(ProxyState.Running, owner.Status.State);
    }

    [Fact]
    public void 无效端口只更新页面错误且关闭页面不再刷新()
    {
        using var owner = new ProxySessionCoordinator(() => throw new InvalidOperationException("不应创建运行"));
        using var panel = new ProxyPanelModel(owner) { Port = "-1" };
        panel.StartCommand.Execute(null);
        Assert.Contains("0～65535", panel.Message);
        panel.Dispose();
        var text = panel.StatusText;
        owner.Dispose(); panel.Refresh();
        Assert.Equal(text, panel.StatusText);
    }
}
