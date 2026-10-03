using System.Globalization;
using NetWorkTrafficPlugin.Application.Rewrite;

namespace NetWorkTrafficPlugin.Application.Dsl;

internal static partial class DslSyntax
{
    internal sealed partial class Parser
    {
        /// <summary>v2 顶层只组织改写语句；词法、表达式、类型推导与 v1 共享，权限检查在绑定完成后执行。</summary>
        internal RewriteCompilation CompileRewrite()
        {
            Lex(); Expect("dsl"); var version = Take();
            if (version.Text != "2" || version.IsString) Fail("RW001", "改写需要 dsl 2;，分析请使用 v1 编辑器。", version.Span);
            Expect(";"); var fields = new List<FieldDefinition>();
            while (Match("field"))
            {
                var name = Identifier();
                if (Reserved.Contains(name.Text) || name.Text is "priority" or "for" or "when" or "set" or "remove")
                    Fail("DSL004", "字段名不能使用保留名。", name.Span);
                Expect("="); var expression = Expression(); Expect(";"); fields.Add(new(name.Text, expression, name.Span));
                if (fields.Count > DslLimits.Fields) Fail("RW009", "字段数量超过32。", name.Span);
            }
            var rules = new List<RewriteRule>(); var total = 0;
            while (Current.Text != "<end>")
            {
                var start = Expect("rewrite").Span; var name = String(); Expect("{"); Expect("priority"); var number = Take();
                if (!int.TryParse(number.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var priority) || number.IsString || priority > 9999)
                    Fail("RW002", "优先级必须为0～9999的整数。", number.Span);
                Expect(";"); Expect("for"); Expect("host"); var hostSpan = Current.Span; var host = String(); Expect(";");
                try { host = RewriteValidation.Host(host); } catch (Exception ex) when (ex is RewriteFailure or ArgumentException) { Fail("RW005", "无效的精确主机作用域。", hostSpan); }
                Expect("when"); var condition = Expression(); Expect(";"); var actions = new List<RewriteAction>();
                while (Current.Text is not ("}" or "<end>"))
                {
                    var action = RewriteAction();
                    if (actions.Any(a => RewriteValidation.Conflicts(a, action)) || action.Kind == RewriteActionKind.Route && actions.Any(a => a.Kind == RewriteActionKind.Route))
                        Fail("RW007", "同一规则存在重复路由或冲突写入。", action.Span);
                    actions.Add(action);
                    if (actions.Count > RewriteLimits.ActionsPerRule || ++total > RewriteLimits.Actions) Fail("RW009", "动作数量超过预算。", action.Span);
                }
                Expect("}");
                if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || actions.Count == 0 || rules.Any(r => r.Name == name || r.Priority == priority))
                    Fail("RW002", "规则名和优先级需唯一，名称非空且最多120字符，至少一个动作。", start);
                rules.Add(new(name, priority, host, condition, actions.AsReadOnly(), start));
                if (rules.Count > RewriteLimits.Rules) Fail("RW009", "规则数量超过32。", start);
            }
            if (rules.Count == 0) Fail("RW001", "至少需要一条 rewrite 规则。", Current.Span);
            var roots = fields.Select(f => f.Expression).Concat(rules.Select(r => r.Condition))
                .Concat(rules.SelectMany(r => r.Actions).Where(a => a.Value is not null).Select(a => a.Value!)).ToArray();
            var binder = new Binder(fields.AsReadOnly(), Check); binder.BindExpressions(roots);
            var types = binder.ExpressionTypes;
            foreach (var expression in roots) ValidateRequestExpression(expression);
            foreach (var rule in rules)
            {
                if (types[rule.Condition] != DslType.Boolean) Fail("RW004", "when 必须为布尔条件。", rule.Condition.Span);
                foreach (var action in rule.Actions.Where(a => a.Value is not null))
                {
                    var type = types[action.Value!];
                    if (action.Kind == RewriteActionKind.SetJson ? !(type.IsScalar || type == DslType.Null) : type != DslType.String)
                        Fail("RW004", "动作值类型不匹配；字符串动作需要显式转换，JSON仅接受标量或null。", action.Value!.Span);
                }
            }
            return new(new(source, new(fields.AsReadOnly(), types), rules.OrderBy(r => r.Priority).ToArray().AsReadOnly()), []);
        }

        private RewriteAction RewriteAction()
        {
            var start = Current.Span; RewriteActionKind kind; string target; DslExpr? value = null;
            if (Match("route")) { Expect("to"); target = String(); kind = RewriteActionKind.Route; }
            else
            {
                var remove = Match("remove"); if (!remove) Expect("set");
                var location = Take();
                if (location.Text == "path" && !location.IsString && !remove) { target = ""; kind = RewriteActionKind.Path; value = Expression(); }
                else if (location.Text is "query" or "header" && !location.IsString)
                {
                    Expect("["); target = String(); Expect("]");
                    kind = location.Text == "query" ? remove ? RewriteActionKind.RemoveQuery : RewriteActionKind.SetQuery : remove ? RewriteActionKind.RemoveHeader : RewriteActionKind.SetHeader;
                    if (!remove) { Expect("="); value = Expression(); }
                }
                else if (location.Text == "json" && !location.IsString)
                {
                    Expect("("); target = String(); Expect(")"); kind = remove ? RewriteActionKind.RemoveJson : RewriteActionKind.SetJson;
                    if (!remove) { Expect("="); value = Expression(); }
                }
                else { Fail("RW003", "不支持的改写动作。", location.Span); return null!; }
            }
            Expect(";");
            try
            {
                if (kind == RewriteActionKind.Route) RewriteValidation.ValidateUri(target, true);
                if (kind is RewriteActionKind.SetHeader or RewriteActionKind.RemoveHeader) RewriteValidation.Header(target, (value as LiteralExpr)?.Value.Data as string);
                if (kind is RewriteActionKind.SetJson or RewriteActionKind.RemoveJson) RewriteValidation.JsonPath(target, kind == RewriteActionKind.RemoveJson);
                if (kind == RewriteActionKind.Path && value is LiteralExpr { Value.Data: string path }) RewriteValidation.Path(path);
            }
            catch (RewriteFailure ex) { Fail(ex.Code, ex.Message, start); }
            catch (FormatException) { Fail("RW005", "路径转义无效。", start); }
            return new(kind, target, value, start);
        }
        private static void ValidateRequestExpression(DslExpr expr)
        {
            if (expr is AccessExpr { Name: "statusCode" }) throw new SyntaxFailure("RW003", "发送前不能读取响应状态。", expr.Span);
            IEnumerable<DslExpr> children = expr switch
            {
                BinaryExpr b => [b.Left, b.Right], UnaryExpr u => [u.Operand], CallExpr c => c.Arguments, ListExpr l => l.Values, _ => []
            };
            foreach (var child in children) ValidateRequestExpression(child);
        }
    }
}
