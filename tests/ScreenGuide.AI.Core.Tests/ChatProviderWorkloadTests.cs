using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class ChatProviderWorkloadTests
{
    [Fact]
    public async Task RegistryExposesExplicitProviderWorkloadSuitability()
    {
        await using var provider = new WorkloadProvider();
        var registry = new ChatProviderRegistry([provider]);

        var descriptor = Assert.Single(registry.Providers);
        Assert.True(descriptor.SupportedWorkloads.HasFlag(ChatProviderWorkloads.OrdinaryChat));
        Assert.False(descriptor.SupportedWorkloads.HasFlag(ChatProviderWorkloads.ProgrammingAgent));
    }

    [Fact]
    public void RegistryRejectsProviderThatCannotServeOrdinaryChat()
    {
        using var provider = new WorkloadProvider(ChatProviderWorkloads.ProgrammingAgent);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ChatProviderRegistry([provider]));

        Assert.Contains("普通聊天", exception.Message, StringComparison.Ordinal);
    }

    private sealed class WorkloadProvider(
        ChatProviderWorkloads workloads = ChatProviderWorkloads.OrdinaryChat) : IChatModelProvider, IDisposable
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            "workload-test",
            "Workload Test",
            "本机测试",
            SendsDataOffDevice: false,
            [new ChatModelDescriptor("test-model", "Test Model", ChatModelCapabilities.None)],
            ChatProviderCredentialKind.None,
            workloads);

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Dispose()
        {
        }
    }
}
