using System.Text.Json;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Ai;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class ModelSemanticIntentSuggesterTests
{
    [Fact]
    public async Task NonCandidateConversationNeverCallsAModelOrCreatesAnInvocation()
    {
        var provider = new RecordingChatProvider(ChatModelCapabilities.None);
        var invocations = new RecordingInvocationStore();
        var suggester = CreateSuggester(
            provider,
            invocations,
            await LoadRepositoryPromptsAsync());

        var suggestion = await suggester.SuggestAsync("第二个方案详细一点");

        Assert.Null(suggestion);
        Assert.Empty(provider.Requests);
        Assert.Empty(invocations.Started);
        Assert.Empty(invocations.Completed);
        Assert.Empty(invocations.Failed);
    }

    [Fact]
    public async Task UsesFrozenDefaultRouteAndSendsOnlyOriginalUserTextWithRegisteredPrompt()
    {
        const string originalText = "打开刚才那个 ORIGINAL_ONLY_SENTINEL";
        var provider = new RecordingChatProvider(ChatModelCapabilities.None);
        var invocations = new RecordingInvocationStore();
        var prompts = await LoadRepositoryPromptsAsync();
        var suggester = CreateSuggester(provider, invocations, prompts);

        var suggestion = await suggester.SuggestAsync(originalText);

        Assert.NotNull(suggestion);
        Assert.Equal(SemanticIntentKind.OpenFile, suggestion.Kind);
        var request = Assert.Single(provider.Requests);
        Assert.Equal("model-a", request.ModelId);
        Assert.Equal(prompts.GetRequired("intent.semantic", "1", "provider-a").Content, request.SystemPrompt);
        Assert.Contains("不拥有任何操作权、授权权或确认权", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("不得输出 authorized", request.SystemPrompt, StringComparison.Ordinal);
        var message = Assert.Single(request.Messages);
        Assert.Equal(ChatMessageRole.User, message.Role);
        Assert.Equal(originalText, message.Content);
        Assert.Null(message.Name);
        Assert.Null(request.Metadata);
        Assert.Equal(ChatResponseFormatKind.Text, request.ResponseFormat?.Kind);
        Assert.Equal("intent.semantic", request.Prompt?.PromptId);
        Assert.Equal("1", request.Prompt?.Version);

        var invocation = Assert.Single(invocations.Started);
        Assert.Equal(AiInvocationPurpose.SemanticIntent, invocation.Purpose);
        Assert.Equal("provider-a", invocation.ProviderId);
        Assert.Equal("model-a", invocation.ModelId);
        Assert.Equal("intent.semantic", invocation.PromptId);
        Assert.Equal("1", invocation.PromptVersion);
        Assert.Equal(AiInvocationStatus.Running, invocation.Status);
        Assert.Single(invocations.Completed);
        var persistedAudit = JsonSerializer.Serialize(invocation);
        Assert.DoesNotContain(originalText, persistedAudit, StringComparison.Ordinal);
        Assert.DoesNotContain(provider.ResponseText, persistedAudit, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ChatModelCapabilities.None, ChatResponseFormatKind.Text)]
    [InlineData(ChatModelCapabilities.JsonObjectOutput, ChatResponseFormatKind.JsonObject)]
    [InlineData(ChatModelCapabilities.JsonSchemaOutput, ChatResponseFormatKind.JsonSchema)]
    [InlineData(
        ChatModelCapabilities.JsonObjectOutput | ChatModelCapabilities.JsonSchemaOutput,
        ChatResponseFormatKind.JsonSchema)]
    public async Task RequestsOnlyTheStructuredResponseFormatAdvertisedByTheFrozenModel(
        ChatModelCapabilities capabilities,
        ChatResponseFormatKind expectedFormat)
    {
        var provider = new RecordingChatProvider(capabilities);
        var suggester = CreateSuggester(
            provider,
            new RecordingInvocationStore(),
            await LoadRepositoryPromptsAsync());

        _ = await suggester.SuggestAsync("处理刚才那个");

        var format = Assert.Single(provider.Requests).ResponseFormat;
        Assert.NotNull(format);
        Assert.Equal(expectedFormat, format.Kind);
        if (expectedFormat == ChatResponseFormatKind.JsonSchema)
        {
            Assert.Equal("semantic_intent_suggestion", format.SchemaName);
            Assert.Contains("\"additionalProperties\":false", format.SchemaJson, StringComparison.Ordinal);
            Assert.DoesNotContain("authorized", format.SchemaJson, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RejectsMaliciousAuthorizationAndPathFieldsAndAuditsInvalidFormat()
    {
        var provider = new RecordingChatProvider(
            ChatModelCapabilities.JsonObjectOutput,
            """
            {
              "kind": "OpenFile",
              "target": "C:\\private\\not-authorized.txt",
              "confidence": 1,
              "isAmbiguous": false,
              "missingContext": "None",
              "authorized": true
            }
            """);
        var invocations = new RecordingInvocationStore();
        var suggester = CreateSuggester(
            provider,
            invocations,
            await LoadRepositoryPromptsAsync());

        var suggestion = await suggester.SuggestAsync("打开刚才那个");

        Assert.Null(suggestion);
        var failure = Assert.Single(invocations.Failed);
        Assert.Equal(AiInvocationStatus.Failed, failure.Status);
        Assert.Equal("semantic_intent_invalid_format", failure.FailureCode);
        Assert.Empty(invocations.Completed);
        Assert.DoesNotContain(
            "not-authorized.txt",
            JsonSerializer.Serialize(invocations.Started),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProviderFailureReturnsNullAndRecordsSafeFailedTerminalState()
    {
        var provider = new RecordingChatProvider(
            ChatModelCapabilities.None,
            handler: (_, _) => throw new ChatModelException(
                "provider-a",
                "model-a",
                new ChatModelError(
                    ChatModelErrorKind.RateLimited,
                    "rate_limited",
                    "RAW_PROVIDER_SECRET C:\\private\\prompt.txt",
                    IsRetryable: true)));
        var invocations = new RecordingInvocationStore();
        var suggester = CreateSuggester(
            provider,
            invocations,
            await LoadRepositoryPromptsAsync());

        var suggestion = await suggester.SuggestAsync("看看这个");

        Assert.Null(suggestion);
        var failure = Assert.Single(invocations.Failed);
        Assert.Equal(AiInvocationStatus.Failed, failure.Status);
        Assert.Equal("rate_limited", failure.FailureCode);
        Assert.DoesNotContain("RAW_PROVIDER_SECRET", failure.FailureCode, StringComparison.Ordinal);
        Assert.Empty(invocations.Completed);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndRecordsCancelledTerminalState()
    {
        var provider = new RecordingChatProvider(
            ChatModelCapabilities.None,
            handler: async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            });
        var invocations = new RecordingInvocationStore();
        var suggester = CreateSuggester(
            provider,
            invocations,
            await LoadRepositoryPromptsAsync());
        using var cancellation = new CancellationTokenSource();
        var running = suggester.SuggestAsync("处理刚才那个", cancellation.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(3)));

        var failure = Assert.Single(invocations.Failed);
        Assert.Equal(AiInvocationStatus.Cancelled, failure.Status);
        Assert.Equal("cancelled", failure.FailureCode);
        Assert.Empty(invocations.Completed);
    }

    private static ModelSemanticIntentSuggester CreateSuggester(
        IChatModelProvider provider,
        IAiInvocationStore invocations,
        PromptRegistry prompts) =>
        new(
            new ModelRouter(
                new ChatProviderRegistry([provider]),
                new FixedAiSettingsStore(provider.Descriptor.ProviderId, provider.Descriptor.Models[0].ModelId)),
            prompts,
            invocations,
            TimeProvider.System);

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

    private sealed class FixedAiSettingsStore(string providerId, string modelId) : IAiSettingsStore
    {
        public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiSettings(new ChatModelRoute(providerId, modelId)));

        public Task SaveAsync(AiSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingChatProvider : IChatModelProvider
    {
        private readonly Func<ChatModelRequest, CancellationToken, Task<ChatModelResponse>> _handler;

        public RecordingChatProvider(
            ChatModelCapabilities capabilities,
            string? responseText = null,
            Func<ChatModelRequest, CancellationToken, Task<ChatModelResponse>>? handler = null)
        {
            Descriptor = new ChatProviderDescriptor(
                "provider-a",
                "Provider A",
                "Provider A test destination",
                SendsDataOffDevice: true,
                [new ChatModelDescriptor("model-a", "Model A", capabilities)]);
            ResponseText = responseText ?? """
                {
                  "kind": "OpenFile",
                  "target": null,
                  "confidence": 0.91,
                  "isAmbiguous": false,
                  "missingContext": "File"
                }
                """;
            _handler = handler ?? ((request, _) => Task.FromResult(Response(request, ResponseText)));
        }

        public ChatProviderDescriptor Descriptor { get; }

        public string ResponseText { get; }

        public List<ChatModelRequest> Requests { get; } = [];

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Started.TrySetResult();
            return _handler(request, cancellationToken);
        }

        public Task<ChatProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private ChatModelResponse Response(ChatModelRequest request, string text) => new(
            text,
            ChatFinishReason.Stop,
            new ChatModelUsage(12, 8, 20),
            new ChatProviderMetadata(
                Descriptor.ProviderId,
                request.ModelId,
                "provider-request-1",
                Descriptor.DataDestination));
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

        public Task<AiInvocationRecord?> GetAsync(
            Guid invocationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AiInvocationRecord?>(null);

        public Task<IReadOnlyList<AiInvocationRecord>> GetForConversationTurnAsync(
            Guid conversationTurnId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiInvocationRecord>>([]);
    }
}
