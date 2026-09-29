using System.Net;

namespace NetWorkTrafficPlugin.Application.Proxy;

/// <summary>纯路由决策，不发送网络请求。保持原始转义文本，避免重新编码改变签名和重复查询参数。</summary>
public static class RouteTargetResolver
{
    public static void Validate(RouteMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.SourceHost) ||
            Uri.CheckHostName(mapping.SourceHost) == UriHostNameType.Unknown)
            throw new ArgumentException("源域名必须为精确主机名。");
        var target = Parse(mapping.TargetOrigin);
        if (target.AbsolutePath != "/" || target.Query.Length != 0 || target.Fragment.Length != 0)
            throw new ArgumentException("映射目标只能包含协议、主机与端口。");
    }

    public static RouteDecision Resolve(string original, RouteMapping? mapping, string? condition, int proxyPort)
    {
        var source = Parse(original);
        var matched = mapping is not null && string.Equals(source.IdnHost, mapping.SourceHost, StringComparison.OrdinalIgnoreCase)
            && (mapping.RequiredHeaderValue is null || mapping.RequiredHeaderValue == condition);
        var effective = original;
        if (matched)
        {
            Validate(mapping!);
            var target = Parse(mapping!.TargetOrigin);
            if (target.Scheme != source.Scheme) throw new ArgumentException("M0 目标映射要求保持原始协议。");
            // 原始字符串中的路径与 Query 保持逐字符一致；URI 只负责验证并定位 authority 的结束处。
            var authorityEnd = original.IndexOfAny(['/', '?', '#'], original.IndexOf("://", StringComparison.Ordinal) + 3);
            var suffix = authorityEnd < 0 ? "/" : original[authorityEnd..];
            if (suffix.StartsWith('?')) suffix = "/" + suffix;
            effective = target.GetLeftPart(UriPartial.Authority) + suffix;
        }
        var result = Parse(effective);
        if (result.Port == proxyPort && (result.IsLoopback ||
            IPAddress.TryParse(result.Host, out var address) && IPAddress.IsLoopback(address)))
            throw new ArgumentException("目标指向当前代理监听地址，已阻止循环。");
        return new(original, effective, matched, mapping?.Revision ?? 0);
    }

    private static Uri Parse(string value)
    {
        if (value.Length > 8192 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("目标必须为不含身份或片段的 HTTP/HTTPS 地址。");
        return uri;
    }
}
