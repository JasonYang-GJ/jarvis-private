using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenGuide.Agent.Abstractions;
using ScreenGuide.Agent.Codex;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.Qwen;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class Stage2ModelRoutingEndToEndTests
{
    [Fact]
    public async Task ProductionFactoryCreatesIndependentCodexOptionsAndKeepsRoutedConversationProvider()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();

        var chatOptions = host.Services.GetRequiredService<CodexChatOptions>();
        var programmingOptions = host.Services.GetRequiredService<CodexConnectorOptions>();
        var productionConversation = host.Services.GetRequiredService<IConversationProvider>();
        var isolatedLegacyConversation = host.Services.GetRequiredService<CodexConversationProvider>();

        Assert.NotSame((object)programmingOptions, chatOptions);
        Assert.Same(chatOptions, host.Services.GetRequiredService<CodexChatOptions>());
        Assert.Same(programmingOptions, host.Services.GetRequiredService<CodexConnectorOptions>());
        Assert.Equal(programmingOptions.DataDirectory, chatOptions.DataDirectory);
        Assert.Equal(programmingOptions.ExecutablePath, chatOptions.ExecutablePath);
        Assert.Equal(programmingOptions.Model, chatOptions.Model);
        Assert.IsType<RoutedConversationProvider>(productionConversation);
        Assert.NotSame(productionConversation, isolatedLegacyConversation);
    }

    [Fact]
    public async Task ProductionFactoryCannotBeReplacedWithPubliclyEnabledCodexChatProvider()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var publiclyEnabledProvider = TryCreatePubliclyEnabledCodexChatProvider(environment.Options);
        var replacement = publiclyEnabledProvider ?? new CodexChatModelProvider(
            new CodexChatOptions(
                environment.Options.CodexDataDirectory,
                environment.Options.CodexExecutablePath));
        using var host = environment.BuildHost(services => services.AddSingleton(replacement));
        var provider = host.Services.GetRequiredService<CodexChatModelProvider>();
        var request = new ChatModelRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CodexChatModelProvider.DefaultModelId,
            "ECHO_CHAT_INPUT",
            [new ChatMessage(ChatMessageRole.User, "生产工厂不得执行这段普通聊天正文。")]);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(request));

        Assert.Equal(ChatModelErrorKind.PolicyDisabled, exception.Error.Kind);
        Assert.Equal("codex.policy_disabled", exception.Error.Code);
        Assert.False(exception.Error.IsRetryable);
        Assert.False(Directory.Exists(environment.Options.CodexDataDirectory));
        Assert.Null(publiclyEnabledProvider);
        var publicConstructor = Assert.Single(typeof(CodexChatModelProvider).GetConstructors());
        var publicParameter = Assert.Single(publicConstructor.GetParameters());
        Assert.Equal(typeof(CodexChatOptions), publicParameter.ParameterType);
    }

    [Fact]
    public async Task SameSessionSwitchesAtoBtoAWithCompleteNeutralHistoryAndNoProviderCrossTalk()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var providerA = new ControllableChatProvider("provider-a", "model-a");
        var providerB = new ControllableChatProvider("provider-b", "model-b");
        using var host = BuildHost(environment, providerA, providerB);
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        await SetRouteAsync(client, providerA);
        var session = await client.StartNewSessionAsync("阶段 2 跨 Provider 连续对话");
        var first = await SubmitAndWaitAsync(client, session.SessionId, "代号甲是蓝鹭。", "route-a-first");

        await SetRouteAsync(client, providerB);
        var second = await SubmitAndWaitAsync(client, session.SessionId, "甲代表什么？", "route-b-second");

        await SetRouteAsync(client, providerA);
        var third = await SubmitAndWaitAsync(client, session.SessionId, "请引用前两轮。", "route-a-third");
        var snapshot = await client.GetCurrentSessionAsync();
        var invocationStore = host.Services.GetRequiredService<IAiInvocationStore>();
        var invocationRoutes = new List<(string ProviderId, string ModelId)>();
        foreach (var turn in new[] { first, second, third })
        {
            var conversationTurnId = turn.Turns[^1].ConversationTurnId;
            Assert.NotNull(conversationTurnId);
            var invocation = Assert.Single(await invocationStore.GetForConversationTurnAsync(
                conversationTurnId.Value));
            invocationRoutes.Add((invocation.ProviderId, invocation.ModelId));
        }

        await host.StopAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(session.SessionId, snapshot.SessionId);
        Assert.Equal(session.ConversationId, snapshot.ConversationId);
        Assert.Equal(3, snapshot.Turns.Count);
        Assert.All(snapshot.Turns, turn => Assert.Equal("Completed", turn.Phase));
        Assert.Equal(
            [
                "代号甲是蓝鹭。",
                "provider-a reply 1",
                "甲代表什么？",
                "provider-b reply 1",
                "请引用前两轮。",
                "provider-a reply 2"
            ],
            snapshot.Messages.Select(message => message.Content).ToArray());
        Assert.Equal(
            [("provider-a", "model-a"), ("provider-b", "model-b"), ("provider-a", "model-a")],
            invocationRoutes);

        Assert.Equal(2, providerA.Requests.Count);
        Assert.Single(providerB.Requests);
        AssertHistory(providerA.Requests[0], "代号甲是蓝鹭。");
        AssertHistory(
            providerB.Requests[0],
            "代号甲是蓝鹭。",
            "provider-a reply 1",
            "甲代表什么？");
        AssertHistory(
            providerA.Requests[1],
            "代号甲是蓝鹭。",
            "provider-a reply 1",
            "甲代表什么？",
            "provider-b reply 1",
            "请引用前两轮。");
        Assert.All(providerA.Requests, request => Assert.Equal("model-a", request.ModelId));
        Assert.All(providerB.Requests, request => Assert.Equal("model-b", request.ModelId));
    }

    [Fact]
    public async Task RouteChangeDuringActiveTurnOnlyAffectsTheNextTurn()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var providerA = new ControllableChatProvider("provider-a", "model-a");
        var providerB = new ControllableChatProvider("provider-b", "model-b");
        providerA.BlockRequest(1);
        using var host = BuildHost(environment, providerA, providerB);
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        await SetRouteAsync(client, providerA);
        var session = await client.StartNewSessionAsync("冻结当前 Turn 路由");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "第一轮保持在 A。",
            "Text",
            "frozen-route-first",
            session.SessionId));
        await providerA.WaitForBlockedRequestAsync().WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var changed = await SetRouteAsync(client, providerB);
            Assert.Equal("provider-b", changed.CurrentChatRoute.ProviderId);
            Assert.Equal("model-b", changed.CurrentChatRoute.ModelId);
        }
        finally
        {
            providerA.ReleaseBlockedRequest();
        }

        var first = await WaitForTerminalTurnAsync(client, submitted.TurnId, TimeSpan.FromSeconds(10));
        Assert.Equal("Completed", first.Turns.Single(turn => turn.Id == submitted.TurnId).Phase);
        var second = await SubmitAndWaitAsync(
            client,
            session.SessionId,
            "第二轮使用新路由。",
            "frozen-route-second");
        await host.StopAsync();

        Assert.Equal("Completed", second.Turns[^1].Phase);
        Assert.Single(providerA.Requests);
        Assert.Single(providerB.Requests);
        Assert.Equal("model-a", providerA.Requests[0].ModelId);
        Assert.Equal("model-b", providerB.Requests[0].ModelId);
        AssertHistory(
            providerB.Requests[0],
            "第一轮保持在 A。",
            "provider-a reply 1",
            "第二轮使用新路由。");
    }

    [Fact]
    public async Task ProviderFailureReleasesSessionAndTheNextTurnCanRetry()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var providerA = new ControllableChatProvider("provider-a", "model-a");
        var providerB = new ControllableChatProvider("provider-b", "model-b");
        providerA.FailNextRequest();
        using var host = BuildHost(environment, providerA, providerB);
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        await SetRouteAsync(client, providerA);
        var session = await client.StartNewSessionAsync("Provider 失败后重试");
        var failedSubmission = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "第一次会失败。",
            "Text",
            "model-provider-failure",
            session.SessionId));
        var failed = await WaitForTerminalTurnAsync(
            client,
            failedSubmission.TurnId,
            TimeSpan.FromSeconds(10));

        Assert.Equal("Failed", failed.Turns.Single(turn => turn.Id == failedSubmission.TurnId).Phase);
        Assert.Empty(failed.ActiveTurns);
        Assert.NotNull(failed.Turns.Single(turn => turn.Id == failedSubmission.TurnId).FailureMessage);

        var recovered = await SubmitAndWaitAsync(
            client,
            session.SessionId,
            "第二次继续同一会话。",
            "model-provider-retry");
        await host.StopAsync();

        Assert.Equal(session.SessionId, recovered.SessionId);
        Assert.Equal(["Failed", "Completed"], recovered.Turns.Select(turn => turn.Phase).ToArray());
        Assert.Empty(recovered.ActiveTurns);
        Assert.Equal(2, providerA.Requests.Count);
        Assert.Empty(providerB.Requests);
        Assert.False(string.IsNullOrWhiteSpace(providerA.Requests[1].SystemPrompt));
        Assert.Equal(
            ["第一次会失败。", "第二次继续同一会话。"],
            providerA.Requests[1].Messages.Select(message => message.Content).ToArray());
        Assert.Equal(
            [ChatMessageRole.User, ChatMessageRole.User],
            providerA.Requests[1].Messages.Select(message => message.Role).ToArray());
        Assert.Equal("provider-a reply 2", recovered.Messages[^1].Content);
    }

    [Fact]
    public async Task OrdinaryChatRouteSwitchDoesNotReplaceCodexProgrammingConnectorOrTaskLifecycle()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, _, task) = await environment.SeedTaskAsync("TEST_SUCCESS");
        var providerA = new ControllableChatProvider("provider-a", "model-a");
        var providerB = new ControllableChatProvider("provider-b", "model-b");
        using var host = BuildHost(environment, providerA, providerB);
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var connectorBefore = host.Services.GetRequiredService<IAgentConnector>();
        var connectorRegistry = host.Services.GetRequiredService<AgentConnectorRegistry>();

        Assert.Equal("codex", connectorBefore.ConnectorId);
        Assert.Same(connectorBefore, connectorRegistry.GetRequired("codex"));

        var firstRoute = await SetRouteAsync(client, providerA);
        var session = await client.StartNewSessionAsync("聊天和编程通道隔离");
        await SubmitAndWaitAsync(client, session.SessionId, "先聊第一轮。", "chat-before-code-a");
        var secondRoute = await SetRouteAsync(client, providerB);
        await SubmitAndWaitAsync(client, session.SessionId, "再聊第二轮。", "chat-before-code-b");

        var connectorAfter = host.Services.GetRequiredService<IAgentConnector>();
        var execution = host.Services.GetRequiredService<AgentTaskExecutionService>();
        await execution.StartTaskAsync(task.Id);
        await execution.WaitForTaskAsync(task.Id);
        var store = host.Services.GetRequiredService<ILocalTaskStore>();
        var persistedTask = await store.GetTaskAsync(task.Id);
        var run = await store.GetAgentRunByTaskAsync(task.Id);
        var invocation = Assert.Single(await store.GetSkillInvocationsAsync(task.Id));
        await host.StopAsync();

        Assert.Equal("Codex", firstRoute.ProgrammingAgent);
        Assert.Equal("Codex", secondRoute.ProgrammingAgent);
        Assert.Same(connectorBefore, connectorAfter);
        Assert.Same(connectorAfter, connectorRegistry.GetRequired("codex"));
        Assert.Equal(AgentTaskStatus.Succeeded, persistedTask?.Status);
        Assert.Equal("codex", run?.ConnectorId);
        Assert.Equal("skill/codex.project-task", run?.Transport);
        Assert.Equal("codex.project-task", invocation.SkillId);
        Assert.Equal(SkillInvocationStatus.Succeeded, invocation.Status);
        Assert.Single(providerA.Requests);
        Assert.Single(providerB.Requests);
    }

    [Fact]
    public async Task ManuallySelectedQwenMissingCredentialFailsClosedWithoutCallingAnotherProvider()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var codex = new ControllableChatProvider("codex", "codex-default");
        var deepseek = new ControllableChatProvider("deepseek", "deepseek-v4-pro");
        var credentials = new MissingCredentialStore();
        await using var qwen = new QwenChatModelProvider(
            credentials,
            new NoHttpHandler());
        using var host = environment.BuildHost(services => services.AddSingleton(
            new ChatProviderRegistry([codex, deepseek, qwen])));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var route = await client.SetChatRouteAsync(
            new SetChatRouteRequestDto("qwen", "qwen3.7-plus"));
        var session = await client.StartNewSessionAsync("Qwen 手动备用失败关闭");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "请使用当前手动选择的千问回答。",
            "Text",
            "qwen-missing-key-no-fallback",
            session.SessionId));
        var terminal = await WaitForTerminalTurnAsync(
            client,
            submitted.TurnId,
            TimeSpan.FromSeconds(10));
        var invocations = await host.Services.GetRequiredService<IAiInvocationStore>()
            .GetForSessionTurnAsync(submitted.TurnId);
        await host.StopAsync();

        Assert.Equal(new AiChatRouteDto("qwen", "qwen3.7-plus"), route.CurrentChatRoute);
        Assert.Equal("Codex", route.ProgrammingAgent);
        var turn = terminal.Turns.Single(item => item.Id == submitted.TurnId);
        Assert.Equal("Failed", turn.Phase);
        Assert.Contains("尚未配置", turn.FailureMessage, StringComparison.Ordinal);
        Assert.NotEmpty(invocations);
        Assert.All(invocations, invocation =>
        {
            Assert.Equal("qwen", invocation.ProviderId);
            Assert.Equal("qwen3.7-plus", invocation.ModelId);
            Assert.Equal("qwen.not_configured", invocation.FailureCode);
        });
        Assert.Empty(codex.Requests);
        Assert.Empty(deepseek.Requests);
        Assert.True(credentials.OpenLeaseCount > 0);
    }

    private static IHost BuildHost(
        DesktopHostTestEnvironment environment,
        ControllableChatProvider providerA,
        ControllableChatProvider providerB) =>
        environment.BuildHost(services => services.AddSingleton(
            new ChatProviderRegistry([providerA, providerB])));

    private static CodexChatModelProvider? TryCreatePubliclyEnabledCodexChatProvider(
        ScreenGuide.DesktopHost.Configuration.DesktopHostOptions options)
    {
        var policyType = typeof(CodexChatModelProvider).Assembly
            .GetExportedTypes()
            .SingleOrDefault(type => string.Equals(
                type.FullName,
                "ScreenGuide.Agent.Codex.CodexChatModelExecutionPolicy",
                StringComparison.Ordinal));
        if (policyType is null)
        {
            return null;
        }

        var testPolicy = Enum.Parse(policyType, "TestOnlyAllowLocalCodexCli");
        var chatOptions = options.CodexExecutablePath is null
            ? CodexChatOptions.FromDataDirectory(options.CodexDataDirectory)
            : new CodexChatOptions(options.CodexDataDirectory, options.CodexExecutablePath);
        return Activator.CreateInstance(
            typeof(CodexChatModelProvider),
            [chatOptions, testPolicy]) as CodexChatModelProvider;
    }

    private static Task<AiSettingsDto> SetRouteAsync(
        IDesktopApiClient client,
        ControllableChatProvider provider) =>
        client.SetChatRouteAsync(new SetChatRouteRequestDto(
            provider.Descriptor.ProviderId,
            provider.Descriptor.Models.Single().ModelId));

    private static async Task<SessionSnapshotDto> SubmitAndWaitAsync(
        IDesktopApiClient client,
        Guid sessionId,
        string text,
        string idempotencyKey)
    {
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            text,
            "Text",
            idempotencyKey,
            sessionId));
        return await WaitForTerminalTurnAsync(client, submitted.TurnId, TimeSpan.FromSeconds(10));
    }

    private static async Task<SessionSnapshotDto> WaitForTerminalTurnAsync(
        IDesktopApiClient client,
        Guid turnId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await client.GetCurrentSessionAsync();
            var turn = snapshot?.Turns.SingleOrDefault(item => item.Id == turnId);
            if (turn is not null && turn.Phase is "Completed" or "Failed" or "Cancelled" or "Interrupted")
            {
                return snapshot!;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("阶段 2 模型路由 Turn 未在预期时间内结束。");
    }

    private static void AssertHistory(ChatModelRequest request, params string[] expected)
    {
        Assert.False(string.IsNullOrWhiteSpace(request.SystemPrompt));
        Assert.Equal(expected, request.Messages.Select(message => message.Content).ToArray());
        Assert.Equal(
            expected.Select((_, index) => index % 2 == 0 ? ChatMessageRole.User : ChatMessageRole.Assistant),
            request.Messages.Select(message => message.Role));
    }

    private sealed class ControllableChatProvider(string providerId, string modelId)
        : IChatModelProvider
    {
        private readonly object _gate = new();
        private readonly List<ChatModelRequest> _requests = [];
        private int _blockedRequestNumber;
        private int _failuresRemaining;
        private TaskCompletionSource<ChatModelRequest>? _blockedRequest;
        private TaskCompletionSource? _blockedRelease;

        public ChatProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            $"{providerId} isolated test destination",
            SendsDataOffDevice: false,
            [new ChatModelDescriptor(modelId, modelId, ChatModelCapabilities.None)]);

        public IReadOnlyList<ChatModelRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        public void BlockRequest(int requestNumber)
        {
            lock (_gate)
            {
                _blockedRequestNumber = requestNumber;
                _blockedRequest = new TaskCompletionSource<ChatModelRequest>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _blockedRelease = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public Task<ChatModelRequest> WaitForBlockedRequestAsync()
        {
            lock (_gate)
            {
                return (_blockedRequest
                        ?? throw new InvalidOperationException("没有配置阻塞请求。"))
                    .Task;
            }
        }

        public void ReleaseBlockedRequest()
        {
            lock (_gate)
            {
                _blockedRelease?.TrySetResult();
            }
        }

        public void FailNextRequest()
        {
            lock (_gate)
            {
                _failuresRemaining++;
            }
        }

        public async Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            int requestNumber;
            Task? blockedRelease = null;
            bool shouldFail;
            lock (_gate)
            {
                _requests.Add(request);
                requestNumber = _requests.Count;
                if (requestNumber == _blockedRequestNumber)
                {
                    _blockedRequest!.TrySetResult(request);
                    blockedRelease = _blockedRelease!.Task;
                }

                shouldFail = _failuresRemaining > 0;
                if (shouldFail)
                {
                    _failuresRemaining--;
                }
            }

            if (blockedRelease is not null)
            {
                await blockedRelease.WaitAsync(cancellationToken);
            }

            if (shouldFail)
            {
                throw new ChatModelException(
                    providerId,
                    request.ModelId,
                    new ChatModelError(
                        ChatModelErrorKind.Unavailable,
                        "fake_unavailable",
                        "测试 Provider 暂时不可用。",
                        IsRetryable: true));
            }

            return new ChatModelResponse(
                $"{providerId} reply {requestNumber}",
                ChatFinishReason.Stop,
                new ChatModelUsage(10, 5, 15),
                new ChatProviderMetadata(
                    providerId,
                    request.ModelId,
                    $"{providerId}-request-{requestNumber}",
                    Descriptor.DataDestination));
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatProviderHealth(
                providerId,
                ChatProviderHealthState.Healthy,
                IsConfigured: true,
                "测试 Provider 正常。",
                DateTimeOffset.UtcNow));

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            ReleaseBlockedRequest();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MissingCredentialStore : IProviderCredentialStore
    {
        public int OpenLeaseCount { get; private set; }

        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCredentialStatus(
                providerId,
                ProviderCredentialState.Missing,
                null));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            OpenLeaseCount++;
            return ValueTask.FromResult<IProviderCredentialLease?>(null);
        }

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Qwen 缺少凭据时不得进入 HTTP。");
    }
}
