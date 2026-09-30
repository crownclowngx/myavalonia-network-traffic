using System.Diagnostics;

namespace NetWorkTrafficPlugin.Application.Dsl;

public interface IFieldSource
{
    ValueTask<FieldValue> ReadAsync(AccessExpr access, CancellationToken token);
}
public sealed class AnalysisLimitException(string message) : Exception(message);

/// <summary>一轮共享工作计数与单调截止，取消不会只是放弃等待，所有子操作必须观察同一令牌。</summary>
public sealed class EvaluationBudget(CancellationToken token, int milliseconds = 10000)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public long Operations { get; private set; }
    public long EncodedBytes { get; private set; }
    public long DecodedBytes { get; private set; }
    public void Check()
    {
        token.ThrowIfCancellationRequested();
        if (_clock.ElapsedMilliseconds > milliseconds) throw new AnalysisLimitException("分析超过时间预算；旧结果保持。");
    }
    public void Step() { if (++Operations > DslLimits.QueryOperations) throw new AnalysisLimitException("整轮求值次数超限。"); if (Operations % 128 == 0) Check(); }
    public void Read(long encoded, long decoded)
    {
        Check(); EncodedBytes += encoded; DecodedBytes += decoded;
        if (EncodedBytes > DslLimits.QueryBodyBytes || DecodedBytes > DslLimits.QueryBodyBytes) throw new AnalysisLimitException("整轮正文读取或解码超过32MiB。");
    }
}

/// <summary>
/// 每记录一个求值上下文，字段按需要求值并记忆一次；短路分支不读取正文。
/// 它只依赖字段端口，不知道文件、代理或控件。Unknown 的原始状态一直保留到展示。
/// </summary>
public sealed class FieldEvaluator(DslProgram program, IFieldSource source, EvaluationBudget budget, CancellationToken token)
{
    private readonly Dictionary<string, FieldValue> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FieldDefinition> _fields = program.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
    private int _steps;
    public async ValueTask<FieldValue> FieldAsync(string name)
    {
        if (_values.TryGetValue(name, out var value)) return value;
        return _values[name] = await EvaluateAsync(_fields[name].Expression);
    }
    public async ValueTask<FieldValue> EvaluateAsync(DslExpr expression)
    {
        budget.Step();
        var result = ++_steps > DslLimits.RecordOperations
            ? FieldValue.Problem(ValueState.BudgetExceeded, reason: "DATA_OPERATION_LIMIT") : await EvaluateCoreAsync(expression);
        // Unknown没有载荷，保留原因/来源并使用绑定类型；直接写在组键里的first(缺失列表)
        // 与先声明字段必须同为String，不能因是否有字段别名而显示不同类型。
        return result.State == ValueState.Value ? result : result with { Type = program.ExpressionTypes[expression] };
    }
    private async ValueTask<FieldValue> EvaluateCoreAsync(DslExpr expression)
    {
        switch (expression)
        {
            case LiteralExpr literal: return literal.Value;
            case NameExpr name: return await FieldAsync(name.Name);
            case AccessExpr access: return await source.ReadAsync(access, token);
            case ListExpr list: return FieldValue.Items(list.Values.Cast<LiteralExpr>().Select(x => x.Value).ToArray());
            case UnaryExpr unary:
                var operand = await EvaluateAsync(unary.Operand); return operand.Truth is { } truth ? Carry(FieldValue.Boolean(!truth), [operand]) : operand;
            case BinaryExpr binary: return await BinaryAsync(binary);
            case CallExpr call: return await CallAsync(call);
            default: throw new InvalidOperationException("未知AST节点。");
        }
    }
    private async ValueTask<FieldValue> BinaryAsync(BinaryExpr expression)
    {
        var left = await EvaluateAsync(expression.Left);
        if (expression.Operator == "and" && left.Truth == false || expression.Operator == "or" && left.Truth == true) return left;
        var right = await EvaluateAsync(expression.Right);
        if (expression.Operator is "and" or "or")
        {
            if (expression.Operator == "and" && right.Truth == false) return Carry(FieldValue.Boolean(false), [left, right]);
            if (expression.Operator == "or" && right.Truth == true) return Carry(FieldValue.Boolean(true), [left, right]);
            if (left.Truth is null || right.Truth is null) return Carry(left.Truth is null ? left : right, [left, right]);
            return Carry(FieldValue.Boolean(expression.Operator == "and"), [left, right]);
        }
        if (left.State != ValueState.Value || right.State != ValueState.Value) return Carry(left.State != ValueState.Value ? left : right, [left, right]);
        bool result;
        if (expression.Operator == "in")
        {
            if (right.ItemsValue.Any(x => x.State == ValueState.Value && Equal(left, x))) result = true;
            else if (right.ItemsValue.FirstOrDefault(x => x.State != ValueState.Value) is { } unknown) return Carry(unknown, [left, right]);
            else result = false;
        }
        else result = expression.Operator switch
        {
            "==" => Equal(left, right), "!=" => !Equal(left, right),
            "<" => (decimal)left.Data! < (decimal)right.Data!, "<=" => (decimal)left.Data! <= (decimal)right.Data!,
            ">" => (decimal)left.Data! > (decimal)right.Data!, ">=" => (decimal)left.Data! >= (decimal)right.Data!, _ => false
        };
        return Carry(FieldValue.Boolean(result), [left, right]);
    }
    private async ValueTask<FieldValue> CallAsync(CallExpr call)
    {
        var args = new List<FieldValue>();
        if (call.Name == "if")
        {
            var condition = await EvaluateAsync(call.Arguments[0]);
            if (condition.Truth is null) return condition;
            return Carry(await EvaluateAsync(call.Arguments[condition.Truth.Value ? 1 : 2]), [condition]);
        }
        foreach (var expression in call.Arguments)
        {
            var value = await EvaluateAsync(expression); args.Add(value);
            if (call.Name == "firstNonEmpty" && !(value.State is ValueState.Missing or ValueState.Null || value.State == ValueState.Value && Equals(value.Data, ""))) return Carry(value, args);
        }
        if (call.Name == "firstNonEmpty") return Carry(FieldValue.Problem(ValueState.Missing), args);
        var first = args[0];
        if (call.Name is "isMissing" or "isNull")
            return first.State is ValueState.Value or ValueState.Missing or ValueState.Null
                ? Carry(FieldValue.Boolean(first.State == (call.Name == "isMissing" ? ValueState.Missing : ValueState.Null)), args) : first;
        // 组合键保留每个分量的状态，不能把 (Value,Missing) 压扁为一个 Missing 后丢失解释。
        if (call.Name == "tuple") return Carry(FieldValue.Items(args.AsReadOnly(), true), args);
        if (args.FirstOrDefault(a => a.State != ValueState.Value) is { } failure) return Carry(failure, args);
        FieldValue result;
        switch (call.Name)
        {
            case "first": result = first.Type.Kind == ValueKind.List ? first.ItemsValue.FirstOrDefault() ?? FieldValue.Problem(ValueState.Missing) : Invalid(); break;
            case "all": result = first.Type.Kind == ValueKind.List ? first : Invalid(); break;
            case "toString": result = first.Type.IsScalar ? FieldValue.Text(first.Display(true)) : Invalid(); break;
            case "toNumber": result = first.Type == DslType.Number ? first : first.Type == DslType.String && DslNumbers.TryParse((string)first.Data!, out var n) ? FieldValue.Number(n) : Invalid(); break;
            case "toBool": result = first.Type == DslType.Boolean ? first : first.Data is "true" or "false" ? FieldValue.Boolean(Equals(first.Data, "true")) : Invalid(); break;
            case "trim": result = FieldValue.Text(((string)first.Data!).Trim(' ', '\t', '\r', '\n')); break;
            case "lower": result = FieldValue.Text(((string)first.Data!).ToLowerInvariant()); break;
            case "upper": result = FieldValue.Text(((string)first.Data!).ToUpperInvariant()); break;
            case "contains": result = FieldValue.Boolean(((string)first.Data!).Contains((string)args[1].Data!, StringComparison.Ordinal)); break;
            case "startsWith": result = FieldValue.Boolean(((string)first.Data!).StartsWith((string)args[1].Data!, StringComparison.Ordinal)); break;
            case "endsWith": result = FieldValue.Boolean(((string)first.Data!).EndsWith((string)args[1].Data!, StringComparison.Ordinal)); break;
            default: throw new InvalidOperationException("编译器允许了未知函数。");
        }
        return Carry(result, args);
    }
    public static bool Equal(FieldValue left, FieldValue right) => left.Type == right.Type && Equals(left.Data, right.Data);
    private static FieldValue Invalid() => FieldValue.Problem(ValueState.Invalid, reason: "DATA_TYPE");
    private static FieldValue Carry(FieldValue result, IEnumerable<FieldValue> values)
    {
        var all = values.Append(result).ToArray();
        return result with { Sensitive = all.Any(v => v.Sensitive), Origins = all.SelectMany(v => v.Origins ?? []).Distinct().Take(8).ToArray() };
    }
}
