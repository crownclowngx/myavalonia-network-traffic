using NetWorkTrafficPlugin.Application.Dsl;

namespace NetWorkTrafficPlugin.Application.Rewrite;

/// <summary>编译和执行共同使用的纯校验。动态动作值必须再校验，不能靠编译字面量挡住头注入或目标越界。</summary>
public static class RewriteValidation
{
    private static readonly HashSet<string> ProtectedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Content-Encoding", "Content-Type", "Connection",
        "Proxy-Connection", "Keep-Alive", "TE", "Trailer", "Upgrade", "Expect",
        "Content-MD5", "Digest", "Content-Digest", "Repr-Digest"
    };
    public static string Host(string text)
    {
        var trimmed = text.EndsWith('.') ? text[..^1] : text;
        if (trimmed.Length == 0 || Uri.CheckHostName(trimmed) == UriHostNameType.Unknown || trimmed.Contains('%'))
            throw new RewriteFailure("RW005", "作用域必须为精确主机名，不含端口或通配符。");
        if (System.Net.IPAddress.TryParse(trimmed, out var ip)) return ip.ToString().ToLowerInvariant();
        return new System.Globalization.IdnMapping().GetAscii(trimmed).ToLowerInvariant();
    }
    public static Uri ValidateUri(string text, bool origin = false)
    {
        if (text.Length > RewriteLimits.UriCharacters || text.Any(char.IsControl) || text.Contains('\\') ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || origin && (uri.AbsolutePath != "/" || uri.Query.Length != 0))
            throw new RewriteFailure("RW005", "目标必须为合法 HTTP/HTTPS 地址；路由目标只能含协议、主机和端口。");
        return uri;
    }
    public static void Path(string text)
    {
        if (!text.StartsWith('/') || text.StartsWith("//", StringComparison.Ordinal) || text.Length > RewriteLimits.UriCharacters ||
            text.Any(c => !char.IsAsciiLetterOrDigit(c) && !"/-._~!$&'()*+,;=:@%".Contains(c)) ||
            text.Split('/').Any(segment => RequestFieldText.Decode(segment, false) is "." or ".."))
            throw new RewriteFailure("RW005", "路径需为单斜线开头的转义绝对路径，不含查询、片段或点段。");
    }
    public static void Header(string name, string? value = null)
    {
        if (name.Length == 0 || ProtectedHeaders.Contains(name) ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)))
            throw new RewriteFailure("RW006", "不能修改协议控制头或非法头名。");
        if (value?.Any(c => c is '\r' or '\n' or '\0') == true)
            throw new RewriteFailure("RW006", "请求头值不能包含换行或零字符。");
    }
    public static IReadOnlyList<object> JsonPath(string text, bool remove)
    {
        if (!JsonFieldPath.TryParse(text, out var path) || path.Count == 0 || remove && path[^1] is int)
            throw new RewriteFailure("RW005", "JSON 动作只支持非根固定路径；删除只支持对象属性。");
        return path;
    }
    public static bool Conflicts(RewriteAction a, RewriteAction b)
    {
        static int Family(RewriteActionKind k) => k switch
        {
            RewriteActionKind.Route => 0, RewriteActionKind.Path => 1,
            RewriteActionKind.SetQuery or RewriteActionKind.RemoveQuery => 2,
            RewriteActionKind.SetHeader or RewriteActionKind.RemoveHeader => 3, _ => 4
        };
        var family = Family(a.Kind);
        if (family == 0 || family != Family(b.Kind)) return false;
        if (family == 1) return true;
        if (family is 2 or 3) return string.Equals(a.Target, b.Target, family == 3 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var left = JsonPath(a.Target, false); var right = JsonPath(b.Target, false);
        return left.Take(Math.Min(left.Count, right.Count)).SequenceEqual(right.Take(Math.Min(left.Count, right.Count)));
    }
}
