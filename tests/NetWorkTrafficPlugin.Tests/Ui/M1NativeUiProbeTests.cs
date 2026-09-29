using System.Diagnostics;
using System.Text.Json;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Ui;

public sealed class M1NativeUiProbeTests
{
    [Fact]
    [Trait("Category", "NativeUiProbe")]
    public async Task 独立桌面生产视图一万摘要五流十分钟和二十轮页面挂卸满足预算()
    {
        var directory = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_TEST_ARTIFACTS") ?? Path.Combine(ProbeProcess.Repository, "TestResults/M1NativeStandalone");
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(ProbeProcess.Repository, "tools/NetWorkTrafficPlugin.UiProbe/bin/Debug/net10.0/NetWorkTrafficPlugin.UiProbe.dll"));
        start.Environment["NETWORK_TRAFFIC_TEST_ARTIFACTS"] = directory;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(16)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var log = await stdout + await stderr; await File.WriteAllTextAsync(Path.Combine(directory, "m1-ui-native.log"), log);
        Assert.True(process.ExitCode == 0, log);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "m1-ui-native.json")));
        var result = json.RootElement.GetProperty("scenarios").GetProperty("sustained");
        Assert.False(result.GetProperty("smoke").GetBoolean()); Assert.Equal(20, result.GetProperty("rounds").GetInt32());
        Assert.True(result.GetProperty("seconds").GetDouble() >= 600); Assert.True(result.GetProperty("sampleCount").GetInt32() >= 200);
    }
}
