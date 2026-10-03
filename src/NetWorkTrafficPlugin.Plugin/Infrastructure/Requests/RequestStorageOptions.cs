namespace NetWorkTrafficPlugin.Infrastructure.Requests;

/// <summary>组合根提供存储根，测试替换成隔离临时目录；页面和应用用例不自行定位用户配置目录。</summary>
public sealed record RequestStorageOptions(string Root)
{
    public static RequestStorageOptions Default => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyAvalonia", "NetworkTraffic", "Requests"));
}
