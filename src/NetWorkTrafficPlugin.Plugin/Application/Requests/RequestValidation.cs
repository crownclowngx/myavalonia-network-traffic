using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NetWorkTrafficPlugin.Application.Requests;

/// <summary>纯协议校验共用于准备器、模板恢复和实际发送边界；拒绝输入而不悄悄修复成另一个请求。</summary>
public static class RequestValidation
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    { "Host", "Content-Length", "Transfer-Encoding", "Connection", "Proxy-Connection", "Proxy-Authorization",
      "Keep-Alive", "TE", "Trailer", "Upgrade", "Expect", "Content-Encoding", "Content-Type",
      "Content-MD5", "Digest", "Content-Digest", "Repr-Digest" };
    public static bool IsProtected(string name) => Protected.Contains(name) || name.StartsWith(':');
    public static bool IsSensitive(string name) => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase);
    public static bool IsToken(string value) => value.Length is > 0 and <= 256 && value.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));
    public static void Method(string value, bool body)
    {
        if (!IsToken(value) || value.Equals("CONNECT", StringComparison.OrdinalIgnoreCase) || value.Equals("TRACE", StringComparison.OrdinalIgnoreCase))
            throw new RequestFailure("RQ001", "方法非法，或首版不支持 CONNECT/TRACE。");
        if (body && (value.Equals("GET", StringComparison.OrdinalIgnoreCase) || value.Equals("HEAD", StringComparison.OrdinalIgnoreCase)))
            throw new RequestFailure("RQ001", "GET/HEAD 首版不支持正文，请明确选择无正文。");
    }
    public static Uri Address(string value)
    {
        if (value.Length > RequestLimits.UriCharacters || value.Any(c => c <= 32 || c == '\\' || c == 127) || !ValidEscapes(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || string.IsNullOrEmpty(uri.Host))
            throw new RequestFailure("RQ002", "地址必须是合法 HTTP/HTTPS 绝对地址，不能含凭据、片段、空白或损坏转义。");
        if (uri.Query.Count(c => c == '&') + (uri.Query.Length > 0 ? 1 : 0) > RequestLimits.Items)
            throw new RequestFailure("RQ008", "Query 超过 256 项。");
        return uri;
    }
    public static bool ValidEscapes(string value)
    {
        for (var i = 0; i < value.Length; i++) if (value[i] == '%')
        { if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) return false; i += 2; }
        return true;
    }
    public static void Header(string name, string value)
    {
        if (!IsToken(name) || IsProtected(name) || value.Any(c => c is '\r' or '\n' or '\0' || c < 32 && c != '\t' || c == 127))
            throw new RequestFailure("RQ003", "Header 名称或值非法，或属于由发送器管理的协议字段。");
    }
    public static string MediaType(string value)
    {
        if (value.Length > 256 || value.Any(c => c is '\r' or '\n' or '\0') || !MediaTypeHeaderValue.TryParse(value, out var parsed))
            throw new RequestFailure("RQ003", "Content-Type 格式非法。");
        return parsed.ToString();
    }
    public static string Utf8MediaType(string value)
    {
        var normalized = MediaType(value); var parsed = MediaTypeHeaderValue.Parse(normalized);
        if (parsed.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            throw new RequestFailure("RQ004", "文本编辑器只生成UTF-8，不能声明其他charset。");
        return normalized;
    }
    public static byte[] Utf8(string value)
    {
        var encoding = new UTF8Encoding(false, true);
        if (value.Length > RequestLimits.BodyBytes || encoding.GetByteCount(value) > RequestLimits.BodyBytes)
            throw new RequestFailure("RQ008", "正文超过 256 KiB 编辑上限。");
        return encoding.GetBytes(value);
    }
    public static JsonDocument Json(string value)
    {
        try
        {
            var bytes = Utf8(value); var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
            var count = 0;
            while (reader.Read()) if (++count > 16384) throw new RequestFailure("RQ008", "JSON token 超过 16384。");
            var result = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            try { CheckKeys(result.RootElement); return result; } catch { result.Dispose(); throw; }
        }
        catch (JsonException) { throw new RequestFailure("RQ004", "JSON 无效、含 BOM/注释/尾逗号或深度超过 32。"); }
    }
    private static void CheckKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!keys.Add(property.Name)) throw new RequestFailure("RQ004", "JSON 包含重复属性。"); CheckKeys(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckKeys(item);
    }
}
