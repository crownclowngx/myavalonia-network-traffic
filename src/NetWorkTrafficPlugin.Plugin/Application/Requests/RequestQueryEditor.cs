namespace NetWorkTrafficPlugin.Application.Requests;

/// <summary>
/// Query编辑态与实际地址分别保存：禁用行是可恢复的草稿内容，发送只使用地址中的有效行。
/// 这里只做原文同步，不解码、不排序、不插值；空参数段也保留。共享纯函数避免UI与库校验出现两套规则。
/// </summary>
public static class RequestQueryEditor
{
    public static string FromAddress(string address) => RawQuery(address).Replace("&", "\n", StringComparison.Ordinal);
    private static string RawQuery(string address) => address.Contains('?') ? address[(address.IndexOf('?') + 1)..] : "";
    private static string EnabledQuery(string text) => string.Join("&", text.Split('\n').Select(s => s.TrimEnd('\r'))
        .Where(s => !s.StartsWith("# ", StringComparison.Ordinal)));
    public static string Apply(string address, string text)
    {
        var query = EnabledQuery(text); return address.Split('?', 2)[0] + (query.Length == 0 ? "" : "?" + query);
    }
    public static bool Matches(string address, string text) => RawQuery(address) == EnabledQuery(text);
}
