using System.Net;
using System.Text;
using System.Text.Json;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.DeepSeek;

namespace ScreenGuide.DesktopV01.RealAcceptanceRunner;

internal static class R3DeepSeekValidationLimits
{
    public const int MinimumRequests = 1;
    public const int MaximumRequests = 4;
    public const int MinimumOutputTokens = 1;
    public const int MaximumOutputTokens = 256;
    public const int MinimumTimeoutSeconds = 10;
    public const int MaximumTimeoutSeconds = 600;
}

public sealed record R3DeepSeekValidationBudget(
    int MaxRequests,
    int MaxOutputTokens,
    TimeSpan TotalTimeout,
    bool NoAutomaticRetry,
    bool NoFallback);

public sealed record R3DeepSeekExecutionPlan(
    bool RunHealth,
    bool RunOrdinaryChat,
    bool RunStreamingSuccess,
    bool RunStreamingCancellation,
    int ExpectedProviderRequests)
{
    public static R3DeepSeekExecutionPlan Full { get; } = new(
        RunHealth: true,
        RunOrdinaryChat: true,
        RunStreamingSuccess: true,
        RunStreamingCancellation: true,
        ExpectedProviderRequests: 4);

    public static R3DeepSeekExecutionPlan CancellationOnly { get; } = new(
        RunHealth: false,
        RunOrdinaryChat: false,
        RunStreamingSuccess: false,
        RunStreamingCancellation: true,
        ExpectedProviderRequests: 1);
}

public sealed record R3DeepSeekValidationOptions(
    bool Requested,
    bool IsValid,
    string? ErrorCode,
    R3DeepSeekValidationBudget? Budget)
{
    private const string ModeArgument = "--stage2-r3-deepseek";
    private const string CancellationOnlyArgument = "--cancellation-only";

    public bool CancellationOnly { get; init; }

    public string? ExpectedModelId { get; init; }

    public R3DeepSeekExecutionPlan ExecutionPlan => CancellationOnly
        ? R3DeepSeekExecutionPlan.CancellationOnly
        : R3DeepSeekExecutionPlan.Full;

    public static R3DeepSeekValidationOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var fullModeRequested = HasFlag(arguments, ModeArgument);
        var cancellationOnly = HasFlag(arguments, CancellationOnlyArgument);
        var requested = fullModeRequested || cancellationOnly;
        if (!requested)
        {
            return Invalid(requested: false, "real_provider_disabled");
        }

        if (!fullModeRequested)
        {
            return Invalid(
                requested: true,
                "real_provider_mode_missing",
                cancellationOnly: true);
        }

        if (!HasFlag(arguments, "--real-provider"))
        {
            return Invalid(
                requested: true,
                "real_provider_approval_missing",
                cancellationOnly);
        }

        var expectedModelArguments = arguments
            .Where(argument => argument.StartsWith(
                "--expected-model=",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (expectedModelArguments.Length == 0)
        {
            return Invalid(
                requested: true,
                "real_provider_expected_model_missing",
                cancellationOnly);
        }

        if (expectedModelArguments.Length != 1)
        {
            return Invalid(
                requested: true,
                "real_provider_expected_model_repeated",
                cancellationOnly);
        }

        var expectedModelId = expectedModelArguments[0]["--expected-model=".Length..];
        if (!R3ExpectedModelGate.IsSupported(expectedModelId))
        {
            return Invalid(
                requested: true,
                "real_provider_expected_model_invalid",
                cancellationOnly);
        }

        if (!HasFlag(arguments, "--no-automatic-retry")
            || !HasFlag(arguments, "--no-fallback")
            || !TryReadInt(arguments, "--max-requests=", out var maxRequests)
            || !TryReadInt(arguments, "--max-output-tokens=", out var maxOutputTokens)
            || !TryReadInt(arguments, "--total-timeout-seconds=", out var timeoutSeconds))
        {
            return Invalid(
                requested: true,
                "real_provider_budget_missing",
                cancellationOnly);
        }

        if ((cancellationOnly && maxRequests != 1)
            || maxRequests is < R3DeepSeekValidationLimits.MinimumRequests
                or > R3DeepSeekValidationLimits.MaximumRequests
            || maxOutputTokens is < R3DeepSeekValidationLimits.MinimumOutputTokens
                or > R3DeepSeekValidationLimits.MaximumOutputTokens
            || timeoutSeconds is < R3DeepSeekValidationLimits.MinimumTimeoutSeconds
                or > R3DeepSeekValidationLimits.MaximumTimeoutSeconds)
        {
            return Invalid(
                requested: true,
                "real_provider_budget_out_of_range",
                cancellationOnly);
        }

        return new R3DeepSeekValidationOptions(
            Requested: true,
            IsValid: true,
            ErrorCode: null,
            new R3DeepSeekValidationBudget(
                maxRequests,
                maxOutputTokens,
                TimeSpan.FromSeconds(timeoutSeconds),
                NoAutomaticRetry: true,
                NoFallback: true))
        {
            CancellationOnly = cancellationOnly,
            ExpectedModelId = expectedModelId
        };
    }

    private static R3DeepSeekValidationOptions Invalid(
        bool requested,
        string errorCode,
        bool cancellationOnly = false) =>
        new(requested, IsValid: false, errorCode, Budget: null)
        {
            CancellationOnly = cancellationOnly
        };

    private static bool HasFlag(IReadOnlyList<string> arguments, string expected) =>
        arguments.Any(argument => string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase));

    private static bool TryReadInt(
        IReadOnlyList<string> arguments,
        string prefix,
        out int value)
    {
        value = 0;
        var matches = arguments
            .Where(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1
               && int.TryParse(matches[0].AsSpan(prefix.Length), out value);
    }
}

public sealed record R3SecureCredentialStoreReference(
    bool IsValid,
    string? ErrorCode,
    string? RootDirectory)
{
    private const string ArgumentPrefix = "--credential-store-root=";

    public static R3SecureCredentialStoreReference Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var matches = arguments
            .Where(argument => argument.StartsWith(
                ArgumentPrefix,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            return Invalid("real_provider_credential_store_root_missing");
        }

        if (matches.Length != 1)
        {
            return Invalid("real_provider_credential_store_root_repeated");
        }

        var configuredRoot = matches[0][ArgumentPrefix.Length..];
        if (string.IsNullOrWhiteSpace(configuredRoot)
            || !Path.IsPathFullyQualified(configuredRoot))
        {
            return Invalid("real_provider_credential_store_root_invalid");
        }

        try
        {
            return new R3SecureCredentialStoreReference(
                IsValid: true,
                ErrorCode: null,
                RootDirectory: Path.GetFullPath(configuredRoot));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return Invalid("real_provider_credential_store_root_invalid");
        }
    }

    private static R3SecureCredentialStoreReference Invalid(string errorCode) =>
        new(IsValid: false, errorCode, RootDirectory: null);
}

public sealed class R3ValidationBudgetExceededException(string code) : Exception(
    "R3 真实 Provider 验收预算已用完，未继续发送请求。")
{
    public string Code { get; } = code;
}

public sealed class R3ValidationFailureException(
    string code,
    string? diagnosticCode = null,
    string? expectedModelId = null,
    string? actualModelId = null,
    string? modelEvidenceLayer = null) : Exception(
    "R3 真实 Provider 验收未通过安全门禁。")
{
    public string Code { get; } = code;

    public string? DiagnosticCode { get; } = NormalizeDiagnosticCode(diagnosticCode);

    public string? ExpectedModelId { get; } = NormalizeModelId(expectedModelId);

    public string? ActualModelId { get; } = NormalizeModelId(actualModelId);

    public string? ModelEvidenceLayer { get; } = modelEvidenceLayer is
        "settings" or "frozen_route" or "invocation"
        ? modelEvidenceLayer
        : null;

    private static string? NormalizeDiagnosticCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-')
            ? value
            : null;

    private static string? NormalizeModelId(string? value) =>
        R3ExpectedModelGate.IsSupported(value)
            ? value
            : value is null
                ? null
                : "unsupported";
}

public static class R3ExpectedModelGate
{
    public const string MismatchErrorCode = "r3_expected_model_mismatch";

    public static bool IsSupported(string? modelId) =>
        string.Equals(
            modelId,
            DeepSeekChatModelProvider.FlashModelId,
            StringComparison.Ordinal)
        || string.Equals(
            modelId,
            DeepSeekChatModelProvider.ProModelId,
            StringComparison.Ordinal);

    public static void RequireMatch(
        string expectedModelId,
        string? actualModelId,
        string? evidenceLayer = null)
    {
        if (!IsSupported(expectedModelId))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedModelId));
        }

        if (!string.Equals(expectedModelId, actualModelId, StringComparison.Ordinal))
        {
            throw new R3ValidationFailureException(
                MismatchErrorCode,
                expectedModelId: expectedModelId,
                actualModelId: actualModelId,
                modelEvidenceLayer: evidenceLayer);
        }
    }

    public static void RequireSettingsMatch(
        string expectedModelId,
        string? settingsModelId) =>
        RequireMatch(expectedModelId, settingsModelId, "settings");

    public static void RequireFrozenRouteMatch(
        string expectedModelId,
        string? frozenRouteModelId) =>
        RequireMatch(expectedModelId, frozenRouteModelId, "frozen_route");

    public static void RequireInvocationMatch(
        string expectedModelId,
        string? invocationModelId) =>
        RequireMatch(expectedModelId, invocationModelId, "invocation");
}

public sealed record R3IsolatedAiSettingsEvidence(
    string ProviderId,
    string ModelId);

public static class R3IsolatedAiSettingsMaterializer
{
    public const string MissingErrorCode = "r3_isolated_settings_missing";
    public const string InvalidErrorCode = "r3_isolated_settings_invalid";

    public static async Task<R3IsolatedAiSettingsEvidence> MaterializeAndVerifyAsync(
        string settingsPath,
        string expectedModelId,
        CancellationToken cancellationToken = default)
    {
        if (!R3ExpectedModelGate.IsSupported(expectedModelId))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedModelId));
        }

        if (!File.Exists(settingsPath))
        {
            using var writer = CreateStore(settingsPath);
            await writer.SaveAsync(
                    new AiSettings(new ChatModelRoute(
                        DeepSeekChatModelProvider.ProviderId,
                        expectedModelId)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await LoadAndVerifyAsync(settingsPath, expectedModelId, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<R3IsolatedAiSettingsEvidence> LoadAndVerifyAsync(
        string settingsPath,
        string expectedModelId,
        CancellationToken cancellationToken = default)
    {
        if (!R3ExpectedModelGate.IsSupported(expectedModelId))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedModelId));
        }

        if (!File.Exists(settingsPath))
        {
            throw new R3ValidationFailureException(
                MissingErrorCode,
                expectedModelId: expectedModelId,
                modelEvidenceLayer: "settings");
        }

        AiSettings settings;
        try
        {
            using var reader = CreateStore(settingsPath);
            settings = await reader.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new R3ValidationFailureException(
                InvalidErrorCode,
                expectedModelId: expectedModelId,
                modelEvidenceLayer: "settings");
        }

        var route = settings.DefaultChatRoute;
        if (!string.Equals(
                route.ProviderId,
                DeepSeekChatModelProvider.ProviderId,
                StringComparison.Ordinal))
        {
            throw new R3ValidationFailureException(
                R3ExpectedModelGate.MismatchErrorCode,
                expectedModelId: expectedModelId,
                actualModelId: route.ModelId,
                modelEvidenceLayer: "settings");
        }

        R3ExpectedModelGate.RequireSettingsMatch(expectedModelId, route.ModelId);
        return new R3IsolatedAiSettingsEvidence(route.ProviderId, route.ModelId);
    }

    private static FileAiSettingsStore CreateStore(string settingsPath) =>
        new(
            settingsPath,
            new AiSettings(new ChatModelRoute(
                "r3-isolated-settings-missing",
                "r3-isolated-settings-missing")));
}

public static class R3SecureCredentialLeaseBinding
{
    public static IProviderCredentialStore CreateReadOnly(
        string secureRootReference,
        Func<string, IProviderCredentialStore> storeFactory)
    {
        if (string.IsNullOrWhiteSpace(secureRootReference))
        {
            throw new ArgumentException(
                "R3 安全凭据存储引用不能为空。",
                nameof(secureRootReference));
        }

        ArgumentNullException.ThrowIfNull(storeFactory);
        var normalizedRootReference = Path.GetFullPath(secureRootReference.Trim());
        var source = storeFactory(normalizedRootReference)
                     ?? throw new InvalidOperationException("R3 安全凭据 Store 未创建。");
        return new ReadOnlyCredentialLeaseStore(source);
    }

    private sealed class ReadOnlyCredentialLeaseStore(IProviderCredentialStore source)
        : IProviderCredentialStore
    {
        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            source.GetStatusAsync(providerId, cancellationToken);

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) =>
            throw new R3ValidationFailureException("r3_credential_store_read_only");

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            source.OpenLeaseAsync(providerId, cancellationToken);

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            throw new R3ValidationFailureException("r3_credential_store_read_only");
    }
}

public enum R3ValidationCallMode
{
    Streaming,
    StreamingCancellation
}

public sealed record R3ProviderRequestIdentity(
    Guid RequestId,
    Guid TurnId,
    string ModelId);

public sealed record R3ExpectedModelEvidence(
    string ExpectedModelId,
    string SettingsModelId,
    IReadOnlyList<string> FrozenRouteModelIds,
    IReadOnlyList<string> InvocationModelIds)
{
    public bool IsCompleteMatch =>
        R3ExpectedModelGate.IsSupported(ExpectedModelId)
        && string.Equals(ExpectedModelId, SettingsModelId, StringComparison.Ordinal)
        && FrozenRouteModelIds.Count > 0
        && FrozenRouteModelIds.All(modelId =>
            string.Equals(ExpectedModelId, modelId, StringComparison.Ordinal))
        && InvocationModelIds.Count > 0
        && InvocationModelIds.All(modelId =>
            string.Equals(ExpectedModelId, modelId, StringComparison.Ordinal));
}

public sealed record R3DeepSeekValidationResult(
    string Stage,
    bool Passed,
    string ProviderId,
    string ModelId,
    int Requests,
    bool HealthPassed,
    bool OrdinaryChatPassed,
    bool StreamingPassed,
    int StreamingDeltaCount,
    bool CancellationPassed,
    int CancellationDeltaCount,
    bool LateDeltaRejected,
    bool LateFinalRejected,
    bool LateSuccessRejected,
    bool AuditPassed,
    bool NoAutomaticRetry,
    bool NoFallback,
    long ElapsedMilliseconds,
    string? ErrorCode)
{
    public string Mode { get; init; } = "full";

    public bool HealthExecuted { get; init; } = true;

    public bool OrdinaryChatExecuted { get; init; } = true;

    public bool StreamingSuccessExecuted { get; init; } = true;

    public bool StreamingCancellationExecuted { get; init; } = true;

    public R3ExpectedModelEvidence? ModelEvidence { get; init; }

    public R3CancellationTerminalStateEvidence? CancellationTerminalEvidence { get; init; }

    public IReadOnlyList<R3SafeResponseShapeEvidence> ResponseShapeEvidence { get; init; } = [];
}

public sealed record R3CancellationTerminalStateEvidence(
    string SessionTurnState,
    string ConversationTurnState,
    string AiInvocationState)
{
    public bool IsCancelled =>
        string.Equals(SessionTurnState, "Cancelled", StringComparison.Ordinal)
        && string.Equals(ConversationTurnState, "Cancelled", StringComparison.Ordinal)
        && string.Equals(AiInvocationState, "Cancelled", StringComparison.Ordinal);
}

public static class R3CancellationTerminalStateGate
{
    public static void RequireCancelled(R3CancellationTerminalStateEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!evidence.IsCancelled)
        {
            throw new R3ValidationFailureException("r3_cancellation_terminal_mismatch");
        }
    }
}

public sealed record R3SafeResponseShapeEvidence(
    int SseEventCount,
    int DeltaCount,
    bool ContentAppeared,
    long ContentCharacterCount,
    bool ReasoningContentAppeared,
    long ReasoningContentCharacterCount,
    bool DoneAppeared,
    bool FinishReasonAppeared,
    string? FinishReasonCategory,
    string? StableErrorKind,
    string? ProviderDiagnosticCode);

public sealed class R3SafeResponseShapeCollector
{
    private readonly AsyncLocal<R3MutableResponseShape?> _current = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<R3SafeResponseShapeEvidence>
        _completed = new();

    internal R3MutableResponseShape? Current => _current.Value;

    public IReadOnlyList<R3SafeResponseShapeEvidence> Snapshot() => _completed.ToArray();

    internal R3SafeResponseShapeScope BeginRequest()
    {
        if (_current.Value is not null)
        {
            throw new InvalidOperationException("R3 response-shape tracking is already active.");
        }

        var shape = new R3MutableResponseShape();
        _current.Value = shape;
        return new R3SafeResponseShapeScope(this, shape);
    }

    internal void Complete(R3MutableResponseShape shape)
    {
        if (!ReferenceEquals(_current.Value, shape))
        {
            throw new InvalidOperationException("R3 response-shape tracking scope mismatched.");
        }

        _current.Value = null;
        _completed.Enqueue(shape.Snapshot());
    }
}

internal sealed class R3SafeResponseShapeScope(
    R3SafeResponseShapeCollector collector,
    R3MutableResponseShape shape) : IDisposable
{
    private int _disposed;

    public void RecordFailure(ChatModelException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        shape.RecordFailure(
            R3FailureEvidenceReporter.StableFailureCode(exception.Error.Kind),
            exception.Error.Code);
    }

    public void RecordUnknownFailure() => shape.RecordFailure("chat_provider_error", null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            collector.Complete(shape);
        }
    }
}

internal sealed class R3MutableResponseShape
{
    private readonly object _sync = new();
    private int _sseEventCount;
    private int _deltaCount;
    private bool _contentAppeared;
    private long _contentCharacterCount;
    private bool _reasoningContentAppeared;
    private long _reasoningContentCharacterCount;
    private bool _doneAppeared;
    private bool _finishReasonAppeared;
    private string? _finishReasonCategory;
    private string? _stableErrorKind;
    private string? _providerDiagnosticCode;

    public void ObserveDataLine(string payload)
    {
        lock (_sync)
        {
            _sseEventCount = SaturatingIncrement(_sseEventCount);
            if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                _doneAppeared = true;
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                {
                    return;
                }

                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finishReason))
                {
                    _finishReasonAppeared = true;
                    _finishReasonCategory = SafeFinishReason(finishReason);
                }

                if (!choice.TryGetProperty("delta", out var delta)
                    || delta.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                _deltaCount = SaturatingIncrement(_deltaCount);
                ObserveTextProperty(
                    delta,
                    "content",
                    ref _contentAppeared,
                    ref _contentCharacterCount);
                ObserveTextProperty(
                    delta,
                    "reasoning_content",
                    ref _reasoningContentAppeared,
                    ref _reasoningContentCharacterCount);
            }
            catch (JsonException)
            {
                // The production provider owns protocol classification. Shape evidence records no raw data.
            }
        }
    }

    public void RecordFailure(string stableErrorKind, string? providerDiagnosticCode)
    {
        lock (_sync)
        {
            _stableErrorKind = stableErrorKind;
            _providerDiagnosticCode = NormalizeDiagnosticCode(providerDiagnosticCode);
        }
    }

    public R3SafeResponseShapeEvidence Snapshot()
    {
        lock (_sync)
        {
            return new R3SafeResponseShapeEvidence(
                _sseEventCount,
                _deltaCount,
                _contentAppeared,
                _contentCharacterCount,
                _reasoningContentAppeared,
                _reasoningContentCharacterCount,
                _doneAppeared,
                _finishReasonAppeared,
                _finishReasonCategory,
                _stableErrorKind,
                _providerDiagnosticCode);
        }
    }

    private static void ObserveTextProperty(
        JsonElement delta,
        string propertyName,
        ref bool appeared,
        ref long characterCount)
    {
        if (!delta.TryGetProperty(propertyName, out var value))
        {
            return;
        }

        appeared = true;
        if (value.ValueKind == JsonValueKind.String)
        {
            characterCount = SaturatingAdd(characterCount, value.GetString()?.Length ?? 0);
        }
    }

    private static string SafeFinishReason(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return "null";
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return "invalid";
        }

        return value.GetString() switch
        {
            "stop" => "stop",
            "length" => "length",
            "content_filter" => "content_filter",
            "insufficient_system_resource" => "error",
            _ => "other"
        };
    }

    private static string? NormalizeDiagnosticCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-')
            ? value
            : null;

    private static int SaturatingIncrement(int value) =>
        value == int.MaxValue ? value : value + 1;

    private static long SaturatingAdd(long value, int addition) =>
        value > long.MaxValue - addition ? long.MaxValue : value + addition;
}

public sealed class R3SafeResponseShapeTrackingHandler : DelegatingHandler
{
    private readonly R3SafeResponseShapeCollector _collector;

    public R3SafeResponseShapeTrackingHandler(
        HttpMessageHandler innerHandler,
        R3SafeResponseShapeCollector collector)
    {
        InnerHandler = innerHandler ?? throw new ArgumentNullException(nameof(innerHandler));
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var shape = _collector.Current;
        if (shape is not null
            && response.Content is { } content
            && string.Equals(
                content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            var trackedContent = new R3SafeResponseShapeTrackingContent(content, shape);
            foreach (var header in content.Headers)
            {
                trackedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = trackedContent;
        }

        return response;
    }
}

internal sealed class R3SafeResponseShapeTrackingContent(
    HttpContent inner,
    R3MutableResponseShape shape) : HttpContent
{
    private readonly HttpContent _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly R3MutableResponseShape _shape = shape ?? throw new ArgumentNullException(nameof(shape));

    protected override bool TryComputeLength(out long length)
    {
        length = _inner.Headers.ContentLength ?? 0;
        return _inner.Headers.ContentLength.HasValue;
    }

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context)
    {
        await using var source = await CreateTrackedStreamAsync(CancellationToken.None)
            .ConfigureAwait(false);
        await source.CopyToAsync(stream).ConfigureAwait(false);
    }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        CreateTrackedStreamAsync(CancellationToken.None);

    protected override Task<Stream> CreateContentReadStreamAsync(
        CancellationToken cancellationToken) =>
        CreateTrackedStreamAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task<Stream> CreateTrackedStreamAsync(CancellationToken cancellationToken)
    {
        var stream = await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new R3SafeResponseShapeTrackingStream(stream, _shape);
    }
}

internal sealed class R3SafeResponseShapeTrackingStream(
    Stream inner,
    R3MutableResponseShape shape) : Stream
{
    private const int MaximumObservedLineBytes = 64 * 1024;
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly R3MutableResponseShape _shape = shape ?? throw new ArgumentNullException(nameof(shape));
    private readonly List<byte> _line = [];
    private bool _discardLine;
    private bool _endObserved;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Observe(buffer.AsSpan(offset, read), read == 0);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
            .ConfigureAwait(false);
        Observe(buffer.AsSpan(offset, read), read == 0);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Observe(buffer.Span[..read], read == 0);
        return read;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void Observe(ReadOnlySpan<byte> bytes, bool endOfStream)
    {
        foreach (var value in bytes)
        {
            if (value == (byte)'\n')
            {
                CompleteLine();
                continue;
            }

            if (_discardLine)
            {
                continue;
            }

            if (_line.Count >= MaximumObservedLineBytes)
            {
                _line.Clear();
                _discardLine = true;
                continue;
            }

            _line.Add(value);
        }

        if (endOfStream && !_endObserved)
        {
            _endObserved = true;
            CompleteLine();
        }
    }

    private void CompleteLine()
    {
        if (_discardLine)
        {
            _discardLine = false;
            _line.Clear();
            return;
        }

        if (_line.Count > 0 && _line[^1] == (byte)'\r')
        {
            _line.RemoveAt(_line.Count - 1);
        }

        if (_line.Count >= 5)
        {
            var line = Encoding.UTF8.GetString(_line.ToArray());
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                _shape.ObserveDataLine(line[5..].TrimStart());
            }
        }

        _line.Clear();
    }
}

public sealed record R3FailureModelEvidence(
    string ExpectedModelId,
    string? SettingsModelId,
    IReadOnlyList<string> FrozenRouteModelIds,
    IReadOnlyList<string> InvocationModelIds);

public sealed class R3RunEvidenceTracker
{
    private readonly List<string> _invocationModelIds = [];
    private string? _settingsModelId;

    public R3RunEvidenceTracker(string expectedModelId)
    {
        if (!R3ExpectedModelGate.IsSupported(expectedModelId))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedModelId));
        }

        ExpectedModelId = expectedModelId;
    }

    public string ExpectedModelId { get; }

    public string FailureStage { get; private set; } = "initialization";

    public bool HealthExecuted { get; private set; }

    public bool OrdinaryChatExecuted { get; private set; }

    public bool StreamingSuccessExecuted { get; private set; }

    public bool StreamingCancellationExecuted { get; private set; }

    public void BeginConfiguration() => FailureStage = "configuration";

    public void RecordSettingsModel(string? modelId)
    {
        FailureStage = "settings";
        _settingsModelId = NormalizeModelId(modelId);
    }

    public void BeginHealth()
    {
        FailureStage = "health";
        HealthExecuted = true;
    }

    public void BeginOrdinaryChat()
    {
        FailureStage = "ordinary_chat";
        OrdinaryChatExecuted = true;
    }

    public void BeginStreamingSuccess()
    {
        FailureStage = "streaming";
        StreamingSuccessExecuted = true;
    }

    public void BeginStreamingCancellation()
    {
        FailureStage = "cancellation";
        StreamingCancellationExecuted = true;
    }

    public void BeginAudit() => FailureStage = "audit";

    public void BeginModelEvidence() => FailureStage = "model_evidence";

    public void RecordInvocationModel(string? modelId)
    {
        var normalized = NormalizeModelId(modelId);
        if (normalized is not null)
        {
            _invocationModelIds.Add(normalized);
        }
    }

    public R3FailureModelEvidence Snapshot(IReadOnlyList<string> frozenRouteModelIds)
    {
        ArgumentNullException.ThrowIfNull(frozenRouteModelIds);
        return new R3FailureModelEvidence(
            ExpectedModelId,
            _settingsModelId,
            frozenRouteModelIds
                .Select(NormalizeModelId)
                .Where(modelId => modelId is not null)
                .Cast<string>()
                .ToArray(),
            _invocationModelIds.ToArray());
    }

    private static string? NormalizeModelId(string? modelId) =>
        R3ExpectedModelGate.IsSupported(modelId)
            ? modelId
            : modelId is null
                ? null
                : "unsupported";
}

public sealed record R3RunnerFailureEvidence(
    string Stage,
    bool Passed,
    string FailureStage,
    string ErrorCode,
    string? ProviderDiagnosticCode,
    string ExpectedModelId,
    string? ActualModelId,
    int RequestCount,
    R3FailureModelEvidence ModelEvidence,
    bool HealthExecuted,
    bool OrdinaryChatExecuted,
    bool StreamingSuccessExecuted,
    bool StreamingCancellationExecuted,
    string ExceptionType,
    bool RealProvider)
{
    public IReadOnlyList<R3SafeResponseShapeEvidence> ResponseShapeEvidence { get; init; } = [];
}

public static class R3FailureEvidenceReporter
{
    public static R3RunnerFailureEvidence Create(
        Exception exception,
        R3RunEvidenceTracker tracker,
        int requestCount,
        IReadOnlyList<string> frozenRouteModelIds,
        IReadOnlyList<R3SafeResponseShapeEvidence>? responseShapeEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(frozenRouteModelIds);
        if (requestCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestCount));
        }

        var modelEvidence = tracker.Snapshot(frozenRouteModelIds);
        var actualModelId = exception switch
        {
            R3ValidationFailureException failure when failure.ActualModelId is not null =>
                failure.ActualModelId,
            ChatModelException chat when R3ExpectedModelGate.IsSupported(chat.ModelId) =>
                chat.ModelId,
            _ => modelEvidence.InvocationModelIds.LastOrDefault()
                 ?? modelEvidence.FrozenRouteModelIds.LastOrDefault()
                 ?? modelEvidence.SettingsModelId
        };

        return new R3RunnerFailureEvidence(
            Stage: "failed",
            Passed: false,
            tracker.FailureStage,
            ErrorCode(exception),
            ProviderDiagnosticCode(exception),
            tracker.ExpectedModelId,
            actualModelId,
            requestCount,
            modelEvidence,
            tracker.HealthExecuted,
            tracker.OrdinaryChatExecuted,
            tracker.StreamingSuccessExecuted,
            tracker.StreamingCancellationExecuted,
            exception.GetType().Name,
            RealProvider: true)
        {
            ResponseShapeEvidence = responseShapeEvidence?.ToArray() ?? []
        };
    }

    private static string ErrorCode(Exception exception) => exception switch
    {
        R3ValidationFailureException failure => failure.Code,
        R3ValidationBudgetExceededException budget => budget.Code,
        OperationCanceledException => "r3_total_timeout",
        ChatModelException chat => StableFailureCode(chat.Error.Kind),
        _ => "r3_validation_failed"
    };

    private static string? ProviderDiagnosticCode(Exception exception) => exception switch
    {
        R3ValidationFailureException failure => failure.DiagnosticCode,
        ChatModelException chat => chat.Error.Code,
        _ => null
    };

    internal static string StableFailureCode(ChatModelErrorKind kind) => kind switch
    {
        ChatModelErrorKind.InvalidRequest => "invalid_request",
        ChatModelErrorKind.Configuration => "configuration",
        ChatModelErrorKind.Unauthorized => "unauthorized",
        ChatModelErrorKind.Authorization => "authorization",
        ChatModelErrorKind.PolicyDisabled => "disabled_by_security_policy",
        ChatModelErrorKind.ModelNotFound => "model_not_found",
        ChatModelErrorKind.RateLimited => "rate_limited",
        ChatModelErrorKind.InsufficientBalance => "insufficient_balance",
        ChatModelErrorKind.Timeout => "timeout",
        ChatModelErrorKind.Cancelled => "cancelled",
        ChatModelErrorKind.Network => "network",
        ChatModelErrorKind.InvalidResponse => "invalid_response",
        ChatModelErrorKind.Unavailable => "unavailable",
        _ => "chat_provider_error"
    };
}

internal sealed record R3DeepSeekCancellationAuditEvidence(
    string TerminalState,
    string? DiagnosticCode,
    bool HasUsage,
    bool HasProviderRequestId);

internal static class R3DeepSeekCancellationAuditGate
{
    private const string ExpectedDiagnosticCode = "deepseek.cancelled";

    public static bool IsSatisfied(R3DeepSeekCancellationAuditEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return string.Equals(evidence.TerminalState, "Cancelled", StringComparison.Ordinal)
               && string.Equals(
                   evidence.DiagnosticCode,
                   ExpectedDiagnosticCode,
                   StringComparison.Ordinal)
               && !evidence.HasUsage
               && !evidence.HasProviderRequestId;
    }
}

internal enum R3CancellationPreconditionDisposition
{
    CancelRequested,
    TerminalFailed,
    TerminalSucceeded,
    TerminalCancelled,
    TerminalInterrupted,
    TimedOut
}

internal sealed record R3CancellationTerminalEvidence(
    string SessionState,
    string ConversationState,
    string InvocationState,
    string? PublicFailureCode,
    string? ProviderDiagnosticCode,
    string? InvocationModelId = null);

internal sealed record R3CancellationPreconditionResult(
    R3CancellationPreconditionDisposition Disposition,
    R3CancellationTerminalEvidence? TerminalEvidence = null);

internal static class R3CancellationPreconditionCoordinator
{
    public static async Task<R3CancellationPreconditionResult> WaitAsync(
        Task atLeastTwoDeltas,
        Task<R3CancellationTerminalEvidence> terminalState,
        Task timeoutSignal,
        Func<Task> cancel)
    {
        ArgumentNullException.ThrowIfNull(atLeastTwoDeltas);
        ArgumentNullException.ThrowIfNull(terminalState);
        ArgumentNullException.ThrowIfNull(timeoutSignal);
        ArgumentNullException.ThrowIfNull(cancel);

        var completed = await Task.WhenAny(atLeastTwoDeltas, terminalState, timeoutSignal)
            .ConfigureAwait(false);
        if (completed == terminalState)
        {
            var evidence = await terminalState.ConfigureAwait(false);
            return new R3CancellationPreconditionResult(
                ClassifyTerminalEvidence(evidence),
                evidence);
        }

        if (completed == atLeastTwoDeltas)
        {
            await atLeastTwoDeltas.ConfigureAwait(false);
            await cancel().ConfigureAwait(false);
            return new R3CancellationPreconditionResult(
                R3CancellationPreconditionDisposition.CancelRequested);
        }

        await timeoutSignal.ConfigureAwait(false);
        return new R3CancellationPreconditionResult(
            R3CancellationPreconditionDisposition.TimedOut);
    }

    private static R3CancellationPreconditionDisposition ClassifyTerminalEvidence(
        R3CancellationTerminalEvidence evidence)
    {
        var states = (
            evidence.SessionState,
            evidence.ConversationState,
            evidence.InvocationState);
        return states switch
        {
            ("Failed", "Failed", "Failed") =>
                R3CancellationPreconditionDisposition.TerminalFailed,
            ("Completed", "Succeeded", "Succeeded") =>
                R3CancellationPreconditionDisposition.TerminalSucceeded,
            ("Cancelled", "Cancelled", "Cancelled") =>
                R3CancellationPreconditionDisposition.TerminalCancelled,
            ("Interrupted", "Interrupted", "Interrupted") =>
                R3CancellationPreconditionDisposition.TerminalInterrupted,
            _ => throw new R3ValidationFailureException(
                "r3_cancellation_terminal_mismatch",
                evidence.ProviderDiagnosticCode)
        };
    }
}

public sealed class R3ValidationCallObservation
{
    private readonly TaskCompletionSource _atLeastTwoDeltas =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _terminal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _deltaCount;
    private int _deltaCountWhenCancellationCompleted = -1;
    private int _finalUpdateObserved;
    private int _responseReturned;

    public Task AtLeastTwoDeltas => _atLeastTwoDeltas.Task;

    public Task Terminal => _terminal.Task;

    public int DeltaCount => Volatile.Read(ref _deltaCount);

    public int? DeltaCountWhenCancellationCompleted =>
        Volatile.Read(ref _deltaCountWhenCancellationCompleted) is var value && value >= 0
            ? value
            : null;

    public bool FinalUpdateObserved => Volatile.Read(ref _finalUpdateObserved) != 0;

    public bool ResponseReturned => Volatile.Read(ref _responseReturned) != 0;

    internal void Record(ChatStreamUpdate update)
    {
        if (update.IsFinal)
        {
            Volatile.Write(ref _finalUpdateObserved, 1);
        }

        if (string.IsNullOrEmpty(update.DeltaText))
        {
            return;
        }

        if (Interlocked.Increment(ref _deltaCount) >= 2)
        {
            _atLeastTwoDeltas.TrySetResult();
        }
    }

    internal void MarkResponseReturned() => Volatile.Write(ref _responseReturned, 1);

    internal void MarkCancellationCompleted() =>
        Volatile.Write(ref _deltaCountWhenCancellationCompleted, DeltaCount);

    internal void MarkTerminal() => _terminal.TrySetResult();
}

public sealed class R3BudgetedChatModelProvider : IChatModelProvider
{
    private readonly IChatModelProvider _inner;
    private readonly string _expectedModelId;
    private readonly R3DeepSeekValidationBudget _budget;
    private readonly R3SafeResponseShapeCollector? _responseShapeCollector;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, R3ValidationCallObservation>
        _activeObservations = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<R3ProviderRequestIdentity>
        _requestIdentities = new();
    private R3ValidationCallObservation? _pendingObservation;
    private int _requestCount;

    public R3BudgetedChatModelProvider(
        IChatModelProvider inner,
        string expectedModelId,
        R3DeepSeekValidationBudget budget,
        R3SafeResponseShapeCollector? responseShapeCollector = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (!R3ExpectedModelGate.IsSupported(expectedModelId))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedModelId));
        }

        _expectedModelId = expectedModelId;
        _budget = ValidateBudget(budget);
        _responseShapeCollector = responseShapeCollector;
    }

    public ChatProviderDescriptor Descriptor => _inner.Descriptor;

    public int RequestCount => Volatile.Read(ref _requestCount);

    public IReadOnlyList<R3ProviderRequestIdentity> RequestIdentities =>
        _requestIdentities.ToArray();

    public IReadOnlyList<R3SafeResponseShapeEvidence> ResponseShapeEvidence =>
        _responseShapeCollector?.Snapshot() ?? [];

    public R3ValidationCallObservation PrepareNextCall(R3ValidationCallMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        var observation = new R3ValidationCallObservation();
        if (Interlocked.CompareExchange(ref _pendingObservation, observation, null) is not null)
        {
            throw new InvalidOperationException("R3 验收已有一条等待执行的流式请求。");
        }

        return observation;
    }

    public async Task<ChatModelResponse> CompleteAsync(
        ChatModelRequest request,
        ChatModelStreamCallback? streamCallback = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        R3ExpectedModelGate.RequireFrozenRouteMatch(_expectedModelId, request.ModelId);
        ReserveRequest();
        _requestIdentities.Enqueue(new R3ProviderRequestIdentity(
            request.RequestId,
            request.TurnId,
            request.ModelId));
        var requestedLimit = request.Options?.MaxOutputTokens;
        var maximumOutputTokens = requestedLimit is null
            ? _budget.MaxOutputTokens
            : Math.Min(requestedLimit.Value, _budget.MaxOutputTokens);
        var observation = Interlocked.Exchange(ref _pendingObservation, null);
        if (observation is not null && !_activeObservations.TryAdd(request.TurnId, observation))
        {
            throw new InvalidOperationException("R3 验收的 Turn 已经存在流式观察。");
        }

        ChatModelStreamCallback? effectiveCallback = streamCallback;
        if (observation is not null)
        {
            effectiveCallback = async (update, token) =>
            {
                observation.Record(update);
                if (streamCallback is not null)
                {
                    await streamCallback(update, token).ConfigureAwait(false);
                }
            };
        }

        using var responseShapeScope = _responseShapeCollector?.BeginRequest();
        try
        {
            var response = await _inner.CompleteAsync(
                    request with
                    {
                        Options = (request.Options ?? new ChatModelOptions()) with
                        {
                            MaxOutputTokens = maximumOutputTokens
                        }
                    },
                    effectiveCallback,
                    cancellationToken)
                .ConfigureAwait(false);
            observation?.MarkResponseReturned();
            return response;
        }
        catch (ChatModelException exception)
        {
            responseShapeScope?.RecordFailure(exception);
            throw;
        }
        catch
        {
            responseShapeScope?.RecordUnknownFailure();
            throw;
        }
        finally
        {
            if (observation is not null)
            {
                _activeObservations.TryRemove(
                    new KeyValuePair<Guid, R3ValidationCallObservation>(request.TurnId, observation));
                observation.MarkTerminal();
            }
        }
    }

    public Task<ChatProviderHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        ReserveRequest();
        return _inner.CheckHealthAsync(cancellationToken);
    }

    public async Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
    {
        _activeObservations.TryGetValue(turnId, out var observation);
        await _inner.CancelAsync(turnId, cancellationToken).ConfigureAwait(false);
        observation?.MarkCancellationCompleted();
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private void ReserveRequest()
    {
        while (true)
        {
            var current = Volatile.Read(ref _requestCount);
            if (current >= _budget.MaxRequests)
            {
                throw new R3ValidationBudgetExceededException("r3_request_budget_exhausted");
            }

            if (Interlocked.CompareExchange(ref _requestCount, current + 1, current) == current)
            {
                return;
            }
        }
    }

    private static R3DeepSeekValidationBudget ValidateBudget(R3DeepSeekValidationBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.MaxRequests is < R3DeepSeekValidationLimits.MinimumRequests
                or > R3DeepSeekValidationLimits.MaximumRequests
            || budget.MaxOutputTokens is < R3DeepSeekValidationLimits.MinimumOutputTokens
                or > R3DeepSeekValidationLimits.MaximumOutputTokens
            || budget.TotalTimeout < TimeSpan.FromSeconds(
                R3DeepSeekValidationLimits.MinimumTimeoutSeconds)
            || budget.TotalTimeout > TimeSpan.FromSeconds(
                R3DeepSeekValidationLimits.MaximumTimeoutSeconds)
            || !budget.NoAutomaticRetry
            || !budget.NoFallback)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "R3 验收预算超出安全范围。");
        }

        return budget;
    }
}
