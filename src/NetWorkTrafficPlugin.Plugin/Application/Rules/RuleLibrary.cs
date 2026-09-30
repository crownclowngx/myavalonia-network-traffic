using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Dsl;

namespace NetWorkTrafficPlugin.Application.Rules;

public sealed record RuleRevision(Guid RevisionId, int LanguageVersion, string SourceText, string SourceHash,
    DateTimeOffset CreatedUtc, Guid? ParentRevisionId = null)
{
    public static RuleRevision Create(string text, Guid? parent = null) => new(Guid.NewGuid(), 1, text, AnalysisQueryEngine.Hash(text), DateTimeOffset.UtcNow, parent);
}
public sealed record SavedView(Guid ViewId, string Name, Guid RuleRevisionId, AnalysisOptions Options, int SchemaVersion = 1);
public sealed record RuleLibrary(int SchemaVersion, long Generation, RuleRevision[] Revisions, SavedView[] Views)
{
    public static RuleLibrary Empty => new(1, 0, [], []);
    public RuleLibrary Save(RuleRevision revision, SavedView view)
    {
        var existing = Revisions.SingleOrDefault(r => r.RevisionId == revision.RevisionId);
        if (existing is not null && existing != revision) throw new InvalidOperationException("不可修改既有规则修订。");
        return this with { Revisions = existing is null ? [.. Revisions, revision] : Revisions.ToArray(), Views = [.. Views.Where(v => v.ViewId != view.ViewId), view] };
    }
}
public interface IRuleLibraryStore
{
    long KnownGeneration { get; }
    Task<RuleLibrary> LoadAsync(CancellationToken token);
    Task<RuleLibrary> CommitAsync(RuleLibrary candidate, long expectedGeneration, CancellationToken token);
}
public sealed class RuleLibraryConflictException(string message) : Exception(message);

/// <summary>结构和语义在读写两侧同时检查；未知格式不是空库，坏修订不能经重启悄悄生效。</summary>
public static class RuleLibraryValidation
{
    public const int MaximumBytes = 8 << 20;
    public static void Validate(RuleLibrary library, CancellationToken token)
    {
        if (library.SchemaVersion != 1 || library.Generation < 0 || library.Revisions is null || library.Views is null || library.Revisions.Length > 200 || library.Views.Length > 50)
            throw new InvalidDataException("规则库格式或数量超限。");
        if (library.Revisions.Any(r => r is null) || library.Views.Any(v => v is null) || library.Revisions.Select(r => r.RevisionId).Distinct().Count() != library.Revisions.Length || library.Views.Select(v => v.ViewId).Distinct().Count() != library.Views.Length)
            throw new InvalidDataException("规则库身份重复或为空。");
        var programs = new Dictionary<Guid, DslProgram>();
        foreach (var revision in library.Revisions)
        {
            token.ThrowIfCancellationRequested();
            if (revision.RevisionId == Guid.Empty || revision.LanguageVersion != 1 || revision.SourceText is null || revision.SourceHash != AnalysisQueryEngine.Hash(revision.SourceText)) throw new InvalidDataException("规则修订身份/版本/摘要无效。");
            var compiled = DslCompiler.Compile(revision.SourceText, token);
            if (!compiled.Success) throw new InvalidDataException("规则修订无法编译。");
            programs.Add(revision.RevisionId, compiled.Program!);
        }
        foreach (var revision in library.Revisions)
            if (revision.ParentRevisionId is { } parent && (!programs.ContainsKey(parent) || parent == revision.RevisionId)) throw new InvalidDataException("父修订缺失或自引用。");
        var parents = library.Revisions.ToDictionary(r => r.RevisionId, r => r.ParentRevisionId);
        foreach (var revision in library.Revisions)
        {
            var visited = new HashSet<Guid>(); Guid? current = revision.RevisionId;
            while (current is { } id)
            {
                if (!visited.Add(id)) throw new InvalidDataException("父修订不能组成循环。");
                current = parents[id];
            }
        }
        foreach (var view in library.Views)
        {
            if (view.ViewId == Guid.Empty || view.SchemaVersion != 1 || string.IsNullOrWhiteSpace(view.Name) || view.Name.Length > 120 || view.Options is null || !programs.TryGetValue(view.RuleRevisionId, out var program)) throw new InvalidDataException("保存视图名称/版本/修订引用无效。");
            try { view.Options.Window?.Validate(); } catch (ArgumentException ex) { throw new InvalidDataException("保存视图窗口无效。", ex); }
            if (view.Options.Sources is { } sources && (sources.Length == 0 || sources.Length > 4 || sources.Distinct().Count() != sources.Length || sources.Any(s => s is not ("capture" or "manual" or "replay" or "unknown")))) throw new InvalidDataException("保存视图来源无效。");
            if (view.Options.Columns is { } columns && (columns.Length > 8 || columns.Distinct().Count() != columns.Length || columns.Any(c => !program.FieldTypes.ContainsKey(c)))) throw new InvalidDataException("保存视图列定义无效。");
        }
    }
}
