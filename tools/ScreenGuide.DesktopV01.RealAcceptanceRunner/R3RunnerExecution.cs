using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.DeepSeek;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopV01.RealAcceptanceRunner;

public sealed record R3RunnerExecutionOutcome(
    bool Passed,
    string StructuredEvidenceJson,
    R3DeepSeekValidationResult? Success,
    R3RunnerFailureEvidence? Failure);

internal sealed record R3RunnerCancellationTurnEvidence(
    AiInvocationRecord Invocation,
    R3CancellationTerminalStateEvidence TerminalEvidence,
    int DeltaCount,
    bool LateDeltaRejected,
    bool LateFinalRejected,
    bool LateSuccessRejected,
    bool Passed);

public static class R3RunnerExecution
{
    public static async Task<R3RunnerExecutionOutcome> ExecuteCancellationOnlyAsync(
        IDesktopApiClient api,
        IHost host,
        R3BudgetedChatModelProvider provider,
        R3DeepSeekValidationOptions options,
        R3RunEvidenceTracker evidenceTracker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(evidenceTracker);
        try
        {
            var result = await ExecuteCancellationOnlyCoreAsync(
                    api,
                    host,
                    provider,
                    options,
                    evidenceTracker,
                    cancellationToken)
                .ConfigureAwait(false);
            return new R3RunnerExecutionOutcome(
                Passed: true,
                JsonSerializer.Serialize(result),
                result,
                Failure: null);
        }
        catch (Exception exception)
        {
            var failure = R3FailureEvidenceReporter.Create(
                exception,
                evidenceTracker,
                provider.RequestCount,
                provider.RequestIdentities.Select(identity => identity.ModelId).ToArray(),
                provider.ResponseShapeEvidence);
            return new R3RunnerExecutionOutcome(
                Passed: false,
                JsonSerializer.Serialize(failure),
                Success: null,
                failure);
        }
    }

    private static async Task<R3DeepSeekValidationResult> ExecuteCancellationOnlyCoreAsync(
        IDesktopApiClient api,
        IHost host,
        R3BudgetedChatModelProvider provider,
        R3DeepSeekValidationOptions options,
        R3RunEvidenceTracker evidenceTracker,
        CancellationToken callerCancellationToken)
    {
        if (!options.CancellationOnly)
        {
            throw new R3ValidationFailureException("r3_cancellation_only_required");
        }

        var budget = options.Budget
                     ?? throw new R3ValidationFailureException("real_provider_budget_missing");
        var expectedModelId = options.ExpectedModelId
                              ?? throw new R3ValidationFailureException(
                                  "real_provider_expected_model_missing");
        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellationToken);
        totalTimeout.CancelAfter(budget.TotalTimeout);
        var cancellationToken = totalTimeout.Token;
        var stopwatch = Stopwatch.StartNew();

        evidenceTracker.BeginConfiguration();
        var settings = await WaitForDeepSeekConfigurationOnlyAsync(api, cancellationToken)
            .ConfigureAwait(false);
        var settingsModelId = settings.CurrentChatRoute.ModelId;
        evidenceTracker.RecordSettingsModel(settingsModelId);
        R3ExpectedModelGate.RequireSettingsMatch(expectedModelId, settingsModelId);

        var invocations = host.Services.GetRequiredService<IAiInvocationStore>();
        var sessions = host.Services.GetRequiredService<ISessionStore>();
        var conversations = host.Services.GetRequiredService<IConversationStore>();
        var session = await api.StartNewSessionAsync(
                "R3 DeepSeek 隔离验收",
                cancellationToken)
            .ConfigureAwait(false);

        var cancellation = await ExecuteCancellationTurnAsync(
            api,
            sessions,
            conversations,
            invocations,
            provider,
            budget,
            expectedModelId,
            session.SessionId,
            evidenceTracker,
            cancellationToken)
            .ConfigureAwait(false);

        evidenceTracker.BeginAudit();
        var auditPassed = R3DeepSeekCancellationAuditGate.IsSatisfied(new(
            cancellation.Invocation.Status.ToString(),
            cancellation.Invocation.FailureCode,
            HasUsage: cancellation.Invocation.Usage is not null,
            HasProviderRequestId: !string.IsNullOrWhiteSpace(
                cancellation.Invocation.ProviderRequestId)));
        Require(auditPassed, "r3_audit_failed");
        evidenceTracker.BeginModelEvidence();
        var modelEvidence = new R3ExpectedModelEvidence(
            expectedModelId,
            settingsModelId,
            provider.RequestIdentities.Select(identity => identity.ModelId).ToArray(),
            [cancellation.Invocation.ModelId]);
        Require(modelEvidence.IsCompleteMatch, R3ExpectedModelGate.MismatchErrorCode);
        Require(provider.RequestCount == 1, "r3_request_count_mismatch");

        stopwatch.Stop();
        return new R3DeepSeekValidationResult(
            Stage: "complete",
            Passed: true,
            DeepSeekChatModelProvider.ProviderId,
            expectedModelId,
            provider.RequestCount,
            HealthPassed: false,
            OrdinaryChatPassed: false,
            StreamingPassed: false,
            StreamingDeltaCount: 0,
            CancellationPassed: cancellation.Passed,
            CancellationDeltaCount: cancellation.DeltaCount,
            LateDeltaRejected: cancellation.LateDeltaRejected,
            LateFinalRejected: cancellation.LateFinalRejected,
            LateSuccessRejected: cancellation.LateSuccessRejected,
            AuditPassed: auditPassed,
            budget.NoAutomaticRetry,
            budget.NoFallback,
            stopwatch.ElapsedMilliseconds,
            ErrorCode: null)
        {
            Mode = "cancellation-only",
            HealthExecuted = false,
            OrdinaryChatExecuted = false,
            StreamingSuccessExecuted = false,
            StreamingCancellationExecuted = true,
            ModelEvidence = modelEvidence,
            CancellationTerminalEvidence = cancellation.TerminalEvidence,
            ResponseShapeEvidence = provider.ResponseShapeEvidence
        };
    }

    internal static async Task<R3RunnerCancellationTurnEvidence> ExecuteCancellationTurnAsync(
        IDesktopApiClient api,
        ISessionStore sessions,
        IConversationStore conversations,
        IAiInvocationStore invocations,
        R3BudgetedChatModelProvider provider,
        R3DeepSeekValidationBudget budget,
        string expectedModelId,
        Guid sessionId,
        R3RunEvidenceTracker evidenceTracker,
        CancellationToken cancellationToken)
    {
        evidenceTracker.BeginStreamingCancellation();
        const string cancellationCanary = "R3-CANCEL-LATE-CANARY";
        var observation = provider.PrepareNextCall(R3ValidationCallMode.StreamingCancellation);
        var cancelling = await api.SubmitSessionInputAsync(
                new SessionInputRequestDto(
                    $"{cancellationCanary}: write 100 numbered short lines and do not summarize.",
                    "Text",
                    $"r3-cancel-{Guid.NewGuid():N}",
                    sessionId),
                cancellationToken)
            .ConfigureAwait(false);
        var earlyTerminal = WaitForCancellationTerminalEvidenceAsync(
            api,
            sessions,
            conversations,
            invocations,
            observation.Terminal,
            cancelling.TurnId,
            budget.TotalTimeout,
            cancellationToken);
        var precondition = await R3CancellationPreconditionCoordinator.WaitAsync(
                observation.AtLeastTwoDeltas,
                earlyTerminal,
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                async () =>
                {
                    _ = await api.CancelSessionTurnAsync(
                            sessionId,
                            cancelling.TurnId,
                            cancellationToken)
                        .ConfigureAwait(false);
                })
            .ConfigureAwait(false);
        ThrowForEarlyTerminal(precondition, evidenceTracker);

        var terminal = precondition.TerminalEvidence
                       ?? await earlyTerminal.ConfigureAwait(false);
        var terminalEvidence = new R3CancellationTerminalStateEvidence(
            terminal.SessionState,
            terminal.ConversationState,
            terminal.InvocationState);
        R3CancellationTerminalStateGate.RequireCancelled(terminalEvidence);
        var snapshot = await WaitForSessionTurnAsync(
                api,
                cancelling.TurnId,
                ["Cancelled"],
                budget.TotalTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var turn = snapshot.Turns.Single(item => item.Id == cancelling.TurnId);
        var invocation = AssertConversationInvocation(
            await invocations.GetForSessionTurnAsync(cancelling.TurnId, cancellationToken)
                .ConfigureAwait(false),
            provider,
            turn,
            expectedModelId,
            AiInvocationStatus.Cancelled);
        evidenceTracker.RecordInvocationModel(invocation.ModelId);
        var lateDeltaRejected = observation.DeltaCountWhenCancellationCompleted
                                == observation.DeltaCount;
        var lateFinalRejected = !observation.FinalUpdateObserved;
        var lateSuccessRejected = !observation.ResponseReturned
                                  && turn.Phase == "Cancelled"
                                  && !snapshot.Messages.Any(message =>
                                      message.Role == "Assistant"
                                      && message.Content.Contains(
                                          cancellationCanary,
                                          StringComparison.Ordinal));
        var passed = lateDeltaRejected && lateFinalRejected && lateSuccessRejected;
        Require(passed, "r3_cancellation_failed");
        return new R3RunnerCancellationTurnEvidence(
            invocation,
            terminalEvidence,
            observation.DeltaCount,
            lateDeltaRejected,
            lateFinalRejected,
            lateSuccessRejected,
            passed);
    }

    private static void ThrowForEarlyTerminal(
        R3CancellationPreconditionResult precondition,
        R3RunEvidenceTracker evidenceTracker)
    {
        if (precondition.Disposition == R3CancellationPreconditionDisposition.TerminalFailed)
        {
            evidenceTracker.RecordInvocationModel(precondition.TerminalEvidence?.InvocationModelId);
            throw new R3ValidationFailureException(
                precondition.TerminalEvidence?.PublicFailureCode
                ?? "r3_cancellation_provider_failed",
                precondition.TerminalEvidence?.ProviderDiagnosticCode);
        }

        if (precondition.Disposition == R3CancellationPreconditionDisposition.TerminalSucceeded)
        {
            evidenceTracker.RecordInvocationModel(precondition.TerminalEvidence?.InvocationModelId);
            throw new R3ValidationFailureException("r3_cancellation_precondition_succeeded");
        }

        if (precondition.Disposition == R3CancellationPreconditionDisposition.TerminalInterrupted)
        {
            evidenceTracker.RecordInvocationModel(precondition.TerminalEvidence?.InvocationModelId);
            throw new R3ValidationFailureException(
                "r3_cancellation_precondition_interrupted",
                precondition.TerminalEvidence?.ProviderDiagnosticCode);
        }

        if (precondition.Disposition == R3CancellationPreconditionDisposition.TimedOut)
        {
            throw new R3ValidationFailureException("r3_cancellation_precondition_timeout");
        }
    }

    private static AiInvocationRecord AssertConversationInvocation(
        IReadOnlyList<AiInvocationRecord> records,
        R3BudgetedChatModelProvider provider,
        UnifiedSessionTurnDto turn,
        string expectedModelId,
        AiInvocationStatus expectedStatus)
    {
        var invocation = records.Single(record =>
            record.Purpose == AiInvocationPurpose.Conversation);
        var identity = provider.RequestIdentities.Single(item => item.TurnId == turn.Id);
        R3ExpectedModelGate.RequireFrozenRouteMatch(expectedModelId, identity.ModelId);
        R3ExpectedModelGate.RequireInvocationMatch(expectedModelId, invocation.ModelId);
        var matches = invocation.Id == identity.RequestId
                      && invocation.SessionTurnId == turn.Id
                      && invocation.ConversationTurnId == turn.ConversationTurnId
                      && invocation.ProviderId == DeepSeekChatModelProvider.ProviderId
                      && invocation.ModelId == expectedModelId
                      && invocation.DataDestination == DeepSeekChatModelProvider.DataDestination
                      && invocation.Status == expectedStatus
                      && !string.IsNullOrWhiteSpace(invocation.PromptId)
                      && !string.IsNullOrWhiteSpace(invocation.PromptVersion)
                      && !string.IsNullOrWhiteSpace(invocation.PromptContentHash)
                      && invocation.StartedAtUtc != default
                      && invocation.CompletedAtUtc is not null
                      && R3DeepSeekCancellationAuditGate.IsSatisfied(new(
                          invocation.Status.ToString(),
                          invocation.FailureCode,
                          HasUsage: invocation.Usage is not null,
                          HasProviderRequestId: !string.IsNullOrWhiteSpace(
                              invocation.ProviderRequestId)));
        Require(matches, "r3_invocation_mismatch");
        return invocation;
    }

    private static async Task<R3CancellationTerminalEvidence>
        WaitForCancellationTerminalEvidenceAsync(
            IDesktopApiClient api,
            ISessionStore sessions,
            IConversationStore conversations,
            IAiInvocationStore invocations,
            Task providerTerminal,
            Guid sessionTurnId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        var snapshot = await WaitForSessionTurnAsync(
                api,
                sessionTurnId,
                ["Failed", "Cancelled", "Completed", "Interrupted"],
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
        await providerTerminal.WaitAsync(cancellationToken).ConfigureAwait(false);
        var turn = snapshot.Turns.Single(item => item.Id == sessionTurnId);
        var persistedTurn = await sessions.GetTurnAsync(sessionTurnId, cancellationToken)
                                  .ConfigureAwait(false)
                              ?? throw new R3ValidationFailureException(
                                  "r3_cancellation_session_turn_missing");
        var conversationTurn = turn.ConversationTurnId is { } conversationTurnId
            ? (await conversations.GetTurnsAsync(snapshot.ConversationId, cancellationToken)
                    .ConfigureAwait(false))
                .SingleOrDefault(item => item.Id == conversationTurnId)
            : null;
        var invocation = (await invocations.GetForSessionTurnAsync(
                sessionTurnId,
                cancellationToken)
            .ConfigureAwait(false))
            .SingleOrDefault(item => item.Purpose == AiInvocationPurpose.Conversation);
        return new R3CancellationTerminalEvidence(
            turn.Phase,
            conversationTurn?.Status.ToString() ?? "Missing",
            invocation?.Status.ToString() ?? "Missing",
            persistedTurn.FailureCode ?? conversationTurn?.FailureCode,
            invocation?.FailureCode,
            invocation?.ModelId);
    }

    private static async Task<AiSettingsDto> WaitForDeepSeekConfigurationOnlyAsync(
        IDesktopApiClient api,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = await api.GetAiSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            var provider = settings.Providers.Single(item =>
                item.ProviderId == DeepSeekChatModelProvider.ProviderId);
            var modelId = settings.CurrentChatRoute.ModelId;
            if (provider.ConfigurationState == "Configured"
                && settings.CurrentChatRoute.ProviderId
                == DeepSeekChatModelProvider.ProviderId
                && modelId is DeepSeekChatModelProvider.FlashModelId
                    or DeepSeekChatModelProvider.ProModelId)
            {
                return settings;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<SessionSnapshotDto> WaitForSessionTurnAsync(
        IDesktopApiClient api,
        Guid turnId,
        string[] expectedPhases,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        SessionSnapshotDto? snapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot ??= await api.GetCurrentSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            var turn = snapshot?.Turns.SingleOrDefault(item => item.Id == turnId);
            if (turn is not null && expectedPhases.Contains(turn.Phase))
            {
                return snapshot!;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            var waitMilliseconds = (int)Math.Clamp(remaining.TotalMilliseconds, 1, 5_000);
            snapshot = await api.WaitForSessionUpdateAsync(
                    snapshot?.ChangeVersion ?? -1,
                    waitMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new R3ValidationFailureException("r3_session_turn_timeout");
    }

    private static void Require(bool condition, string errorCode)
    {
        if (!condition)
        {
            throw new R3ValidationFailureException(errorCode);
        }
    }
}
