using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NetWorkTrafficPlugin.Application.Dsl;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Application.Analysis;

/// <summary>
/// 顺序扫描冻结保留集，按实际依赖求值。候选全部成功才交给页面，超限不会伪装成前N组成功。
/// 统计桶互斥且可复算；窗口外上下文只投影，不再次进入主统计。
/// </summary>
public sealed class AnalysisQueryEngine(AnalysisResources resources)
{
    private readonly byte[] _keySalt = RandomNumberGenerator.GetBytes(32);
    /// <summary>
    /// 预览与正式查询共享并发/内存预算和字段管线，仅输出最多20条有界的遮罩文本。
    /// 预览不创建可应用结果；编排层负责取消和代次，应用层负责资源，不把这些规则放进控件。
    /// </summary>
    public async Task<string> PreviewAsync(IAnalysisSource source, DslProgram program, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var ct = deadline.Token;
        using var slot = resources.Enter();
        using var snapshot = await source.FreezeAsync(ct);
        using var allocations = new AnalysisReservations(resources.Memory);
        allocations.Add(SnapshotCost(snapshot));
        // 最坏20条 × 32字段 ×（值4096字符+来源8条），提前预留输出空间。
        allocations.Add(20L * 32 * 16384);
        var lines = new List<string>();
        var work = new EvaluationBudget(ct, 2000);
        foreach (var record in snapshot.Records.OrderBy(r => r.StartedUtc).ThenBy(r => r.Sequence).ThenBy(r => r.RecordId).Take(20))
        {
            work.Check();
            var fields = snapshot.OpenFields(record, work);
            try
            {
                var evaluator = new FieldEvaluator(program, fields, work, ct);
                var condition = await evaluator.EvaluateAsync(program.Filter);
                lines.Add($"#{record.Sequence} · 条件 {condition.Truth?.ToString() ?? "Unknown"}");
                foreach (var definition in program.Fields)
                {
                    var value = await evaluator.FieldAsync(definition.Name);
                    lines.Add($"  {definition.Name}: {value.Type.Kind} / {value.State} = {value.Display()} · " +
                        string.Join(", ", (value.Origins ?? []).Select(o => $"{o.Location}[{o.Path}]")));
                }
            }
            finally { (fields as IDisposable)?.Dispose(); }
        }
        return $"样本 {Math.Min(20, snapshot.Records.Count)} / 保留 {snapshot.Records.Count}（非全量统计）\n" + string.Join('\n', lines);
    }
    private static long SnapshotCost(AnalysisSnapshot snapshot) => snapshot.Records.Sum(r => 512L + 2L * (r.OriginalUri.Length + r.EffectiveUri.Length) +
        r.RequestHeaders.Concat(r.ResponseHeaders).Sum(h => 64L + 2L * (h.Name.Length + h.Value.Length)));
    public async Task<AnalysisResult> RunAsync(IAnalysisSource source, DslProgram program, Guid revisionId, string ruleHash,
        AnalysisOptions options, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = deadline.Token; using var slot = resources.Enter(); var work = new EvaluationBudget(ct);
        var allocations = new AnalysisReservations(resources.Memory);
        try
        {
            using var snapshot = await source.FreezeAsync(ct); work.Check();
            if (snapshot.Records.Count > DslLimits.Records) throw new AnalysisLimitException("保留记录超过10,000条，无法完整分析。");
            // 快照头数组也计入候选寿命的额度；不能只计算最终短摘要而漏掉冻结阶段峰值。
            allocations.Add(SnapshotCost(snapshot));
            var (from, to) = (options.Window ?? program.Window).Resolve(snapshot.Anchor);
            var columns = options.Columns ?? program.Columns.ToArray();
            if (columns.Length > 8 || columns.Distinct().Count() != columns.Length || columns.Any(c => !program.FieldTypes.ContainsKey(c))) throw new ArgumentException("显示列必须引用最多8个不同的字段。");
            var sources = options.Sources ?? ["capture"];
            if (sources.Length == 0 || sources.Any(s => s is not ("capture" or "manual" or "replay" or "unknown"))) throw new ArgumentException("来源选择无效。");
            var groups = new Dictionary<string, (IReadOnlyList<FieldValue> Key, List<AnalyzedRequest> Rows)>(StringComparer.Ordinal);
            var unknown = new List<AnalyzedRequest>(); var context = new List<AnalyzedRequest>(); var seen = new HashSet<Guid>();
            int sourceExcluded = 0, outside = 0, filterFalse = 0, filterUnknown = 0, filterTrue = 0, grouped = 0;
            foreach (var record in snapshot.Records)
            {
                work.Check(); if (!seen.Add(record.RecordId)) throw new InvalidDataException("快照包含重复记录身份，拒绝不确定修订。");
                if (!sources.Contains(snapshot.Source(record), StringComparer.Ordinal)) { sourceExcluded++; continue; }
                bool inWindow = record.StartedUtc >= from && record.StartedUtc < to;
                if (!inWindow) outside++;
                var overlap = options.IncludeContext && record.StartedUtc < from && snapshot.Anchor >= from &&
                    (record.ResponseBody.Transport == TransportState.Receiving || double.IsFinite(record.ElapsedMilliseconds) && record.ElapsedMilliseconds >= 0 &&
                     (from - record.StartedUtc).TotalMilliseconds < record.ElapsedMilliseconds);
                if (!inWindow && !overlap) continue;
                var fields = snapshot.OpenFields(record, work);
                try
                {
                    var evaluator = new FieldEvaluator(program, fields, work, ct); var condition = await evaluator.EvaluateAsync(program.Filter);
                    if (condition.Truth != true) { if (inWindow) { if (condition.Truth == false) filterFalse++; else filterUnknown++; } continue; }
                    if (inWindow) filterTrue++;
                    var keys = new List<FieldValue>(); foreach (var key in program.GroupKeys) keys.Add(await evaluator.EvaluateAsync(key));
                    if (keys.Count == 1 && keys[0].State == ValueState.Value && keys[0].Type == DslType.Tuple) keys = keys[0].ItemsValue.ToList();
                    var values = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
                    foreach (var name in columns) values[name] = await evaluator.FieldAsync(name);
                    var unresolved = keys.Any(k => k.State != ValueState.Value) ? string.Join(" / ", keys.Select(k => k.State.ToString())) : null;
                    allocations.Add(1024 + record.OriginalUri.Length * 2L + values.Sum(kv => kv.Key.Length * 2L + Cost(kv.Value)) + keys.Sum(Cost));
                    var uri = new Uri(record.OriginalUri);
                    var row = new AnalyzedRequest(record.RecordId, record.Sequence, record.StartedUtc, record.ElapsedMilliseconds, record.Method,
                        uri.Host, uri.AbsolutePath, record.StatusCode, record.ResponseBody.Transport, values, unresolved, !inWindow, record.CompletedUtc);
                    if (!inWindow) { context.Add(row); continue; }
                    if (unresolved is not null) { unknown.Add(row); continue; }
                    var canonical = Canonical(keys);
                    if (!groups.TryGetValue(canonical, out var group))
                    {
                        if (groups.Count == DslLimits.Groups) throw new AnalysisLimitException("业务组超过5,000个；请缩小窗口或减少键基数。");
                        allocations.Add(512 + canonical.Length * 2L); group = (keys.AsReadOnly(), []); groups.Add(canonical, group);
                    }
                    group.Rows.Add(row); grouped++;
                }
                finally { (fields as IDisposable)?.Dispose(); }
            }
            work.Check();
            var output = groups.OrderBy(g => g.Value.Rows.Min(r => r.StartedUtc)).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select((g, index) => new TrafficGroup(Convert.ToHexString(HMACSHA256.HashData(_keySalt, Encoding.UTF8.GetBytes(source.InstanceId + ruleHash + g.Key))), $"组 {index + 1}", g.Value.Key, Sort(g.Value.Rows))).ToArray();
            if (options.SortByCount) output = output.OrderByDescending(g => g.Count).ThenBy(g => g.FirstStarted).ThenBy(g => g.Alias, StringComparer.Ordinal).ToArray();
            var coverage = new Coverage(snapshot.Records.Count, sourceExcluded, outside, filterFalse, filterUnknown, filterTrue, grouped, unknown.Count, snapshot.Metadata.Total, snapshot.Metadata.Evicted);
            return new(Guid.NewGuid(), source.InstanceId, snapshot.Metadata.SessionId, snapshot.Metadata.Revision, revisionId, ruleHash, snapshot.Anchor, from, to,
                output, Sort(unknown), Sort(context), coverage, work, allocations);
        }
        catch { allocations.Dispose(); throw; }
    }
    private static AnalyzedRequest[] Sort(IEnumerable<AnalyzedRequest> rows) => rows.OrderBy(r => r.StartedUtc).ThenBy(r => r.Sequence).ThenBy(r => r.RecordId).ToArray();
    private static long Cost(FieldValue value) => 160 + (value.Data switch { string s => s.Length * 2L, IReadOnlyList<FieldValue> items => items.Sum(Cost), _ => 32L }) + (value.Origins?.Sum(o => 64L + (o.Path.Length + o.Location.Length) * 2L) ?? 0);
    /// <summary>长度与类型均进入键，禁止使用人类显示文本或分隔符拼接来决定相等。</summary>
    public static string Canonical(IEnumerable<FieldValue> keys) => string.Concat(keys.Select(k =>
    {
        var value = k.Type.Kind switch { ValueKind.Number => ((decimal)k.Data!).ToString("G29", CultureInfo.InvariantCulture), ValueKind.Boolean => (bool)k.Data! ? "1" : "0", _ => (string)k.Data! };
        return $"{(int)k.Type.Kind}:{value.Length}:{value}";
    }));
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
