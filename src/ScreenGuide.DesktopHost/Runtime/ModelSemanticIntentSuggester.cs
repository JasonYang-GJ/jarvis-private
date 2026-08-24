using ScreenGuide.AI.Core;
using ScreenGuide.Core.Ai;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Runtime;

/// <summary>
/// Uses the current chat model only for an untrusted semantic suggestion.
/// Deterministic planning remains the sole authority for context, consent and confirmation.
/// </summary>
public sealed class ModelSemanticIntentSuggester(
    ModelRouter router,
    PromptRegistry prompts,
    IAiInvocationStore invocations,
    TimeProvider timeProvider) : ISemanticIntentSuggester
{
    private const string PromptId = "intent.semantic";
    private const string PromptVersion = "1";
    private const string ResponseSchemaName = "semantic_intent_suggestion";
    private const string ResponseSchema =
        "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"kind\",\"target\",\"confidence\",\"isAmbiguous\",\"missingContext\"],\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"Conversation\",\"CodingTask\",\"OpenFile\",\"DescribeForeground\"]},\"target\":{\"type\":[\"string\",\"null\"],\"maxLength\":200},\"confidence\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1},\"isAmbiguous\":{\"type\":\"boolean\"},\"missingContext\":{\"type\":\"string\",\"enum\":[\"None\",\"Project\",\"File\",\"Window\",\"WindowConsent\"]}}}";

    public async Task<SemanticIntentSuggestion?> SuggestAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (!SemanticIntentCandidateDetector.ShouldEvaluate(text))
        {
            return null;
        }

        FrozenChatModelRoute route;
        PromptDefinition prompt;
        try
        {
            route = await router.FreezeDefaultChatRouteAsync(cancellationToken)
                .ConfigureAwait(false);
            prompt = prompts.GetRequired(PromptId, PromptVersion, route.ProviderId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }

        var invocationId = Guid.NewGuid();
        var invocation = new AiInvocationRecord
        {
            Id = invocationId,
            Purpose = AiInvocationPurpose.SemanticIntent,
            ProviderId = route.ProviderId,
            ModelId = route.ModelId,
            PromptId = prompt.PromptId,
            PromptVersion = prompt.Version,
            PromptContentHash = prompt.ContentSha256,
            DataDestination = route.DataDestination,
            Status = AiInvocationStatus.Running,
            StartedAtUtc = timeProvider.GetUtcNow()
        };
        await invocations.StartAsync(invocation, cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await router.CompleteAsync(
                    route,
                    new ChatModelRequest(
                        invocationId,
                        Guid.NewGuid(),
                        route.ModelId,
                        prompt.Content,
                        [new ChatMessage(ChatMessageRole.User, text)],
                        ResponseFormat: SelectResponseFormat(route.Capabilities),
                        Prompt: new ChatPromptReference(
                            prompt.PromptId,
                            prompt.Version,
                            prompt.ContentSha256)),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (response.FinishReason == ChatFinishReason.Cancelled)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                await MarkFailedAsync(
                        invocationId,
                        AiInvocationStatus.Cancelled,
                        "cancelled")
                    .ConfigureAwait(false);
                return null;
            }

            if (response.FinishReason != ChatFinishReason.Stop)
            {
                await MarkFailedAsync(
                        invocationId,
                        AiInvocationStatus.Failed,
                        "semantic_intent_incomplete_response")
                    .ConfigureAwait(false);
                return null;
            }

            SemanticIntentSuggestion suggestion;
            try
            {
                suggestion = StrictSemanticIntentSuggestionParser.Parse(
                    response.StructuredJson ?? response.Text);
            }
            catch (SemanticIntentFormatException)
            {
                await MarkFailedAsync(
                        invocationId,
                        AiInvocationStatus.Failed,
                        "semantic_intent_invalid_format")
                    .ConfigureAwait(false);
                return null;
            }

            await invocations.CompleteAsync(
                    invocationId,
                    response.FinishReason.ToString(),
                    response.Usage is null
                        ? null
                        : new AiTokenUsage(
                            response.Usage.InputTokens ?? 0,
                            response.Usage.OutputTokens ?? 0,
                            response.Usage.TotalTokens ?? 0),
                    response.Metadata.ProviderRequestId,
                    timeProvider.GetUtcNow(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return suggestion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await MarkFailedAsync(invocationId, AiInvocationStatus.Cancelled, "cancelled")
                .ConfigureAwait(false);
            throw;
        }
        catch (ChatModelException exception)
        {
            var cancelled = exception.Error.Kind == ChatModelErrorKind.Cancelled;
            await MarkFailedAsync(
                    invocationId,
                    cancelled ? AiInvocationStatus.Cancelled : AiInvocationStatus.Failed,
                    cancelled
                        ? "cancelled"
                        : SensitiveDataSanitizer.DiagnosticCode(
                            exception.Error.Code,
                            "semantic_intent_provider_error"))
                .ConfigureAwait(false);
            if (cancelled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return null;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            await MarkFailedAsync(
                    invocationId,
                    AiInvocationStatus.Failed,
                    "semantic_intent_provider_error")
                .ConfigureAwait(false);
            return null;
        }
    }

    private async Task MarkFailedAsync(
        Guid invocationId,
        AiInvocationStatus status,
        string failureCode) =>
        await invocations.FailAsync(
                invocationId,
                status,
                failureCode,
                timeProvider.GetUtcNow(),
                CancellationToken.None)
            .ConfigureAwait(false);

    private static ChatResponseFormat SelectResponseFormat(ChatModelCapabilities capabilities)
    {
        if ((capabilities & ChatModelCapabilities.JsonSchemaOutput) != 0)
        {
            return ChatResponseFormat.JsonSchema(ResponseSchemaName, ResponseSchema);
        }

        return (capabilities & ChatModelCapabilities.JsonObjectOutput) != 0
            ? ChatResponseFormat.JsonObject
            : ChatResponseFormat.Text;
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException;
}
