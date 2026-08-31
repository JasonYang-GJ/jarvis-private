using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenGuide.DesktopProtocol;

public sealed record BridgeLimitsDto(
    [property: JsonPropertyName("outer_frame_bytes")] int OuterFrameBytes,
    [property: JsonPropertyName("snapshot_bytes")] int SnapshotBytes,
    [property: JsonPropertyName("event_batch_bytes")] int EventBatchBytes,
    [property: JsonPropertyName("events_per_batch")] int EventsPerBatch,
    [property: JsonPropertyName("event_bytes")] int EventBytes);

public sealed record BridgeHandshakeResponseDto(
    [property: JsonPropertyName("bridge_protocol_version")] int BridgeProtocolVersion,
    [property: JsonPropertyName("server_instance_id")] string ServerInstanceId,
    [property: JsonPropertyName("server_product")] string ServerProduct,
    [property: JsonPropertyName("server_product_version")] string ServerProductVersion,
    [property: JsonPropertyName("desktop_ipc_protocol_version")] int DesktopIpcProtocolVersion,
    [property: JsonPropertyName("enabled_capabilities")] IReadOnlyList<string> EnabledCapabilities,
    [property: JsonPropertyName("disabled_capabilities")] IReadOnlyDictionary<string, string> DisabledCapabilities,
    [property: JsonPropertyName("capability_parameters")] IReadOnlyDictionary<string, JsonElement> CapabilityParameters,
    [property: JsonPropertyName("limits")] BridgeLimitsDto Limits,
    [property: JsonPropertyName("issued_at_utc")] string IssuedAtUtc);

public sealed record BridgePresentationDto(
    [property: JsonPropertyName("presentation_key")] string PresentationKey,
    [property: JsonPropertyName("entity_kind")] string EntityKind,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("turn_id")] string? TurnId,
    [property: JsonPropertyName("task_id")] string? TaskId,
    [property: JsonPropertyName("entity_version")] long EntityVersion,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("island_kind")] string IslandKind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("indicator")] string? Indicator,
    [property: JsonPropertyName("is_terminal")] bool IsTerminal,
    [property: JsonPropertyName("requires_user_action")] bool RequiresUserAction,
    [property: JsonPropertyName("updated_at_utc")] string UpdatedAtUtc,
    [property: JsonPropertyName("actions")] IReadOnlyList<JsonElement> Actions);

public sealed record BridgeSnapshotDto(
    [property: JsonPropertyName("bridge_protocol_version")] int BridgeProtocolVersion,
    [property: JsonPropertyName("server_instance_id")] string ServerInstanceId,
    [property: JsonPropertyName("snapshot_id")] string SnapshotId,
    [property: JsonPropertyName("snapshot_sequence")] long SnapshotSequence,
    [property: JsonPropertyName("generated_at_utc")] string GeneratedAtUtc,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("presentations")] IReadOnlyList<BridgePresentationDto> Presentations);
