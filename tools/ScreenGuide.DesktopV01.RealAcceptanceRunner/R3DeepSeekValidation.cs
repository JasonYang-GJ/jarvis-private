using ScreenGuide.AI.Core;

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

public sealed record R3DeepSeekValidationOptions(
    bool Requested,
    bool IsValid,
    string? ErrorCode,
    R3DeepSeekValidationBudget? Budget)
{
    private const string ModeArgument = "--stage2-r3-deepseek";

    public static R3DeepSeekValidationOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var requested = arguments.Any(argument =>
            string.Equals(argument, ModeArgument, StringComparison.OrdinalIgnoreCase));
        if (!requested)
        {
            return Invalid(requested: false, "real_provider_disabled");
        }

        if (!HasFlag(arguments, "--real-provider"))
        {
            return Invalid(requested: true, "real_provider_approval_missing");
        }

        if (!HasFlag(arguments, "--no-automatic-retry")
            || !HasFlag(arguments, "--no-fallback")
            || !TryReadInt(arguments, "--max-requests=", out var maxRequests)
            || !TryReadInt(arguments, "--max-output-tokens=", out var maxOutputTokens)
            || !TryReadInt(arguments, "--total-timeout-seconds=", out var timeoutSeconds))
        {
            return Invalid(requested: true, "real_provider_budget_missing");
        }

        if (maxRequests is < R3DeepSeekValidationLimits.MinimumRequests
                or > R3DeepSeekValidationLimits.MaximumRequests
            || maxOutputTokens is < R3DeepSeekValidationLimits.MinimumOutputTokens
                or > R3DeepSeekValidationLimits.MaximumOutputTokens
            || timeoutSeconds is < R3DeepSeekValidationLimits.MinimumTimeoutSeconds
                or > R3DeepSeekValidationLimits.MaximumTimeoutSeconds)
        {
            return Invalid(requested: true, "real_provider_budget_out_of_range");
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
                NoFallback: true));
    }

    private static R3DeepSeekValidationOptions Invalid(bool requested, string errorCode) =>
        new(requested, IsValid: false, errorCode, Budget: null);

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

public sealed class R3ValidationFailureException(string code) : Exception(
    "R3 真实 Provider 验收未通过安全门禁。")
{
    public string Code { get; } = code;
}

public enum R3ValidationCallMode
{
    Streaming,
    StreamingCancellation
}

public sealed record R3ProviderRequestIdentity(Guid RequestId, Guid TurnId);

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
    string? ErrorCode);

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
    private readonly R3DeepSeekValidationBudget _budget;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, R3ValidationCallObservation>
        _activeObservations = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<R3ProviderRequestIdentity>
        _requestIdentities = new();
    private R3ValidationCallObservation? _pendingObservation;
    private int _requestCount;

    public R3BudgetedChatModelProvider(
        IChatModelProvider inner,
        R3DeepSeekValidationBudget budget)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
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
        ReserveRequest();
        _requestIdentities.Enqueue(new R3ProviderRequestIdentity(request.RequestId, request.TurnId));
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
