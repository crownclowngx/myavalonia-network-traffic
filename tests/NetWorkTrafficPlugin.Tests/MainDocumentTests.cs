using NetWorkTrafficPlugin.Constants;
using NetWorkTrafficPlugin.Features.Main;
using MyAvaloniaManagement.PluginSdk;
using Xunit;

namespace NetWorkTrafficPlugin.Tests;

public sealed class MainDocumentTests
{
    [Fact]
    public async Task 初始化时采用Host提供的标题()
    {
        var document = new MainDocument();

        await document.InitializeAsync(
            new NewDocumentActivation("测试标题"),
            CancellationToken.None);

        Assert.Equal("测试标题", document.Presentation.Title);
    }

    [Fact]
    public async Task 工作台命令只修改当前实例并定向通知状态变化()
    {
        var current = new MainDocument();
        var other = new MainDocument();
        var notifications = new List<CommandId>();
        current.CommandStateChanged += (_, args) => notifications.Add(args.CommandId);

        Assert.True(current.CanExecute(PluginIds.ApplyWorkbenchMessage));
        Assert.True(other.CanExecute(PluginIds.ApplyWorkbenchMessage));

        await current.ExecuteAsync(PluginIds.ApplyWorkbenchMessage, CancellationToken.None);

        Assert.Equal("Workbench Command 已在当前文档实例执行", current.Message);
        Assert.Equal("Hello from NetWorkTrafficPlugin", other.Message);
        Assert.False(current.CanExecute(PluginIds.ApplyWorkbenchMessage));
        Assert.True(other.CanExecute(PluginIds.ApplyWorkbenchMessage));
        Assert.Equal([PluginIds.ApplyWorkbenchMessage], notifications);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await current.ExecuteAsync(PluginIds.ApplyWorkbenchMessage, CancellationToken.None));
    }

    [Fact]
    public async Task 工作台命令拒绝未知身份并观察取消()
    {
        var document = new MainDocument();
        var unknown = new CommandId("myavalonia.plugin.network.traffic.command.main.unknown");

        Assert.False(document.CanExecute(unknown));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await document.ExecuteAsync(unknown, CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await document.ExecuteAsync(
                PluginIds.ApplyWorkbenchMessage,
                cancellation.Token));
        Assert.True(document.CanExecute(PluginIds.ApplyWorkbenchMessage));
    }

    [Fact]
    public async Task 工作台命令并发进入时只有一次能够修改实例状态()
    {
        var document = new MainDocument();

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    await document.ExecuteAsync(
                        PluginIds.ApplyWorkbenchMessage,
                        CancellationToken.None);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, succeeded => succeeded);
        Assert.False(document.CanExecute(PluginIds.ApplyWorkbenchMessage));
        Assert.Equal("Workbench Command 已在当前文档实例执行", document.Message);
    }
}
