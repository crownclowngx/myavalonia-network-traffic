using System.Globalization;

namespace NetWorkTrafficPlugin.Application.Dsl;

public enum ValueState { Value, Missing, Null, NotReady, Unavailable, Invalid, BudgetExceeded }
public enum ValueKind { String, Number, Boolean, List, Tuple, Json, Null }
public sealed record DslType(ValueKind Kind, DslType? Element = null)
{
    public static readonly DslType String = new(ValueKind.String), Number = new(ValueKind.Number), Boolean = new(ValueKind.Boolean),
        Json = new(ValueKind.Json), Null = new(ValueKind.Null), Tuple = new(ValueKind.Tuple);
    public static readonly DslType Strings = new(ValueKind.List, String);
    public bool IsScalar => Kind is ValueKind.String or ValueKind.Number or ValueKind.Boolean;
}
public sealed record SourceSpan(int Start, int Length, int Line, int Column);
public sealed record DslDiagnostic(string Code, string Message, SourceSpan Span, string? FieldName = null)
{
    public string Severity => "Error";
    public string Advice => Code switch
    {
        "DSL001" => "使用 dsl 1; 声明。",
        "DSL005" => "检查参数类型；JSON标量须显式转换，列表须先选值。",
        "DSL007" => "删除字段之间的循环引用。",
        "DSL009" => "缩短规则、字段数量或表达式深度。",
        "DSL010" => "使用七天内的正时长或带时区的起止时间。",
        _ => "按当前位置修正规则，然后重新验证。"
    };
    public string Display => $"{Code} · 第{Span.Line}行第{Span.Column}列：{Message} {Advice}";
}
public sealed record FieldOrigin(string Location, string Path, ValueState State);

/// <summary>
/// 状态与载荷分开，避免把“记录不完整”转换为空字符串后错误回退。
/// 列表成员仍是结果对象，故 first 能保留首项 Null；来源只含位置，不复制凭据。
/// </summary>
public sealed record FieldValue(ValueState State, DslType Type, object? Data = null,
    string? Reason = null, bool Sensitive = false, IReadOnlyList<FieldOrigin>? Origins = null)
{
    public static FieldValue Text(string text, bool sensitive = false) => text.Length > DslLimits.ValueCharacters
        ? Problem(ValueState.BudgetExceeded, DslType.String, "DATA_VALUE_LIMIT") : new(ValueState.Value, DslType.String, text, Sensitive: sensitive);
    public static FieldValue Number(decimal n) => new(ValueState.Value, DslType.Number, n);
    public static FieldValue Boolean(bool b) => new(ValueState.Value, DslType.Boolean, b);
    public static FieldValue Problem(ValueState state, DslType? type = null, string? reason = null) => new(state, type ?? DslType.String, Reason: reason);
    public static FieldValue Items(IReadOnlyList<FieldValue> values, bool tuple = false) => values.Count > DslLimits.CollectionItems
        ? Problem(ValueState.BudgetExceeded, DslType.Strings, "DATA_COLLECTION_LIMIT")
        : new(ValueState.Value, tuple ? DslType.Tuple : new(ValueKind.List, values.FirstOrDefault()?.Type ?? DslType.String),
            values, Sensitive: values.Any(v => v.Sensitive));
    public IReadOnlyList<FieldValue> ItemsValue => (IReadOnlyList<FieldValue>)Data!;
    public bool? Truth => State == ValueState.Value && Type == DslType.Boolean ? (bool)Data! : null;
    public string Display(bool reveal = false) => State != ValueState.Value ? $"{State}（{Reason ?? "无确定值"}）" :
        Sensitive && !reveal ? "••••" : Type.Kind switch
        {
            ValueKind.Number => ((decimal)Data!).ToString("G29", CultureInfo.InvariantCulture),
            ValueKind.Boolean => (bool)Data! ? "true" : "false",
            ValueKind.List or ValueKind.Tuple => string.Join(", ", ItemsValue.Select(v => v.Display(reveal))),
            _ => (string?)Data ?? ""
        };
}

public abstract record DslExpr(SourceSpan Span);
public sealed record LiteralExpr(FieldValue Value, SourceSpan Location) : DslExpr(Location);
public sealed record NameExpr(string Name, SourceSpan Location) : DslExpr(Location);
public sealed record AccessExpr(string Name, string? Argument, SourceSpan Location) : DslExpr(Location);
public sealed record CallExpr(string Name, IReadOnlyList<DslExpr> Arguments, SourceSpan Location) : DslExpr(Location);
public sealed record BinaryExpr(string Operator, DslExpr Left, DslExpr Right, SourceSpan Location) : DslExpr(Location);
public sealed record UnaryExpr(DslExpr Operand, SourceSpan Location) : DslExpr(Location);
public sealed record ListExpr(IReadOnlyList<DslExpr> Values, SourceSpan Location) : DslExpr(Location);
public sealed record FieldDefinition(string Name, DslExpr Expression, SourceSpan Span);
public sealed record AnalysisWindow(TimeSpan? Last, DateTimeOffset? From = null, DateTimeOffset? To = null)
{
    public (DateTimeOffset From, DateTimeOffset To) Resolve(DateTimeOffset anchor)
    {
        Validate(); return Last is { } last ? (anchor - last, anchor) : (From!.Value, To!.Value);
    }
    public void Validate()
    {
        if (Last is { } duration ? From is not null || To is not null || duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(7)
            : From is null || To is null || From >= To || To - From > TimeSpan.FromDays(7))
            throw new ArgumentException("窗口必须为七天内的正时长或带时区的半开区间。");
    }
}
public sealed record DslProgram(string Source, string Name, IReadOnlyList<FieldDefinition> Fields, DslExpr Filter,
    AnalysisWindow Window, IReadOnlyList<DslExpr> GroupKeys, IReadOnlyList<string> Columns,
    IReadOnlyDictionary<string, DslType> FieldTypes);
public sealed record Compilation(DslProgram? Program, IReadOnlyList<DslDiagnostic> Diagnostics)
{
    public bool Success => Program is not null && Diagnostics.Count == 0;
}

/// <summary>硬预算集中定义；开发 profile 与测试必须核对这些值，不能只修改检查器放宽生产限制。</summary>
public static class DslLimits
{
    public const int SourceBytes = 65536, Tokens = 8192, Depth = 32, Fields = 32, Diagnostics = 50;
    public const int ValueCharacters = 4096, CollectionItems = 128, JsonDepth = 32, JsonTokens = 16384, PathSegments = 16;
    public const int BodyBytes = 262144, RecordOperations = 10000, QueryOperations = 5000000, Records = 10000, Groups = 5000;
    public const long QueryBodyBytes = 32L << 20, AnalysisBytes = 64L << 20;
}
