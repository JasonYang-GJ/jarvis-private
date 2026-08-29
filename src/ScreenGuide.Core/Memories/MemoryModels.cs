namespace ScreenGuide.Core.Memories;

public enum MemoryCategory
{
    UserFact,
    UserPreference,
    ProjectNote,
    Decision
}

public enum MemoryScopeKind
{
    Global,
    Project
}

public enum MemorySourceKind
{
    UserExplicit
}

public enum MemoryStatus
{
    Active,
    Disabled,
    Deleted
}

public sealed class MemoryValidationException(string message) : ArgumentException(message);

public sealed record MemoryScope
{
    private MemoryScope(MemoryScopeKind kind, Guid? projectId)
    {
        Kind = kind;
        ProjectId = projectId;
    }

    public MemoryScopeKind Kind { get; }

    public Guid? ProjectId { get; }

    public static MemoryScope Global { get; } = new(MemoryScopeKind.Global, null);

    public static MemoryScope ForProject(Guid projectId)
    {
        if (projectId == Guid.Empty)
        {
            throw new MemoryValidationException("项目记忆必须绑定有效的项目 ID。");
        }

        return new MemoryScope(MemoryScopeKind.Project, projectId);
    }

    public static MemoryScope Create(MemoryScopeKind kind, Guid? projectId) => kind switch
    {
        MemoryScopeKind.Global when projectId is null => Global,
        MemoryScopeKind.Project when projectId is { } value => ForProject(value),
        _ => throw new MemoryValidationException("记忆作用域无效。")
    };
}

public sealed record MemoryDraft
{
    private MemoryDraft(
        MemoryCategory category,
        MemoryScope scope,
        string title,
        string body,
        DateTimeOffset? expiresAtUtc)
    {
        Category = category;
        Scope = scope;
        Title = title;
        Body = body;
        ExpiresAtUtc = expiresAtUtc;
    }

    public MemoryCategory Category { get; }

    public MemoryScope Scope { get; }

    public string Title { get; }

    public string Body { get; }

    public MemorySourceKind SourceKind => MemorySourceKind.UserExplicit;

    public double Confidence => 1.0;

    public DateTimeOffset? ExpiresAtUtc { get; }

    public static MemoryDraft Create(
        MemoryCategory category,
        MemoryScope scope,
        string title,
        string body,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset nowUtc)
    {
        if (!Enum.IsDefined(category))
        {
            throw new MemoryValidationException("记忆类别无效。");
        }

        ArgumentNullException.ThrowIfNull(scope);
        _ = MemoryScope.Create(scope.Kind, scope.ProjectId);
        var normalizedTitle = NormalizeContent(title, 80, "标题", allowTextLayoutControls: false);
        var normalizedBody = NormalizeContent(body, 2_000, "正文", allowTextLayoutControls: true);
        if (expiresAtUtc is { } expires && expires <= nowUtc)
        {
            throw new MemoryValidationException("到期时间必须晚于当前时间。");
        }

        return new MemoryDraft(
            category,
            scope,
            normalizedTitle,
            normalizedBody,
            expiresAtUtc);
    }

    private static string NormalizeContent(
        string value,
        int maximumLength,
        string fieldName,
        bool allowTextLayoutControls)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MemoryValidationException($"{fieldName}不能为空。");
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new MemoryValidationException($"{fieldName}超过长度限制。");
        }

        if (normalized.Any(character => char.IsControl(character)
                && (!allowTextLayoutControls || character is not '\r' and not '\n' and not '\t')))
        {
            throw new MemoryValidationException($"{fieldName}包含不允许的控制字符。");
        }

        return normalized;
    }
}

public sealed record MemoryMetadata
{
    private Guid _id;
    private MemoryCategory _category;
    private MemoryScope _scope = MemoryScope.Global;
    private MemoryStatus _status;
    private MemorySourceKind _sourceKind;
    private double _confidence;
    private int _version;

    public Guid Id
    {
        get => _id;
        init => _id = value != Guid.Empty
            ? value
            : throw new MemoryValidationException("记忆 ID 无效。");
    }

    public MemoryCategory Category
    {
        get => _category;
        init => _category = Enum.IsDefined(value)
            ? value
            : throw new MemoryValidationException("记忆类别无效。");
    }

    public MemoryScope Scope
    {
        get => _scope;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _ = MemoryScope.Create(value.Kind, value.ProjectId);
            _scope = value;
        }
    }

    public MemoryStatus Status
    {
        get => _status;
        init => _status = Enum.IsDefined(value)
            ? value
            : throw new MemoryValidationException("记忆状态无效。");
    }

    public MemorySourceKind SourceKind
    {
        get => _sourceKind;
        init => _sourceKind = value == MemorySourceKind.UserExplicit
            ? value
            : throw new MemoryValidationException("记忆来源无效。");
    }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public double Confidence
    {
        get => _confidence;
        init => _confidence = value == 1.0
            ? value
            : throw new MemoryValidationException("用户明确保存的记忆置信度必须为 1.0。");
    }

    public int Version
    {
        get => _version;
        init => _version = value > 0
            ? value
            : throw new MemoryValidationException("记忆版本必须为正整数。");
    }

    public static MemoryMetadata Create(
        Guid id,
        MemoryCategory category,
        MemoryScope scope,
        MemoryStatus status,
        MemorySourceKind sourceKind,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? expiresAtUtc,
        double confidence,
        int version) => new()
        {
            Id = id,
            Category = category,
            Scope = scope,
            Status = status,
            SourceKind = sourceKind,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            Confidence = confidence,
            Version = version
        };
}

public sealed record MemoryItem(MemoryMetadata Metadata, string? Title, string? Body)
{
    public bool IsRetrievalCandidate(DateTimeOffset nowUtc) =>
        Metadata.Status == MemoryStatus.Active
        && (Metadata.ExpiresAtUtc is null || Metadata.ExpiresAtUtc > nowUtc);
}

public sealed record ProtectedMemoryItem(
    MemoryMetadata Metadata,
    byte[]? ProtectedTitle,
    byte[]? ProtectedBody,
    string? SourceReference);
