using System.Text;
using System.Text.Json;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Application.Requests;

public sealed record RequestDifference(string Location, string Before, string After);
public sealed record RequestComparisonResult(IReadOnlyList<RequestDifference> Differences, bool Complete, string Message);

/// <summary>
/// 对比不持发送能力。按位置比较文本行、按属性名比较对象、按下标比较数组，避免为大文本分配二次方矩阵。
/// “已保存前缀相同”不等于“完整正文相同”；完整性单独传入且参与最后结论。
/// </summary>
public static class RequestComparison
{
    public static RequestComparisonResult Compare(TrafficRecord before, byte[] beforeBody, TrafficRecord after, byte[] afterBody, CancellationToken token,
        byte[]? beforeRequest = null, byte[]? afterRequest = null)
    {
        var rows = new List<RequestDifference>(); var operations = 0; var complete = true;
        void Add(string path, string left, string right)
        {
            token.ThrowIfCancellationRequested();
            if (++operations > RequestLimits.CompareOperations) { complete = false; return; }
            if (left == right) return;
            if (rows.Count == RequestLimits.CompareDifferences) { complete = false; return; }
            // 单行也有限制；巨长单行不通过差异结果绕过正文输入上限。
            static string Limit(string value) => value.Length <= 1024 ? value : value[..1024] + "…";
            if (left.Length > 1024 || right.Length > 1024) complete = false;
            rows.Add(new(path, Limit(left), Limit(right)));
        }
        Add("方法", before.Method, after.Method); Add("目标", before.EffectiveUri, after.EffectiveUri);
        Add("HTTP状态", before.StatusCode?.ToString() ?? "未知", after.StatusCode?.ToString() ?? "未知");
        Add("总耗时(ms)", before.ElapsedMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), after.ElapsedMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        complete &= Headers("请求头", before.RequestHeaders, after.RequestHeaders, Add);
        complete &= Headers("响应头", before.ResponseHeaders, after.ResponseHeaders, Add);
        complete &= !before.MetadataPartial && !after.MetadataPartial;
        CompareBody("响应正文", before.ResponseBody, beforeBody, after.ResponseBody, afterBody);
        if (beforeRequest is null || afterRequest is null) complete = false;
        else CompareBody("请求正文", before.RequestBody, beforeRequest, after.RequestBody, afterRequest);
        return new(rows, complete, complete ? "已比较两侧完整保存内容；耗时差仅代表本次观测。" : "仅部分对比或正文不可比较，不能推断全文相同。");

        void CompareBody(string prefix, BodyDescriptor leftBody, byte[] leftBytes, BodyDescriptor rightBody, byte[] rightBytes)
        {
        bool Full(BodyDescriptor body, byte[] bytes) => body.Transport == TransportState.Completed &&
            (body.Capture == CaptureState.Complete || body.ExpectedBytes == 0 && body.ObservedBytes == 0) &&
            body.CommittedBytes == body.ObservedBytes && body.CommittedBytes == bytes.Length &&
            (body.ExpectedBytes is null || body.ExpectedBytes == bytes.Length);
        var full = Full(leftBody, leftBytes) && Full(rightBody, rightBytes);
        complete &= full;
        void BodyAdd(string path, string left, string right) => Add(prefix + "/" + path, left, right);
        if (leftBytes.Length > RequestLimits.BodyBytes || rightBytes.Length > RequestLimits.BodyBytes) { complete = false; return; }
        // 编码实体只比较同表示的字节摘要，不擅自解压；完整实体相同也不等于解码语义相同。
        if (leftBody.Representation != rightBody.Representation || leftBody.Representation == BodyRepresentation.Unknown ||
            !string.Equals(leftBody.ContentEncoding ?? "identity", rightBody.ContentEncoding ?? "identity", StringComparison.OrdinalIgnoreCase))
        { complete = false; return; }
        if (leftBody.ContentEncoding is not (null or "" or "identity")) { Binary(leftBytes, rightBytes, full, BodyAdd); return; }
        try
        {
            var encoding = new UTF8Encoding(false, true);
            var left = encoding.GetString(leftBytes); var right = encoding.GetString(rightBytes);
            var leftJson = leftBody.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
            var rightJson = rightBody.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
            var text = leftBody.ContentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true &&
                       rightBody.ContentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true;
            if (leftJson && rightJson && full)
            {
                using var a = RequestValidation.Json(left); using var b = RequestValidation.Json(right);
                Json(a.RootElement, b.RootElement, "$", BodyAdd, token);
            }
            else if (text || leftJson && rightJson)
            {
                var a = left.Split('\n'); var b = right.Split('\n'); var count = Math.Max(a.Length, b.Length);
                if (count > RequestLimits.CompareLines) complete = false;
                for (var i = 0; i < Math.Min(count, RequestLimits.CompareLines); i++) BodyAdd($"行{i + 1}", i < a.Length ? a[i] : "[不存在]", i < b.Length ? b[i] : "[不存在]");
            }
            else Binary(leftBytes, rightBytes, full, BodyAdd);
        }
        catch (DecoderFallbackException) { Binary(leftBytes, rightBytes, full, BodyAdd); }
        catch (RequestFailure) { complete = false; }
        }
    }
    private static void Binary(byte[] a, byte[] b, bool complete, Action<string, string, string> add)
    {
        if (complete) add("完整实体SHA256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(a)), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)));
    }
    private static bool Headers(string name, HeaderField[] a, HeaderField[] b, Action<string, string, string> add)
    {
        var complete = true;
        foreach (var key in a.Concat(b).Select(h => h.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (RequestValidation.IsSensitive(key) || key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            { complete = false; add(name + "/" + key, "[敏感值未比较]", "[无法判定相同]"); continue; }
            string Values(HeaderField[] headers) => string.Join("\n", headers.Where(h => h.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value));
            add(name + "/" + key, Values(a), Values(b));
        }
        return complete;
    }
    private static void Json(JsonElement a, JsonElement b, string path, Action<string, string, string> add, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (a.ValueKind != b.ValueKind) { add(path, a.GetRawText(), b.GetRawText()); return; }
        if (a.ValueKind == JsonValueKind.Object)
        {
            var left = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            var right = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            foreach (var key in left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                if (left.TryGetValue(key, out var x) && right.TryGetValue(key, out var y)) Json(x, y, path + "/" + key, add, token);
                else add(path + "/" + key, left.TryGetValue(key, out x) ? x.GetRawText() : "[不存在]", right.TryGetValue(key, out y) ? y.GetRawText() : "[不存在]");
        }
        else if (a.ValueKind == JsonValueKind.Array)
        {
            // JsonElement按下标定位需要遍历前缀；先枚举一次，避免大数组退化为平方成本。
            var left = a.EnumerateArray().ToArray(); var right = b.EnumerateArray().ToArray();
            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
                if (i < left.Length && i < right.Length) Json(left[i], right[i], path + "/" + i, add, token);
                else add(path + "/" + i, i < left.Length ? left[i].GetRawText() : "[不存在]", i < right.Length ? right[i].GetRawText() : "[不存在]");
        }
        else add(path, a.GetRawText(), b.GetRawText());
    }
}
