using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class ChatProviderRegistryTests
{
    [Fact]
    public void ResolvesARegisteredProviderAndModelByStableIds()
    {
        var providerA = Provider("provider-a", "model-a");
        var providerB = Provider("provider-b", "model-b");
        var registry = new ChatProviderRegistry([providerA, providerB]);

        var registration = registry.GetRequired("PROVIDER-B", "MODEL-B");

        Assert.Same(providerB, registration.Provider);
        Assert.Equal("model-b", registration.Model.ModelId);
        Assert.Equal(["provider-a", "provider-b"],
            registry.Providers.Select(item => item.ProviderId).ToArray());
    }

    [Fact]
    public void RejectsDuplicateProviderIdsIgnoringCase()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ChatProviderRegistry([
                Provider("provider-a", "model-a"),
                Provider("PROVIDER-A", "model-b")
            ]));

        Assert.Contains("Provider ID 重复", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateModelIdsInsideTheSameProviderIgnoringCase()
    {
        var provider = new StubProvider(new ChatProviderDescriptor(
            "provider-a",
            "Provider A",
            "api.provider-a.example",
            true,
            [
                new ChatModelDescriptor("model-a", "Model A", ChatModelCapabilities.None),
                new ChatModelDescriptor("MODEL-A", "Model A duplicate", ChatModelCapabilities.None)
            ]));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ChatProviderRegistry([provider]));

        Assert.Contains("Model ID 重复", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsOrdinaryChatProviderWithoutAnyModel()
    {
        var provider = new StubProvider(new ChatProviderDescriptor(
            "provider-a",
            "Provider A",
            "api.provider-a.example",
            true,
            []));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ChatProviderRegistry([provider]));

        Assert.Contains("至少一个", exception.Message, StringComparison.Ordinal);
    }

    private static StubProvider Provider(string providerId, string modelId) => new(
        new ChatProviderDescriptor(
            providerId,
            providerId,
            $"api.{providerId}.example",
            true,
            [new ChatModelDescriptor(modelId, modelId, ChatModelCapabilities.None)]));

    private sealed class StubProvider(ChatProviderDescriptor descriptor) : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = descriptor;

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
