using NetWorkTrafficPlugin.Application.Capture;

namespace NetWorkTrafficPlugin.Application.Content;

public sealed record ParsedField(string Name, string? Value, string Raw, string? Diagnostic = null);

/// <summary>查看器的解析不回写网络。原始值保留用于排错，重复键保持出现顺序。</summary>
public static class HttpFieldParser
{
    public static ParsedField[] Query(string uri)
    {
        var question = uri.IndexOf('?');
        if (question < 0) return [];
        var query = uri[(question + 1)..].Split('#')[0];
        if (query.Length == 0) return [];
        return query.Split('&').Select(part =>
        {
            var index = part.IndexOf('=');
            var name = index < 0 ? part : part[..index];
            var value = index < 0 ? null : part[(index + 1)..];
            var invalid = InvalidEscape(name) || value is not null && InvalidEscape(value);
            // URI Query 的加号不是 HTML 表单空格；不要使用会自动把 + 换空格的表单解析器。
            return new ParsedField(invalid ? name : Uri.UnescapeDataString(name),
                value is null ? null : invalid ? value : Uri.UnescapeDataString(value), part,
                invalid ? "百分号编码不完整，保留原值。" : null);
        }).ToArray();
    }
    private static bool InvalidEscape(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '%' && (i + 2 >= text.Length || !Uri.IsHexDigit(text[i + 1]) || !Uri.IsHexDigit(text[i + 2]))) return true;
        return false;
    }
    public static ParsedField[] Cookies(IEnumerable<HeaderField> headers) => headers
        .Where(h => h.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || h.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
        .SelectMany(h => h.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ? [h.Value] : h.Value.Split(';'))
        .Select(raw =>
        {
            var index = raw.IndexOf('=');
            return new ParsedField(index < 0 ? raw.Trim() : raw[..index].Trim(), index < 0 ? null : raw[(index + 1)..], raw,
                index < 0 ? "Cookie 无等号，保留原始行。" : null);
        }).ToArray();
    public static bool Sensitive(string name) => new[] { "authorization", "proxy-authorization", "cookie", "set-cookie", "token", "access_token", "password", "secret", "api_key", "x-api-key" }
        .Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase) || name.EndsWith("-" + n, StringComparison.OrdinalIgnoreCase));
    public static string Display(IEnumerable<HeaderField> headers, bool reveal = false) => string.Join(Environment.NewLine,
        headers.Select(h => $"{h.Name}: {(reveal || !Sensitive(h.Name) ? h.Value : "••••（点击显示原值）")}"));
    public static string SafeFileName(string? name)
    {
        var value = string.Concat((name ?? "body.bin").Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c));
        value = value.Trim().TrimEnd('.');
        if (value.Length == 0 || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "LPT1" }.Contains(value.Split('.')[0], StringComparer.OrdinalIgnoreCase)) value = "body.bin";
        return value.Length > 120 ? value[..120] : value;
    }
}
