using ScreenGuide.AI.Core;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Conversations;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class RoutedConversationProviderTests
{
    [Fact]
    public async Task SwitchingAtoBtoA_keeps_one_supplier_neutral_history_and_audits_each_turn()
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        var providerA = new RecordingChatProvider("provider-a", "model-a");
        var providerB = new RecordingChatProvider("provider-b", "model-b");
        var settings = new MutableAiSettingsStore("provider-a", "model-a");
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(new ChatProviderRegistry([providerA, providerB]), settings),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);

        history.Add(ConversationMessageRole.User, "甲代表蓝鹭");
        var first = await routed.SendAsync(Request(conversationId, "甲代表蓝鹭"));
        history.Add(ConversationMessageRole.Assistant, first.Reply!);

        await settings.SaveAsync(new AiSettings(new ChatModelRoute("provider-b", "model-b")));
        history.Add(ConversationMessageRole.User, "刚才那个甲代表什么？");
        var second = await routed.SendAsync(Request(conversationId, "刚才那个甲代表什么？"));
        history.Add(ConversationMessageRole.Assistant, second.Reply!);

        await settings.SaveAsync(new AiSettings(new ChatModelRoute("provider-a", "model-a")));
        history.Add(ConversationMessageRole.User, "继续，并引用第二个回答");
        var third = await routed.SendAsync(Request(conversationId, "继续，并引用第二个回答"));

        Assert.Equal(ConversationProviderOutcome.Succeeded, third.Outcome);
        Assert.Equal(2, providerA.Requests.Count);
        Assert.Single(providerB.Requests);
        Assert.Equal(
            ["甲代表蓝鹭", "provider-a 回答 1", "刚才那个甲代表什么？"],
            providerB.Requests[0].Messages.Select(item => item.Content).ToArray());
        Assert.Equal(
            [
                "甲代表蓝鹭",
                "provider-a 回答 1",
                "刚才那个甲代表什么？",
                "provider-b 回答 1",
                "继续，并引用第二个回答"
            ],
            providerA.Requests[1].Messages.Select(item => item.Content).ToArray());
        Assert.Equal(["provider-a", "provider-b", "provider-a"],
            invocations.Started.Select(item => item.ProviderId).ToArray());
        Assert.All(invocations.Started, item =>
        {
            Assert.Equal("chat.general", item.PromptId);
            Assert.Equal("1", item.PromptVersion);
            Assert.Equal(AiInvocationPurpose.Conversation, item.Purpose);
        });
        Assert.Equal(3, invocations.Completed.Count);
    }

    [Fact]
    public async Task Cancel_targets_the_provider_frozen_for_the_active_conversation_turn()
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "请给一个很长的回答");
        var provider = new BlockingChatProvider();
        var settings = new MutableAiSettingsStore("provider-a", "model-a");
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(new ChatProviderRegistry([provider]), settings),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);

        var running = routed.SendAsync(Request(conversationId, "请给一个很长的回答"));
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await routed.CancelAsync(conversationId);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ConversationProviderOutcome.Cancelled, result.Outcome);
        Assert.Equal(provider.ActiveTurnId, provider.CancelledTurnId);
        Assert.DoesNotContain("旧回答", result.Reply ?? string.Empty, StringComparison.Ordinal);
        Assert.Single(invocations.Failed);
        Assert.Equal(AiInvocationStatus.Cancelled, invocations.Failed[0].Status);
    }

    [Fact]
    public async Task Provider_failure_returns_only_the_typed_safe_message()
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "你好");
        var provider = new FailingChatProvider();
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(
                new ChatProviderRegistry([provider]),
                new MutableAiSettingsStore("provider-a", "model-a")),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);

        var result = await routed.SendAsync(Request(conversationId, "你好"));

        Assert.Equal(ConversationProviderOutcome.Failed, result.Outcome);
        Assert.Equal("rate_limited", result.FailureCode);
        Assert.Equal("服务繁忙，请稍后再试。", result.FailureMessage);
        Assert.DoesNotContain("RAW_SECRET", result.FailureMessage, StringComparison.Ordinal);
        Assert.Equal("rate_limited", Assert.Single(invocations.Failed).FailureCode);
    }

    private static ConversationProviderRequest Request(Guid conversationId, string message) =>
        new(conversationId, Guid.NewGuid(), message, null);

    private static Task<PromptRegistry> LoadRepositoryPromptsAsync() =>
        PromptRegistry.LoadAsync(Path.Combine(FindRepositoryRoot(), "prompts", "runtime"));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ScreenGuide.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
               ?? throw new DirectoryNotFoundException("找不到测试仓库根目录。");
    }

    private sealed class MutableAiSettingsStore(string providerId, string modelId) : IAiSettingsStore
    {
        private AiSettings _settings = new(new ChatModelRoute(providerId, modelId));

        public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings);

        public Task SaveAsync(AiSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingChatProvider(string providerId, string modelId) : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            $"{providerId} test destination",
            true,
            [new ChatModelDescriptor(modelId, modelId, ChatModelCapabilities.None)]);

        public List<ChatModelRequest> Requests { get; } = [];

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ChatModelResponse(
                $"{providerId} 回答 {Requests.Count}",
                ChatFinishReason.Stop,
                new ChatModelUsage(10, 5, 15),
                new ChatProviderMetadata(providerId, modelId, $"request-{Requests.Count}", Descriptor.DataDestination)));
        }

        public Task<ChatProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingChatProvider : IChatModelProvider
    {
        private readonly TaskCompletionSource _cancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChatProviderDescriptor Descriptor { get; } = new(
            "provider-a",
            "Provider A",
            "Provider A test destination",
            true,
            [new ChatModelDescriptor("model-a", "Model A", ChatModelCapabilities.None)]);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid? ActiveTurnId { get; private set; }

        public Guid? CancelledTurnId { get; private set; }

        public async Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            ActiveTurnId = request.TurnId;
            Started.TrySetResult();
            await _cancelled.Task.WaitAsync(cancellationToken);
            return new ChatModelResponse(
                "旧回答",
                ChatFinishReason.Cancelled,
                null,
                new ChatProviderMetadata("provider-a", request.ModelId, null, Descriptor.DataDestination));
        }

        public Task<ChatProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
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

    private sealed class FailingChatProvider : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            "provider-a",
            "Provider A",
            "Provider A test destination",
            true,
            [new ChatModelDescriptor("model-a", "Model A", ChatModelCapabilities.None)]);

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default) =>
            throw new ChatModelException(
                "provider-a",
                request.ModelId,
                new ChatModelError(
                    ChatModelErrorKind.RateLimited,
                    "rate_limited",
                    "服务繁忙，请稍后再试。",
                    true));

        public Task<ChatProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingInvocationStore : IAiInvocationStore
    {
        public List<AiInvocationRecord> Started { get; } = [];

        public List<(Guid Id, string FinishReason)> Completed { get; } = [];

        public List<(Guid Id, AiInvocationStatus Status, string FailureCode)> Failed { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartAsync(AiInvocationRecord invocation, CancellationToken cancellationToken = default)
        {
            Started.Add(invocation);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            Guid invocationId,
            string finishReason,
            AiTokenUsage? usage,
            string? providerRequestId,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Completed.Add((invocationId, finishReason));
            return Task.CompletedTask;
        }

        public Task FailAsync(
            Guid invocationId,
            AiInvocationStatus status,
            string failureCode,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Failed.Add((invocationId, status, failureCode));
            return Task.CompletedTask;
        }

        public Task<AiInvocationRecord?> GetAsync(Guid invocationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AiInvocationRecord?>(null);

        public Task<IReadOnlyList<AiInvocationRecord>> GetForConversationTurnAsync(
            Guid conversationTurnId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiInvocationRecord>>([]);
    }

    private sealed class HistoryStore(Guid conversationId) : IConversationStore
    {
        private readonly List<ConversationMessageRecord> _messages = [];

        public void Add(ConversationMessageRole role, string content) => _messages.Add(new ConversationMessageRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SequenceNumber = _messages.Count + 1,
            Role = role,
            Content = content,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        public Task<IReadOnlyList<ConversationMessageRecord>> GetMessagesAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationMessageRecord>>(_messages.ToArray());

        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConversationRecord?> GetConversationAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConversationRecord>> GetConversationsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConversationTurnRecord>> GetTurnsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConversationTurnRegistration> StartTurnAsync(Guid id, Guid turnId, string message, string key, DateTimeOffset startedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RecordProviderStartedAsync(Guid id, Guid turnId, string thread, int processId, DateTimeOffset startedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteTurnAsync(Guid id, Guid turnId, string reply, string? providerMessageId, DateTimeOffset completedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task FailTurnAsync(Guid id, Guid turnId, ConversationTurnStatus status, string code, string message, DateTimeOffset completedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConversationRecoveryResult> RecoverInterruptedAsync(DateTimeOffset recoveredAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
