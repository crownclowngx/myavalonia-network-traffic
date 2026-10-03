namespace NetWorkTrafficPlugin.Application.Requests;

public sealed record RequestTemplate(Guid Id, long Revision, string Name, RequestDraft Draft)
{ public override string ToString() => Name; }
public sealed record RequestLibrary(int SchemaVersion, long Revision, RequestTemplate[] Templates, RequestEnvironment[] Environments)
{
    public static RequestLibrary Empty => new(1, 0, [], []);
}
public interface IRequestLibraryStore
{
    Task<RequestLibrary> LoadAsync(CancellationToken token);
    Task<RequestLibrary> CommitAsync(RequestLibrary candidate, long expectedRevision, CancellationToken token);
}

/// <summary>模板保存的是未解析输入。敏感变量只保留占位符，不把上一次运行的凭据变成新环境的默认身份。</summary>
public static class RequestTemplateSanitizer
{
    public static RequestDraft Sanitize(RequestDraft draft)
    {
        var headers = draft.Headers.Select((h, index) => h.Sensitive || RequestValidation.IsSensitive(h.Name)
            ? h with { Value = IsSensitivePlaceholder(h.Value) ? h.Value : "${secret_header_" + index + "}", Sensitive = true }
            : h).ToArray();
        var sensitiveNames = (draft.Variables ?? []).Concat(draft.Defaults ?? []).Where(v => v.Sensitive).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        return draft with
        {
            Id = Guid.NewGuid(), Revision = 1, Origin = null, Headers = headers, ApplyRules = false,
            Variables = (draft.Variables ?? []).Where(v => !sensitiveNames.Contains(v.Name)).ToArray(),
            Defaults = (draft.Defaults ?? []).Where(v => !sensitiveNames.Contains(v.Name)).ToArray()
        };
    }
    public static bool IsSensitivePlaceholder(string value)
    {
        if (value.StartsWith("Bearer ", StringComparison.Ordinal)) value = value[7..];
        return value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}') && RequestVariableResolver.ValidName(value[2..^1]);
    }
}
