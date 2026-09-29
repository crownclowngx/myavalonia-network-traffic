using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Integration;

public sealed class SystemSettingsTests
{
    [Fact]
    public async Task 代理起停不改变系统信任代理或hosts()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows 本机附加检查；不是 Windows CI。
        var before = Snapshot();
        await using (var proxy = new ProxyFixture()) { await proxy.StartAsync(new()); }
        Assert.Equal(before, Snapshot());
        ProbeEvidence.Write("protocol-probes", "D03", new { platform = "Windows", settingsUnchanged = true });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string Snapshot()
    {
        var values = new List<string>();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var roots = new X509Store(StoreName.Root, location);
            roots.Open(OpenFlags.ReadOnly);
            foreach (var cert in roots.Certificates)
            {
                using (cert) values.Add($"{location}:{cert.Thumbprint}");
            }
        }
        using var internet = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        foreach (var name in new[] { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL" })
            values.Add($"{name}:{internet?.GetValue(name)}");
        var hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers/etc/hosts");
        if (File.Exists(hosts)) values.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hosts))));
        return string.Join('\n', values.Order());
    }
}
