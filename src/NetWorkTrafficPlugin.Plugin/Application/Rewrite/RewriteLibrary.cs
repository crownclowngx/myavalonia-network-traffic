using NetWorkTrafficPlugin.Application.Analysis;

namespace NetWorkTrafficPlugin.Application.Rewrite;

/// <summary>修订只含源码与身份，不存请求、正文、激活状态或网络句柄。父修订用于追溯，不能原地覆盖。</summary>
public sealed record RewriteRevision(Guid RevisionId, int LanguageVersion, string SourceText, string SourceHash,
    DateTimeOffset CreatedUtc, Guid? ParentRevisionId = null)
{
    public static RewriteRevision Create(string text, Guid? parent = null) =>
        new(Guid.NewGuid(), 2, text, AnalysisQueryEngine.Hash(text), DateTimeOffset.UtcNow, parent);
}
public sealed record RewriteConfiguration(Guid ConfigurationId, string Name, Guid RevisionId);
public sealed record RewriteLibrary(int SchemaVersion, long Generation, RewriteRevision[] Revisions, RewriteConfiguration[] Configurations)
{
    public static RewriteLibrary Empty => new(1, 0, [], []);
    public RewriteLibrary Save(RewriteRevision revision, RewriteConfiguration configuration)
    {
        var previous = Revisions.SingleOrDefault(r => r.RevisionId == revision.RevisionId);
        if (previous is not null && previous != revision) throw new InvalidDataException("不能修改既有改写修订。");
        return this with
        {
            Revisions = previous is null ? [.. Revisions, revision] : Revisions.ToArray(),
            Configurations = [.. Configurations.Where(c => c.ConfigurationId != configuration.ConfigurationId), configuration]
        };
    }
}
public interface IRewriteLibraryStore
{
    Task<RewriteLibrary> LoadAsync(CancellationToken token);
    Task<RewriteLibrary> CommitAsync(RewriteLibrary candidate, long expectedGeneration, CancellationToken token);
}
public sealed class RewriteGenerationConflictException(string message) : Exception(message);

/// <summary>磁盘输入与提交候选使用同一验证。损坏的库必须报错并保留，不能当成空库覆盖。</summary>
public static class RewriteLibraryValidation
{
    public const int MaximumBytes = 8 << 20, MaximumRevisions = 200, MaximumConfigurations = 50;
    public static void Validate(RewriteLibrary library, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (library.SchemaVersion != 1 || library.Generation < 0 || library.Revisions is null || library.Configurations is null ||
            library.Revisions.Length > MaximumRevisions || library.Configurations.Length > MaximumConfigurations)
            throw new InvalidDataException("改写库版本、代次或数量无效。");
        if (library.Revisions.Any(r => r is null) || library.Configurations.Any(c => c is null) ||
            library.Revisions.Select(r => r.RevisionId).Distinct().Count() != library.Revisions.Length ||
            library.Configurations.Select(c => c.ConfigurationId).Distinct().Count() != library.Configurations.Length)
            throw new InvalidDataException("改写库存在空对象或重复身份。");
        var parents = new Dictionary<Guid, Guid?>();
        foreach (var revision in library.Revisions)
        {
            token.ThrowIfCancellationRequested();
            if (revision.RevisionId == Guid.Empty || revision.LanguageVersion != 2 || revision.SourceText is null ||
                revision.SourceHash != AnalysisQueryEngine.Hash(revision.SourceText) || !RewriteCompiler.Compile(revision.SourceText, token).Success)
                throw new InvalidDataException("改写修订身份、语言版本、摘要或源码无效。");
            parents.Add(revision.RevisionId, revision.ParentRevisionId);
        }
        foreach (var revision in library.Revisions)
        {
            var visited = new HashSet<Guid>(); Guid? current = revision.RevisionId;
            while (current is { } id)
            {
                if (!visited.Add(id) || !parents.TryGetValue(id, out current))
                    throw new InvalidDataException("改写修订的父引用缺失或存在循环。");
            }
        }
        foreach (var configuration in library.Configurations)
            if (configuration.ConfigurationId == Guid.Empty || string.IsNullOrWhiteSpace(configuration.Name) || configuration.Name.Length > 120 || !parents.ContainsKey(configuration.RevisionId))
                throw new InvalidDataException("改写配置名称、身份或修订引用无效。");
    }
}
