using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Dsl;

namespace NetWorkTrafficPlugin.Application.Rewrite;

public enum RewriteActionKind { Route, Path, SetQuery, RemoveQuery, SetHeader, RemoveHeader, SetJson, RemoveJson }
public sealed record RewriteAction(RewriteActionKind Kind, string Target, DslExpr? Value, SourceSpan Span);
public sealed record RewriteRule(string Name, int Priority, string Host, DslExpr Condition, IReadOnlyList<RewriteAction> Actions, SourceSpan Span);
public sealed record RewriteProgram(string Source, ExpressionContext Expressions, IReadOnlyList<RewriteRule> Rules);
public sealed record RewriteCompilation(RewriteProgram? Program, IReadOnlyList<DslDiagnostic> Diagnostics)
{
    public bool Success => Program is not null && Diagnostics.Count == 0;
}

/// <summary>原始输入只保存本次请求的事实。创建时复制有序头；Body 由窄读取端口按需取得，不能带入发送能力。</summary>
public sealed record RewriteInput(string Method, string Uri, IReadOnlyList<HeaderField> Headers)
{
    public static RewriteInput Create(string method, string uri, IEnumerable<HeaderField> headers)
    {
        RewriteValidation.ValidateUri(uri);
        var copy = new List<HeaderField>(); var bytes = 0;
        foreach (var header in headers)
        {
            bytes = checked(bytes + System.Text.Encoding.UTF8.GetByteCount(header.Name) + System.Text.Encoding.UTF8.GetByteCount(header.Value) + 4);
            if (copy.Count == RewriteLimits.HeaderLines || bytes > RewriteLimits.HeaderBytes)
                throw new RewriteFailure("RW009", "请求头超过预算。", 413);
            copy.Add(header);
        }
        return new(method, uri, copy.AsReadOnly());
    }
}
public interface IRewriteBodyReader
{
    ValueTask<byte[]> ReadAsync(CancellationToken token);
}
public sealed record RewriteRuleTrace(string Name, int Priority, string Condition, bool RouteSelected = false, bool RouteShadowed = false);
public sealed record RewriteDifference(string Location, string Target, string Change);
public sealed record RewritePlan(string Uri, IReadOnlyList<HeaderField> Headers, byte[]? Body,
    IReadOnlyList<RewriteRuleTrace> Rules, IReadOnlyList<RewriteDifference> Differences, bool Matched);
public sealed record RewriteResult(RewritePlan? Plan, string? ErrorCode, string? Message, int Status,
    IReadOnlyList<RewriteRuleTrace> Rules)
{
    public bool Blocked => Plan is null;
}
public sealed class RewriteFailure(string code, string message, int status = 502) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
/// <summary>发送前限制独立于离线分析；profile 和真实边界测试必须与这些常量一致。</summary>
public static class RewriteLimits
{
    public const int Rules = 32, ActionsPerRule = 16, Actions = 128, HeaderBytes = 65536, HeaderLines = 256;
    public const int BodyBytes = 262144, BodySlots = 4, BodyMilliseconds = 2000, MetadataMilliseconds = 50;
    public const int Operations = 10000, UriCharacters = 8192, TraceBytes = 16384;
    public const long BodyReservationBytes = 4L << 20;
}
