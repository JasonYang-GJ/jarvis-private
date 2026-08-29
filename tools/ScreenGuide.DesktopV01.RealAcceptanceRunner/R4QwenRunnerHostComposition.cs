using System.Net;
using System.Text.Json;
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
    private R4HealthResponseShapeEvidence? _healthResponseShape;

    public int HealthRequests => Volatile.Read(ref _healthRequests);

    public int ModelRequests => Volatile.Read(ref _modelRequests);

    public int TotalRequests => HealthRequests + ModelRequests;

    public IReadOnlyList<int> StatusCodes => _statusCodes.ToArray();

    public R4HealthResponseShapeEvidence? HealthResponseShape =>
        Volatile.Read(ref _healthResponseShape);

    internal void RecordHealth() => Interlocked.Increment(ref _healthRequests);

    internal void RecordModel() => Interlocked.Increment(ref _modelRequests);

    internal void RecordStatus(HttpStatusCode statusCode) => _statusCodes.Enqueue((int)statusCode);

    internal void RecordHealthResponseShape(R4HealthResponseShapeEvidence evidence) =>
        Interlocked.CompareExchange(ref _healthResponseShape, evidence, null);
}

public sealed record R4JsonFieldEvidence(
    bool Present,
    string Kind,
    string? ValueCategory,
    int? NumericValue);

public sealed record R4HealthResponseShapeEvidence(
    int HttpStatus,
    string ContentTypeCategory,
    long BodyByteCount,
    string RootJsonKind,
    bool ParseValid,
    bool SizeLimitExceeded,
    bool Truncated,
    R4JsonFieldEvidence Success,
    R4JsonFieldEvidence Code,
    R4JsonFieldEvidence Output,
    R4JsonFieldEvidence Total,
    R4JsonFieldEvidence PageNo,
    R4JsonFieldEvidence PageSize,
    R4JsonFieldEvidence Permissions,
    int? PermissionsCount,
    bool ExactlyOneModel,
    bool ExactModelMatch,
    R4JsonFieldEvidence NestedPermissions,
    R4JsonFieldEvidence Inference);

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
        if (request.Method == HttpMethod.Get)
        {
            if (response.Content is null)
            {
                _evidence.RecordHealthResponseShape(
                    R4HealthResponseShapeCollector.Empty(response.StatusCode));
            }
            else
            {
                response.Content = new R4ObservedHealthContent(
                    response.Content,
                    response.StatusCode,
                    _evidence.RecordHealthResponseShape);
            }
        }

        return response;
    }
}

internal sealed class R4ObservedHealthContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly HttpStatusCode _statusCode;
    private readonly Action<R4HealthResponseShapeEvidence> _completed;
    private readonly long? _declaredLength;

    public R4ObservedHealthContent(
        HttpContent inner,
        HttpStatusCode statusCode,
        Action<R4HealthResponseShapeEvidence> completed)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _statusCode = statusCode;
        _completed = completed ?? throw new ArgumentNullException(nameof(completed));
        _declaredLength = inner.Headers.ContentLength;
        foreach (var header in inner.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context)
    {
        await using var observed = await CreateContentReadStreamAsync().ConfigureAwait(false);
        await observed.CopyToAsync(stream).ConfigureAwait(false);
    }

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        await using var observed = await CreateContentReadStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await observed.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync()
    {
        var stream = await _inner.ReadAsStreamAsync().ConfigureAwait(false);
        return Observe(stream);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync(
        CancellationToken cancellationToken)
    {
        var stream = await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return Observe(stream);
    }

    protected override bool TryComputeLength(out long length)
    {
        if (_declaredLength is { } contentLength)
        {
            length = contentLength;
            return true;
        }

        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private Stream Observe(Stream stream) => new R4ObservedHealthStream(
        stream,
        new R4HealthResponseShapeCollector(
            _statusCode,
            Headers.ContentType?.MediaType,
            _declaredLength,
            _completed));
}

internal sealed class R4ObservedHealthStream(
    Stream inner,
    R4HealthResponseShapeCollector collector) : Stream
{
    private int _completed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Observe(buffer.AsSpan(offset, read), read == 0);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Observe(buffer[..read], read == 0);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Observe(buffer.Span[..read], read == 0);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer, offset, count, cancellationToken)
            .ConfigureAwait(false);
        Observe(buffer.AsSpan(offset, read), read == 0);
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        Complete(reachedEnd: false);
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        Complete(reachedEnd: false);
        await inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void Observe(ReadOnlySpan<byte> bytes, bool reachedEnd)
    {
        collector.Observe(bytes);
        if (reachedEnd)
        {
            Complete(reachedEnd: true);
        }
    }

    private void Complete(bool reachedEnd)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            collector.Complete(reachedEnd);
        }
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}

internal sealed class R4HealthResponseShapeCollector
{
    internal const int MaxEvidenceBytes = 65_536;
    private readonly HttpStatusCode _statusCode;
    private readonly string _contentTypeCategory;
    private readonly long? _declaredLength;
    private readonly Action<R4HealthResponseShapeEvidence> _completed;
    private readonly byte[] _buffer = new byte[MaxEvidenceBytes + 1];
    private int _captured;
    private long _observed;

    public R4HealthResponseShapeCollector(
        HttpStatusCode statusCode,
        string? mediaType,
        long? declaredLength,
        Action<R4HealthResponseShapeEvidence> completed)
    {
        _statusCode = statusCode;
        _contentTypeCategory = ContentTypeCategory(mediaType);
        _declaredLength = declaredLength;
        _completed = completed;
    }

    public void Observe(ReadOnlySpan<byte> bytes)
    {
        _observed += bytes.Length;
        var count = Math.Min(bytes.Length, _buffer.Length - _captured);
        if (count > 0)
        {
            bytes[..count].CopyTo(_buffer.AsSpan(_captured));
            _captured += count;
        }
    }

    public void Complete(bool reachedEnd)
    {
        try
        {
            _completed(CreateEvidence(reachedEnd));
        }
        finally
        {
            Array.Clear(_buffer);
        }
    }

    public static R4HealthResponseShapeEvidence Empty(HttpStatusCode statusCode) =>
        CreateDefault(
            statusCode,
            "missing",
            bodyByteCount: 0,
            sizeLimitExceeded: false,
            truncated: false);

    private R4HealthResponseShapeEvidence CreateEvidence(bool reachedEnd)
    {
        var bodyByteCount = _declaredLength ?? _observed;
        var oversized = bodyByteCount > MaxEvidenceBytes || _captured > MaxEvidenceBytes;
        var truncated = !reachedEnd
                        && (_declaredLength is null || _observed < _declaredLength);
        if (oversized || truncated || _captured == 0)
        {
            return CreateDefault(
                _statusCode,
                _contentTypeCategory,
                bodyByteCount,
                oversized,
                truncated);
        }

        try
        {
            using var document = JsonDocument.Parse(_buffer.AsMemory(0, _captured));
            return FromJson(
                _statusCode,
                _contentTypeCategory,
                bodyByteCount,
                document.RootElement);
        }
        catch (JsonException)
        {
            return CreateDefault(
                _statusCode,
                _contentTypeCategory,
                bodyByteCount,
                sizeLimitExceeded: false,
                truncated: false);
        }
    }

    private static R4HealthResponseShapeEvidence FromJson(
        HttpStatusCode statusCode,
        string contentTypeCategory,
        long bodyByteCount,
        JsonElement root)
    {
        var success = Field(root, "success", SuccessCategory);
        var code = Field(root, "code", CodeCategory);
        var output = Field(root, "output");
        var outputElement = TryObject(root, "output");
        var total = Field(outputElement, "total");
        var pageNo = Field(outputElement, "page_no");
        var pageSize = Field(outputElement, "page_size");
        var permissions = Field(outputElement, "permissions");
        JsonElement? permissionArray = TryArray(outputElement, "permissions");
        var permissionsCount = permissionArray?.GetArrayLength();
        JsonElement? permission = null;
        var exactlyOneModel = false;
        var exactModelMatch = false;
        if (permissionArray is { } array
            && array.GetArrayLength() == 1
            && array[0].ValueKind == JsonValueKind.Object)
        {
            permission = array[0];
            if (permission.Value.TryGetProperty("model", out var model)
                && model.ValueKind == JsonValueKind.String)
            {
                exactlyOneModel = true;
                exactModelMatch = string.Equals(
                    model.GetString(),
                    QwenChatModelProvider.DefaultModelId,
                    StringComparison.Ordinal);
            }
        }
        var nestedPermissions = Field(permission, "permissions");
        var nested = TryObject(permission, "permissions");
        var inference = Field(nested, "inference", SuccessCategory);
        return new R4HealthResponseShapeEvidence(
            (int)statusCode,
            contentTypeCategory,
            bodyByteCount,
            Kind(root.ValueKind),
            ParseValid: true,
            SizeLimitExceeded: false,
            Truncated: false,
            success,
            code,
            output,
            total,
            pageNo,
            pageSize,
            permissions,
            permissionsCount,
            exactlyOneModel,
            exactModelMatch,
            nestedPermissions,
            inference);
    }

    private static R4HealthResponseShapeEvidence CreateDefault(
        HttpStatusCode statusCode,
        string contentTypeCategory,
        long bodyByteCount,
        bool sizeLimitExceeded,
        bool truncated) =>
        new(
            (int)statusCode,
            contentTypeCategory,
            bodyByteCount,
            "unknown",
            ParseValid: false,
            sizeLimitExceeded,
            truncated,
            MissingField(),
            MissingField(),
            MissingField(),
            MissingField(),
            MissingField(),
            MissingField(),
            MissingField(),
            PermissionsCount: null,
            ExactlyOneModel: false,
            ExactModelMatch: false,
            MissingField(),
            MissingField());

    private static R4JsonFieldEvidence Field(
        JsonElement? parent,
        string name,
        Func<JsonElement, string?>? category = null)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } objectElement
            || !objectElement.TryGetProperty(name, out var value))
        {
            return MissingField();
        }

        return new R4JsonFieldEvidence(
            Present: true,
            Kind(value.ValueKind),
            category?.Invoke(value),
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                ? number
                : null);
    }

    private static R4JsonFieldEvidence MissingField() =>
        new(Present: false, "missing", ValueCategory: null, NumericValue: null);

    private static JsonElement? TryObject(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } objectElement
        && objectElement.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static JsonElement? TryArray(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } objectElement
        && objectElement.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value
            : null;

    private static string? SuccessCategory(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "other"
    };

    private static string? CodeCategory(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.String when value.GetString() is "" => "empty",
        JsonValueKind.String => "nonempty",
        _ => "other"
    };

    private static string Kind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => "unknown"
    };

    private static string ContentTypeCategory(string? mediaType) => mediaType switch
    {
        null or "" => "missing",
        "application/json" => "application_json",
        _ when mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) => "application_json",
        _ => "other"
    };
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
