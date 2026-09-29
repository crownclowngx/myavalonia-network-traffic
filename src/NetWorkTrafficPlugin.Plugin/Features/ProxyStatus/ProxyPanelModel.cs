using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Features.ProxyStatus;

/// <summary>
/// 页面只持有显示投影和自己的等待令牌。刷新由可见 View 驱动，关闭视图停止刷新，
/// Dispose 取消页面等待但不停止共享代理。网络/证书/文件逻辑始终位于应用与适配层。
/// </summary>
public sealed partial class ProxyPanelModel : ObservableObject, IDisposable
{
    private readonly ProxySessionCoordinator _owner;
    private readonly CancellationTokenSource _closing = new();
    private bool _disposed;
    [ObservableProperty] private string _port = "0";
    [ObservableProperty] private string _statusText = "已停止";
    [ObservableProperty] private string _address = "尚未监听";
    [ObservableProperty] private string _message = "手动启动显式代理；不修改系统代理，也不安装系统证书。";
    [ObservableProperty] private string _metricsText = "尚无捕获";
    [ObservableProperty] private string _recentText = "尚无请求";
    [ObservableProperty] private string _certificatePem = "";
    [ObservableProperty] private string _protocolPolicy = "启动后显示实际协议范围";

    public ProxyPanelModel(ProxySessionCoordinator owner)
    {
        _owner = owner;
        StartCommand = new AsyncRelayCommand(StartAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
        Refresh();
    }
    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }

    private async Task StartAsync()
    {
        if (_disposed) return;
        if (!int.TryParse(Port, out var port) || port is < 0 or > 65535) { Message = "端口应为 0～65535；0 表示分配可用端口。"; return; }
        try
        {
            await _owner.StartAsync(new(Port: port), _closing.Token);
            if (!_disposed) Message = "代理已运行。关闭页面会继续捕获；停止按钮或应用退出才结束运行。";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed) Message = $"启动失败（{exception.GetType().Name}），请检查端口和运行状态。"; }
        finally { Refresh(); }
    }
    private async Task StopAsync()
    {
        if (_disposed) return;
        try { await _owner.StopAsync(_closing.Token); if (!_disposed) Message = "代理已停止，监听和在途任务已收口。"; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed) Message = $"停止未完成（{exception.GetType().Name}），不能视为已释放。"; }
        finally { Refresh(); }
    }

    public void Refresh()
    {
        if (_disposed) return;
        var status = _owner.Status;
        StatusText = status.State switch
        {
            ProxyState.Stopped => "已停止", ProxyState.Starting => "正在启动", ProxyState.Running => "运行中",
            ProxyState.Stopping => "正在停止", ProxyState.Faulted => "发生故障", _ => "已释放"
        };
        Address = status.Runtime is { } info ? $"127.0.0.1:{info.Port}" : "尚未监听";
        ProtocolPolicy = status.Runtime?.ProtocolPolicy ?? "启动后显示实际协议范围";
        CertificatePem = status.Runtime is { } active ? System.Security.Cryptography.PemEncoding.WriteString("CERTIFICATE", Convert.FromBase64String(active.PublicRootCertificate)) : "";
        var metrics = _owner.Metrics;
        MetricsText = $"活动 {metrics.ActiveRequests} · 已观察 {metrics.ReceivedBytes:N0} 字节 · 已保存 {metrics.StoredBytes:N0} 字节 · 拒绝 {metrics.RejectedRequests} · 已淘汰 {metrics.EvictedObservations}";
        // 诊断页仅显示最近 8 条元信息；不是 M1 的完整列表或正文查看器。
        RecentText = metrics.Recent.Count == 0 ? "尚无请求" : string.Join(Environment.NewLine,
            metrics.Recent.TakeLast(8).Select(r => $"{r.Method} {SafeTarget(r.Route.EffectiveUri)} · {r.StatusCode} · {r.Body.TransportState} / {r.Body.CaptureState}"));
        if (status.Failure is not null) Message = status.Failure;
    }
    private static string SafeTarget(string address)
    {
        // 默认诊断不显示 Query，避免常见 token 被直接暴露在屏幕或截图中。
        var uri = new Uri(address);
        var display = uri.GetLeftPart(UriPartial.Path);
        return display.Length <= 180 ? display : display[..180] + "…";
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _closing.Cancel(); _closing.Dispose(); }
}
