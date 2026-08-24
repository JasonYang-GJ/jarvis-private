using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class ModelRouterTests
{
    [Fact]
    public async Task FrozenRouteKeepsItsProviderWhenTheDefaultChanges()
    {
        var providerA = new RecordingProvider("provider-a", "model-a");
        var providerB = new RecordingProvider("provider-b", "model-b");
        var settings = new MutableSettingsStore(new AiSettings(
            new ChatModelRoute("provider-a", "model-a")));
        var router = new ModelRouter(
            new ChatProviderRegistry([providerA, providerB]),
            settings);
        var frozenA = await router.FreezeDefaultChatRouteAsync();
        await settings.SaveAsync(new AiSettings(
            new ChatModelRoute("provider-b", "model-b")));

        var first = await router.CompleteAsync(frozenA, Request("model-a"));
        var frozenB = await router.FreezeDefaultChatRouteAsync();
        var second = await router.CompleteAsync(frozenB, Request("model-b"));

        Assert.Equal("provider-a", first.Metadata.ProviderId);
        Assert.Equal("provider-b", second.Metadata.ProviderId);
        Assert.Equal(1, providerA.CompleteCount);
        Assert.Equal(1, providerB.CompleteCount);
        Assert.Equal("provider-a", frozenA.ProviderId);
        Assert.Equal("provider-b", frozenB.ProviderId);
    }

    [Fact]
    public async Task MissingDefaultModelFailsWithoutSilentlyChoosingAnotherProvider()
    {
        var providerA = new RecordingProvider("provider-a", "model-a");
        var providerB = new RecordingProvider("provider-b", "model-b");
        var router = new ModelRouter(
            new ChatProviderRegistry([providerA, providerB]),
            new MutableSettingsStore(new AiSettings(
                new ChatModelRoute("provider-a", "missing-model"))));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            router.FreezeDefaultChatRouteAsync());

        Assert.Equal(ChatModelErrorKind.ModelNotFound, exception.Error.Kind);
        Assert.Equal("provider-a", exception.ProviderId);
        Assert.Equal(0, providerA.CompleteCount);
        Assert.Equal(0, providerB.CompleteCount);
    }

    [Fact]
    public async Task CancellationIsForwardedToTheProviderFrozenForThatTurn()
    {
        var provider = new BlockingProvider("provider-a", "model-a");
        var router = new ModelRouter(
            new ChatProviderRegistry([provider]),
            new MutableSettingsStore(new AiSettings(
                new ChatModelRoute("provider-a", "model-a"))));
        var route = await router.FreezeDefaultChatRouteAsync();
        var request = Request("model-a");
        var running = router.CompleteAsync(route, request);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await router.CancelAsync(request.TurnId);
        var response = await running.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(request.TurnId, provider.CancelledTurnId);
        Assert.Equal(ChatFinishReason.Cancelled, response.FinishReason);
    }

    private static ChatModelRequest Request(string modelId) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        modelId,
        "system",
        [new ChatMessage(ChatMessageRole.User, "你好")]);

    private sealed class MutableSettingsStore(AiSettings current) : IAiSettingsStore
    {
        private AiSettings _current = current;

        public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_current);

        public Task SaveAsync(
            AiSettings settings,
            CancellationToken cancellationToken = default)
        {
            _current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProvider(string providerId, string modelId) : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            $"api.{providerId}.example",
            true,
            [new ChatModelDescriptor(modelId, modelId, ChatModelCapabilities.Streaming)]);

        public int CompleteCount { get; private set; }

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            CompleteCount++;
            return Task.FromResult(new ChatModelResponse(
                Descriptor.ProviderId,
                ChatFinishReason.Stop,
                null,
                new ChatProviderMetadata(
                    Descriptor.ProviderId,
                    request.ModelId,
                    null,
                    Descriptor.DataDestination)));
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingProvider(string providerId, string modelId) : IChatModelProvider
    {
        private readonly TaskCompletionSource _cancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChatProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            $"api.{providerId}.example",
            true,
            [new ChatModelDescriptor(modelId, modelId, ChatModelCapabilities.None)]);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid? CancelledTurnId { get; private set; }

        public async Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await _cancelled.Task.WaitAsync(cancellationToken);
            return new ChatModelResponse(
                string.Empty,
                ChatFinishReason.Cancelled,
                null,
                new ChatProviderMetadata(
                    Descriptor.ProviderId,
                    request.ModelId,
                    null,
                    Descriptor.DataDestination));
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
        {
            CancelledTurnId = turnId;
            _cancelled.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _cancelled.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
