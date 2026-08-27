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
    string? ProviderDiagnosticCode);

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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, R3ValidationCallObservation>
        _activeObservations = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<R3ProviderRequestIdentity>
        _requestIdentities = new();
    private R3ValidationCallObservation? _pendingObservation;
    private int _requestCount;

    public R3BudgetedChatModelProvider(
        IChatModelProvider inner,
        string expectedModelId,
        R3DeepSeekValidationBudget budget)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (!R3ExpectedModelGate.IsSupported(expectedModelId))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedModelId));
        }

        _expectedModelId = expectedModelId;
        _budget = ValidateBudget(budget);
    }

    public ChatProviderDescriptor Descriptor => _inner.Descriptor;

    public int RequestCount => Volatile.Read(ref _requestCount);

    public IReadOnlyList<R3ProviderRequestIdentity> RequestIdentities =>
        _requestIdentities.ToArray();

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
