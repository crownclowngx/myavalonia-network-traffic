using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using NetWorkTrafficPlugin.Application.Dsl;

namespace NetWorkTrafficPlugin.Features.Rules;

/// <summary>草稿只属于当前页面。辅助表单生成明确片段，不把复杂DSL反向转换成会丢注释的表单。</summary>
public sealed partial class RuleEditorModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _validation;
    private readonly object _validationSync = new();
    private bool _disposed;
    public const string DefaultText = """
        dsl 1;
        // 业务身份可来自不同位置；未知来源不会被后续值掩盖。
        field caller = firstNonEmpty(
            first(original.query["user"]),
            first(original.headers["x-business-user"]),
            first(original.cookies["business_user"]),
            toString(original.json("$.identity.user")),
            first(original.form["user"]),
            if(startsWith(original.path, "/actors/"), original.pathSegment(1), null)
        );
        view "调用过程" {
            where source == "capture";
            within last(30m);
            group by caller;
            columns caller;
            timeline by startedAt;
        }
        """;
    [ObservableProperty] private string _text = DefaultText;
    [ObservableProperty] private string _fieldName = "businessId", _argument = "id", _location = "query", _selection = "first", _conversion = "原类型";
    [ObservableProperty] private string _fragment = "", _status = "编辑规则后验证，预览不会应用。";
    public string[] Locations { get; } = ["query", "headers", "cookies", "json", "form", "pathSegment"];
    public string[] Selections { get; } = ["first", "all"];
    public string[] Conversions { get; } = ["原类型", "toString", "toNumber", "toBool"];
    public ObservableCollection<DslDiagnostic> Diagnostics { get; } = [];
    public long Generation { get; private set; }
    public Task ValidationTask { get; private set; } = Task.CompletedTask;
    partial void OnTextChanged(string value)
    {
        Generation++; Diagnostics.Clear(); Status = "草稿已修改，当前结果仍使用上次已应用修订。";
        CancelValidation();
        if (!_disposed) ValidationTask = ValidateLaterAsync(value, Generation);
    }
    private async Task ValidateLaterAsync(string text, long generation)
    {
        using var cancellation = new CancellationTokenSource();
        lock (_validationSync) _validation = cancellation;
        try
        {
            // 编辑消抖仅编译有限语言，不访问捕获/正文。恢复到创建任务的UI上下文后再发布诊断。
            await Task.Delay(200, cancellation.Token);
            var compilation = await Task.Run(() => DslCompiler.Compile(text, cancellation.Token), cancellation.Token);
            if (!_disposed && generation == Generation && !cancellation.IsCancellationRequested) ShowCompilation(compilation);
        }
        catch (OperationCanceledException) { }
        finally { lock (_validationSync) { if (ReferenceEquals(_validation, cancellation)) _validation = null; } }
    }
    public Compilation Validate()
    {
        CancelValidation();
        var compilation = DslCompiler.Compile(Text); ShowCompilation(compilation);
        return compilation;
    }
    private void ShowCompilation(Compilation compilation)
    {
        Diagnostics.Clear();
        foreach (var diagnostic in compilation.Diagnostics) Diagnostics.Add(diagnostic);
        Status = compilation.Success ? "语法有效；应用或预览后才执行分析。" : $"有 {Diagnostics.Count} 项诊断。";
    }
    private void CancelValidation() { lock (_validationSync) _validation?.Cancel(); }
    public void Dispose() { _disposed = true; CancelValidation(); }
    public string GenerateFragment()
    {
        if (string.IsNullOrEmpty(FieldName) || !(char.IsAsciiLetter(FieldName[0]) || FieldName[0] == '_') || FieldName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("字段名称只能使用ASCII字母、数字和下划线，不能以数字开头。");
        if (!Locations.Contains(Location) || !Selections.Contains(Selection) || !Conversions.Contains(Conversion)) throw new ArgumentException("辅助表单选项无效。");
        var argument = JsonSerializer.Serialize(Argument);
        var expression = Location switch
        {
            "json" => $"original.json({argument})",
            "pathSegment" => $"original.pathSegment({Argument})",
            _ => $"{Selection}(original.{Location}[{argument}])"
        };
        if (Conversion != "原类型") expression = $"{Conversion}({expression})";
        var fragment = $"field {FieldName} = {expression};";
        var candidate = $"dsl 1; {fragment} view \"验证片段\" {{ where true; within last(30m); group by original.host; columns {FieldName}; timeline by startedAt; }}";
        if (!DslCompiler.Compile(candidate).Success) throw new ArgumentException("字段片段的路径、名称或转换无效。");
        return Fragment = fragment;
    }
    public void InsertFragment()
    {
        GenerateFragment();
        // 只插入用户确认的片段，不重排整份文本；重复名称由同一编译器定位。
        var position = 0;
        // 开头中文注释可能包含分号；必须跳过注释再识别版本声明，不能插进用户注释中。
        while (position < Text.Length)
        {
            if (char.IsWhiteSpace(Text[position])) { position++; continue; }
            if (Text.AsSpan(position).StartsWith("//"))
            { while (position < Text.Length && Text[position] != '\n') position++; continue; }
            break;
        }
        if (!Text.AsSpan(position).StartsWith("dsl")) throw new ArgumentException("请先填写 dsl 1; 声明。");
        position += 3;
        if (position >= Text.Length || !char.IsWhiteSpace(Text[position])) throw new ArgumentException("版本声明需要空白。");
        while (position < Text.Length && char.IsWhiteSpace(Text[position])) position++;
        if (position >= Text.Length || Text[position++] != '1') throw new ArgumentException("仅支持dsl 1。");
        while (position < Text.Length && char.IsWhiteSpace(Text[position])) position++;
        if (position >= Text.Length || Text[position] != ';') throw new ArgumentException("版本声明需要分号。");
        Text = Text.Insert(position + 1, Environment.NewLine + Fragment);
    }
}
