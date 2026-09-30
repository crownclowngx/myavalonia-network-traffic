using System.Security.Cryptography.X509Certificates;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Features.ProxyStatus;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Proxy;

/// <summary>用受控启动/清理屏障逐个观察状态，不把任务开始当作运行成功，也不把停止失败当成已释放。</summary>
public sealed class UXProxyTests
{
    private sealed class Runtime : IProxyRuntime
    {
        public readonly TaskCompletionSource StartRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource StopRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailDispose; public int Starts, Disposes;
        public async Task<ProxyRuntimeInfo> StartAsync(ProxyOptions options, CancellationToken token)
        { Starts++; await StartRelease.Task; using var root = ProxyCertificates.CreateRoot(); return new(12345, Convert.ToBase64String(root.Export(X509ContentType.Cert))); }
        public Task StopAsync() => Task.CompletedTask;
        public async ValueTask DisposeAsync() { Disposes++; await StopRelease.Task; if (FailDispose) throw new IOException("合成清理失败"); }
        public ProxyMetrics GetMetrics() => new(0, 0, 0, 0, 0, 0, []);
    }
    [Fact]
    public async Task 主动作在启停故障期间符合实际能力且多页共享一个代理()
    {
        var runtime = new Runtime(); await using var owner = new ProxySessionCoordinator(() => runtime);
        using var a = new ProxyPanelModel(owner); using var b = new ProxyPanelModel(owner);
        Assert.True(a.StartVisible); Assert.False(a.StopVisible); Assert.False(a.HasAddress);
        var starting = a.StartCommand.ExecuteAsync(null); b.Refresh();
        Assert.False(a.StartVisible); Assert.True(a.StopVisible); Assert.True(a.StopCommand.CanExecute(null)); Assert.Equal("正在启动", b.StatusText);
        runtime.StartRelease.SetResult(); await starting; b.Refresh(); Assert.Equal(1, runtime.Starts); Assert.Equal(a.Address, b.Address);
        b.Port = "8888"; Assert.Contains("12345", b.Address); Assert.Contains("下次", b.EffectiveConfiguration);
        a.Dispose(); Assert.Equal(ProxyState.Running, owner.Status.State);
        var stopping = b.StopCommand.ExecuteAsync(null); b.Refresh(); Assert.Equal("正在停止", b.StatusText); Assert.False(b.StopCommand.CanExecute(null));
        runtime.StopRelease.SetResult(); await stopping; Assert.Equal(ProxyState.Stopped, owner.Status.State); Assert.True(b.StartVisible); Assert.Equal(1, runtime.Disposes);
        ProbeEvidence.Write("ux-interactions", "proxy-states", new { starts = runtime.Starts, disposes = runtime.Disposes, states = new[] { "Stopped", "Starting", "Running", "Stopping", "Stopped" }, shared = true });
    }
    [Fact]
    public async Task 停止失败保留故障和恢复入口()
    {
        var runtime = new Runtime { FailDispose = true }; runtime.StartRelease.SetResult(); runtime.StopRelease.SetResult();
        var owner = new ProxySessionCoordinator(() => runtime); using var panel = new ProxyPanelModel(owner);
        try
        {
            await panel.StartCommand.ExecuteAsync(null); await panel.StopCommand.ExecuteAsync(null);
            Assert.Equal(ProxyState.Faulted, owner.Status.State); Assert.True(panel.StopVisible); Assert.True(panel.StartVisible); Assert.Contains("停止", panel.Message);
        }
        finally { runtime.FailDispose = false; await Assert.ThrowsAsync<IOException>(async () => await owner.DisposeAsync()); }
    }
}

