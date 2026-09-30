using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NetWorkTrafficPlugin.Application.Dsl;

/// <summary>
/// 有限文法采用普通递归下降；深度在递归前检查。编译不读取捕获、更不修改已应用规则。
/// 每个标记保存 UTF-16 偏移，编辑器可直接选择中文/emoji 周围的错误文本。
/// </summary>
public static class DslCompiler
{
    internal sealed class SyntaxFailure(string code, string message, SourceSpan span) : Exception(message)
    { public DslDiagnostic Diagnostic { get; } = new(code, message, span); }
    private sealed record Token(string Text, SourceSpan Span, string? StringValue = null, bool IsString = false);
    public static Compilation Compile(string source, CancellationToken token = default)
    {
        try { return new Parser(source, token).Compile(); }
        catch (SyntaxFailure ex) { return new(null, [ex.Diagnostic]); }
    }
    private sealed class Parser(string source, CancellationToken cancellation)
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly List<Token> _tokens = [];
        private readonly List<DslDiagnostic> _diagnostics = [];
        private int _index, _depth;
        private Token Current => _tokens[_index];
        private void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if (_watch.ElapsedMilliseconds > 500) throw new SyntaxFailure("DSL009", "编译超过 500 毫秒预算。", new(0, 0, 1, 1));
        }
        private static void Fail(string code, string message, SourceSpan span) => throw new SyntaxFailure(code, message, span);
        private void Lex()
        {
            if (source.Length > DslLimits.SourceBytes || Encoding.UTF8.GetByteCount(source) > DslLimits.SourceBytes)
                Fail("DSL009", "规则超过 64 KiB。", new(0, 0, 1, 1));
            int i = 0, line = 1, column = 1;
            while (i < source.Length)
            {
                Check(); var ch = source[i];
                if (char.IsWhiteSpace(ch)) { i++; if (ch == '\n') { line++; column = 1; } else column++; continue; }
                if (ch == '/' && i + 1 < source.Length && source[i + 1] == '/')
                { while (i < source.Length && source[i] != '\n') { i++; column++; } continue; }
                var start = i; var startColumn = column; string? value = null; bool isString = ch == '"';
                if (isString)
                {
                    i++; bool closed = false;
                    while (i < source.Length)
                    {
                        if (source[i] is '\r' or '\n') break;
                        if (source[i] == '\\') { i += 2; continue; }
                        if (source[i++] == '"') { closed = true; break; }
                    }
                    if (!closed || i > source.Length) Fail("DSL002", "字符串或转义未闭合。", new(start, Math.Min(i, source.Length) - start, line, startColumn));
                    try
                    {
                        value = JsonSerializer.Deserialize<string>(source[start..i])!;
                        _ = new UTF8Encoding(false, true).GetBytes(value);
                    }
                    catch (Exception ex) when (ex is JsonException or ArgumentException)
                    { Fail("DSL002", "字符串转义或 Unicode 无效。", new(start, i - start, line, startColumn)); }
                }
                else if (char.IsAsciiLetter(ch) || ch == '_')
                { while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_')) i++; }
                else if (char.IsAsciiDigit(ch) || ch == '-' && i + 1 < source.Length && char.IsAsciiDigit(source[i + 1]))
                { i++; while (i < source.Length && (char.IsAsciiDigit(source[i]) || source[i] == '.')) i++; }
                else if ("{}[](),;.".Contains(ch)) i++;
                else if ("=!<>".Contains(ch)) { i++; if (i < source.Length && source[i] == '=') i++; }
                else Fail("DSL002", "无法识别的字符。", new(start, 1, line, startColumn));
                _tokens.Add(new(source[start..i], new(start, i - start, line, startColumn), value, isString)); column += i - start;
                if (_tokens.Count > DslLimits.Tokens) Fail("DSL009", "标记数量超过 8192。", _tokens[^1].Span);
            }
            _tokens.Add(new("<end>", new(source.Length, 0, line, column)));
        }
        private Token Take() { Check(); var t = Current; if (_index < _tokens.Count - 1) _index++; return t; }
        private bool Match(string text) { if (Current.Text != text || Current.IsString) return false; Take(); return true; }
        private Token Expect(string text)
        { if (Current.Text != text || Current.IsString) Fail("DSL003", $"此处需要 {text}。", Current.Span); return Take(); }
        private Token Identifier()
        {
            if (Current.IsString || !char.IsAsciiLetter(Current.Text[0]) && Current.Text[0] != '_') Fail("DSL003", "此处需要字段名称。", Current.Span);
            return Take();
        }
        private string String()
        { if (!Current.IsString) Fail("DSL003", "此处需要双引号字符串。", Current.Span); return Take().StringValue!; }
        public Compilation Compile()
        {
            Lex(); Expect("dsl"); var version = Take(); if (version.Text != "1") Fail("DSL001", "仅支持 dsl 1。", version.Span); Expect(";");
            var fields = new List<FieldDefinition>();
            while (Match("field"))
            {
                try
                {
                    var name = Identifier(); if (Reserved.Contains(name.Text)) Fail("DSL004", "字段名不能使用保留名。", name.Span);
                    Expect("="); var expression = Expression(); Expect(";"); fields.Add(new(name.Text, expression, name.Span));
                    if (fields.Count > DslLimits.Fields) Fail("DSL009", "字段超过32个。", name.Span);
                }
                catch (SyntaxFailure ex)
                {
                    _diagnostics.Add(ex.Diagnostic); if (_diagnostics.Count >= DslLimits.Diagnostics) return new(null, _diagnostics);
                    // 每次恢复至少消耗输入；在字段结束处继续，避免一处错误隐藏后续独立错误。
                    while (Current.Text is not (";" or "view" or "field" or "<end>")) Take(); Match(";");
                }
            }
            Expect("view"); var nameText = String(); Expect("{"); Expect("where"); var filter = Expression(); Expect(";");
            Expect("within"); var window = Window(); Expect(";"); Expect("group"); Expect("by");
            var keys = new List<DslExpr> { Expression() }; while (Match(",")) keys.Add(Expression()); Expect(";");
            var columns = new List<string>(); if (Match("columns")) { columns.Add(Identifier().Text); while (Match(",")) columns.Add(Identifier().Text); Expect(";"); }
            Expect("timeline"); Expect("by"); Expect("startedAt"); Expect(";"); Expect("}"); Expect("<end>");
            if (_diagnostics.Count > 0) return new(null, _diagnostics);
            var types = new Binder(fields, Check).Bind(filter, keys, columns);
            return new(new(source, nameText, fields.AsReadOnly(), filter, window, keys.AsReadOnly(), columns.AsReadOnly(), types), []);
        }
        private AnalysisWindow Window()
        {
            var start = Current.Span; AnalysisWindow window;
            if (Match("last"))
            {
                Expect("("); var n = Take();
                if (!int.TryParse(n.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var count)) Fail("DSL010", "时长需要正整数。", n.Span);
                var unit = Take(); double seconds = unit.Text switch { "s" => count, "m" => count * 60d, "h" => count * 3600d, "d" => count * 86400d, _ => -1 };
                Expect(")"); window = new(TimeSpan.FromSeconds(seconds));
            }
            else
            {
                Expect("range"); Expect("("); var from = Date(String(), start); Expect(","); var to = Date(String(), start); Expect(")"); window = new(null, from, to);
            }
            try { window.Validate(); } catch (ArgumentException) { Fail("DSL010", "窗口需带时区且为七天内正区间。", start); }
            return window;
        }
        private static DateTimeOffset Date(string text, SourceSpan span)
        {
            if (!(text.EndsWith('Z') || text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-') ||
                !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            { Fail("DSL010", "时间必须包含 Z 或明确的时区偏移。", span); return default; }
            return value.ToUniversalTime();
        }
        private DslExpr Expression(int precedence = 0)
        {
            if (++_depth > DslLimits.Depth) Fail("DSL009", "表达式深度超过32。", Current.Span);
            try
            {
                var left = Primary();
                while (!Current.IsString && Precedence(Current.Text) is var p && p > precedence)
                {
                    var op = Take(); var right = Expression(p); left = new BinaryExpr(op.Text, left, right, op.Span);
                    if (p == 3 && Precedence(Current.Text) == 3) Fail("DSL003", "比较运算不能连续书写。", Current.Span);
                }
                return left;
            }
            finally { _depth--; }
        }
        private static int Precedence(string text) => text switch { "or" => 1, "and" => 2, "==" or "!=" or "<" or "<=" or ">" or ">=" or "in" => 3, _ => 0 };
        private DslExpr Primary()
        {
            var t = Take(); if (t.IsString) return new LiteralExpr(FieldValue.Text(t.StringValue!), t.Span);
            if (t.Text == "(") { var result = Expression(); Expect(")"); return result; }
            if (t.Text == "not") { if (Current.Text == "not") Fail("DSL003", "重复not需要括号。", Current.Span); return new UnaryExpr(Expression(3), t.Span); }
            if (t.Text is "true" or "false") return new LiteralExpr(FieldValue.Boolean(t.Text == "true"), t.Span);
            if (t.Text == "null") return new LiteralExpr(FieldValue.Problem(ValueState.Null, DslType.Null), t.Span);
            if (char.IsAsciiDigit(t.Text[0]) || t.Text[0] == '-')
            {
                if (!DslNumbers.TryParse(t.Text, out var number)) Fail("DSL002", "数字必须为无损的有限十进制定点数。", t.Span);
                return new LiteralExpr(FieldValue.Number(number), t.Span);
            }
            if (t.Text == "[")
            {
                var items = Arguments("]"); if (items.Any(x => x is not LiteralExpr) || items.Count > DslLimits.CollectionItems) Fail("DSL005", "列表只能包含最多128个同类字面量。", t.Span);
                return new ListExpr(items, t.Span);
            }
            if (t.Text == "original")
            {
                Expect("."); var member = Identifier(); string? argument = null;
                if (member.Text is "query" or "headers" or "cookies" or "form") { Expect("["); argument = String(); Expect("]"); }
                else if (member.Text is "json" or "pathSegment") { Expect("("); argument = member.Text == "json" ? String() : Take().Text; Expect(")"); }
                else if (member.Text is not ("host" or "method" or "path")) Fail("DSL008", "不支持的 original 访问器。", member.Span);
                if (member.Text == "pathSegment" && (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)) Fail("DSL005", "路径下标必须为非负整数。", member.Span);
                if (member.Text == "json" && !JsonFieldPath.TryParse(argument!, out _)) Fail("DSL008", "JSON路径仅支持属性和固定数组下标，最多16段。", member.Span);
                return new AccessExpr(member.Text, argument, t.Span);
            }
            if (t.Text is "source" or "statusCode") return new AccessExpr(t.Text, null, t.Span);
            if (Match("(")) return new CallExpr(t.Text, Arguments(")"), t.Span);
            if (!char.IsAsciiLetter(t.Text[0]) && t.Text[0] != '_') Fail("DSL003", "此处需要表达式。", t.Span);
            return new NameExpr(t.Text, t.Span);
        }
        private IReadOnlyList<DslExpr> Arguments(string end)
        {
            var args = new List<DslExpr>(); if (!Match(end)) { do { args.Add(Expression()); if (args.Count > 128) Fail("DSL009", "参数数量超限。", Current.Span); } while (Match(",")); Expect(end); }
            return args.AsReadOnly();
        }
    }
    internal static readonly HashSet<string> Functions = ["first", "all", "firstNonEmpty", "if", "trim", "lower", "upper", "contains", "startsWith", "endsWith", "toString", "toNumber", "toBool", "isMissing", "isNull", "tuple"];
    private static readonly HashSet<string> Reserved = new(Functions.Concat(["dsl", "field", "view", "where", "within", "group", "by", "columns", "timeline", "startedAt", "source", "statusCode", "original", "true", "false", "null", "and", "or", "not", "in", "last", "range", "route", "rewrite", "send", "effective", "response"]));

    private sealed class Binder(IReadOnlyList<FieldDefinition> fields, Action check)
    {
        private readonly Dictionary<string, DslType> _types = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);
        private Dictionary<string, FieldDefinition> _fields = [];
        public IReadOnlyDictionary<string, DslType> Bind(DslExpr filter, IReadOnlyList<DslExpr> keys, IReadOnlyList<string> columns)
        {
            foreach (var field in fields) if (!_fields.TryAdd(field.Name, field)) throw new SyntaxFailure("DSL006", "字段名称重复。", field.Span);
            foreach (var field in fields) Field(field.Name, field.Span);
            Require(Type(filter) == DslType.Boolean, filter, "where 必须为布尔条件。");
            Require(keys.Count is >= 1 and <= 4, filter, "分组最多4个分量。");
            foreach (var key in keys) { var type = Type(key); Require(type.IsScalar || keys.Count == 1 && type == DslType.Tuple, key, "分组需要确定标量或一个 tuple。"); }
            Require(columns.Count <= 8 && columns.Distinct().Count() == columns.Count, filter, "显示列最多8个且不能重复。");
            foreach (var name in columns) Field(name, filter.Span);
            return new System.Collections.ObjectModel.ReadOnlyDictionary<string, DslType>(_types);
        }
        private DslType Field(string name, SourceSpan span)
        {
            check(); if (_types.TryGetValue(name, out var cached)) return cached;
            if (!_fields.TryGetValue(name, out var field)) throw new SyntaxFailure("DSL004", $"未知字段：{name}。", span);
            if (!_visiting.Add(name)) throw new SyntaxFailure("DSL007", $"字段存在依赖环：{string.Join(" → ", _visiting)} → {name}。", span);
            var type = Type(field.Expression); _visiting.Remove(name); return _types[name] = type;
        }
        private static void Require(bool condition, DslExpr expr, string message)
        { if (!condition) throw new SyntaxFailure("DSL005", message, expr.Span); }
        private DslType Type(DslExpr expr, int depth = 0)
        {
            check(); if (depth > DslLimits.Depth) throw new SyntaxFailure("DSL009", "AST深度超过32。", expr.Span);
            switch (expr)
            {
                case LiteralExpr literal: Require(literal.Value.State != ValueState.BudgetExceeded, expr, "字面量长度超限。"); return literal.Value.Type;
                case NameExpr name: return Field(name.Name, name.Span);
                case AccessExpr access: return access.Name switch { "query" or "headers" or "cookies" or "form" => DslType.Strings, "json" => DslType.Json, "statusCode" => DslType.Number, _ => DslType.String };
                case ListExpr list:
                    var types = list.Values.Select(x => Type(x, depth + 1)).ToArray();
                    Require(types.All(t => t.IsScalar && t == types[0]), expr, "列表需为同类型非 null 字面量。"); return new(ValueKind.List, types.FirstOrDefault() ?? DslType.Null);
                case UnaryExpr unary: Require(Type(unary.Operand, depth + 1) == DslType.Boolean, expr, "not 需要布尔值。"); return DslType.Boolean;
                case BinaryExpr binary:
                    var left = Type(binary.Left, depth + 1); var right = Type(binary.Right, depth + 1);
                    Require(binary.Operator switch
                    {
                        "and" or "or" => left == DslType.Boolean && right == DslType.Boolean,
                        "in" => left.IsScalar && right.Kind == ValueKind.List && (right.Element == left || right.Element == DslType.Null),
                        "==" or "!=" => left.IsScalar && left == right,
                        _ => left == DslType.Number && right == DslType.Number
                    }, expr, "操作数类型不匹配；请显式转换或使用 isNull。"); return DslType.Boolean;
                case CallExpr call:
                    if (!Functions.Contains(call.Name)) throw new SyntaxFailure("DSL004", "未知函数。", call.Span);
                    var a = call.Arguments.Select(x => Type(x, depth + 1)).ToArray();
                    Require(call.Name switch { "tuple" => a.Length is >= 2 and <= 4, "firstNonEmpty" => a.Length is >= 2 and <= 8, "if" => a.Length == 3, "contains" or "startsWith" or "endsWith" => a.Length == 2, _ => a.Length == 1 }, expr, "函数参数数量错误。");
                    if (call.Name is "isMissing" or "isNull") return DslType.Boolean;
                    if (call.Name == "tuple") { Require(a.All(t => t.IsScalar), expr, "tuple仅接受标量，不可嵌套。"); return DslType.Tuple; }
                    if (call.Name is "first" or "all") { Require(a[0].Kind is ValueKind.List or ValueKind.Json, expr, "需要列表。"); return call.Name == "first" ? a[0].Element ?? DslType.Json : a[0]; }
                    if (call.Name == "if") { Require(a[0] == DslType.Boolean && (a[1] == a[2] || a[1] == DslType.Null || a[2] == DslType.Null), expr, "if条件或分支类型不一致。"); return a[1] == DslType.Null ? a[2] : a[1]; }
                    if (call.Name == "firstNonEmpty") { var t = a.FirstOrDefault(t => t != DslType.Null) ?? DslType.String; Require(t.IsScalar && a.All(x => x == t || x == DslType.Null), expr, "回退参数须为同类型标量。"); return t; }
                    if (call.Name.StartsWith("to", StringComparison.Ordinal))
                    {
                        // 静态类型已经证明不可能成功的转换在执行前报错。
                        // JSON的真实标量类型只能在读取记录后判断，仍由求值器给出Invalid。
                        var allowed = a[0] == DslType.Json || a[0] == DslType.String ||
                            call.Name == "toString" && a[0].IsScalar ||
                            call.Name == "toNumber" && a[0] == DslType.Number ||
                            call.Name == "toBool" && a[0] == DslType.Boolean;
                        Require(allowed, expr, "转换参数类型不匹配；数值不能直接转布尔。");
                        return call.Name switch { "toNumber" => DslType.Number, "toBool" => DslType.Boolean, _ => DslType.String };
                    }
                    Require(a.All(t => t == DslType.String), expr, "字符串函数需要String参数。");
                    return call.Name is "contains" or "startsWith" or "endsWith" ? DslType.Boolean : DslType.String;
                default: throw new InvalidOperationException("未知AST节点。");
            }
        }
    }
}

public static class DslNumbers
{
    public static bool TryParse(string text, out decimal value)
    {
        value = 0; var digits = text.TrimStart('-');
        if (digits.Length == 0 || digits.Count(c => c == '.') > 1 || digits.Any(c => !char.IsAsciiDigit(c) && c != '.') || digits.StartsWith('.') || digits.EndsWith('.')) return false;
        var point = digits.IndexOf('.'); if (point >= 0 && digits.Length - point - 1 > 28 || digits.Replace(".", "").TrimStart('0').Length > 28) return false;
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
}
