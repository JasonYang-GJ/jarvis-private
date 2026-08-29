using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenGuide.Core.Memories;

public static class MemoryOutboundLimits
{
    public const int MinimumItems = 1;
    public const int MaximumItems = 8;
    public const int MaximumContentCharacters = 4_000;
    public const int MaximumSerializedContextCharacters = 6_000;
    public static readonly TimeSpan MaximumPreparedLifetime = TimeSpan.FromMinutes(10);
}

public static class MemoryOutboundErrorCodes
{
    public const string ConsentRequired = "memory_outbound_consent_required";
    public const string ConsentStale = "memory_outbound_consent_stale";
    public const string DestinationUnverifiable = "memory_outbound_destination_unverifiable";
    public const string ProjectUnauthorized = "memory_outbound_project_unauthorized";
    public const string ItemUnavailable = "memory_outbound_item_unavailable";
    public const string BudgetExceeded = "memory_outbound_budget_exceeded";
    public const string Cancelled = "memory_outbound_cancelled";
    public const string CommitFailed = "memory_outbound_commit_failed";
    public const string DerivedHistoryRouteMismatch = "memory_derived_history_route_mismatch";
}

public enum MemoryOutboundConsentState
{
    None,
    Prepared,
    WaitingForMemoryOutboundConsent,
    Committing,
    Consumed,
    Declined,
    Invalidated,
    Cancelled,
    Interrupted
}

public sealed record MemoryOutboundItemReference
{
    public MemoryOutboundItemReference(Guid memoryId, int expectedVersion)
    {
        if (memoryId == Guid.Empty || expectedVersion <= 0)
        {
            throw new MemoryValidationException("记忆出站引用无效。");
        }

        MemoryId = memoryId;
        ExpectedVersion = expectedVersion;
    }

    public Guid MemoryId { get; }

    public int ExpectedVersion { get; }
}

public sealed record MemoryOutboundPreparationRequest(
    Guid CoordinatorInstanceId,
    Guid SessionId,
    Guid TurnId,
    long TurnVersion,
    string ProviderId,
    string ModelId,
    string DataDestination,
    string PromptId,
    string PromptVersion,
    string PromptContentHash,
    Guid? ProjectId,
    IReadOnlyList<MemoryOutboundItemReference> Items);

public sealed record MemoryOutboundPreparedItem(
    Guid Id,
    int Version,
    MemoryCategory Category,
    MemoryScopeKind Scope,
    string Title,
    string Body,
    int CharacterCount)
{
    public override string ToString() =>
        $"MemoryOutboundPreparedItem {{ Id = {Id:D}, Version = {Version}, Category = {Category}, Scope = {Scope}, CharacterCount = {CharacterCount}, Content = [REDACTED] }}";
}

public sealed record MemoryOutboundAuditMetadata(
    Guid ConsentId,
    DateTimeOffset PreparedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ConsumedAtUtc,
    string ProviderId,
    string ModelId,
    string DestinationOrigin,
    string PromptId,
    string PromptVersion,
    string PromptContentHash,
    Guid? ProjectId,
    IReadOnlyList<MemoryOutboundItemReference> Items,
    int ItemCount,
    int TotalCharacters,
    string ManifestHash);

public sealed record MemoryOutboundPreparedConsent
{
    public required Guid ConsentId { get; init; }
    public required Guid CoordinatorInstanceId { get; init; }
    public required Guid SessionId { get; init; }
    public required Guid TurnId { get; init; }
    public required long TurnVersion { get; init; }
    public required DateTimeOffset PreparedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required string DestinationOrigin { get; init; }
    public required string PromptId { get; init; }
    public required string PromptVersion { get; init; }
    public required string PromptContentHash { get; init; }
    public Guid? ProjectId { get; init; }
    public required IReadOnlyList<MemoryOutboundPreparedItem> Items { get; init; }
    public required int TotalCharacters { get; init; }
    public required string SerializedContext { get; init; }
    public required string ManifestHash { get; init; }

    public MemoryOutboundAuditMetadata ToAuditMetadata(DateTimeOffset? consumedAtUtc = null) => new(
        ConsentId,
        PreparedAtUtc,
        ExpiresAtUtc,
        consumedAtUtc,
        ProviderId,
        ModelId,
        DestinationOrigin,
        PromptId,
        PromptVersion,
        PromptContentHash,
        ProjectId,
        Items.Select(item => new MemoryOutboundItemReference(item.Id, item.Version)).ToArray(),
        Items.Count,
        TotalCharacters,
        ManifestHash);

    public override string ToString() =>
        $"MemoryOutboundPreparedConsent {{ ConsentId = {ConsentId:D}, TurnId = {TurnId:D}, ItemCount = {Items.Count}, TotalCharacters = {TotalCharacters}, Content = [REDACTED] }}";
}

public sealed record MemoryOutboundEnvelope(
    string SerializedContext,
    MemoryOutboundAuditMetadata Audit)
{
    public override string ToString() =>
        $"MemoryOutboundEnvelope {{ Audit = {Audit}, SerializedContext = [REDACTED] }}";
}

public static class MemoryOutboundContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string NormalizeHttpsOrigin(string destination)
    {
        if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new MemoryValidationException("普通聊天目的地无法验证。");
        }

        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }

    public static string SerializeContext(IReadOnlyList<MemoryOutboundPreparedItem> items)
    {
        var payload = new OutboundPayload(
            "USER_SELECTED_MEMORY_CONTEXT_V1",
            items.Select(item => new OutboundItem(
                item.Category.ToString(),
                item.Scope.ToString(),
                item.Title,
                item.Body)).ToArray());
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static string CreateManifestHash(
        MemoryOutboundPreparationRequest request,
        string destinationOrigin,
        IReadOnlyList<MemoryOutboundPreparedItem> items,
        int totalCharacters)
    {
        var safeManifest = string.Join('\n',
        [
            request.CoordinatorInstanceId.ToString("D"),
            request.SessionId.ToString("D"),
            request.TurnId.ToString("D"),
            request.TurnVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.ProviderId,
            request.ModelId,
            destinationOrigin,
            request.PromptId,
            request.PromptVersion,
            request.PromptContentHash,
            request.ProjectId?.ToString("D") ?? string.Empty,
            totalCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture),
            .. items.Select(item => $"{item.Id:D}:{item.Version}")
        ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(safeManifest)));
    }

    private sealed record OutboundPayload(string Type, IReadOnlyList<OutboundItem> Items);

    private sealed record OutboundItem(string Category, string Scope, string Title, string Body);
}
