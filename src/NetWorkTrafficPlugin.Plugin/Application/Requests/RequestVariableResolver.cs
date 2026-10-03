using System.Text;
using System.Text.Json;

namespace NetWorkTrafficPlugin.Application.Requests;

/// <summary>
/// 单次扫描占位符，替换后的字符不重新进入解析器；避免递归变量、双重编码和变量内容意外取得执行能力。
/// 解析结果只供本次准备使用，敏感标记跨作用域向下传播，不因普通覆盖值丢失。
/// </summary>
public sealed class RequestVariableResolver
{
    private readonly Dictionary<string, RequestVariable> _values = new(StringComparer.Ordinal);
    public bool UsedSensitive { get; private set; }
    public RequestVariableResolver(params IEnumerable<RequestVariable>[] scopesLowToHigh)
    {
        foreach (var scope in scopesLowToHigh)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in scope)
            {
                if (names.Count >= RequestLimits.Variables || !ValidName(value.Name) || !names.Add(value.Name) || value.Value.Length > RequestLimits.ValueCharacters)
                    throw new RequestFailure("RQ005", "变量名称、重复项或数量/长度超出约定。");
                _values[value.Name] = value with { Sensitive = value.Sensitive || (_values.TryGetValue(value.Name, out var old) && old.Sensitive) };
            }
        }
    }
    public static bool ValidName(string name) => name.Length is > 0 and <= 64 &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
    public string Resolve(string text, Func<string, string>? encode = null)
    {
        if (text.Length > RequestLimits.BodyBytes) throw new RequestFailure("RQ008", "输入超过编辑预算。");
        var output = new StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            string fragment;
            if (text.AsSpan(i).StartsWith("$${")) { fragment = "${"; i += 3; }
            else if (text.AsSpan(i).StartsWith("${"))
            {
                var end = text.IndexOf('}', i + 2);
                if (end < 0) throw new RequestFailure("RQ005", "变量占位符缺少右括号。");
                var name = text[(i + 2)..end];
                if (!ValidName(name) || !_values.TryGetValue(name, out var variable))
                    throw new RequestFailure("RQ005", "变量未定义或名称非法；请检查占位符和所选环境。");
                UsedSensitive |= variable.Sensitive;
                fragment = encode?.Invoke(variable.Value) ?? variable.Value; i = end + 1;
            }
            else { fragment = text[i].ToString(); i++; }
            if (output.Length + fragment.Length > RequestLimits.BodyBytes) throw new RequestFailure("RQ008", "替换结果超过预算。");
            output.Append(fragment);
        }
        return output.ToString();
    }
    public string Address(string text)
    {
        if (text.StartsWith("${baseUrl}", StringComparison.Ordinal))
        {
            var origin = RequestValidation.Address(Resolve("${baseUrl}"));
            if (origin.AbsolutePath != "/" || origin.Query.Length != 0) throw new RequestFailure("RQ005", "baseUrl 只允许协议、主机与端口。");
            var suffix = text[10..];
            if (suffix.Length > 0 && !suffix.StartsWith('/')) throw new RequestFailure("RQ005", "baseUrl 后必须是绝对路径。");
            text = origin.GetLeftPart(UriPartial.Authority) + suffix;
        }
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) throw new RequestFailure("RQ002", "地址缺少协议。");
        var end = text.IndexOfAny(['/', '?', '#'], scheme + 3); if (end < 0) end = text.Length;
        if (text[..end].Contains("${", StringComparison.Ordinal)) throw new RequestFailure("RQ005", "主机或协议不支持普通变量，请使用 baseUrl。");
        return text[..end] + Resolve(text[end..], Uri.EscapeDataString);
    }
    public string Json(string text)
    {
        using var json = RequestValidation.Json(text);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) Write(json.RootElement, writer, output);
        if (output.Length > RequestLimits.BodyBytes) throw new RequestFailure("RQ008", "JSON 替换结果超过预算。");
        return Encoding.UTF8.GetString(output.ToArray());
    }
    private void Write(JsonElement value, Utf8JsonWriter writer, MemoryStream output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Name.Replace("$${", "", StringComparison.Ordinal).Contains("${", StringComparison.Ordinal)) throw new RequestFailure("RQ005", "JSON 属性名不支持变量。");
                    writer.WritePropertyName(property.Name.Replace("$${", "${", StringComparison.Ordinal)); Write(property.Value, writer, output);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item, writer, output); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(Resolve(value.GetString()!)); break;
            default: value.WriteTo(writer); break; // 直接保留 JSON 数字词法，不经过 double 改变精度。
        }
        writer.Flush();
        if (output.Length > RequestLimits.BodyBytes) throw new RequestFailure("RQ008", "JSON 替换结果超过预算。");
    }
}
