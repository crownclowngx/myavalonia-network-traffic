using System.Text.Json;

namespace NetWorkTrafficPlugin.Application.Dsl;

/// <summary>只解释可预先检查的属性/固定下标路径；没有递归搜索、过滤表达式或可执行片段。</summary>
public static class JsonFieldPath
{
    public static bool TryParse(string path, out IReadOnlyList<object> segments)
    {
        var result = new List<object>(); segments = result;
        if (path.Length == 0 || path[0] != '$') return false;
        try
        {
            for (var i = 1; i < path.Length;)
            {
                if (result.Count == DslLimits.PathSegments) return false;
                if (path[i] == '.')
                {
                    var start = ++i;
                    if (i >= path.Length || !(char.IsAsciiLetter(path[i]) || path[i] == '_')) return false;
                    while (i < path.Length && (char.IsAsciiLetterOrDigit(path[i]) || path[i] == '_')) i++;
                    result.Add(path[start..i]);
                }
                else if (path[i++] == '[')
                {
                    if (i < path.Length && path[i] == '"')
                    {
                        var start = i++;
                        while (i < path.Length && path[i] != '"') { if (path[i] == '\\') i++; i++; }
                        if (i >= path.Length) return false;
                        result.Add(JsonSerializer.Deserialize<string>(path[start..++i])!);
                    }
                    else
                    {
                        var start = i; while (i < path.Length && char.IsAsciiDigit(path[i])) i++;
                        if (!int.TryParse(path[start..i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)) return false;
                        result.Add(index);
                    }
                    if (i >= path.Length || path[i++] != ']') return false;
                }
                else return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return false; }
    }
    public static FieldValue Read(JsonElement root, string path)
    {
        if (!TryParse(path, out var segments)) return FieldValue.Problem(ValueState.Invalid, reason: "DATA_PATH");
        var item = root;
        foreach (var segment in segments)
        {
            if (segment is string name)
            {
                if (item.ValueKind != JsonValueKind.Object) return FieldValue.Problem(ValueState.Invalid, reason: "DATA_TYPE");
                if (!item.TryGetProperty(name, out item)) return FieldValue.Problem(ValueState.Missing);
            }
            else
            {
                if (item.ValueKind != JsonValueKind.Array) return FieldValue.Problem(ValueState.Invalid, reason: "DATA_TYPE");
                if ((int)segment >= item.GetArrayLength()) return FieldValue.Problem(ValueState.Missing);
                item = item[(int)segment];
            }
        }
        return Value(item);
    }
    private static FieldValue Value(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null: return FieldValue.Problem(ValueState.Null);
            case JsonValueKind.String: return FieldValue.Text(element.GetString()!, true);
            case JsonValueKind.Number: return DslNumbers.TryParse(element.GetRawText(), out var n) ? FieldValue.Number(n) : FieldValue.Problem(ValueState.Invalid, reason: "DATA_NUMBER");
            case JsonValueKind.True: return FieldValue.Boolean(true);
            case JsonValueKind.False: return FieldValue.Boolean(false);
            case JsonValueKind.Array:
                if (element.GetArrayLength() > DslLimits.CollectionItems) return FieldValue.Problem(ValueState.BudgetExceeded, reason: "DATA_COLLECTION_LIMIT");
                var items = element.EnumerateArray().Select(Value).ToArray();
                if (items.FirstOrDefault(x => x.State == ValueState.BudgetExceeded) is { } exhausted) return exhausted;
                if (items.Any(x => x.Type.Kind is ValueKind.List or ValueKind.Tuple || x.State is ValueState.Invalid or ValueState.BudgetExceeded)) return FieldValue.Problem(ValueState.Invalid, reason: "DATA_TYPE");
                var types = items.Where(x => x.State == ValueState.Value).Select(x => x.Type).Distinct().ToArray();
                return types.Length <= 1 ? FieldValue.Items(items) : FieldValue.Problem(ValueState.Invalid, reason: "DATA_TYPE");
            default: return FieldValue.Problem(ValueState.Invalid, reason: "DATA_TYPE");
        }
    }
}
