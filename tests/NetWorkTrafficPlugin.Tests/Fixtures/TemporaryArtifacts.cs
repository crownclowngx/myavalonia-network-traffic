namespace NetWorkTrafficPlugin.Tests.Fixtures;

public static class TemporaryArtifacts
{
    /// <summary>递归清理前核对绝对路径与 GUID 名称，仅接收本夹具创建的临时目录。</summary>
    public static void Delete(string directory)
    {
        var full = Path.GetFullPath(directory);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith("network-m0-", StringComparison.Ordinal) || !Guid.TryParseExact(name[11..], "N", out _))
            throw new InvalidOperationException("拒绝清理不属于本轮夹具的路径。");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
