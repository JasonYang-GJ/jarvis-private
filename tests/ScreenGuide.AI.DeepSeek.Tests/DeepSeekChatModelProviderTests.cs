using System.Net;
using System.Text;
using System.Text.Json;
using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.DeepSeek.Tests;

public sealed class DeepSeekChatModelProviderTests
{
    [Fact]
    public async Task DescriptorExposesOnlyTheFixedDeepSeekDestinationAndOfficialModels()
    {
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore(),
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        Assert.Equal("deepseek", provider.Descriptor.ProviderId);
        Assert.Equal("DeepSeek", provider.Descriptor.DisplayName);
        Assert.Equal("https://api.deepseek.com", provider.Descriptor.DataDestination);
        Assert.True(provider.Descriptor.SendsDataOffDevice);
        Assert.Equal(ChatProviderCredentialKind.ApiKey, provider.Descriptor.CredentialKind);
        Assert.Equal(
            ["deepseek-v4-flash", "deepseek-v4-pro"],
            provider.Descriptor.Models.Select(model => model.ModelId).ToArray());
        Assert.All(provider.Descriptor.Models, model =>
        {
            Assert.Equal(1_000_000, model.ContextWindowTokens);
            Assert.Equal(
                ChatModelCapabilities.Streaming
                | ChatModelCapabilities.JsonObjectOutput
                | ChatModelCapabilities.Reasoning,
                model.Capabilities);
        });
    }

    [Fact]
    public async Task NonStreamingCompletionUsesCredentialLeaseAndMapsJsonObjectUsageAndFinishReason()
    {
        const string secret = "ds-test-secret-never-log";
        var credentialStore = new TestCredentialStore(secret);
        HttpMethod? observedMethod = null;
        Uri? observedUri = null;
        string? observedAuthorization = null;
        string? observedBody = null;
        await using var provider = new DeepSeekChatModelProvider(
            credentialStore,
            new StubHttpMessageHandler(async (request, cancellationToken) =>
            {
                observedMethod = request.Method;
                observedUri = request.RequestUri;
                observedAuthorization = request.Headers.Authorization?.ToString();
                observedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse("""
                    {
                      "id": "req-deepseek-1",
                      "model": "deepseek-v4-flash",
                      "choices": [
                        {
                          "message": { "role": "assistant", "content": "{\"answer\":\"你好\"}" },
                          "finish_reason": "stop"
                        }
                      ],
                      "usage": {
                        "prompt_tokens": 12,
                        "completion_tokens": 5,
                        "total_tokens": 17,
                        "prompt_cache_hit_tokens": 3,
                        "completion_tokens_details": { "reasoning_tokens": 2 }
                      }
                    }
                    """);
            }));

        var response = await provider.CompleteAsync(new ChatModelRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DeepSeekChatModelProvider.FlashModelId,
            "你是元枢。",
            [
                new ChatMessage(ChatMessageRole.User, "第一轮"),
                new ChatMessage(ChatMessageRole.Assistant, "上一轮回答"),
                new ChatMessage(ChatMessageRole.User, "继续")
            ],
            new ChatModelOptions(Temperature: 0.2, MaxOutputTokens: 512, TopP: 0.9, Seed: 7),
            ChatResponseFormat.JsonObject,
            new ChatPromptReference("chat.general", "2", "ABC123")));

        Assert.Equal(HttpMethod.Post, observedMethod);
        Assert.Equal("https://api.deepseek.com/chat/completions", observedUri?.AbsoluteUri);
        Assert.Equal($"Bearer {secret}", observedAuthorization);
        Assert.True(credentialStore.LastLeaseDisposed);
        using var body = JsonDocument.Parse(observedBody!);
        Assert.Equal("deepseek-v4-flash", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(4, body.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.DoesNotContain(secret, observedBody, StringComparison.Ordinal);

        Assert.Equal("{\"answer\":\"你好\"}", response.Text);
        Assert.Equal(response.Text, response.StructuredJson);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(new ChatModelUsage(12, 5, 17, 3, 2), response.Usage);
        Assert.Equal("deepseek", response.Metadata.ProviderId);
        Assert.Equal("deepseek-v4-flash", response.Metadata.ModelId);
        Assert.Equal("req-deepseek-1", response.Metadata.ProviderRequestId);
        Assert.Equal("https://api.deepseek.com", response.Metadata.DataDestination);
    }

    [Fact]
    public async Task JsonSchemaIsRejectedBeforeCredentialOrNetworkAccess()
    {
        var credentials = new TestCredentialStore("ds-never-open");
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("Network must not be reached."));
        await using var provider = new DeepSeekChatModelProvider(credentials, handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(
                responseFormat: ChatResponseFormat.JsonSchema(
                    "answer-v1",
                    "{\"type\":\"object\"}"))));

        Assert.Equal(ChatModelErrorKind.InvalidRequest, exception.Error.Kind);
        Assert.Equal("deepseek.json_schema_not_supported", exception.Error.Code);
        Assert.Equal(0, credentials.OpenLeaseCount);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task MissingCredentialAndUnknownModelNeverReachTheNetwork()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("Network must not be reached."));
        await using var provider = new DeepSeekChatModelProvider(new TestCredentialStore(), handler);

        var missing = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));
        var unknown = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(modelId: "deepseek-unknown")));

        Assert.Equal(ChatModelErrorKind.Configuration, missing.Error.Kind);
        Assert.Equal("deepseek.not_configured", missing.Error.Code);
        Assert.Equal(ChatModelErrorKind.ModelNotFound, unknown.Error.Kind);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task InputLimitsAreEnforcedBeforeCredentialOrNetworkAccess()
    {
        var credentials = new TestCredentialStore("ds-never-open");
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("Network must not be reached."));
        await using var provider = new DeepSeekChatModelProvider(
            credentials,
            handler,
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaxMessageCount: 2,
                MaxInputCharacters: 10,
                MaxOutputCharacters: 20,
                MaxErrorBodyCharacters: 16));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(messages:
                [new ChatMessage(ChatMessageRole.User, "12345678901")])));

        Assert.Equal(ChatModelErrorKind.InvalidRequest, exception.Error.Kind);
        Assert.Equal("deepseek.input_too_large", exception.Error.Code);
        Assert.Equal(0, credentials.OpenLeaseCount);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task StreamingCompletionIgnoresKeepAliveRecognizesDoneAndMapsUsage()
    {
        const string sse = """
            : keep-alive

            data: {"id":"req-stream-1","choices":[{"delta":{"content":"你"},"finish_reason":null}]}

            data:{"id":"req-stream-1","choices":[{"delta":{"content":"好"},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":2,"total_tokens":9}}

            data:[DONE]

            """;
        string? observedBody = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            observedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        });
        var streamCredentials = new TestCredentialStore("ds-stream-test");
        await using var provider = new DeepSeekChatModelProvider(
            streamCredentials,
            handler);
        var updates = new List<ChatStreamUpdate>();

        var response = await provider.CompleteAsync(
            Request(),
            (update, _) =>
            {
                Assert.True(
                    streamCredentials.LastLeaseDisposed,
                    "响应头到达后应在处理 SSE 内容前释放凭据租约。");
                updates.Add(update);
                return ValueTask.CompletedTask;
            });

        using var body = JsonDocument.Parse(observedBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(["你", "好", ""], updates.Select(update => update.DeltaText).ToArray());
        Assert.Equal([1L, 2L, 3L], updates.Select(update => update.SequenceNumber).ToArray());
        Assert.False(updates[0].IsFinal);
        Assert.False(updates[1].IsFinal);
        Assert.True(updates[2].IsFinal);
        Assert.Equal("你好", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(new ChatModelUsage(7, 2, 9), response.Usage);
        Assert.Equal("req-stream-1", response.Metadata.ProviderRequestId);
    }

    [Fact]
    public async Task CancelAsyncCancelsTheBoundTurnAndRejectsALateSuccessfulResponse()
    {
        var turnId = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLateSuccess = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => cancellationObserved.TrySetResult());
            started.TrySetResult();
            await allowLateSuccess.Task;
            return JsonResponse("""
                {
                  "id":"late-success",
                  "choices":[{"message":{"content":"不应出现"},"finish_reason":"stop"}]
                }
                """);
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-cancel-test"),
            handler);

        var completion = provider.CompleteAsync(Request(turnId: turnId));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancellation = provider.CancelAsync(turnId);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        allowLateSuccess.TrySetResult();
        await cancellation.WaitAsync(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<ChatModelException>(() => completion);

        Assert.Equal(ChatModelErrorKind.Cancelled, exception.Error.Kind);
        Assert.Equal("deepseek.cancelled", exception.Error.Code);
        Assert.DoesNotContain("ds-cancel-test", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("不应出现", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestTimeoutCancelsHttpAndMapsToTimeoutInsteadOfUserCancellation()
    {
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => cancellationObserved.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-timeout-test"),
            handler,
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromMilliseconds(50),
                MaxMessageCount: 10,
                MaxInputCharacters: 1_000,
                MaxOutputCharacters: 1_000,
                MaxErrorBodyCharacters: 100));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ChatModelErrorKind.Timeout, exception.Error.Kind);
        Assert.Equal("deepseek.timeout", exception.Error.Code);
        Assert.True(exception.Error.IsRetryable);
    }

    [Theory]
    [InlineData(400, ChatModelErrorKind.InvalidRequest, "deepseek.bad_request", false)]
    [InlineData(401, ChatModelErrorKind.Unauthorized, "deepseek.unauthorized", false)]
    [InlineData(402, ChatModelErrorKind.InsufficientBalance, "deepseek.insufficient_balance", false)]
    [InlineData(403, ChatModelErrorKind.Authorization, "deepseek.authorization_denied", false)]
    [InlineData(422, ChatModelErrorKind.InvalidRequest, "deepseek.unprocessable_request", false)]
    [InlineData(429, ChatModelErrorKind.RateLimited, "deepseek.rate_limited", true)]
    [InlineData(500, ChatModelErrorKind.Unavailable, "deepseek.server_error", true)]
    [InlineData(503, ChatModelErrorKind.Unavailable, "deepseek.unavailable", true)]
    public async Task HttpFailuresMapToStableSafeErrors(
        int statusCode,
        ChatModelErrorKind expectedKind,
        string expectedCode,
        bool retryable)
    {
        const string secret = "ds-http-failure-secret";
        var rawBody = "{\"error\":{\"code\":\"provider_error\",\"message\":\""
                      + secret
                      + new string('x', 2_000)
                      + "\"}}";
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)statusCode)
            {
                Content = new StringContent(rawBody, Encoding.UTF8, "application/json")
            };
            if (statusCode is 429 or 503)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(3));
            }

            return response;
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore(secret),
            handler,
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaxMessageCount: 10,
                MaxInputCharacters: 1_000,
                MaxOutputCharacters: 1_000,
                MaxErrorBodyCharacters: 64));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));

        Assert.Equal(expectedKind, exception.Error.Kind);
        Assert.Equal(expectedCode, exception.Error.Code);
        Assert.Equal(retryable, exception.Error.IsRetryable);
        Assert.Equal(statusCode is 429 or 503 ? TimeSpan.FromSeconds(3) : null, exception.Error.RetryAfter);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("provider_error", exception.ToString(), StringComparison.Ordinal);
        Assert.True(exception.ToString().Length < 2_000);
    }

    [Theory]
    [InlineData(401, "model_not_found", "model not found", ChatModelErrorKind.Unauthorized, "deepseek.unauthorized")]
    [InlineData(403, "invalid_model", "model does not exist", ChatModelErrorKind.Authorization, "deepseek.authorization_denied")]
    [InlineData(402, "model_not_found", "model not found", ChatModelErrorKind.InsufficientBalance, "deepseek.insufficient_balance")]
    [InlineData(429, "invalid_model", "model does not exist", ChatModelErrorKind.RateLimited, "deepseek.rate_limited")]
    [InlineData(500, "model_not_found", "model not found", ChatModelErrorKind.Unavailable, "deepseek.server_error")]
    [InlineData(503, "invalid_model", "model does not exist", ChatModelErrorKind.Unavailable, "deepseek.unavailable")]
    [InlineData(404, "model_not_found", "model not found", ChatModelErrorKind.ModelNotFound, "deepseek.model_not_found")]
    [InlineData(400, "invalid_model", "model does not exist", ChatModelErrorKind.ModelNotFound, "deepseek.model_not_found")]
    [InlineData(422, "model_not_found", "model not found", ChatModelErrorKind.ModelNotFound, "deepseek.model_not_found")]
    [InlineData(400, "invalid_request", "bad payload", ChatModelErrorKind.InvalidRequest, "deepseek.bad_request")]
    [InlineData(422, "invalid_request", "bad payload", ChatModelErrorKind.InvalidRequest, "deepseek.unprocessable_request")]
    public async Task AuthoritativeHttpStatusWinsWhenProviderBodySuggestsAConflictingModelError(
        int statusCode,
        string providerCode,
        string providerMessage,
        ChatModelErrorKind expectedKind,
        string expectedDiagnosticCode)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(
            (HttpStatusCode)statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    error = new { code = providerCode, message = providerMessage }
                }),
                Encoding.UTF8,
                "application/json")
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-status-precedence-test"),
            handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));

        Assert.Equal(expectedKind, exception.Error.Kind);
        Assert.Equal(expectedDiagnosticCode, exception.Error.Code);
    }

    [Theory]
    [InlineData(400, "3")]
    [InlineData(429, "0")]
    [InlineData(429, "86401")]
    [InlineData(503, "0")]
    [InlineData(503, "86401")]
    public async Task InvalidOrUntrustedRetryAfterIsDiscarded(int statusCode, string retryAfter)
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)statusCode);
            Assert.True(response.Headers.TryAddWithoutValidation("Retry-After", retryAfter));
            return response;
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-retry-after-test"),
            handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));

        Assert.Null(exception.Error.RetryAfter);
    }

    [Fact]
    public async Task MaliciousProviderRequestIdIsNotReturnedAsMetadata()
    {
        const string maliciousId = "Bearer fake-key C:\\\\Users\\\\person\\\\tasking.db prompt stack trace";
        var handler = new StubHttpMessageHandler(_ => JsonResponse($$"""
            {"id":"{{maliciousId}}","choices":[{"message":{"content":"安全回答"},"finish_reason":"stop"}]}
            """));
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-request-id-test"),
            handler);

        var response = await provider.CompleteAsync(Request());

        Assert.Null(response.Metadata.ProviderRequestId);
        Assert.DoesNotContain("fake-key", response.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelErrorAndRedirectAreRejectedWithoutFollowingAnotherDestination()
    {
        var modelHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":\"model_not_found\",\"message\":\"missing\"}}",
                Encoding.UTF8,
                "application/json")
        });
        await using var modelProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-model-test"),
            modelHandler);
        var modelError = await Assert.ThrowsAsync<ChatModelException>(() =>
            modelProvider.CompleteAsync(Request()));

        var redirectHandler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.invalid/steal");
            return response;
        });
        await using var redirectProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-redirect-test"),
            redirectHandler);
        var redirectError = await Assert.ThrowsAsync<ChatModelException>(() =>
            redirectProvider.CompleteAsync(Request()));

        Assert.Equal(ChatModelErrorKind.ModelNotFound, modelError.Error.Kind);
        Assert.Equal("deepseek.model_not_found", modelError.Error.Code);
        Assert.Equal(ChatModelErrorKind.InvalidResponse, redirectError.Error.Kind);
        Assert.Equal("deepseek.redirect_rejected", redirectError.Error.Code);
        Assert.Equal(1, redirectHandler.SendCount);
    }

    [Fact]
    public async Task NetworkAndMalformedSuccessNeverExposeRawExceptionOrBody()
    {
        const string secret = "ds-network-secret";
        var networkProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore(secret),
            new StubHttpMessageHandler((_, _) =>
                throw new HttpRequestException($"socket failed Authorization: Bearer {secret}")));
        await using (networkProvider)
        {
            var network = await Assert.ThrowsAsync<ChatModelException>(() =>
                networkProvider.CompleteAsync(Request()));
            Assert.Equal(ChatModelErrorKind.Network, network.Error.Kind);
            Assert.DoesNotContain(secret, network.ToString(), StringComparison.Ordinal);
        }

        await using var malformedProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore(secret),
            new StubHttpMessageHandler(_ => JsonResponse($"not-json-{secret}")));
        var malformed = await Assert.ThrowsAsync<ChatModelException>(() =>
            malformedProvider.CompleteAsync(Request()));
        Assert.Equal(ChatModelErrorKind.InvalidResponse, malformed.Error.Kind);
        Assert.DoesNotContain(secret, malformed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthWithoutCredentialIsNotConfiguredAndDoesNotUseNetwork()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("Network must not be reached."));
        await using var provider = new DeepSeekChatModelProvider(new TestCredentialStore(), handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal("deepseek", health.ProviderId);
        Assert.Equal(ChatProviderHealthState.NotConfigured, health.State);
        Assert.False(health.IsConfigured);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task HealthUsesFixedModelsEndpointAndPerRequestAuthorization()
    {
        const string secret = "ds-health-secret";
        HttpMethod? observedMethod = null;
        Uri? observedUri = null;
        string? observedAuthorization = null;
        var credentials = new TestCredentialStore(secret);
        var handler = new StubHttpMessageHandler(request =>
        {
            observedMethod = request.Method;
            observedUri = request.RequestUri;
            observedAuthorization = request.Headers.Authorization?.ToString();
            return JsonResponse("{\"data\":[]}");
        });
        await using var provider = new DeepSeekChatModelProvider(credentials, handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal(ChatProviderHealthState.Healthy, health.State);
        Assert.True(health.IsConfigured);
        Assert.Equal(HttpMethod.Get, observedMethod);
        Assert.Equal("https://api.deepseek.com/models", observedUri?.AbsoluteUri);
        Assert.Equal($"Bearer {secret}", observedAuthorization);
        Assert.True(credentials.LastLeaseDisposed);
    }

    [Theory]
    [InlineData(401, ChatProviderHealthState.Unavailable)]
    [InlineData(403, ChatProviderHealthState.Unavailable)]
    [InlineData(402, ChatProviderHealthState.Degraded)]
    [InlineData(429, ChatProviderHealthState.Degraded)]
    [InlineData(500, ChatProviderHealthState.Unavailable)]
    [InlineData(503, ChatProviderHealthState.Unavailable)]
    public async Task HealthMapsProviderFailureWithoutThrowingOrLeakingBody(
        int statusCode,
        ChatProviderHealthState expectedState)
    {
        const string secret = "ds-health-error-secret";
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            Content = new StringContent(
                $"{{\"error\":{{\"message\":\"{secret}\"}}}}",
                Encoding.UTF8,
                "application/json")
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore(secret),
            handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal(expectedState, health.State);
        Assert.True(health.IsConfigured);
        Assert.DoesNotContain(secret, health.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryRequestOpensANewCredentialLeaseAndUsesTheRotatedKey()
    {
        var credentials = new QueueCredentialStore("ds-key-one", "ds-key-two");
        var authorizations = new List<string?>();
        var handler = new StubHttpMessageHandler(request =>
        {
            authorizations.Add(request.Headers.Authorization?.ToString());
            return JsonResponse("""
                {"id":"rotation","choices":[{"message":{"content":"好"},"finish_reason":"stop"}]}
                """);
        });
        await using var provider = new DeepSeekChatModelProvider(credentials, handler);

        _ = await provider.CompleteAsync(Request());
        _ = await provider.CompleteAsync(Request());

        Assert.Equal(["Bearer ds-key-one", "Bearer ds-key-two"], authorizations);
        Assert.Equal(2, credentials.DisposedLeaseCount);
    }

    [Fact]
    public async Task DuplicateActiveTurnIsRejectedAndCallerCancellationCancelsHttp()
    {
        var turnId = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-duplicate-test"),
            handler);
        using var callerCancellation = new CancellationTokenSource();
        var first = provider.CompleteAsync(Request(turnId: turnId), cancellationToken: callerCancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var duplicate = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(turnId: turnId)));
        callerCancellation.Cancel();
        var cancelled = await Assert.ThrowsAsync<ChatModelException>(() => first);

        Assert.Equal("deepseek.turn_already_active", duplicate.Error.Code);
        Assert.Equal(ChatModelErrorKind.Cancelled, cancelled.Error.Kind);
    }

    [Fact]
    public async Task IncompleteSseInvalidJsonObjectAndOversizedOutputAreSafeFailures()
    {
        var incompleteHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data:{\"choices\":[{\"delta\":{\"content\":\"半截\"}}]}\n",
                Encoding.UTF8,
                "text/event-stream")
        });
        await using var incompleteProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-incomplete-test"),
            incompleteHandler);
        var incomplete = await Assert.ThrowsAsync<ChatModelException>(() =>
            incompleteProvider.CompleteAsync(Request(), (_, _) => ValueTask.CompletedTask));

        await using var invalidJsonProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-json-test"),
            new StubHttpMessageHandler(_ => JsonResponse("""
                {"choices":[{"message":{"content":"[]"},"finish_reason":"stop"}]}
                """)));
        var invalidJson = await Assert.ThrowsAsync<ChatModelException>(() =>
            invalidJsonProvider.CompleteAsync(Request(responseFormat: ChatResponseFormat.JsonObject)));

        await using var oversizedProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-output-test"),
            new StubHttpMessageHandler(_ => JsonResponse("""
                {"choices":[{"message":{"content":"123456"},"finish_reason":"stop"}]}
                """)),
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaxMessageCount: 10,
                MaxInputCharacters: 1_000,
                MaxOutputCharacters: 5,
                MaxErrorBodyCharacters: 100));
        var oversized = await Assert.ThrowsAsync<ChatModelException>(() =>
            oversizedProvider.CompleteAsync(Request()));

        Assert.Equal("deepseek.stream_incomplete", incomplete.Error.Code);
        Assert.Equal(ChatModelErrorKind.InvalidResponse, invalidJson.Error.Kind);
        Assert.Equal("deepseek.output_too_large", oversized.Error.Code);
    }

    [Fact]
    public async Task DoneWithoutAssistantContentIsInvalidAndNeverPublishesFinalUpdate()
    {
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-empty-stream-test"),
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data:[DONE]\n", Encoding.UTF8, "text/event-stream")
            }));
        var updates = new List<ChatStreamUpdate>();

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(
                Request(),
                (update, _) =>
                {
                    updates.Add(update);
                    return ValueTask.CompletedTask;
                }));

        Assert.Equal(ChatModelErrorKind.InvalidResponse, exception.Error.Kind);
        Assert.Equal("deepseek.stream_empty", exception.Error.Code);
        Assert.DoesNotContain(updates, update => update.IsFinal);
    }

    [Theory]
    [InlineData("stop", ChatFinishReason.Stop)]
    [InlineData("length", ChatFinishReason.Length)]
    [InlineData("content_filter", ChatFinishReason.ContentFilter)]
    [InlineData("insufficient_system_resource", ChatFinishReason.Error)]
    [InlineData("future_reason", ChatFinishReason.Unknown)]
    public async Task FinishReasonsAreMappedWithoutGuessing(
        string providerReason,
        ChatFinishReason expected)
    {
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-finish-test"),
            new StubHttpMessageHandler(_ => JsonResponse(
                $$"""
                {"choices":[{"message":{"content":"结果"},"finish_reason":"{{providerReason}}"}]}
                """)));

        var response = await provider.CompleteAsync(Request());

        Assert.Equal(expected, response.FinishReason);
    }

    [Fact]
    public async Task CancelAsyncStopsAnActiveSseReadForTheExactTurn()
    {
        var turnId = Guid.NewGuid();
        var slowStream = new BlockingSseStream(
            "data:{\"id\":\"slow\",\"choices\":[{\"delta\":{\"content\":\"第一段\"}}]}\n\n");
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(slowStream)
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-slow-stream-test"),
            handler);
        var updates = new List<ChatStreamUpdate>();
        var completion = provider.CompleteAsync(
            Request(turnId: turnId),
            (update, _) =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            });
        await slowStream.FirstChunkRead.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => updates.Any(update => update.DeltaText == "第一段"),
            TimeSpan.FromSeconds(2));

        await provider.CancelAsync(turnId).WaitAsync(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<ChatModelException>(() => completion);

        await slowStream.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ChatModelErrorKind.Cancelled, exception.Error.Kind);
        Assert.DoesNotContain(updates, update => update.IsFinal);
    }

    [Fact]
    public async Task OversizedNonStreamingResponseIsRejectedBeforeReadingTheWholeBody()
    {
        var content = new StringContent(new string('x', 70_000), Encoding.UTF8, "application/json");
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-large-body-test"),
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            }),
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaxMessageCount: 10,
                MaxInputCharacters: 1_000,
                MaxOutputCharacters: 5,
                MaxErrorBodyCharacters: 100));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));

        Assert.Equal(ChatModelErrorKind.InvalidResponse, exception.Error.Kind);
        Assert.Equal("deepseek.response_body_too_large", exception.Error.Code);
    }

    [Fact]
    public async Task OversizedSseEventIsRejectedBeforeTheWholeLineIsBuffered()
    {
        var stream = new RepeatingSseLineStream(totalLength: 100_000);
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-large-sse-test"),
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            }),
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaxMessageCount: 10,
                MaxInputCharacters: 1_000,
                MaxOutputCharacters: 1_000,
                MaxErrorBodyCharacters: 100,
                MaxSseEventCharacters: 128));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(), (_, _) => ValueTask.CompletedTask));

        Assert.Equal(ChatModelErrorKind.InvalidResponse, exception.Error.Kind);
        Assert.Equal("deepseek.stream_event_too_large", exception.Error.Code);
        Assert.True(stream.BytesRead < stream.TotalLength);
    }

    [Fact]
    public async Task OversizedSseResponseIsRejectedEvenWhenEveryEventIsSmall()
    {
        var stream = new RepeatingPatternStream(": keep-alive\n"u8.ToArray(), totalLength: 100_000);
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-large-sse-response-test"),
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            }),
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaxMessageCount: 10,
                MaxInputCharacters: 1_000,
                MaxOutputCharacters: 1_000,
                MaxErrorBodyCharacters: 100,
                MaxSseEventCharacters: 128,
                MaxSseResponseCharacters: 256));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(), (_, _) => ValueTask.CompletedTask));

        Assert.Equal(ChatModelErrorKind.InvalidResponse, exception.Error.Kind);
        Assert.Equal("deepseek.stream_response_too_large", exception.Error.Code);
        Assert.True(stream.BytesRead < stream.TotalLength);
    }

    [Fact]
    public async Task EmptyCredentialLeasesAreDisposedWithoutUsingTheNetwork()
    {
        var completionCredentials = new TestCredentialStore(string.Empty);
        var completionHandler = new StubHttpMessageHandler(_ =>
            throw new Xunit.Sdk.XunitException("Empty credentials must not reach the network."));
        await using var completionProvider = new DeepSeekChatModelProvider(
            completionCredentials,
            completionHandler);

        var completionError = await Assert.ThrowsAsync<ChatModelException>(() =>
            completionProvider.CompleteAsync(Request()));

        var healthCredentials = new TestCredentialStore(string.Empty);
        var healthHandler = new StubHttpMessageHandler(_ =>
            throw new Xunit.Sdk.XunitException("Empty credentials must not reach the network."));
        await using var healthProvider = new DeepSeekChatModelProvider(
            healthCredentials,
            healthHandler);
        var health = await healthProvider.CheckHealthAsync();

        Assert.Equal("deepseek.not_configured", completionError.Error.Code);
        Assert.True(completionCredentials.LastLeaseDisposed);
        Assert.Equal(ChatProviderHealthState.NotConfigured, health.State);
        Assert.True(healthCredentials.LastLeaseDisposed);
        Assert.Equal(0, completionHandler.SendCount);
        Assert.Equal(0, healthHandler.SendCount);
    }

    [Fact]
    public void UnreasonableProviderLimitsAreRejectedAtConstruction()
    {
        var defaults = DeepSeekProviderOptions.Default;
        DeepSeekProviderOptions[] invalidOptions =
        [
            defaults with { RequestTimeout = TimeSpan.FromHours(1) },
            defaults with { MaxMessageCount = int.MaxValue },
            defaults with { MaxInputCharacters = int.MaxValue },
            defaults with { MaxOutputCharacters = int.MaxValue },
            defaults with { MaxErrorBodyCharacters = int.MaxValue },
            defaults with { MaxSseEventCharacters = int.MaxValue },
            defaults with { MaxSseResponseCharacters = int.MaxValue }
        ];

        foreach (var options in invalidOptions)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new DeepSeekChatModelProvider(
                    new TestCredentialStore("ds-never-used"),
                    new StubHttpMessageHandler(_ =>
                        throw new Xunit.Sdk.XunitException("Invalid options must not reach the network.")),
                    options));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The expected test condition was not reached.");
    }

    private static ChatModelRequest Request(
        string modelId = DeepSeekChatModelProvider.FlashModelId,
        IReadOnlyList<ChatMessage>? messages = null,
        ChatResponseFormat? responseFormat = null,
        Guid? turnId = null) =>
        new(
            Guid.NewGuid(),
            turnId ?? Guid.NewGuid(),
            modelId,
            string.Empty,
            messages ?? [new ChatMessage(ChatMessageRole.User, "你好")],
            ResponseFormat: responseFormat);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            : this((request, _) => Task.FromResult(responseFactory(request)))
        {
        }

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) =>
            _responseFactory = responseFactory;

        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return _responseFactory(request, cancellationToken);
        }
    }

    private sealed class TestCredentialStore(string? secret = null) : IProviderCredentialStore
    {
        public bool LastLeaseDisposed { get; private set; }

        public int OpenLeaseCount { get; private set; }

        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCredentialStatus(
                providerId,
                secret is null ? ProviderCredentialState.Missing : ProviderCredentialState.Configured,
                secret is null ? null : DateTimeOffset.UtcNow));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            OpenLeaseCount++;
            return ValueTask.FromResult<IProviderCredentialLease?>(
                secret is null ? null : new TestLease(secret, () => LastLeaseDisposed = true));
        }

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        private sealed class TestLease(string secret, Action disposed) : IProviderCredentialLease
        {
            public ReadOnlyMemory<char> Secret { get; } = secret.ToCharArray();

            public void Dispose() => disposed();
        }
    }

    private sealed class QueueCredentialStore(params string[] secrets) : IProviderCredentialStore
    {
        private readonly Queue<string> _secrets = new(secrets);

        public int DisposedLeaseCount { get; private set; }

        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCredentialStatus(
                providerId,
                ProviderCredentialState.Configured,
                DateTimeOffset.UtcNow));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IProviderCredentialLease?>(
                new QueueLease(_secrets.Dequeue(), () => DisposedLeaseCount++));

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        private sealed class QueueLease(string secret, Action disposed) : IProviderCredentialLease
        {
            public ReadOnlyMemory<char> Secret { get; } = secret.ToCharArray();

            public void Dispose() => disposed();
        }
    }

    private sealed class BlockingSseStream(string firstChunk) : Stream
    {
        private readonly byte[] _firstChunk = Encoding.UTF8.GetBytes(firstChunk);
        private bool _sent;

        public Task FirstChunkRead => _firstChunkRead.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        private readonly TaskCompletionSource _firstChunkRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                _firstChunk.CopyTo(buffer);
                _firstChunkRead.TrySetResult();
                return _firstChunk.Length;
            }

            using var registration = cancellationToken.Register(
                () => _cancellationObserved.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RepeatingSseLineStream(int totalLength) : Stream
    {
        private static readonly byte[] Prefix = "data:"u8.ToArray();
        private int _position;

        public int BytesRead { get; private set; }

        public int TotalLength { get; } = totalLength;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => TotalLength;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, TotalLength - _position);
            if (count <= 0)
            {
                return ValueTask.FromResult(0);
            }

            var destination = buffer.Span[..count];
            destination.Fill((byte)'x');
            for (var index = 0; index < count && _position + index < Prefix.Length; index++)
            {
                destination[index] = Prefix[_position + index];
            }

            _position += count;
            BytesRead += count;
            return ValueTask.FromResult(count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RepeatingPatternStream(byte[] pattern, int totalLength) : Stream
    {
        private int _position;

        public int BytesRead { get; private set; }

        public int TotalLength { get; } = totalLength;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => TotalLength;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, TotalLength - _position);
            if (count <= 0)
            {
                return ValueTask.FromResult(0);
            }

            var destination = buffer.Span[..count];
            for (var index = 0; index < count; index++)
            {
                destination[index] = pattern[(_position + index) % pattern.Length];
            }

            _position += count;
            BytesRead += count;
            return ValueTask.FromResult(count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
