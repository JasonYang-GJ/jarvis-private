using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class ChatModelContractsTests
{
    [Fact]
    public void HealthAndErrorContractsRepresentPolicyConfigurationAndAuthorization()
    {
        Assert.Equal("PolicyDisabled", ChatProviderHealthState.PolicyDisabled.ToString());
        Assert.Equal("Configuration", ChatModelErrorKind.Configuration.ToString());
        Assert.Equal("Authorization", ChatModelErrorKind.Authorization.ToString());
        Assert.Equal("PolicyDisabled", ChatModelErrorKind.PolicyDisabled.ToString());
    }

    [Theory]
    [InlineData(ChatModelErrorKind.RateLimited, true)]
    [InlineData(ChatModelErrorKind.Timeout, true)]
    [InlineData(ChatModelErrorKind.Network, true)]
    [InlineData(ChatModelErrorKind.Unavailable, true)]
    [InlineData(ChatModelErrorKind.Configuration, false)]
    [InlineData(ChatModelErrorKind.Unauthorized, false)]
    [InlineData(ChatModelErrorKind.Authorization, false)]
    [InlineData(ChatModelErrorKind.InsufficientBalance, false)]
    [InlineData(ChatModelErrorKind.InvalidRequest, false)]
    [InlineData(ChatModelErrorKind.ModelNotFound, false)]
    [InlineData(ChatModelErrorKind.InvalidResponse, false)]
    [InlineData(ChatModelErrorKind.Cancelled, false)]
    [InlineData(ChatModelErrorKind.PolicyDisabled, false)]
    [InlineData(ChatModelErrorKind.Unknown, false)]
    public void CoreOwnsTheRetryableMatrix(ChatModelErrorKind kind, bool expectedRetryable)
    {
        var error = new ChatModelError(kind, "provider.reason", "安全错误信息。");

        Assert.Equal(expectedRetryable, error.IsRetryable);
    }

    [Fact]
    public void CoreAcceptsOnlyBoundedTrustedRetryAfterFacts()
    {
        var validRateLimit = new ChatModelError(
            ChatModelErrorKind.RateLimited,
            "provider.rate_limited",
            "安全错误信息。",
            TimeSpan.FromSeconds(30));
        var validUnavailable = new ChatModelError(
            ChatModelErrorKind.Unavailable,
            "provider.unavailable",
            "安全错误信息。",
            TimeSpan.FromHours(24));
        var zero = new ChatModelError(
            ChatModelErrorKind.RateLimited,
            "provider.rate_limited",
            "安全错误信息。",
            TimeSpan.Zero);
        var tooLong = new ChatModelError(
            ChatModelErrorKind.RateLimited,
            "provider.rate_limited",
            "安全错误信息。",
            TimeSpan.FromHours(24) + TimeSpan.FromTicks(1));
        var unsupportedKind = new ChatModelError(
            ChatModelErrorKind.Timeout,
            "provider.timeout",
            "安全错误信息。",
            TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), validRateLimit.RetryAfter);
        Assert.Equal(TimeSpan.FromHours(24), validUnavailable.RetryAfter);
        Assert.Null(zero.RetryAfter);
        Assert.Null(tooLong.RetryAfter);
        Assert.Null(unsupportedKind.RetryAfter);
    }

    [Fact]
    public void DiagnosticCodesAcceptNewAndLegacySafeFormsButRejectSensitivePayloads()
    {
        var current = new ChatModelError(
            ChatModelErrorKind.RateLimited,
            "deepseek.rate_limited",
            "安全错误信息。");
        var legacy = new ChatModelError(
            ChatModelErrorKind.RateLimited,
            "deepseek_rate_limited",
            "安全错误信息。");
        var malicious = new ChatModelError(
            ChatModelErrorKind.Unknown,
            "Bearer fake-secret\nC:\\Users\\Example\\private.txt",
            "安全错误信息。");
        var oversized = new ChatModelError(
            ChatModelErrorKind.Unknown,
            new string('a', 81),
            "安全错误信息。");

        Assert.Equal("deepseek.rate_limited", current.Code);
        Assert.Equal("deepseek_rate_limited", legacy.Code);
        Assert.Equal("component.invalid_diagnostic_code", malicious.Code);
        Assert.Equal("component.invalid_diagnostic_code", oversized.Code);
    }

    [Fact]
    public async Task ProviderReceivesSupplierNeutralRequestAndCancelsByTurn()
    {
        var provider = new RecordingProvider();
        var turnId = Guid.NewGuid();
        var request = new ChatModelRequest(
            Guid.NewGuid(),
            turnId,
            "chat-model-a",
            "只回答问题，不执行电脑操作。",
            [
                new ChatMessage(ChatMessageRole.User, "第一个方案是什么？"),
                new ChatMessage(ChatMessageRole.Assistant, "第一个方案是 A。"),
                new ChatMessage(ChatMessageRole.User, "继续说刚才那个。")
            ],
            new ChatModelOptions(Temperature: 0.2, MaxOutputTokens: 512),
            ChatResponseFormat.JsonSchema("answer-v1", "{\"type\":\"object\"}"),
            new ChatPromptReference("chat.general", "1", "ABC123"));
        var streamed = new List<string>();

        var response = await provider.CompleteAsync(
            request,
            (update, _) =>
            {
                streamed.Add(update.DeltaText);
                return ValueTask.CompletedTask;
            });
        await provider.CancelAsync(turnId);

        Assert.Same(request, provider.LastRequest);
        Assert.Equal(["继续", "回答"], streamed);
        Assert.Equal("完整回答", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(new ChatModelUsage(21, 8, 29), response.Usage);
        Assert.Equal("provider-a", response.Metadata.ProviderId);
        Assert.Equal("chat-model-a", response.Metadata.ModelId);
        Assert.Equal(turnId, provider.CancelledTurnId);
    }

    [Fact]
    public void TypedProviderErrorCarriesSafeFailureInformation()
    {
        var error = new ChatModelError(
            ChatModelErrorKind.RateLimited,
            "rate_limited",
            "这个 AI 服务暂时繁忙，请稍后再试。",
            IsRetryable: true,
            RetryAfter: TimeSpan.FromSeconds(30));

        var exception = new ChatModelException("provider-a", "chat-model-a", error);

        Assert.Equal(ChatModelErrorKind.RateLimited, exception.Error.Kind);
        Assert.True(exception.Error.IsRetryable);
        Assert.Equal("provider-a", exception.ProviderId);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonObjectAndJsonSchemaCapabilitiesAreDeclaredSeparately()
    {
        var capabilities = ChatModelCapabilities.JsonObjectOutput;

        Assert.True(capabilities.HasFlag(ChatModelCapabilities.JsonObjectOutput));
        Assert.False(capabilities.HasFlag(ChatModelCapabilities.JsonSchemaOutput));
    }

    [Fact]
    public void ProviderDescriptorDeclaresWhetherYuanshuMustStoreAnApiKey()
    {
        var descriptor = new ChatProviderDescriptor(
            "provider-a",
            "Provider A",
            "api.provider-a.example",
            SendsDataOffDevice: true,
            [new ChatModelDescriptor("model-a", "Model A", ChatModelCapabilities.None)],
            ChatProviderCredentialKind.ApiKey);

        Assert.Equal(ChatProviderCredentialKind.ApiKey, descriptor.CredentialKind);
    }

    private sealed class RecordingProvider : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            "provider-a",
            "Provider A",
            "api.provider-a.example",
            SendsDataOffDevice: true,
            [
                new ChatModelDescriptor(
                    "chat-model-a",
                    "Chat Model A",
                    ChatModelCapabilities.Streaming |
                    ChatModelCapabilities.StructuredOutput,
                    ContextWindowTokens: 32_000)
            ]);

        public ChatModelRequest? LastRequest { get; private set; }

        public Guid? CancelledTurnId { get; private set; }

        public async Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (streamCallback is not null)
            {
                await streamCallback(new ChatStreamUpdate(1, "继续"), cancellationToken);
                await streamCallback(new ChatStreamUpdate(2, "回答", IsFinal: true), cancellationToken);
            }

            return new ChatModelResponse(
                "完整回答",
                ChatFinishReason.Stop,
                new ChatModelUsage(21, 8, 29),
                new ChatProviderMetadata(
                    Descriptor.ProviderId,
                    request.ModelId,
                    "request-123",
                    Descriptor.DataDestination));
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatProviderHealth(
                Descriptor.ProviderId,
                ChatProviderHealthState.Healthy,
                IsConfigured: true,
                "可用",
                DateTimeOffset.Parse("2026-08-24T00:00:00Z")));

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
        {
            CancelledTurnId = turnId;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
