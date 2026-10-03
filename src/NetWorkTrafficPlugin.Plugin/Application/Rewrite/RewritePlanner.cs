using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Dsl;

namespace NetWorkTrafficPlugin.Application.Rewrite;

/// <summary>
/// 只生成候选，不持网络发送能力。所有条件/值从原始字段源读取，全部校验成功后才返回完整候选。
/// 首路由选择与字段写冲突分别处理，避免把优先级误用成静默覆盖，或在失败时发出半份修改。
/// </summary>
public static class RewritePlanner
{
    public static async Task<RewriteResult> PlanAsync(RewriteProgram program, RewriteInput original, RewriteFieldSource fields, CancellationToken token = default)
    {
        var traces = new List<RewriteRuleTrace>();
        try
        {
            var clock = Stopwatch.StartNew();
            var evaluator = new FieldEvaluator(program.Expressions, fields, new EvaluationBudget(token, RewriteLimits.BodyMilliseconds + RewriteLimits.MetadataMilliseconds, RewriteLimits.Operations), token);
            var actions = new List<(RewriteAction Action, FieldValue? Value)>();
            var sourceUri = RewriteValidation.ValidateUri(original.Uri); var host = RewriteValidation.Host(sourceUri.IdnHost);
            var routed = false;
            foreach (var rule in program.Rules)
            {
                token.ThrowIfCancellationRequested(); if (rule.Host != host) continue;
                var condition = await evaluator.EvaluateAsync(rule.Condition);
                traces.Add(new(rule.Name, rule.Priority, condition.Truth?.ToString() ?? $"Unknown:{condition.State}"));
                RejectBudgetValue(condition);
                if (condition.Truth is null) throw new RewriteFailure("RW008", $"规则“{rule.Name}”条件未确定（{condition.State}），已阻断。");
                if (!condition.Truth.Value) continue;
                foreach (var action in rule.Actions)
                {
                    if (action.Kind == RewriteActionKind.Route)
                    {
                        traces[^1] = traces[^1] with { RouteSelected = !routed, RouteShadowed = routed };
                        if (routed) continue; routed = true;
                    }
                    if (actions.Any(a => RewriteValidation.Conflicts(a.Action, action)))
                        throw new RewriteFailure("RW007", $"命中规则写入冲突：{action.Kind}。");
                    var value = action.Value is null ? null : await evaluator.EvaluateAsync(action.Value);
                    if (value is not null) RejectBudgetValue(value);
                    if (value is not null && value.State != ValueState.Value && !(action.Kind == RewriteActionKind.SetJson && value.State == ValueState.Null))
                        throw new RewriteFailure("RW008", $"动作值未确定（{value.State}）。");
                    actions.Add((action, value));
                }
            }
            var uri = original.Uri; var headers = original.Headers.ToList(); byte[]? body = null;
            var differences = new List<RewriteDifference>();
            foreach (var (action, value) in actions.OrderBy(a => Order(a.Action.Kind)))
            {
                token.ThrowIfCancellationRequested(); var text = value?.Data as string;
                var beforeUri = uri; var beforeHeaders = headers.ToArray();
                if (action.Kind == RewriteActionKind.Route)
                {
                    var target = RewriteValidation.ValidateUri(action.Target, true);
                    if (target.Scheme != sourceUri.Scheme) throw new RewriteFailure("RW005", "首版路由必须保持原始协议。");
                    var suffix = uri.IndexOfAny(['/', '?'], uri.IndexOf("://", StringComparison.Ordinal) + 3);
                    var tail = suffix < 0 ? "/" : uri[suffix..]; if (tail.StartsWith('?')) tail = "/" + tail;
                    uri = target.GetLeftPart(UriPartial.Authority) + tail;
                }
                else if (action.Kind == RewriteActionKind.Path)
                {
                    RewriteValidation.Path(text!); var parsed = new Uri(uri); var q = uri.IndexOf('?');
                    uri = parsed.GetLeftPart(UriPartial.Authority) + text + (q < 0 ? "" : uri[q..]);
                }
                else if (action.Kind is RewriteActionKind.SetQuery or RewriteActionKind.RemoveQuery)
                    uri = ChangeQuery(uri, action.Target, text, action.Kind == RewriteActionKind.RemoveQuery);
                else if (action.Kind is RewriteActionKind.SetHeader or RewriteActionKind.RemoveHeader)
                {
                    RewriteValidation.Header(action.Target, text);
                    if (original.Headers.Where(h => h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(h => h.Value.Split(',')).Any(n => n.Trim().Equals(action.Target, StringComparison.OrdinalIgnoreCase)))
                        throw new RewriteFailure("RW006", "不能修改 Connection 指定的逐跳字段。");
                    var first = headers.FindIndex(h => h.Name.Equals(action.Target, StringComparison.OrdinalIgnoreCase));
                    headers.RemoveAll(h => h.Name.Equals(action.Target, StringComparison.OrdinalIgnoreCase));
                    if (action.Kind == RewriteActionKind.SetHeader) headers.Insert(first < 0 ? headers.Count : first, new(action.Target, text!));
                }
                // JSON 动作统一在一个候选树内执行，避免每次重新解析或逐步覆盖原始正文。
                if (action.Kind is not (RewriteActionKind.SetJson or RewriteActionKind.RemoveJson))
                {
                    // 每步限制候选，防止一组动作先把 URI/头放大，再到最终提交处才发现超限。
                    _ = RewriteInput.Create(original.Method, uri, headers);
                    differences.Add(new(action.Kind.ToString(), action.Target, beforeUri == uri && beforeHeaders.SequenceEqual(headers) ? "NoOp" : "已修改"));
                }
            }
            var jsonActions = actions.Where(a => a.Action.Kind is RewriteActionKind.SetJson or RewriteActionKind.RemoveJson).ToArray();
            if (jsonActions.Length > 0)
            {
                var json = await fields.JsonAsync(token);
                var candidate = JsonNode.Parse(json.RootElement.GetRawText(), documentOptions: new JsonDocumentOptions { MaxDepth = DslLimits.JsonDepth });
                var bodyChanged = false;
                foreach (var (action, value) in jsonActions)
                {
                    token.ThrowIfCancellationRequested(); var changed = ChangeJson(candidate, action, value);
                    bodyChanged |= changed;
                    differences.Add(new("Json", action.Target, changed ? "已修改" : "NoOp"));
                }
                if (bodyChanged)
                {
                    using var output = new BoundedJsonOutput();
                    using (var writer = new Utf8JsonWriter(output)) { if (candidate is null) writer.WriteNullValue(); else candidate.WriteTo(writer); }
                    body = output.ToArray();
                    headers.RemoveAll(h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || h.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                        new[] { "Content-MD5", "Digest", "Content-Digest", "Repr-Digest" }.Contains(h.Name, StringComparer.OrdinalIgnoreCase));
                    headers.Add(new("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
            if (routed)
            {
                headers.RemoveAll(h => h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase));
                headers.Add(new("Host", new Uri(uri).Authority));
            }
            var validated = RewriteInput.Create(original.Method, uri, headers);
            fields.CheckDeadline();
            if (!fields.BodyRead && clock.ElapsedMilliseconds > RewriteLimits.MetadataMilliseconds)
                throw new RewriteFailure("RW009", "元信息改写超过50毫秒预算。");
            token.ThrowIfCancellationRequested();
            var plan = new RewritePlan(validated.Uri, validated.Headers, body, traces.AsReadOnly(), differences.AsReadOnly(), actions.Count > 0);
            return new(plan, null, null, 0, traces.AsReadOnly());
        }
        catch (RewriteFailure ex) { return new(null, ex.Code, ex.Message, ex.Status, traces.AsReadOnly()); }
        catch (AnalysisLimitException) { return new(null, "RW009", "改写求值超过预算。", 504, traces.AsReadOnly()); }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException or InvalidOperationException or IOException)
        { return new(null, "RW008", "原始数据格式或动作目标类型无效。", 502, traces.AsReadOnly()); }
    }
    private static void RejectBudgetValue(FieldValue value)
    {
        if (value.State == ValueState.BudgetExceeded)
            throw new RewriteFailure("RW009", "字段值或集合超过预算。", 413);
    }
    private static int Order(RewriteActionKind action) => action switch
    {
        RewriteActionKind.Route => 0, RewriteActionKind.Path => 1, RewriteActionKind.SetQuery or RewriteActionKind.RemoveQuery => 2,
        RewriteActionKind.SetHeader or RewriteActionKind.RemoveHeader => 3, _ => 4
    };
    private static string ChangeQuery(string uri, string key, string? value, bool remove)
    {
        var start = uri.IndexOf('?'); var prefix = start < 0 ? uri : uri[..start];
        var result = new List<string>(); var inserted = false;
        foreach (var item in start < 0 || start == uri.Length - 1 ? [] : uri[(start + 1)..].Split('&'))
        {
            var eq = item.IndexOf('='); var name = RequestFieldText.Decode(eq < 0 ? item : item[..eq], false);
            // 修改 Query 时必须确认所有键值可解析；保留的项目仍用原文本，不重新编码签名输入。
            if (eq >= 0) _ = RequestFieldText.Decode(item[(eq + 1)..], false);
            if (name != key) { result.Add(item); continue; }
            if (!inserted && !remove) { result.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value!)); inserted = true; }
        }
        if (!inserted && !remove) result.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value!));
        return prefix + (result.Count == 0 ? "" : "?" + string.Join('&', result));
    }
    private static bool ChangeJson(JsonNode? root, RewriteAction action, FieldValue? value)
    {
        var path = RewriteValidation.JsonPath(action.Target, action.Kind == RewriteActionKind.RemoveJson); var parent = root;
        foreach (var segment in path.Take(path.Count - 1))
            parent = segment switch
            {
                string name when parent is JsonObject obj && obj.TryGetPropertyValue(name, out var child) => child,
                int index when parent is JsonArray array && index < array.Count => array[index],
                _ => throw new RewriteFailure("RW008", "JSON父路径不存在或类型不匹配。")
            };
        JsonNode? replacement = value?.State == ValueState.Null ? null : value?.Data switch
        { string s => JsonValue.Create(s), decimal n => JsonValue.Create(n), bool b => JsonValue.Create(b), _ => null };
        if (path[^1] is string key && parent is JsonObject target)
        {
            if (action.Kind == RewriteActionKind.RemoveJson) return target.Remove(key);
            var existed = target.TryGetPropertyValue(key, out var previous);
            if (existed && JsonNode.DeepEquals(previous, replacement)) return false;
            target[key] = replacement; return true;
        }
        if (path[^1] is int i && parent is JsonArray list && i < list.Count)
        { if (JsonNode.DeepEquals(list[i], replacement)) return false; list[i] = replacement; return true; }
        throw new RewriteFailure("RW008", "JSON目标不存在或类型不匹配。");
    }
    /// <summary>用组合限制写入。MemoryStream 的 Span 回退会调用虚拟数组重载，继承后互相转调会递归。</summary>
    private sealed class BoundedJsonOutput : Stream
    {
        private readonly MemoryStream _inner = new();
        public byte[] ToArray() => _inner.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > RewriteLimits.BodyBytes) throw new RewriteFailure("RW009", "JSON输出超过256 KiB。", 413);
            _inner.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
