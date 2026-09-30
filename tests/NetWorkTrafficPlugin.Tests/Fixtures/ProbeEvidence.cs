using System.Text.Json;

namespace NetWorkTrafficPlugin.Tests.Fixtures;

/// <summary>只记录受控合成输入；本轮身份来自门禁。独立运行测试时不伪造门禁身份。</summary>
public static class ProbeEvidence
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Dictionary<string, object>> Groups = [];
    public static void Write(string group, string scenario, object evidence)
    {
        var directory = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_TEST_ARTIFACTS");
        if (directory is null) return;
        lock (Sync)
        {
            if (!Groups.TryGetValue(group, out var entries)) Groups[group] = entries = [];
            entries[scenario] = evidence;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, group + ".json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                runId = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_RUN_ID"),
                sourceHash = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_SOURCE_HASH"),
                profileHash = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_M1_PROFILE_HASH"),
                uxProfileHash = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_UX_PROFILE_HASH"),
                m2ProfileHash = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_M2_PROFILE_HASH"),
                scenarioIds = entries.Keys.ToArray(),
                recordedUtc = DateTimeOffset.UtcNow,
                scenarios = entries
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
