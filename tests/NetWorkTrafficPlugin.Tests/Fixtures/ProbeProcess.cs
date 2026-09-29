using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Proxy;

namespace NetWorkTrafficPlugin.Tests.Fixtures;

public sealed record ProbeSnapshot(ProxyMetrics Metrics, long ManagedBytes);
public sealed record StoppedProbe(ProxyMetrics Metrics, int Handles);
public sealed record ResourceSample(string Phase, DateTimeOffset Utc, long PrivateBytes, long WorkingSet,
    long ManagedBytes, int Handles, int ActiveRequests, int QueueBytes, long StoredBytes);

/// <summary>父进程只通过 stdin 控制自己创建的代理子进程；失败时仅终止该子进程树。</summary>
public sealed class ProbeProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly SemaphoreSlim _commands = new(1);
    private readonly Task<string> _errors;
    private X509Certificate2? _root;
    public ProxyRuntimeInfo Info { get; private set; } = null!;
    public static string Repository => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    public ProbeProcess()
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(Repository, "tools/NetWorkTrafficPlugin.TechnicalProbe/bin/Debug/net10.0/NetWorkTrafficPlugin.TechnicalProbe.dll"));
        _process = Process.Start(start) ?? throw new InvalidOperationException("不能启动独立代理。");
        _errors = _process.StandardError.ReadToEndAsync();
    }
    public async Task StartAsync(ProxyOptions options)
    {
        Info = await CommandAsync<ProxyRuntimeInfo>(JsonSerializer.Serialize(options));
        _root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(Info.PublicRootCertificate));
    }
    public HttpClient Client() => new(new SocketsHttpHandler
    {
        Proxy = new WebProxy($"http://127.0.0.1:{Info.Port}") { BypassProxyOnLocal = false },
        UseProxy = true, AllowAutoRedirect = false,
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, cert, _, errors) => ProxyCertificates.ValidateWithRoot(cert, errors, _root!) }
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private async Task<T> CommandAsync<T>(string command)
    {
        await _commands.WaitAsync();
        try
        {
            await _process.StandardInput.WriteLineAsync(command);
            await _process.StandardInput.FlushAsync();
            var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return JsonSerializer.Deserialize<T>(line ?? throw new IOException("探针提前结束。")) ?? throw new IOException("探针响应为空。");
        }
        finally { _commands.Release(); }
    }
    public async Task<ResourceSample> SampleAsync(string phase)
    {
        var snapshot = await CommandAsync<ProbeSnapshot>("snapshot");
        _process.Refresh();
        return new(phase, DateTimeOffset.UtcNow, _process.PrivateMemorySize64, _process.WorkingSet64,
            snapshot.ManagedBytes, _process.HandleCount, snapshot.Metrics.ActiveRequests, snapshot.Metrics.QueueBytes, snapshot.Metrics.StoredBytes);
    }
    public async Task<(ProxyMetrics Metrics, double Milliseconds, int ExitCode, int Handles)> StopAsync()
    {
        var elapsed = Stopwatch.StartNew();
        var result = await CommandAsync<StoppedProbe>("stop");
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (_process.ExitCode != 0) throw new IOException(await _errors);
        return (result.Metrics, elapsed.Elapsed.TotalMilliseconds, _process.ExitCode, result.Handles);
    }
    public ValueTask DisposeAsync()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        _root?.Dispose(); _process.Dispose(); _commands.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 用与测量相同的并发度预热 .NET 线程池。单连接预热会把首次并发产生的线程/事件句柄
    /// 误算为代理退出泄漏；不强制 GC、不提高任何内存或句柄阈值。端点/客户端仍在父进程。
    /// </summary>
    public async Task WarmupAsync(int bytes, int concurrency)
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("warm", root, https: false);
        using var client = Client();
        var responses = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => client.GetAsync(
            new Uri(origin.Address, $"/binary?size={bytes}&hold=1"), HttpCompletionOption.ResponseHeadersRead)));
        try
        {
            origin.ContinueStream.TrySetResult();
            await Task.WhenAll(responses.Select(async response =>
            {
                response.EnsureSuccessStatusCode();
                var digest = await TransferDigest.ReadAsync(await response.Content.ReadAsStreamAsync());
                if (digest.Bytes != bytes) throw new IOException("预热传输未完成。");
            }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await SampleAsync("warm-drain")).ActiveRequests > 0) await Task.Delay(20, timeout.Token);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }
}
