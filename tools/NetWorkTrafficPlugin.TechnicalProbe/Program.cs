using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Plugin;
using NetWorkTrafficPlugin.Application.Capture;

// API 核对只读取本次构建实际加载的程序集，不根据在线最新版文档猜测固定包的能力。
if (args.Contains("--api", StringComparer.Ordinal))
{
    foreach (var type in new[] { typeof(Titanium.Web.Proxy.ProxyServer), typeof(Titanium.Web.Proxy.EventArguments.BeforeBodyWriteEventArgs),
        typeof(Titanium.Web.Proxy.EventArguments.SessionEventArgs), typeof(Titanium.Web.Proxy.EventArguments.SessionEventArgsBase),
        typeof(Titanium.Web.Proxy.EventArguments.SessionEventArgs).GetProperty("HttpClient")!.PropertyType })
    {
        Console.WriteLine(type.FullName);
        foreach (var member in type.GetMembers())
            Console.WriteLine(member.ToString());
    }
    return 0;
}

// 独立资源测量宿主只承载生产服务，不实现另一套代理。配置经标准输入传入，避免命令行携带证书材料。
await using var services = new ServiceCollection().AddNetWorkTrafficPluginServices().BuildServiceProvider();
var coordinator = services.GetRequiredService<ProxySessionCoordinator>();
try
{
    var input = await Console.In.ReadLineAsync() ?? throw new InvalidOperationException("缺少运行配置。");
    var options = JsonSerializer.Deserialize<ProxyOptions>(input) ?? throw new InvalidOperationException("运行配置无效。");
    var started = await coordinator.StartAsync(options);
    Console.WriteLine(JsonSerializer.Serialize(started));
    while (await Console.In.ReadLineAsync() is { } command)
    {
        if (command == "snapshot") Console.WriteLine(JsonSerializer.Serialize(new { Metrics = coordinator.Metrics, ManagedBytes = GC.GetTotalMemory(false) }));
        else if (command == "capture")
        {
            var session = services.GetRequiredService<CaptureSessionStore>().Current ?? throw new InvalidOperationException("不是 M1 捕获运行。");
            using var lease = await session.FreezeAsync(default);
            Console.WriteLine(JsonSerializer.Serialize(lease.Records));
        }
        else if (command == "stop")
        {
            await coordinator.StopAsync();
            Console.WriteLine(JsonSerializer.Serialize(new { Metrics = coordinator.Metrics, Handles = System.Diagnostics.Process.GetCurrentProcess().HandleCount }));
            return 0;
        }
        else throw new ArgumentException("未知探针命令。");
    }
    // 父进程结束或控制通道断开同样必须收口，不留下孤立监听器。
    await coordinator.StopAsync();
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"探针失败（{exception.GetType().Name}）。");
    return 1;
}
