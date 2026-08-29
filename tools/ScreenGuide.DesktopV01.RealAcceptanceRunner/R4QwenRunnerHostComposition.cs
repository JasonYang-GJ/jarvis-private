using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenGuide.Agent.Codex;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.DeepSeek;
using ScreenGuide.AI.Qwen;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopV01.RealAcceptanceRunner;

public enum R4QwenCallMode
{
    Ordinary,
    Cancellation
}

public sealed record R4QwenRequestIdentity(Guid RequestId, Guid TurnId, string ModelId);

public sealed class R4ForbiddenProviderProbe
{
    private int _codexChatCalls;
    private int _deepSeekCalls;

    public int CodexChatCalls => Volatile.Read(ref _codexChatCalls);

    public int DeepSeekCalls => Volatile.Read(ref _deepSeekCalls);

    internal void Record(string providerId)
    {
        if (string.Equals(providerId, "codex", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _codexChatCalls);
        }
        else if (string.Equals(providerId, DeepSeekChatModelProvider.ProviderId, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _deepSeekCalls);
        }
    }
}

internal sealed class R4ForbiddenChatModelProvider(
    ChatProviderDescriptor descriptor,
    R4ForbiddenProviderProbe probe) : IChatModelProvider
{
    public ChatProviderDescriptor Descriptor { get; } = descriptor;

    public Task<ChatModelResponse> CompleteAsync(
        ChatModelRequest request,
        ChatModelStreamCallback? streamCallback = null,
        CancellationToken cancellationToken = default)
    {
        probe.Record(Descriptor.ProviderId);
        throw new R4ValidationFailureException("r4_cross_provider_call_blocked");
    }

    public Task<ChatProviderHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        probe.Record(Descriptor.ProviderId);
        throw new R4ValidationFailureException("r4_cross_provider_call_blocked");
    }

    public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class R4QwenHttpEvidence
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<int> _statusCodes = new();
    private int _healthRequests;
    private int _modelRequests;

    public int HealthRequests => Volatile.Read(ref _healthRequests);

    public int ModelRequests => Volatile.Read(ref _modelRequests);

    public int TotalRequests => HealthRequests + ModelRequests;

    public IReadOnlyList<int> StatusCodes => _statusCodes.ToArray();

    internal void RecordHealth() => Interlocked.Increment(ref _healthRequests);

    internal void RecordModel() => Interlocked.Increment(ref _modelRequests);

    internal void RecordStatus(HttpStatusCode statusCode) => _statusCodes.Enqueue((int)statusCode);
}

internal sealed class R4QwenHttpEvidenceHandler : DelegatingHandler
{
    internal const string HealthUri =
        "https://dashscope.aliyuncs.com/api/v1/models/permissions?model=qwen3.7-plus&authorization_scope=AUTHORIZED&action=INFERENCE&page_no=1&page_size=1";
    internal const string ChatUri =
        "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";

    private readonly R4QwenHttpEvidence _evidence;
    private readonly R4QwenValidationBudget _budget;

    public R4QwenHttpEvidenceHandler(
        HttpMessageHandler innerHandler,
        R4QwenHttpEvidence evidence,
        R4QwenValidationBudget budget)
    {
        InnerHandler = innerHandler ?? throw new ArgumentNullException(nameof(innerHandler));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri?.AbsoluteUri;
        if (request.Method == HttpMethod.Get
            && string.Equals(uri, HealthUri, StringComparison.Ordinal))
        {
            _evidence.RecordHealth();
        }
        else if (request.Method == HttpMethod.Post
                 && string.Equals(uri, ChatUri, StringComparison.Ordinal))
        {
            _evidence.RecordModel();
        }
        else
        {
            throw new R4ValidationFailureException("r4_http_target_mismatch");
        }

        if (_evidence.TotalRequests > _budget.MaxTotalRequests
            || _evidence.ModelRequests > _budget.MaxModelRequests
            || _evidence.HealthRequests > 1)
        {
            throw new R4ValidationFailureException("r4_http_budget_exhausted");
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _evidence.RecordStatus(response.StatusCode);
        return response;
    }
}

public sealed class R4QwenBudgetedChatModelProvider : IChatModelProvider
{
    private readonly IChatModelProvider _inner;
    private readonly R4QwenValidationBudget _budget;
    private readonly R3SafeResponseShapeCollector _responseShapes;
    private readonly System.Collections.Concurrent.ConcurrentQueue<R4QwenRequestIdentity>
        _requestIdentities = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, R3ValidationCallObservation>
        _activeObservations = new();
    private PendingCall? _pendingCall;
    private int _totalRequests;
    private int _modelRequests;
    private int _healthRequests;

    public R4QwenBudgetedChatModelProvider(
        IChatModelProvider inner,
        R4QwenValidationBudget budget,
        R3SafeResponseShapeCollector responseShapes)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _responseShapes = responseShapes ?? throw new ArgumentNullException(nameof(responseShapes));
    }

    public ChatProviderDescriptor Descriptor => _inner.Descriptor;

    public int TotalRequestCount => Volatile.Read(ref _totalRequests);

    public int ModelRequestCount => Volatile.Read(ref _modelRequests);

    public int HealthRequestCount => Volatile.Read(ref _healthRequests);

    public IReadOnlyList<R4QwenRequestIdentity> RequestIdentities => _requestIdentities.ToArray();

    public IReadOnlyList<R3SafeResponseShapeEvidence> ResponseShapeEvidence =>
        _responseShapes.Snapshot();

    public R3ValidationCallObservation PrepareNextCall(R4QwenCallMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        var expectedMode = ModelRequestCount == 0
            ? R4QwenCallMode.Ordinary
            : R4QwenCallMode.Cancellation;
        if (mode != expectedMode)
        {
            throw new R4ValidationFailureException("r4_request_sequence_mismatch");
        }

        var pending = new PendingCall(mode, new R3ValidationCallObservation());
        if (Interlocked.CompareExchange(ref _pendingCall, pending, null) is not null)
        {
            throw new R4ValidationFailureException("r4_request_already_pending");
        }

        return pending.Observation;
    }

    public async Task<ChatModelResponse> CompleteAsync(
        ChatModelRequest request,
        ChatModelStreamCallback? streamCallback = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireQwenRoute(Descriptor.ProviderId, request.ModelId);
        var pending = Interlocked.Exchange(ref _pendingCall, null)
                      ?? throw new R4ValidationFailureException("r4_unplanned_model_request");
        ReserveModelRequest();
        _requestIdentities.Enqueue(new R4QwenRequestIdentity(
            request.RequestId,
            request.TurnId,
            request.ModelId));
        if (!_activeObservations.TryAdd(request.TurnId, pending.Observation))
        {
            throw new R4ValidationFailureException("r4_duplicate_turn_observation");
        }

        var maximumOutputTokens = pending.Mode == R4QwenCallMode.Ordinary
            ? _budget.OrdinaryMaxOutputTokens
            : _budget.CancellationMaxOutputTokens;
        ChatModelStreamCallback effectiveCallback = async (update, token) =>
        {
            pending.Observation.Record(update, countProtocolDelta: false);
            if (streamCallback is not null)
            {
                await streamCallback(update, token).ConfigureAwait(false);
            }
        };
        using var responseShapeScope = _responseShapes.BeginRequest(
            pending.Observation.RecordProtocolDelta);
        try
        {
            var response = await _inner.CompleteAsync(
                    request with
                    {
                        Options = new ChatModelOptions(MaxOutputTokens: maximumOutputTokens)
                    },
                    effectiveCallback,
                    cancellationToken)
                .ConfigureAwait(false);
            pending.Observation.MarkResponseReturned();
            return response;
        }
        catch (ChatModelException exception)
        {
            if (exception.Error.Kind == ChatModelErrorKind.Cancelled)
            {
                pending.Observation.MarkCancellationCompleted();
            }

            responseShapeScope.RecordFailure(exception);
            throw;
        }
        catch
        {
            responseShapeScope.RecordUnknownFailure();
            throw;
        }
        finally
        {
            _activeObservations.TryRemove(request.TurnId, out _);
            pending.Observation.MarkTerminal();
        }
    }

    public Task<ChatProviderHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        ReserveHealthRequest();
        return _inner.CheckHealthAsync(cancellationToken);
    }

    public async Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
    {
        _activeObservations.TryGetValue(turnId, out var observation);
        await _inner.CancelAsync(turnId, cancellationToken).ConfigureAwait(false);
        observation?.MarkCancellationCompleted();
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private void ReserveHealthRequest()
    {
        if (Interlocked.Increment(ref _healthRequests) != 1)
        {
            throw new R4ValidationFailureException("r4_health_request_count_mismatch");
        }

        ReserveTotalRequest();
    }

    private void ReserveModelRequest()
    {
        if (Interlocked.Increment(ref _modelRequests) > _budget.MaxModelRequests)
        {
            throw new R4ValidationFailureException("r4_model_request_budget_exhausted");
        }

        ReserveTotalRequest();
    }

    private void ReserveTotalRequest()
    {
        if (Interlocked.Increment(ref _totalRequests) > _budget.MaxTotalRequests)
        {
            throw new R4ValidationFailureException("r4_total_request_budget_exhausted");
        }
    }

    private static void RequireQwenRoute(string providerId, string modelId)
    {
        if (!string.Equals(providerId, QwenChatModelProvider.ProviderId, StringComparison.Ordinal)
            || !string.Equals(modelId, QwenChatModelProvider.DefaultModelId, StringComparison.Ordinal))
        {
            throw new R4ValidationFailureException("r4_expected_route_mismatch");
        }
    }

    private sealed record PendingCall(R4QwenCallMode Mode, R3ValidationCallObservation Observation);
}

public static class R4QwenRunnerHostComposition
{
    public static IHost BuildCurrentUser(
        DesktopHostOptions hostOptions,
        R4QwenValidationOptions validation)
    {
        var canonicalCredentialRoot = R3CanonicalCredentialStoreRoot.ResolveCurrentUser();
        return BuildCore(
            hostOptions,
            validation,
            serviceProvider => new WindowsDpapiCredentialStore(
                canonicalCredentialRoot,
                serviceProvider.GetRequiredService<TimeProvider>()),
            () => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
    }

    public static IHost BuildOffline(
        DesktopHostOptions hostOptions,
        R4QwenValidationOptions validation,
        IProviderCredentialStore fakeCredentialStore,
        HttpMessageHandler stubTransport)
    {
        ArgumentNullException.ThrowIfNull(fakeCredentialStore);
        ArgumentNullException.ThrowIfNull(stubTransport);
        return BuildCore(
            hostOptions,
            validation,
            _ => fakeCredentialStore,
            () => stubTransport);
    }

    private static IHost BuildCore(
        DesktopHostOptions hostOptions,
        R4QwenValidationOptions validation,
        Func<IServiceProvider, IProviderCredentialStore> credentialStoreFactory,
        Func<HttpMessageHandler> transportFactory)
    {
        ArgumentNullException.ThrowIfNull(hostOptions);
        ArgumentNullException.ThrowIfNull(validation);
        if (!validation.IsValid
            || validation.Budget is null
            || validation.ExpectedProviderId != QwenChatModelProvider.ProviderId
            || validation.ExpectedModelId != QwenChatModelProvider.DefaultModelId)
        {
            throw new R4ValidationFailureException("r4_runner_composition_invalid");
        }

        return DesktopHostFactory.Build(
            [],
            hostOptions,
            services =>
            {
                services.AddSingleton(serviceProvider =>
                    R4QwenCredentialLeaseBinding.CreateReadOnlyQwenOnly(
                        credentialStoreFactory(serviceProvider)));
                services.AddSingleton<IProviderCredentialStore>(serviceProvider =>
                    serviceProvider.GetRequiredService<R4QwenCredentialLeaseBinding.ReadOnlyStore>());
                services.AddSingleton<R3SafeResponseShapeCollector>();
                services.AddSingleton<R4QwenHttpEvidence>();
                services.AddSingleton<R4ForbiddenProviderProbe>();
                services.AddSingleton(serviceProvider =>
                {
                    var budget = validation.Budget;
                    var shapes = serviceProvider.GetRequiredService<R3SafeResponseShapeCollector>();
                    var httpEvidence = serviceProvider.GetRequiredService<R4QwenHttpEvidence>();
                    var trackedTransport = new R3SafeResponseShapeTrackingHandler(
                        transportFactory(),
                        shapes);
                    var guardedTransport = new R4QwenHttpEvidenceHandler(
                        trackedTransport,
                        httpEvidence,
                        budget);
                    return new R4QwenBudgetedChatModelProvider(
                        new QwenChatModelProvider(
                            serviceProvider.GetRequiredService<IProviderCredentialStore>(),
                            guardedTransport),
                        budget,
                        shapes);
                });
                services.AddSingleton(serviceProvider =>
                {
                    var probe = serviceProvider.GetRequiredService<R4ForbiddenProviderProbe>();
                    return new ChatProviderRegistry(
                    [
                        new R4ForbiddenChatModelProvider(
                            serviceProvider.GetRequiredService<CodexChatModelProvider>().Descriptor,
                            probe),
                        new R4ForbiddenChatModelProvider(
                            serviceProvider.GetRequiredService<DeepSeekChatModelProvider>().Descriptor,
                            probe),
                        serviceProvider.GetRequiredService<R4QwenBudgetedChatModelProvider>()
                    ]);
                });
            });
    }
}

public static class R4QwenCredentialLeaseBinding
{
    public static ReadOnlyStore CreateReadOnlyQwenOnly(IProviderCredentialStore source) =>
        new(source ?? throw new ArgumentNullException(nameof(source)));

    public sealed class ReadOnlyStore(IProviderCredentialStore source) : IProviderCredentialStore
    {
        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            string.Equals(providerId, QwenChatModelProvider.ProviderId, StringComparison.Ordinal)
                ? GetQwenStatusAsync(cancellationToken)
                : Task.FromResult(new ProviderCredentialStatus(
                    providerId,
                    ProviderCredentialState.Missing,
                    null));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) =>
            throw new R4ValidationFailureException("r4_credential_store_read_only");

        public async ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(providerId, QwenChatModelProvider.ProviderId, StringComparison.Ordinal))
            {
                return null;
            }

            IProviderCredentialLease? lease = null;
            try
            {
                lease = await source.OpenLeaseAsync(providerId, cancellationToken)
                    .ConfigureAwait(false);
                if (lease is null || lease.Secret.IsEmpty)
                {
                    lease?.Dispose();
                    return null;
                }

                return lease;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lease?.Dispose();
                throw;
            }
            catch
            {
                lease?.Dispose();
                return null;
            }
        }

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            throw new R4ValidationFailureException("r4_credential_store_read_only");

        private async Task<ProviderCredentialStatus> GetQwenStatusAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                return await source.GetStatusAsync(
                        QwenChatModelProvider.ProviderId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new ProviderCredentialStatus(
                    QwenChatModelProvider.ProviderId,
                    ProviderCredentialState.Missing,
                    null);
            }
        }
    }
}
