using System.Collections.Concurrent;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Windows;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed record LocalSessionSnapshot(
    long ChangeVersion,
    string CoordinatorInstanceId,
    DateTimeOffset CoordinatorStartedAtUtc,
    SessionRecord Session,
    string? SelectedProjectName,
    IReadOnlyList<SessionTurnRecord> Turns,
    IReadOnlyList<SessionTurnRecord> ActiveTurns,
    IReadOnlyList<ConversationMessageRecord> Messages,
    IReadOnlyList<MemoryOutboundPreparedConsent> MemoryOutboundConsents);

public sealed record SessionSubmitResult(Guid SessionId, Guid TurnId, bool WasDuplicate);

public interface ISessionMemoryConsentPublicationObserver
{
    Task BeforePublishAsync(Guid turnId, Guid consentId, CancellationToken cancellationToken);

    Task AfterPublishAsync(Guid turnId, Guid consentId, CancellationToken cancellationToken);
}

/// <summary>
/// Desktop Host 中唯一的当前会话与前台 Turn 协调入口。
/// 它只协调状态和取消，不拥有任何项目、文件、窗口或电脑动作授权。
/// </summary>
public sealed class SessionCoordinator(
    ISessionStore sessionStore,
    IConversationStore conversationStore,
    ConversationService conversations,
    AssistantCommandService assistantCommands,
    LocalTaskEntryService tasks,
    SessionTaskStateSynchronizer taskStateSynchronizer,
    DesktopHostState hostState,
    ModelRouter modelRouter,
    PromptRegistry prompts,
    MemoryService memories,
    IEnumerable<ISessionMemoryConsentPublicationObserver> memoryConsentPublicationObservers,
    IForegroundWindowContextProvider foregroundWindows,
    TimeProvider timeProvider)
{
    private const int MaximumInputLength = 20_000;
    private readonly string _coordinatorInstanceId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _coordinatorStartedAtUtc = DateTimeOffset.UtcNow;
    private readonly SemaphoreSlim _currentSessionGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionGates = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _turnGates = new();
    private readonly ConcurrentDictionary<Guid, ActiveSessionWork> _activeWork = new();
    private readonly ConcurrentDictionary<Guid, ActiveTaskMonitor> _taskMonitors = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<MemoryOutboundItemReference>>
        _requestedMemorySelections = new();
    private readonly ConcurrentDictionary<Guid, MemoryOutboundPreparedConsent>
        _preparedMemoryConsents = new();
    private readonly object _changeGate = new();
    private readonly SessionChangeJournal _changeJournal = new();
    private TaskCompletionSource<long> _nextChange = NewChangeSource();
    private long _changeVersion = 1;

    public async Task<LocalSessionSnapshot?> GetCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        var current = await sessionStore.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        return current is null ? null : await BuildSnapshotAsync(current, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalSessionSnapshot> StartNewAsync(
        string? title,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        await _currentSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await StartNewCoreAsync(title, cancellationToken).ConfigureAwait(false);
            return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _currentSessionGate.Release();
        }
    }

    private async Task<SessionRecord> StartNewCoreAsync(
        string? title,
        CancellationToken cancellationToken)
    {
        var host = RequireStartedHost();
        var previous = await sessionStore.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            await CancelForegroundWorkAsync(
                    previous,
                    "已开始新话题，上一条前台请求已停止。",
                    excludedTurnId: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var conversation = await conversations.CreateAsync(title, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var session = new SessionRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            CreatedByDeviceId = host.LocalDevice!.Id,
            Title = conversation.Title,
            IsCurrent = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastActiveAtUtc = now
        };
        await sessionStore.CreateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        PublishChange();
        return session;
    }

    public async Task<LocalSessionSnapshot> SetCurrentAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        await _currentSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await SetCurrentCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _currentSessionGate.Release();
        }
    }

    private async Task<SessionRecord> SetCurrentCoreAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var previous = await sessionStore.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        if (previous is not null && previous.Id != sessionId)
        {
            await CancelForegroundWorkAsync(
                    previous,
                    "已切换话题，上一条前台请求已停止。",
                    excludedTurnId: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var session = await sessionStore.SetCurrentSessionAsync(
                sessionId,
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        PublishChange();
        return session;
    }

    public async Task<SessionSubmitResult> SubmitAsync(
        Guid? requestedSessionId,
        string text,
        string inputModality,
        string? idempotencyKey,
        CancellationToken cancellationToken = default,
        string? expectedIntentKind = null,
        string? expectedTarget = null,
        IReadOnlyList<MemoryOutboundItemReference>? memoryItems = null)
    {
        RequireStartedHost();
        var normalized = NormalizeInput(text);
        var normalizedMemoryItems = NormalizeMemorySelection(memoryItems);
        var expectation = NormalizeActionExpectation(expectedIntentKind, expectedTarget);
        await _currentSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var selected = requestedSessionId is { } sessionId
                ? await sessionStore.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                : await sessionStore.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
            SessionRecord session;
            if (selected is null)
            {
                session = await StartNewCoreAsync(BuildTitle(normalized), cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!selected.IsCurrent)
            {
                session = await SetCurrentCoreAsync(selected.Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                session = selected;
            }

            // Selecting the current Session and registering its foreground Turn form one
            // linearized operation. Releasing this gate earlier lets New Topic switch away
            // before the Turn becomes visible, which can create two foreground Sessions.
            var gate = _sessionGates.GetOrAdd(session.Id, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var normalizedIdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
                    ? Guid.NewGuid().ToString("N")
                    : idempotencyKey.Trim();
                var existingTurn = (await sessionStore.GetTurnsAsync(session.Id, cancellationToken)
                        .ConfigureAwait(false))
                    .SingleOrDefault(turn => string.Equals(
                        turn.IdempotencyKey,
                        normalizedIdempotencyKey,
                        StringComparison.Ordinal));
                if (existingTurn is not null)
                {
                    if (!MatchesExpectedAction(
                            existingTurn,
                            expectation.IntentKind,
                            expectation.Target))
                    {
                        throw new InvalidOperationException(
                            "同一个请求编号不能改成另一个电脑操作目标。 ");
                    }

                    if (!MemorySelectionMatches(existingTurn, normalizedMemoryItems))
                    {
                        throw new InvalidOperationException(
                            "同一个请求编号不能改成另一组长期记忆。");
                    }

                    return new SessionSubmitResult(session.Id, existingTurn.Id, true);
                }

                var resolvedRoute = await modelRouter.ResolveDefaultChatRouteAsync(cancellationToken)
                    .ConfigureAwait(false);
                var startedAtUtc = timeProvider.GetUtcNow();
                var registration = await sessionStore.StartTurnAsync(
                        session.Id,
                        normalized,
                        NormalizeModality(inputModality),
                        normalizedIdempotencyKey,
                        ToFrozenRoute(resolvedRoute, startedAtUtc),
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                var registeredTurn = registration.Turn;
                if (!registration.Accepted)
                {
                    if (!MatchesExpectedAction(
                            registeredTurn,
                            expectation.IntentKind,
                            expectation.Target))
                    {
                        throw new InvalidOperationException(
                            "同一个请求编号不能改成另一个电脑操作目标。 ");
                    }

                    return new SessionSubmitResult(session.Id, registration.Turn.Id, true);
                }

                if (normalizedMemoryItems.Count > 0)
                {
                    _requestedMemorySelections[registeredTurn.Id] = normalizedMemoryItems;
                }

                if (expectation.IntentKind is not null)
                {
                    registeredTurn = await sessionStore.UpdateTurnAsync(
                            registeredTurn with
                            {
                                ExpectedIntentKind = expectation.IntentKind,
                                ExpectedTarget = expectation.Target
                            },
                            registeredTurn.Version,
                            timeProvider.GetUtcNow(),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await CancelForegroundWorkAsync(
                        session,
                        "新输入已取代上一条请求。",
                        registration.Turn.Id,
                        cancellationToken)
                    .ConfigureAwait(false);

                var active = new ActiveSessionWork(registeredTurn.Id);
                if (!_activeWork.TryAdd(registeredTurn.Id, active))
                {
                    throw new InvalidOperationException("同一个会话请求已经在处理中。");
                }

                active.SetCompletion(Task.Run(
                    () => RunInitialTurnAsync(session, registeredTurn, active.Cancellation.Token),
                    CancellationToken.None));
                PublishChange(session.Id, registeredTurn);
                return new SessionSubmitResult(session.Id, registeredTurn.Id, false);
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            _currentSessionGate.Release();
        }
    }

    private static SessionTurnFrozenRoute ToFrozenRoute(
        DefaultChatRouteResolution route,
        DateTimeOffset frozenAtUtc) => new()
    {
        Status = route.Status == ChatRouteResolutionStatus.Ready
            ? SessionTurnRouteStatus.Ready
            : SessionTurnRouteStatus.Unavailable,
        ProviderId = route.ProviderId,
        ModelId = route.ModelId,
        DataDestination = route.DataDestination,
        SendsDataOffDevice = route.SendsDataOffDevice,
        FrozenAtUtc = frozenAtUtc,
        FailureCode = route.FailureCode
    };

    public async Task<LocalSessionSnapshot> ProvideProjectAsync(
        Guid sessionId,
        Guid turnId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        var project = (await tasks.GetAuthorizedProjectsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == projectId)
            ?? throw new UnauthorizedAccessException("所选项目没有授权或已经失效。 ");
        var gate = _turnGates.GetOrAdd(turnId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (session, turn) = await RequireWaitingTurnAsync(
                    sessionId,
                    turnId,
                    SessionTurnPhase.WaitingForProject,
                    cancellationToken)
                .ConfigureAwait(false);
            session = await sessionStore.SetSelectedProjectAsync(
                    session.Id,
                    project.Id,
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            turn = await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        ProjectId = project.Id,
                        Phase = SessionTurnPhase.Understanding,
                        MissingContext = SessionMissingContext.None,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await PlanAndRouteAsync(
                    session,
                    turn,
                    observationConsent: false,
                    executeReadyPlan: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LocalSessionSnapshot> ProvideFileAsync(
        Guid sessionId,
        Guid turnId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("请选择这一次要处理的文件。", nameof(filePath));
        }

        var fullPath = Path.GetFullPath(filePath.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("刚才选择的文件已经不存在。", fullPath);
        }

        var gate = _turnGates.GetOrAdd(turnId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (session, turn) = await RequireWaitingTurnAsync(
                    sessionId,
                    turnId,
                    SessionTurnPhase.WaitingForFile,
                    cancellationToken)
                .ConfigureAwait(false);
            turn = await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        FilePath = fullPath,
                        Phase = SessionTurnPhase.Understanding,
                        MissingContext = SessionMissingContext.None,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await PlanAndRouteAsync(
                    session,
                    turn,
                    observationConsent: false,
                    executeReadyPlan: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LocalSessionSnapshot> RespondWindowConsentAsync(
        Guid sessionId,
        Guid turnId,
        bool granted,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        var gate = _turnGates.GetOrAdd(turnId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SessionRecord session;
        ActiveSessionWork? active = null;
        try
        {
            var required = await RequireWaitingTurnAsync(
                    sessionId,
                    turnId,
                    SessionTurnPhase.WaitingForWindowConsent,
                    cancellationToken)
                .ConfigureAwait(false);
            session = required.Session;
            if (!granted)
            {
                await TransitionAsync(
                        required.Turn.Id,
                        current => current with
                        {
                            Phase = SessionTurnPhase.Cancelled,
                            MissingContext = SessionMissingContext.None,
                            CancellationRequested = true,
                            ResultSummary = "你没有同意本次窗口查看，任务已安全结束。",
                            FailureCode = "window_consent_rejected",
                            FailureMessage = "未获得本次窗口查看授权。",
                            CompletedAtUtc = timeProvider.GetUtcNow()
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                if (!MatchesPlannedExpectation(required.Turn))
                {
                    throw new UnauthorizedAccessException(
                        "实际操作目标与刚才确认的目标不一致，已拒绝执行。 ");
                }

                if (IsWindowBoundTurn(required.Turn)
                    && !MatchesPersistedWindowIdentity(required.Turn))
                {
                    await RequireFreshWindowConfirmationAsync(required.Turn, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    active = StartAuthorizedContinuation(
                        required.Session,
                        required.Turn,
                        observationConsent: true);
                }
            }
        }
        finally
        {
            gate.Release();
        }

        if (active is not null)
        {
            await active.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalSessionSnapshot> RetryTurnAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        var gate = _turnGates.GetOrAdd(turnId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (session, turn) = await RequireWaitingTurnAsync(
                    sessionId,
                    turnId,
                    SessionTurnPhase.WaitingForWindow,
                    cancellationToken)
                .ConfigureAwait(false);
            turn = await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        Phase = SessionTurnPhase.Understanding,
                        MissingContext = SessionMissingContext.None,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await PlanAndRouteAsync(
                    session,
                    turn,
                    observationConsent: false,
                    executeReadyPlan: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LocalSessionSnapshot> ConfirmTurnAsync(
        Guid sessionId,
        Guid turnId,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        var gate = _turnGates.GetOrAdd(turnId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SessionRecord session;
        ActiveSessionWork? active = null;
        try
        {
            var required = await RequireWaitingTurnAsync(
                    sessionId,
                    turnId,
                    SessionTurnPhase.WaitingForConfirmation,
                    cancellationToken)
                .ConfigureAwait(false);
            session = required.Session;
            if (!confirmed)
            {
                await TransitionAsync(
                        required.Turn.Id,
                        current => current with
                        {
                            Phase = SessionTurnPhase.Cancelled,
                            MissingContext = SessionMissingContext.None,
                            CancellationRequested = true,
                            ResultSummary = "你取消了这次操作，没有执行任何动作。",
                            FailureCode = "confirmation_rejected",
                            FailureMessage = "用户未确认本次操作。",
                            CompletedAtUtc = timeProvider.GetUtcNow()
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                if (!MatchesPlannedExpectation(required.Turn))
                {
                    throw new UnauthorizedAccessException(
                        "实际操作目标与刚才确认的目标不一致，已拒绝执行。 ");
                }

                if (IsWindowBoundTurn(required.Turn)
                    && !MatchesPersistedWindowIdentity(required.Turn))
                {
                    await RequireFreshWindowConfirmationAsync(required.Turn, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    active = StartAuthorizedContinuation(
                        required.Session,
                        required.Turn,
                        observationConsent: false);
                }
            }
        }
        finally
        {
            gate.Release();
        }

        if (active is not null)
        {
            await active.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalSessionSnapshot> CancelTurnAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        var gate = _turnGates.GetOrAdd(turnId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SessionRecord session;
        SessionTurnRecord turn;
        ActiveSessionWork? active = null;
        SessionTurnPhase phaseAtCancellation = default;
        Guid? taskIdAtCancellation = null;
        string? operationIdAtCancellation = null;
        try
        {
            session = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            turn = await RequireTurnAsync(session, turnId, cancellationToken).ConfigureAwait(false);
            phaseAtCancellation = turn.Phase;
            taskIdAtCancellation = turn.TaskId;
            operationIdAtCancellation = turn.OperationId;
            if (SessionTurnPhases.IsTerminal(turn.Phase))
            {
                return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
            }

            _preparedMemoryConsents.TryRemove(turn.Id, out _);
            _requestedMemorySelections.TryRemove(turn.Id, out _);

            if (_activeWork.TryGetValue(turn.Id, out active))
            {
                if (turn.Phase != SessionTurnPhase.Responding)
                {
                    CancelActiveWork(active);
                }
            }
            else
            {
                turn = await TransitionAsync(
                        turn.Id,
                        current => current with
                        {
                            Phase = SessionTurnPhase.Cancelled,
                            MissingContext = SessionMissingContext.None,
                            CancellationRequested = true,
                            MemoryOutboundState = current.MemoryOutboundState is
                                MemoryOutboundConsentState.Prepared or
                                MemoryOutboundConsentState.WaitingForMemoryOutboundConsent or
                                MemoryOutboundConsentState.Committing
                                    ? MemoryOutboundConsentState.Cancelled
                                    : current.MemoryOutboundState,
                            ResultSummary = "这次请求已取消。",
                            FailureCode = "user_cancelled",
                            FailureMessage = "用户取消了当前请求。",
                            CompletedAtUtc = timeProvider.GetUtcNow()
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }

        if (active is not null)
        {
            if (phaseAtCancellation == SessionTurnPhase.Responding)
            {
                try
                {
                    await conversations.CancelAsync(session.ConversationId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // The provider may have completed immediately before cancellation won the turn gate.
                    CancelActiveWork(active);
                }
            }

            if (!string.IsNullOrWhiteSpace(operationIdAtCancellation))
            {
                assistantCommands.CancelWindowObservation(operationIdAtCancellation);
            }

            await active.Completion.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken)
                .ConfigureAwait(false);
        }

        if (taskIdAtCancellation is { } taskId
            && phaseAtCancellation is SessionTurnPhase.ProgrammingTask or SessionTurnPhase.WaitingForUser)
        {
            await tasks.CancelTaskAsync(
                    taskId,
                    $"session-cancel-{turn.Id:N}",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var latest = await sessionStore.GetTurnAsync(turn.Id, cancellationToken).ConfigureAwait(false);
        if (latest is not null && !SessionTurnPhases.IsTerminal(latest.Phase))
        {
            await TryEndTurnAsync(
                    latest.Id,
                    SessionTurnPhase.Cancelled,
                    "user_cancelled",
                    "用户取消了当前请求。")
                .ConfigureAwait(false);
        }

        return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalSessionSnapshot?> WaitForChangeAsync(
        long knownChangeVersion,
        TimeSpan maximumWait,
        CancellationToken cancellationToken = default)
    {
        if (maximumWait < TimeSpan.Zero || maximumWait > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWait));
        }

        Task<long>? wait = null;
        if (knownChangeVersion < 0)
        {
            var versionBeforeLookup = ChangeVersion;
            var current = await GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                return current;
            }

            lock (_changeGate)
            {
                if (versionBeforeLookup == _changeVersion)
                {
                    wait = _nextChange.Task;
                }
            }
        }

        lock (_changeGate)
        {
            if (wait is null && knownChangeVersion == _changeVersion)
            {
                wait = _nextChange.Task;
            }
        }

        if (wait is not null)
        {
            try
            {
                await wait.WaitAsync(maximumWait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        return await GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalSessionProjectionUpdate> WaitForProjectionAsync(
        string coordinatorInstanceId,
        DateTimeOffset coordinatorStartedAtUtc,
        Guid sessionId,
        long knownChangeVersion,
        long knownMessageSequenceNumber,
        TimeSpan maximumWait,
        CancellationToken cancellationToken = default)
    {
        if (maximumWait < TimeSpan.Zero || maximumWait > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWait));
        }

        if (!string.Equals(coordinatorInstanceId, _coordinatorInstanceId, StringComparison.Ordinal)
            || coordinatorStartedAtUtc != _coordinatorStartedAtUtc)
        {
            return ResetProjection(sessionId, "coordinator_changed");
        }

        var currentSession = await sessionStore.GetCurrentSessionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (currentSession is null || currentSession.Id != sessionId)
        {
            return ResetProjection(currentSession?.Id, "session_changed");
        }

        Task<long>? wait = null;
        lock (_changeGate)
        {
            if (knownChangeVersion == _changeVersion)
            {
                wait = _nextChange.Task;
            }
        }

        if (wait is not null)
        {
            try
            {
                await wait.WaitAsync(maximumWait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        var currentVersion = ChangeVersion;
        if (knownChangeVersion == currentVersion)
        {
            return new LocalSessionProjectionUpdate(
                LocalSessionProjectionKind.NoChange,
                currentVersion,
                _coordinatorInstanceId,
                _coordinatorStartedAtUtc,
                sessionId,
                [],
                [],
                knownMessageSequenceNumber);
        }

        if (knownChangeVersion < 0 || knownChangeVersion > currentVersion)
        {
            return ResetProjection(sessionId, "change_version_invalid");
        }

        var journal = _changeJournal.Read(knownChangeVersion, currentVersion, sessionId);
        if (journal.ResetRequired)
        {
            return ResetProjection(sessionId, journal.ResetReason ?? "journal_gap");
        }

        var messageChanges = await conversationStore.GetMessageChangesAsync(
                currentSession.ConversationId,
                knownMessageSequenceNumber,
                50,
                cancellationToken)
            .ConfigureAwait(false);
        if (messageChanges.HasMore)
        {
            return ResetProjection(sessionId, "message_upsert_overflow");
        }

        return new LocalSessionProjectionUpdate(
            LocalSessionProjectionKind.Delta,
            currentVersion,
            _coordinatorInstanceId,
            _coordinatorStartedAtUtc,
            sessionId,
            journal.TurnUpserts,
            messageChanges.Items,
            messageChanges.LastSequenceNumber);
    }

    public async Task<LocalSessionMessagesPage> GetMessagesPageAsync(
        Guid sessionId,
        long? beforeSequenceNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var page = await conversationStore.GetMessagesPageAsync(
                session.ConversationId,
                beforeSequenceNumber,
                pageSize,
                cancellationToken)
            .ConfigureAwait(false);
        return new LocalSessionMessagesPage(
            _coordinatorInstanceId,
            _coordinatorStartedAtUtc,
            sessionId,
            page);
    }

    public async Task<LocalSessionTurnDetails> GetTurnDetailsAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var turn = await sessionStore.GetTurnAsync(turnId, cancellationToken).ConfigureAwait(false);
        if (turn is null || turn.SessionId != sessionId)
        {
            throw new SessionProjectionException(
                "session_turn_not_found",
                "没有找到这条会话请求。");
        }

        return new LocalSessionTurnDetails(
            _coordinatorInstanceId,
            _coordinatorStartedAtUtc,
            sessionId,
            turn);
    }

    public async Task<SessionRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await sessionStore.RecoverInterruptedAsync(
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var turnId in result.InterruptedTurnIds)
        {
            await ReconcileRecoveredTurnAsync(turnId, cancellationToken).ConfigureAwait(false);
        }

        if (result.InterruptedTurnIds.Count > 0)
        {
            PublishChange();
        }

        return result;
    }

    private async Task ReconcileRecoveredTurnAsync(
        Guid turnId,
        CancellationToken cancellationToken)
    {
        var turn = await sessionStore.GetTurnAsync(turnId, cancellationToken).ConfigureAwait(false);
        if (turn is null)
        {
            return;
        }

        SessionTurnPhase? authoritativePhase = null;
        string? summary = turn.ResultSummary;
        string? failureCode = turn.FailureCode;
        string? failureMessage = turn.FailureMessage;
        DateTimeOffset? completedAt = turn.CompletedAtUtc;
        if (turn.ConversationTurnId is { } conversationTurnId)
        {
            var session = await sessionStore.GetSessionAsync(turn.SessionId, cancellationToken)
                .ConfigureAwait(false);
            var details = session is null
                ? null
                : await conversations.GetDetailsAsync(session.ConversationId, cancellationToken)
                    .ConfigureAwait(false);
            var conversationTurn = details?.Turns.SingleOrDefault(item => item.Id == conversationTurnId);
            if (conversationTurn is not null)
            {
                authoritativePhase = conversationTurn.Status switch
                {
                    ConversationTurnStatus.Succeeded => SessionTurnPhase.Completed,
                    ConversationTurnStatus.Cancelled => SessionTurnPhase.Cancelled,
                    ConversationTurnStatus.Failed => SessionTurnPhase.Failed,
                    _ => SessionTurnPhase.Interrupted
                };
                summary = conversationTurn.AssistantMessageId is { } assistantId
                    ? details!.Messages.SingleOrDefault(message => message.Id == assistantId)?.Content
                    : summary;
                failureCode = conversationTurn.FailureCode;
                failureMessage = conversationTurn.FailureMessage;
                completedAt = conversationTurn.CompletedAtUtc ?? completedAt;
            }
        }
        else if (turn.TaskId is { } taskId)
        {
            var details = await tasks.GetTaskDetailsAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (details is not null)
            {
                authoritativePhase = details.Task.Status switch
                {
                    AgentTaskStatus.Succeeded => SessionTurnPhase.Completed,
                    AgentTaskStatus.Cancelled => SessionTurnPhase.Cancelled,
                    AgentTaskStatus.Failed => SessionTurnPhase.Failed,
                    _ => SessionTurnPhase.Interrupted
                };
                summary = details.Evidence?.UserSummary ?? summary;
                failureCode = details.Task.FailureCode ?? failureCode;
                failureMessage = details.Task.FailureMessage ?? failureMessage;
                completedAt = details.Task.CompletedAtUtc ?? completedAt;
            }
        }

        if (authoritativePhase is null || authoritativePhase == turn.Phase)
        {
            return;
        }

        await sessionStore.UpdateTurnAsync(
                turn with
                {
                    Phase = authoritativePhase.Value,
                    MissingContext = SessionMissingContext.None,
                    ResultSummary = summary,
                    FailureCode = failureCode,
                    FailureMessage = failureMessage,
                    CompletedAtUtc = completedAt ?? timeProvider.GetUtcNow()
                },
                turn.Version,
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _preparedMemoryConsents.Clear();
        _requestedMemorySelections.Clear();
        var active = _activeWork.Values.ToArray();
        foreach (var work in active)
        {
            work.Cancellation.Cancel();
        }

        var current = await sessionStore.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            var turns = await sessionStore.GetActiveTurnsAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
            if (turns.Any(turn => turn.Phase == SessionTurnPhase.Responding))
            {
                try
                {
                    await conversations.CancelAsync(current.ConversationId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        if (active.Length > 0)
        {
            await Task.WhenAll(active.Select(item => item.Completion))
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
        }

        var monitors = _taskMonitors.Values.ToArray();
        foreach (var monitor in monitors)
        {
            monitor.Cancellation.Cancel();
        }

        if (monitors.Length > 0)
        {
            await Task.WhenAll(monitors.Select(item => item.Completion))
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RunInitialTurnAsync(
        SessionRecord session,
        SessionTurnRecord turn,
        CancellationToken cancellationToken)
    {
        try
        {
            await PlanAndRouteAsync(
                    session,
                    turn,
                    observationConsent: false,
                    executeReadyPlan: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryEndTurnAsync(
                    turn.Id,
                    SessionTurnPhase.Cancelled,
                    "cancelled",
                    "上一条请求已停止。")
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TryEndTurnAsync(
                    turn.Id,
                    SessionTurnPhase.Failed,
                    "session_request_failed",
                    FriendlyException(exception))
                .ConfigureAwait(false);
        }
        finally
        {
            RemoveActiveWork(turn.Id);
        }
    }

    private ActiveSessionWork StartAuthorizedContinuation(
        SessionRecord session,
        SessionTurnRecord turn,
        bool observationConsent)
    {
        var active = new ActiveSessionWork(turn.Id);
        if (!_activeWork.TryAdd(turn.Id, active))
        {
            throw new InvalidOperationException("同一个会话请求已经在处理中。 ");
        }

        active.SetCompletion(Task.Run(async () =>
        {
            try
            {
                var prepared = await TransitionAsync(
                        turn.Id,
                        current => current with
                        {
                            Phase = SessionTurnPhase.Understanding,
                            MissingContext = SessionMissingContext.None,
                            ConfirmationGranted = true,
                            FailureCode = null,
                            FailureMessage = null
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (SessionTurnPhases.IsTerminal(prepared.Phase))
                {
                    return;
                }

                await PlanAndRouteAsync(
                        session,
                        prepared,
                        observationConsent,
                        executeReadyPlan: true,
                        active.Cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
            {
                await TryEndTurnAsync(
                        turn.Id,
                        SessionTurnPhase.Cancelled,
                        "cancelled",
                        "这次请求已停止。")
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await TryEndTurnAsync(
                        turn.Id,
                        SessionTurnPhase.Failed,
                        "session_execution_failed",
                        FriendlyException(exception))
                    .ConfigureAwait(false);
            }
            finally
            {
                RemoveActiveWork(turn.Id);
            }
        }, CancellationToken.None));
        return active;
    }

    private async Task PlanAndRouteAsync(
        SessionRecord session,
        SessionTurnRecord turn,
        bool observationConsent,
        bool executeReadyPlan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var latest = await sessionStore.GetTurnAsync(turn.Id, CancellationToken.None).ConfigureAwait(false);
        if (latest is null || SessionTurnPhases.IsTerminal(latest.Phase))
        {
            return;
        }

        turn = latest;
        var semanticRoute = RestoreReadyRoute(turn.FrozenRoute);
        var memorySelectionRequested = _requestedMemorySelections.TryGetValue(
                turn.Id,
                out var requestedMemoryItems)
            && requestedMemoryItems.Count > 0;
        var persistedIntent = memorySelectionRequested
            ? UniversalIntentKind.Conversation
            : TryParsePersistedIntent(turn.IntentKind);
        var plan = await assistantCommands.PlanSessionAsync(
                new PlanAssistantCommandRequestDto(
                    turn.InputText,
                    turn.FilePath,
                    turn.ProjectId ?? session.SelectedProjectId,
                    observationConsent,
                    turn.InputModality),
                turn.Id,
                semanticRoute,
                persistedIntent,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!MatchesExpectedAction(turn, plan.IntentKind, plan.CanonicalTarget))
        {
            await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        WorkKind = WorkKindFor(ParseIntentKind(plan.IntentKind)),
                        IntentKind = plan.IntentKind,
                        PlanId = null,
                        PlanTarget = plan.CanonicalTarget,
                        Phase = SessionTurnPhase.Failed,
                        MissingContext = SessionMissingContext.None,
                        ResultSummary = "安全检查发现实际计划与刚才确认的目标不一致，因此没有执行。",
                        FailureCode = "confirmed_target_mismatch",
                        FailureMessage = "结构化操作目标不一致。",
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        var kind = ParseIntentKind(plan.IntentKind);
        var plannedWindow = kind is UniversalIntentKind.DescribeForeground
            or UniversalIntentKind.SearchForeground
            ? ResolvePlannedWindow(plan.ForegroundApplication)
            : null;

        turn = await TransitionAsync(
                turn.Id,
                current => current with
                {
                    IntentKind = plan.IntentKind,
                    PlanTarget = plan.CanonicalTarget
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        if (SessionTurnPhases.IsTerminal(turn.Phase))
        {
            return;
        }

        var workKind = WorkKindFor(kind);

        if (executeReadyPlan
            && kind is UniversalIntentKind.DescribeForeground or UniversalIntentKind.SearchForeground
            && !MatchesPersistedWindowIdentity(turn, plannedWindow))
        {
            var changedPhase = kind == UniversalIntentKind.DescribeForeground
                ? SessionTurnPhase.WaitingForWindowConsent
                : SessionTurnPhase.WaitingForConfirmation;
            var changedMissing = kind == UniversalIntentKind.DescribeForeground
                ? SessionMissingContext.WindowConsent
                : SessionMissingContext.Confirmation;
            await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        WorkKind = workKind,
                        IntentKind = plan.IntentKind,
                        PlanId = null,
                        Phase = plannedWindow is null
                            ? SessionTurnPhase.WaitingForWindow
                            : changedPhase,
                        MissingContext = plannedWindow is null
                            ? SessionMissingContext.Window
                            : changedMissing,
                        WindowHandle = plannedWindow?.WindowHandle,
                        WindowTitle = plannedWindow?.WindowTitle,
                        WindowProcessName = plannedWindow?.ProcessName,
                        WindowProcessId = plannedWindow?.ProcessId,
                        WindowProcessStartTimeUtc = plannedWindow?.ProcessStartTimeUtc,
                        ConfirmationGranted = false,
                        RequiresConfirmation = true,
                        ResultSummary = plannedWindow is null
                            ? "目标窗口已经不可用，请切换到要操作的窗口后继续。"
                            : "前台窗口已经变化，请确认新的目标窗口后再继续。",
                        FailureCode = "window_target_changed",
                        FailureMessage = "授权后目标窗口发生变化。"
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(plan.Readiness, IntentPlanReadiness.NeedsContext.ToString(), StringComparison.Ordinal))
        {
            var (phase, missing) = MissingContextState(kind, plan);
            if (kind is UniversalIntentKind.DescribeForeground or UniversalIntentKind.SearchForeground
                && plannedWindow is null)
            {
                phase = SessionTurnPhase.WaitingForWindow;
                missing = SessionMissingContext.Window;
            }
            if (kind == UniversalIntentKind.CodingTask
                && (turn.ProjectId ?? session.SelectedProjectId) is not null)
            {
                session = await sessionStore.SetSelectedProjectAsync(
                        session.Id,
                        projectId: null,
                        timeProvider.GetUtcNow(),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        WorkKind = workKind,
                        IntentKind = plan.IntentKind,
                        PlanId = null,
                        Phase = phase,
                        MissingContext = missing,
                        ProjectId = kind == UniversalIntentKind.CodingTask
                            ? null
                            : current.ProjectId,
                        WindowHandle = plannedWindow?.WindowHandle,
                        WindowTitle = plannedWindow?.WindowTitle,
                        WindowProcessName = plannedWindow?.ProcessName,
                        WindowProcessId = plannedWindow?.ProcessId,
                        WindowProcessStartTimeUtc = plannedWindow?.ProcessStartTimeUtc,
                        RequiresConfirmation = phase == SessionTurnPhase.WaitingForWindowConsent,
                        ResultSummary = plan.UserSummary,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(plan.Readiness, IntentPlanReadiness.Ready.ToString(), StringComparison.Ordinal)
            || kind == UniversalIntentKind.Unsupported)
        {
            await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        WorkKind = workKind,
                        IntentKind = plan.IntentKind,
                        Phase = SessionTurnPhase.Failed,
                        MissingContext = SessionMissingContext.None,
                        ResultSummary = plan.UserSummary,
                        FailureCode = "intent_unsupported",
                        FailureMessage = plan.UserSummary,
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        if (kind == UniversalIntentKind.Conversation)
        {
            if (memorySelectionRequested)
            {
                await PrepareMemoryOutboundConsentAsync(
                        session,
                        turn,
                        requestedMemoryItems!,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await RunConversationTurnAsync(session, turn, memoryOutbound: null, cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        var directVoiceAction = IsDirectVoiceAction(kind, turn.InputModality);
        if (plan.RequiresConfirmation && !executeReadyPlan && !directVoiceAction)
        {
            await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        WorkKind = workKind,
                        IntentKind = plan.IntentKind,
                        PlanId = plan.PlanId,
                        Phase = SessionTurnPhase.WaitingForConfirmation,
                        MissingContext = SessionMissingContext.Confirmation,
                        ProjectId = turn.ProjectId ?? session.SelectedProjectId,
                        FilePath = turn.FilePath,
                        WindowHandle = plannedWindow?.WindowHandle,
                        WindowTitle = plannedWindow?.WindowTitle,
                        WindowProcessName = plannedWindow?.ProcessName,
                        WindowProcessId = plannedWindow?.ProcessId,
                        WindowProcessStartTimeUtc = plannedWindow?.ProcessStartTimeUtc,
                        RequiresConfirmation = true,
                        ResultSummary = plan.ConfirmationText ?? plan.UserSummary,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        await ExecutePlanAsync(
                session,
                turn,
                plan,
                plannedWindow,
                directVoiceAction,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ExecutePlanAsync(
        SessionRecord session,
        SessionTurnRecord turn,
        AssistantIntentPlanDto plan,
        ForegroundWindowSnapshot? plannedWindow,
        bool directVoiceAction,
        CancellationToken cancellationToken)
    {
        var kind = ParseIntentKind(plan.IntentKind);
        var workKind = WorkKindFor(kind);
        var operationId = $"session-action-{turn.Id:N}";
        var runningPhase = kind == UniversalIntentKind.DescribeForeground
            ? SessionTurnPhase.ObservingWindow
            : SessionTurnPhase.Executing;
        var running = await TransitionAsync(
                turn.Id,
                current => current with
                {
                    WorkKind = workKind,
                    IntentKind = plan.IntentKind,
                    PlanId = plan.PlanId,
                    Phase = runningPhase,
                    MissingContext = SessionMissingContext.None,
                    OperationId = operationId,
                    ProjectId = current.ProjectId ?? session.SelectedProjectId,
                    WindowHandle = plannedWindow?.WindowHandle ?? current.WindowHandle,
                    WindowTitle = plannedWindow?.WindowTitle ?? current.WindowTitle,
                    WindowProcessName = plannedWindow?.ProcessName ?? current.WindowProcessName,
                    WindowProcessId = plannedWindow?.ProcessId ?? current.WindowProcessId,
                    WindowProcessStartTimeUtc = plannedWindow?.ProcessStartTimeUtc
                        ?? current.WindowProcessStartTimeUtc,
                    RequiresConfirmation = plan.RequiresConfirmation,
                    FailureCode = null,
                    FailureMessage = null
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (SessionTurnPhases.IsTerminal(running.Phase) || running.Phase != runningPhase)
        {
            return;
        }

        if (IsWindowBoundTurn(running) && !MatchesPersistedWindowIdentity(running))
        {
            await RequireFreshWindowConfirmationAsync(running, CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        var result = await assistantCommands.ExecuteAsync(
                new ExecuteAssistantCommandRequestDto(
                    plan.PlanId,
                    Confirmed: !directVoiceAction,
                    operationId,
                    directVoiceAction ? "ExplicitVoice" : "VisibleConfirmation"),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.TaskId is { } taskId)
        {
            await TransitionAsync(
                    running.Id,
                    current => current with
                    {
                        TaskId = taskId,
                        Phase = SessionTurnPhase.ProgrammingTask,
                        ResultSummary = result.UserSummary,
                        CompletedAtUtc = null
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            StartTaskMonitor(turn.Id, taskId);
            return;
        }

        var succeeded = string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase);
        await TransitionAsync(
                running.Id,
                current => current with
                {
                    Phase = succeeded ? SessionTurnPhase.Completed : SessionTurnPhase.Failed,
                    ResultSummary = result.UserSummary,
                    FailureCode = succeeded ? null : "action_failed",
                    FailureMessage = succeeded ? null : result.UserSummary,
                    CompletedAtUtc = timeProvider.GetUtcNow()
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task PrepareMemoryOutboundConsentAsync(
        SessionRecord session,
        SessionTurnRecord turn,
        IReadOnlyList<MemoryOutboundItemReference> selection,
        CancellationToken cancellationToken)
    {
        try
        {
            if (turn.FrozenRoute is not
                {
                    Status: SessionTurnRouteStatus.Ready,
                    ProviderId: { Length: > 0 } providerId,
                    ModelId: { Length: > 0 } modelId,
                    DataDestination: { Length: > 0 } destination
                })
            {
                await TransitionAsync(
                        turn.Id,
                        current => current with
                        {
                            WorkKind = SessionWorkKind.Conversation,
                            Phase = SessionTurnPhase.Failed,
                            MemoryOutboundState = MemoryOutboundConsentState.Invalidated,
                            FailureCode = turn.FrozenRoute?.FailureCode ?? "frozen_chat_route_invalid",
                            FailureMessage = "当前普通聊天路由不可用，所选记忆没有发送。",
                            CompletedAtUtc = timeProvider.GetUtcNow()
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            var prompt = prompts.GetRequired("chat.general", "2", providerId);
            var request = new MemoryOutboundPreparationRequest(
                Guid.Parse(_coordinatorInstanceId),
                session.Id,
                turn.Id,
                turn.Version,
                providerId,
                modelId,
                destination,
                prompt.PromptId,
                prompt.Version,
                prompt.ContentSha256,
                turn.ProjectId ?? session.SelectedProjectId,
                selection);
            var prepared = await memories.PrepareOutboundAsync(request, cancellationToken)
                .ConfigureAwait(false);
            foreach (var observer in memoryConsentPublicationObservers)
            {
                await observer.BeforePublishAsync(turn.Id, prepared.ConsentId, cancellationToken)
                    .ConfigureAwait(false);
            }

            var gate = _turnGates.GetOrAdd(turn.Id, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var latestSession = await sessionStore.GetSessionAsync(session.Id, cancellationToken)
                    .ConfigureAwait(false);
                var latestTurn = await sessionStore.GetTurnAsync(turn.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (latestSession is null
                    || latestTurn is null
                    || !CanPublishPreparedConsent(latestSession, latestTurn, prepared, selection))
                {
                    throw new MemoryServiceException(
                        MemoryOutboundErrorCodes.ConsentStale,
                        "记忆出站确认已失效，请重新发送并检查。");
                }

                _preparedMemoryConsents[turn.Id] = prepared;
                try
                {
                    _ = await TransitionAsync(
                            turn.Id,
                            current => current with
                            {
                                WorkKind = SessionWorkKind.Conversation,
                                IntentKind = "Conversation",
                                Phase = SessionTurnPhase.WaitingForMemoryOutboundConsent,
                                MissingContext = SessionMissingContext.None,
                                MemoryOutboundState = MemoryOutboundConsentState.WaitingForMemoryOutboundConsent,
                                MemoryOutbound = prepared.ToAuditMetadata(),
                                ResultSummary = "请检查本次将发送的完整记忆内容、AI 服务和目的地后再确认。",
                                FailureCode = null,
                                FailureMessage = null
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    _requestedMemorySelections.TryRemove(turn.Id, out _);
                }
                catch
                {
                    _preparedMemoryConsents.TryRemove(turn.Id, out _);
                    throw;
                }
            }
            finally
            {
                gate.Release();
            }

            foreach (var observer in memoryConsentPublicationObservers)
            {
                await observer.AfterPublishAsync(turn.Id, prepared.ConsentId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _preparedMemoryConsents.TryRemove(turn.Id, out _);
            _requestedMemorySelections.TryRemove(turn.Id, out _);
            throw;
        }
        catch (MemoryServiceException exception)
        {
            _preparedMemoryConsents.TryRemove(turn.Id, out _);
            _requestedMemorySelections.TryRemove(turn.Id, out _);
            await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        WorkKind = SessionWorkKind.Conversation,
                        Phase = SessionTurnPhase.Failed,
                        MemoryOutboundState = MemoryOutboundConsentState.Invalidated,
                        FailureCode = exception.Code,
                        FailureMessage = exception.Message,
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private bool CanPublishPreparedConsent(
        SessionRecord session,
        SessionTurnRecord turn,
        MemoryOutboundPreparedConsent prepared,
        IReadOnlyList<MemoryOutboundItemReference> selection)
    {
        if (!session.IsCurrent
            || prepared.CoordinatorInstanceId != Guid.Parse(_coordinatorInstanceId)
            || prepared.SessionId != session.Id
            || prepared.TurnId != turn.Id
            || prepared.TurnVersion != turn.Version
            || turn.Phase != SessionTurnPhase.Understanding
            || SessionTurnPhases.IsTerminal(turn.Phase)
            || turn.CancellationRequested
            || turn.MemoryOutboundState != MemoryOutboundConsentState.None
            || turn.MemoryOutbound is not null
            || prepared.ProjectId != (turn.ProjectId ?? session.SelectedProjectId)
            || !MemorySelectionMatches(turn, selection)
            || prepared.Items.Count != selection.Count
            || prepared.Items.Zip(selection).Any(pair =>
                pair.First.Id != pair.Second.MemoryId
                || pair.First.Version != pair.Second.ExpectedVersion))
        {
            return false;
        }

        if (turn.FrozenRoute is not
            {
                Status: SessionTurnRouteStatus.Ready,
                ProviderId: { Length: > 0 } providerId,
                ModelId: { Length: > 0 } modelId,
                DataDestination: { Length: > 0 } destination
            }
            || !string.Equals(providerId, prepared.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(modelId, prepared.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var prompt = prompts.GetRequired("chat.general", "2", providerId);
            return string.Equals(
                    MemoryOutboundContract.NormalizeHttpsOrigin(destination),
                    prepared.DestinationOrigin,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(prompt.PromptId, prepared.PromptId, StringComparison.Ordinal)
                && string.Equals(prompt.Version, prepared.PromptVersion, StringComparison.Ordinal)
                && string.Equals(
                    prompt.ContentSha256,
                    prepared.PromptContentHash,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task CommitAndRunMemoryConversationAsync(
        SessionRecord session,
        SessionTurnRecord committing,
        MemoryOutboundPreparedConsent prepared,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await memories.CommitOutboundAsync(prepared, cancellationToken)
                .ConfigureAwait(false);
            _preparedMemoryConsents.TryRemove(committing.Id, out _);
            _requestedMemorySelections.TryRemove(committing.Id, out _);
            var consumed = await TransitionAsync(
                    committing.Id,
                    current => current with
                    {
                        MemoryOutboundState = MemoryOutboundConsentState.Consumed,
                        MemoryOutbound = envelope.Audit,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            await RunConversationTurnAsync(session, consumed, envelope, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await EndMemoryTurnAsync(
                    committing.Id,
                    SessionTurnPhase.Cancelled,
                    MemoryOutboundConsentState.Cancelled,
                    MemoryOutboundErrorCodes.Cancelled,
                    "记忆出站已取消，没有复用本次确认。")
                .ConfigureAwait(false);
        }
        catch (MemoryServiceException exception)
        {
            await EndMemoryTurnAsync(
                    committing.Id,
                    SessionTurnPhase.Failed,
                    MemoryOutboundConsentState.Invalidated,
                    exception.Code,
                    exception.Message)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await EndMemoryTurnAsync(
                    committing.Id,
                    SessionTurnPhase.Failed,
                    MemoryOutboundConsentState.Invalidated,
                    MemoryOutboundErrorCodes.CommitFailed,
                    "记忆出站提交未能安全完成，没有发送。")
                .ConfigureAwait(false);
        }
        finally
        {
            _preparedMemoryConsents.TryRemove(committing.Id, out _);
            _requestedMemorySelections.TryRemove(committing.Id, out _);
            RemoveActiveWork(committing.Id);
            PublishChange();
        }
    }

    private async Task EndMemoryTurnAsync(
        Guid turnId,
        SessionTurnPhase phase,
        MemoryOutboundConsentState consentState,
        string code,
        string message)
    {
        try
        {
            await TransitionAsync(
                    turnId,
                    current => current with
                    {
                        Phase = phase,
                        MemoryOutboundState = consentState,
                        CancellationRequested = phase == SessionTurnPhase.Cancelled,
                        FailureCode = code,
                        FailureMessage = message,
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task RunConversationTurnAsync(
        SessionRecord session,
        SessionTurnRecord sessionTurn,
        MemoryOutboundEnvelope? memoryOutbound,
        CancellationToken cancellationToken)
    {
        try
        {
            var responding = await TransitionAsync(
                    sessionTurn.Id,
                    turn => turn with
                    {
                        WorkKind = SessionWorkKind.Conversation,
                        IntentKind = "Conversation",
                        Phase = SessionTurnPhase.Responding,
                        MissingContext = SessionMissingContext.None
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            var sent = await conversations.SendSessionAsync(
                    session.ConversationId,
                    responding.Id,
                    responding.FrozenRoute,
                    responding.InputText,
                    $"session-conversation-{responding.Id:N}",
                    memoryOutbound,
                    cancellationToken)
                .ConfigureAwait(false);
            await TransitionAsync(
                    responding.Id,
                    turn => turn with { ConversationTurnId = sent.TurnId },
                    CancellationToken.None)
                .ConfigureAwait(false);
            await conversations.WaitForTurnAsync(sent.TurnId, CancellationToken.None).ConfigureAwait(false);
            var details = await conversations.GetDetailsAsync(session.ConversationId, CancellationToken.None)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("会话已经不存在。");
            var conversationTurn = details.Turns.Single(turn => turn.Id == sent.TurnId);
            var assistant = conversationTurn.AssistantMessageId is { } assistantId
                ? details.Messages.SingleOrDefault(message => message.Id == assistantId)
                : null;
            var phase = conversationTurn.Status switch
            {
                ConversationTurnStatus.Succeeded => SessionTurnPhase.Completed,
                ConversationTurnStatus.Cancelled => SessionTurnPhase.Cancelled,
                ConversationTurnStatus.Interrupted => SessionTurnPhase.Interrupted,
                _ => SessionTurnPhase.Failed
            };
            await TransitionAsync(
                    responding.Id,
                    turn => turn with
                    {
                        Phase = phase,
                        CancellationRequested = phase == SessionTurnPhase.Cancelled,
                        ResultSummary = assistant?.Content,
                        FailureCode = conversationTurn.FailureCode,
                        FailureMessage = conversationTurn.FailureMessage,
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryEndTurnAsync(
                    sessionTurn.Id,
                    SessionTurnPhase.Cancelled,
                    "cancelled",
                    "上一条回答已停止。")
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TryEndTurnAsync(
                    sessionTurn.Id,
                    SessionTurnPhase.Failed,
                    "session_conversation_failed",
                    FriendlyException(exception))
                .ConfigureAwait(false);
        }
    }

    private async Task CancelForegroundWorkAsync(
        SessionRecord session,
        string reason,
        Guid? excludedTurnId,
        CancellationToken cancellationToken)
    {
        var turns = await sessionStore.GetActiveTurnsAsync(session.Id, cancellationToken)
            .ConfigureAwait(false);
        foreach (var turn in turns.Where(item =>
                     item.Id != excludedTurnId && SessionTurnPhases.IsForegroundWork(item.Phase)))
        {
            var gate = _turnGates.GetOrAdd(turn.Id, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            ActiveSessionWork? active = null;
            SessionTurnRecord? latest = null;
            SessionTurnPhase phaseAtCancellation = default;
            string? operationIdAtCancellation = null;
            try
            {
                _preparedMemoryConsents.TryRemove(turn.Id, out _);
                _requestedMemorySelections.TryRemove(turn.Id, out _);
                latest = await sessionStore.GetTurnAsync(turn.Id, cancellationToken).ConfigureAwait(false);
                if (latest is null
                    || SessionTurnPhases.IsTerminal(latest.Phase)
                    || !SessionTurnPhases.IsForegroundWork(latest.Phase))
                {
                    continue;
                }

                phaseAtCancellation = latest.Phase;
                operationIdAtCancellation = latest.OperationId;
                if (_activeWork.TryGetValue(latest.Id, out active))
                {
                    if (latest.Phase != SessionTurnPhase.Responding)
                    {
                        // Confirm/consent and replacement share this Turn gate. Whichever
                        // wins first either never starts, or receives cancellation here.
                        CancelActiveWork(active);
                    }
                }
                else
                {
                    latest = await TransitionAsync(
                            latest.Id,
                            current => current with
                            {
                                Phase = SessionTurnPhase.Cancelled,
                                MissingContext = SessionMissingContext.None,
                                CancellationRequested = true,
                                FailureCode = "replaced_by_new_input",
                                FailureMessage = reason,
                                CompletedAtUtc = timeProvider.GetUtcNow()
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }

            if (active is not null)
            {
                if (phaseAtCancellation == SessionTurnPhase.Responding)
                {
                    try
                    {
                        await conversations.CancelAsync(session.ConversationId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        CancelActiveWork(active);
                    }
                }

                if (!string.IsNullOrWhiteSpace(operationIdAtCancellation))
                {
                    assistantCommands.CancelWindowObservation(operationIdAtCancellation);
                }

                await active.Completion.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken)
                    .ConfigureAwait(false);
            }

            latest = await sessionStore.GetTurnAsync(turn.Id, cancellationToken).ConfigureAwait(false);
            if (latest is not null && !SessionTurnPhases.IsTerminal(latest.Phase))
            {
                await TryEndTurnAsync(
                        latest.Id,
                        SessionTurnPhase.Cancelled,
                        "replaced_by_new_input",
                        reason)
                    .ConfigureAwait(false);
            }
        }
    }

    private void StartTaskMonitor(Guid turnId, Guid taskId)
    {
        var monitor = new ActiveTaskMonitor(turnId, taskId);
        if (!_taskMonitors.TryAdd(turnId, monitor))
        {
            monitor.Cancellation.Dispose();
            return;
        }

        monitor.SetCompletion(Task.Run(
            () => MonitorTaskAsync(monitor),
            CancellationToken.None));
    }

    private async Task MonitorTaskAsync(ActiveTaskMonitor monitor)
    {
        try
        {
            await taskStateSynchronizer.RunAsync(
                    monitor.TurnId,
                    monitor.TaskId,
                    (update, _) => ApplyTaskStateAsync(monitor, update),
                    monitor.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (monitor.Cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _taskMonitors.TryRemove(monitor.TurnId, out _);
            monitor.Cancellation.Dispose();
        }
    }

    private async Task ApplyTaskStateAsync(
        ActiveTaskMonitor monitor,
        SessionTaskStateUpdate update)
    {
        if (update.TaskId != monitor.TaskId)
        {
            return;
        }

        var current = await sessionStore.GetTurnAsync(monitor.TurnId, CancellationToken.None)
            .ConfigureAwait(false);
        if (current is null
            || current.TaskId != monitor.TaskId
            || SessionTurnPhases.IsTerminal(current.Phase))
        {
            return;
        }

        var phase = Enum.Parse<SessionTurnPhase>(update.Phase, ignoreCase: false);
        if (SessionTurnPhases.IsTerminal(phase) && !update.Finalized)
        {
            return;
        }

        if (current.Phase == phase
            && string.Equals(current.ResultSummary, update.ResultSummary, StringComparison.Ordinal)
            && string.Equals(current.FailureCode, update.FailureCode, StringComparison.Ordinal))
        {
            return;
        }

        await TransitionAsync(
                current.Id,
                turn => turn with
                {
                    Phase = phase,
                    MissingContext = phase == SessionTurnPhase.WaitingForUser
                        ? SessionMissingContext.UserInput
                        : SessionMissingContext.None,
                    ResultSummary = update.ResultSummary ?? turn.ResultSummary,
                    FailureCode = update.FailureCode,
                    FailureMessage = update.FailureMessage,
                    CompletedAtUtc = SessionTurnPhases.IsTerminal(phase)
                        ? update.CompletedAtUtc ?? timeProvider.GetUtcNow()
                        : null
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<SessionRecord> RequireSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        await sessionStore.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("会话已经不存在。 ");

    private async Task<SessionTurnRecord> RequireTurnAsync(
        SessionRecord session,
        Guid turnId,
        CancellationToken cancellationToken)
    {
        var turn = await sessionStore.GetTurnAsync(turnId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("会话请求已经不存在。 ");
        if (turn.SessionId != session.Id)
        {
            throw new UnauthorizedAccessException("不能为另一个会话补充信息或授权。 ");
        }

        return turn;
    }

    private async Task<(SessionRecord Session, SessionTurnRecord Turn)> RequireWaitingTurnAsync(
        Guid sessionId,
        Guid turnId,
        SessionTurnPhase requiredPhase,
        CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var turn = await RequireTurnAsync(session, turnId, cancellationToken).ConfigureAwait(false);
        if (turn.Phase != requiredPhase)
        {
            throw new InvalidOperationException("这条请求当前不接受这项补充或确认。 ");
        }

        return (session, turn);
    }

    private static UniversalIntentKind ParseIntentKind(string value) =>
        Enum.TryParse<UniversalIntentKind>(value, ignoreCase: false, out var kind)
            ? kind
            : UniversalIntentKind.Unsupported;

    private static UniversalIntentKind? TryParsePersistedIntent(string? value) =>
        Enum.TryParse<UniversalIntentKind>(value, ignoreCase: false, out var kind)
        && kind != UniversalIntentKind.Unsupported
            ? kind
            : null;

    private FrozenChatModelRoute? RestoreReadyRoute(SessionTurnFrozenRoute? frozenRoute)
    {
        if (frozenRoute?.Status != SessionTurnRouteStatus.Ready
            || string.IsNullOrWhiteSpace(frozenRoute.ProviderId)
            || string.IsNullOrWhiteSpace(frozenRoute.ModelId)
            || string.IsNullOrWhiteSpace(frozenRoute.DataDestination)
            || frozenRoute.SendsDataOffDevice is null)
        {
            return null;
        }

        try
        {
            return modelRouter.RestoreFrozenChatRoute(
                frozenRoute.ProviderId,
                frozenRoute.ModelId,
                frozenRoute.DataDestination,
                frozenRoute.SendsDataOffDevice.Value);
        }
        catch (ChatModelException exception) when (
            string.Equals(exception.Error.Code, "frozen_chat_route_invalid", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task<LocalSessionSnapshot> ConfirmMemoryOutboundAsync(
        Guid sessionId,
        Guid turnId,
        Guid consentId,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        var gate = _turnGates.GetOrAdd(turnId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SessionRecord session;
        try
        {
            (SessionRecord Session, SessionTurnRecord Turn) required;
            try
            {
                required = await RequireWaitingTurnAsync(
                        sessionId,
                        turnId,
                        SessionTurnPhase.WaitingForMemoryOutboundConsent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                throw new MemoryServiceException(
                    MemoryOutboundErrorCodes.ConsentStale,
                    "记忆出站确认已失效，不能重复使用。",
                    exception);
            }
            session = required.Session;
            if (!_preparedMemoryConsents.TryGetValue(turnId, out var prepared)
                || consentId == Guid.Empty
                || prepared.ConsentId != consentId
                || !PreparedConsentMatches(required.Session, required.Turn, prepared))
            {
                _preparedMemoryConsents.TryRemove(turnId, out _);
                await TransitionAsync(
                        turnId,
                        current => current with
                        {
                            Phase = SessionTurnPhase.Failed,
                            MissingContext = SessionMissingContext.None,
                            MemoryOutboundState = MemoryOutboundConsentState.Invalidated,
                            FailureCode = MemoryOutboundErrorCodes.ConsentStale,
                            FailureMessage = "记忆出站确认已失效，请重新发送并检查。",
                            CompletedAtUtc = timeProvider.GetUtcNow()
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
            }

            if (!confirmed)
            {
                _preparedMemoryConsents.TryRemove(turnId, out _);
                _requestedMemorySelections.TryRemove(turnId, out _);
                await TransitionAsync(
                        turnId,
                        current => current with
                        {
                            Phase = SessionTurnPhase.Cancelled,
                            MissingContext = SessionMissingContext.None,
                            MemoryOutboundState = MemoryOutboundConsentState.Declined,
                            CancellationRequested = true,
                            FailureCode = MemoryOutboundErrorCodes.Cancelled,
                            FailureMessage = "你未同意发送所选记忆，本次请求已取消。",
                            CompletedAtUtc = timeProvider.GetUtcNow()
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
            }

            var committing = await TransitionAsync(
                    turnId,
                    current => current with
                    {
                        Phase = SessionTurnPhase.Understanding,
                        MemoryOutboundState = MemoryOutboundConsentState.Committing,
                        MissingContext = SessionMissingContext.None,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            _preparedMemoryConsents.TryRemove(turnId, out _);
            _requestedMemorySelections.TryRemove(turnId, out _);
            var active = new ActiveSessionWork(turnId);
            if (!_activeWork.TryAdd(turnId, active))
            {
                active.Cancellation.Dispose();
                throw new InvalidOperationException("同一个会话请求已经在处理中。");
            }

            active.SetCompletion(Task.Run(
                () => CommitAndRunMemoryConversationAsync(
                    session,
                    committing,
                    prepared,
                    active.Cancellation.Token),
                CancellationToken.None));
            PublishChange();
        }
        finally
        {
            gate.Release();
        }

        return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private static SessionWorkKind WorkKindFor(UniversalIntentKind kind) => kind switch
    {
        UniversalIntentKind.Conversation => SessionWorkKind.Conversation,
        UniversalIntentKind.CodingTask => SessionWorkKind.CodingTask,
        UniversalIntentKind.DescribeForeground => SessionWorkKind.WindowObservation,
        UniversalIntentKind.Unsupported => SessionWorkKind.Unknown,
        _ => SessionWorkKind.DesktopAction
    };

    private static (SessionTurnPhase Phase, SessionMissingContext Missing) MissingContextState(
        UniversalIntentKind kind,
        AssistantIntentPlanDto plan) => kind switch
        {
            UniversalIntentKind.CodingTask =>
                (SessionTurnPhase.WaitingForProject, SessionMissingContext.Project),
            UniversalIntentKind.OpenFile =>
                (SessionTurnPhase.WaitingForFile, SessionMissingContext.File),
            UniversalIntentKind.DescribeForeground when plan.ForegroundApplication is not null =>
                (SessionTurnPhase.WaitingForWindowConsent, SessionMissingContext.WindowConsent),
            UniversalIntentKind.DescribeForeground or UniversalIntentKind.SearchForeground =>
                (SessionTurnPhase.WaitingForWindow, SessionMissingContext.Window),
            _ => (SessionTurnPhase.Failed, SessionMissingContext.None)
        };

    private static bool IsDirectVoiceAction(UniversalIntentKind kind, string inputModality) =>
        string.Equals(inputModality, "Voice", StringComparison.OrdinalIgnoreCase)
        && kind is UniversalIntentKind.OpenApplication
            or UniversalIntentKind.OpenWebsite
            or UniversalIntentKind.SearchForeground;

    private void RemoveActiveWork(Guid turnId)
    {
        if (_activeWork.TryRemove(turnId, out var removed))
        {
            removed.Cancellation.Dispose();
        }
    }

    private static void CancelActiveWork(ActiveSessionWork active)
    {
        try
        {
            active.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The work reached a terminal state between lookup and cancellation.
        }
    }

    private async Task<SessionTurnRecord> TransitionAsync(
        Guid turnId,
        Func<SessionTurnRecord, SessionTurnRecord> transition,
        CancellationToken cancellationToken)
    {
        var current = await sessionStore.GetTurnAsync(turnId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("会话请求不存在。");
        if (SessionTurnPhases.IsTerminal(current.Phase))
        {
            return current;
        }

        var updated = await sessionStore.UpdateTurnAsync(
                transition(current),
                current.Version,
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        PublishChange(updated.SessionId, updated);
        return updated;
    }

    private async Task TryEndTurnAsync(
        Guid turnId,
        SessionTurnPhase phase,
        string failureCode,
        string failureMessage)
    {
        try
        {
            await TransitionAsync(
                    turnId,
                    turn => turn with
                    {
                        Phase = phase,
                        CancellationRequested = phase == SessionTurnPhase.Cancelled,
                        FailureCode = failureCode,
                        FailureMessage = failureMessage,
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (SessionTurnPhases.IsTerminal(phase))
            {
                _preparedMemoryConsents.TryRemove(turnId, out _);
                _requestedMemorySelections.TryRemove(turnId, out _);
            }
        }
        catch
        {
        }
    }

    private async Task<LocalSessionSnapshot> BuildSnapshotAsync(
        SessionRecord session,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var before = ChangeVersion;
            var latestSessionTask = sessionStore.GetSessionAsync(session.Id, cancellationToken);
            var turnsTask = sessionStore.GetTurnsPageAsync(
                session.Id,
                beforeSequenceNumber: null,
                pageSize: 32,
                cancellationToken);
            var activeTask = sessionStore.GetActiveTurnsAsync(session.Id, cancellationToken);
            var messagesTask = conversationStore.GetMessagesPageAsync(
                session.ConversationId,
                beforeSequenceNumber: null,
                pageSize: 50,
                cancellationToken);
            var projectsTask = tasks.GetAuthorizedProjectsAsync(cancellationToken);
            await Task.WhenAll(
                    latestSessionTask,
                    turnsTask,
                    activeTask,
                    messagesTask,
                    projectsTask)
                .ConfigureAwait(false);
            var after = ChangeVersion;
            if (before != after && attempt < 3)
            {
                continue;
            }

            var latestSession = await latestSessionTask.ConfigureAwait(false) ?? session;
            var turns = await turnsTask.ConfigureAwait(false);
            var messages = await messagesTask.ConfigureAwait(false);
            var activeTurns = await activeTask.ConfigureAwait(false);
            var projectedTurns = SessionTurnProjection.Select(turns.Items, activeTurns);
            var selectedName = latestSession.SelectedProjectId is { } selectedProjectId
                ? (await projectsTask.ConfigureAwait(false))
                    .SingleOrDefault(project => project.Id == selectedProjectId)?.Name
                : null;
            return new LocalSessionSnapshot(
                after,
                _coordinatorInstanceId,
                _coordinatorStartedAtUtc,
                latestSession,
                selectedName,
                projectedTurns.Turns,
                projectedTurns.AdditionalActiveTurns,
                messages.Items,
                _preparedMemoryConsents.Values
                    .Where(item => item.SessionId == session.Id
                                   && projectedTurns.SelectedActiveTurns.Any(turn => turn.Id == item.TurnId
                                       && SessionTurnPhases.IsForegroundWork(turn.Phase)))
                    .OrderBy(item => item.PreparedAtUtc)
                    .ToArray());
        }

        throw new InvalidOperationException("会话状态更新过于频繁，请稍后重试。 ");
    }

    private DesktopHostSnapshot RequireStartedHost()
    {
        var snapshot = hostState.Snapshot;
        return snapshot.IsStarted && snapshot.LocalDevice is not null
            ? snapshot
            : throw new InvalidOperationException("Desktop Host 尚未完成启动。");
    }

    private long ChangeVersion
    {
        get
        {
            lock (_changeGate)
            {
                return _changeVersion;
            }
        }
    }

    private LocalSessionProjectionUpdate ResetProjection(Guid? sessionId, string reason) => new(
        LocalSessionProjectionKind.ResetRequired,
        ChangeVersion,
        _coordinatorInstanceId,
        _coordinatorStartedAtUtc,
        sessionId,
        [],
        [],
        0,
        reason);

    private void PublishChange(Guid? sessionId = null, SessionTurnRecord? turnUpsert = null)
    {
        TaskCompletionSource<long> completed;
        long version;
        lock (_changeGate)
        {
            version = ++_changeVersion;
            completed = _nextChange;
            _nextChange = NewChangeSource();
        }

        if (turnUpsert is null)
        {
            _changeJournal.AppendReset(version, sessionId);
        }
        else
        {
            _changeJournal.AppendTurn(version, turnUpsert);
        }

        completed.TrySetResult(version);
    }

    private static TaskCompletionSource<long> NewChangeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string NormalizeInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("请输入想让元枢处理的内容。", nameof(text));
        }

        var value = text.Trim();
        if (value.Length > MaximumInputLength)
        {
            throw new ArgumentException($"单条消息不能超过 {MaximumInputLength} 个字符。", nameof(text));
        }

        return value;
    }

    private static IReadOnlyList<MemoryOutboundItemReference> NormalizeMemorySelection(
        IReadOnlyList<MemoryOutboundItemReference>? items)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        if (items.Count > MemoryOutboundLimits.MaximumItems
            || items.Select(item => item.MemoryId).Distinct().Count() != items.Count)
        {
            throw new ArgumentException("每个 Turn 只能有序选择 1 到 8 条不同的长期记忆。", nameof(items));
        }

        return items.Select(item => new MemoryOutboundItemReference(
            item.MemoryId,
            item.ExpectedVersion)).ToArray();
    }

    private bool MemorySelectionMatches(
        SessionTurnRecord turn,
        IReadOnlyList<MemoryOutboundItemReference> requested)
    {
        var existing = _requestedMemorySelections.GetValueOrDefault(turn.Id)
            ?? turn.MemoryOutbound?.Items
            ?? [];
        return existing.Count == requested.Count
            && existing.Zip(requested).All(pair =>
                pair.First.MemoryId == pair.Second.MemoryId
                && pair.First.ExpectedVersion == pair.Second.ExpectedVersion);
    }

    private bool PreparedConsentMatches(
        SessionRecord session,
        SessionTurnRecord turn,
        MemoryOutboundPreparedConsent prepared)
    {
        if (!string.Equals(
                prepared.CoordinatorInstanceId.ToString("N"),
                _coordinatorInstanceId,
                StringComparison.OrdinalIgnoreCase)
            || prepared.SessionId != session.Id
            || prepared.TurnId != turn.Id
            || turn.Version != prepared.TurnVersion + 1
            || prepared.ProjectId != (turn.ProjectId ?? session.SelectedProjectId)
            || turn.MemoryOutbound?.ConsentId != prepared.ConsentId
            || !string.Equals(
                turn.MemoryOutbound.ManifestHash,
                prepared.ManifestHash,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (turn.FrozenRoute is not
            {
                Status: SessionTurnRouteStatus.Ready,
                ProviderId: { Length: > 0 } providerId,
                ModelId: { Length: > 0 } modelId,
                DataDestination: { Length: > 0 } destination
            }
            || !string.Equals(providerId, prepared.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(modelId, prepared.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var prompt = prompts.GetRequired("chat.general", "2", providerId);
            return string.Equals(
                    MemoryOutboundContract.NormalizeHttpsOrigin(destination),
                    prepared.DestinationOrigin,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(prompt.PromptId, prepared.PromptId, StringComparison.Ordinal)
                && string.Equals(prompt.Version, prepared.PromptVersion, StringComparison.Ordinal)
                && string.Equals(
                    prompt.ContentSha256,
                    prepared.PromptContentHash,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private ForegroundWindowSnapshot? ResolvePlannedWindow(ForegroundApplicationDto? planned)
    {
        if (planned is null)
        {
            return null;
        }

        var resolved = foregroundWindows.ResolveWindow(planned.WindowHandle);
        if (resolved is null
            || resolved.ProcessId <= 0
            || resolved.ProcessStartTimeUtc == default
            || resolved.WindowHandle != planned.WindowHandle
            || !string.Equals(
                resolved.ProcessName,
                planned.ProcessName,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(resolved.WindowTitle, planned.WindowTitle, StringComparison.Ordinal))
        {
            return null;
        }

        return resolved;
    }

    private static bool IsWindowBoundTurn(SessionTurnRecord turn) =>
        string.Equals(turn.IntentKind, UniversalIntentKind.DescribeForeground.ToString(), StringComparison.Ordinal)
        || string.Equals(turn.IntentKind, UniversalIntentKind.SearchForeground.ToString(), StringComparison.Ordinal);

    private bool MatchesPersistedWindowIdentity(
        SessionTurnRecord turn,
        ForegroundWindowSnapshot? resolved = null)
    {
        if (turn.WindowHandle is not { } windowHandle
            || turn.WindowProcessId is not { } processId
            || turn.WindowProcessStartTimeUtc is not { } processStartTimeUtc
            || string.IsNullOrWhiteSpace(turn.WindowProcessName)
            || string.IsNullOrWhiteSpace(turn.WindowTitle))
        {
            return false;
        }

        var expected = new ForegroundWindowSnapshot(
            windowHandle,
            turn.WindowTitle,
            turn.WindowProcessName,
            processId,
            processStartTimeUtc,
            DateTimeOffset.MinValue);
        return WindowIdentityContract.Matches(
            expected,
            resolved ?? foregroundWindows.ResolveWindow(windowHandle));
    }

    private async Task RequireFreshWindowConfirmationAsync(
        SessionTurnRecord turn,
        CancellationToken cancellationToken)
    {
        var candidate = foregroundWindows.GetLastExternalWindow();
        var current = candidate is null
            ? null
            : foregroundWindows.ResolveWindow(candidate.WindowHandle);
        if (candidate is null || !WindowIdentityContract.Matches(candidate, current))
        {
            current = null;
        }

        var describe = string.Equals(
            turn.IntentKind,
            UniversalIntentKind.DescribeForeground.ToString(),
            StringComparison.Ordinal);
        await TransitionAsync(
                turn.Id,
                value => value with
                {
                    PlanId = null,
                    Phase = current is null
                        ? SessionTurnPhase.WaitingForWindow
                        : describe
                            ? SessionTurnPhase.WaitingForWindowConsent
                            : SessionTurnPhase.WaitingForConfirmation,
                    MissingContext = current is null
                        ? SessionMissingContext.Window
                        : describe
                            ? SessionMissingContext.WindowConsent
                            : SessionMissingContext.Confirmation,
                    WindowHandle = current?.WindowHandle,
                    WindowTitle = current?.WindowTitle,
                    WindowProcessName = current?.ProcessName,
                    WindowProcessId = current?.ProcessId,
                    WindowProcessStartTimeUtc = current?.ProcessStartTimeUtc,
                    ConfirmationGranted = false,
                    RequiresConfirmation = true,
                    ResultSummary = current is null
                        ? "目标窗口已经不可用，请切换到要操作的窗口后继续。"
                        : "前台窗口已经变化（身份不再一致），请确认新的目标窗口后再继续。",
                    FailureCode = "window_identity_changed",
                    FailureMessage = "授权后目标窗口身份发生变化。",
                    CompletedAtUtc = null
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string NormalizeModality(string value) =>
        string.Equals(value, "Voice", StringComparison.OrdinalIgnoreCase)
            ? "Voice"
            : string.Equals(value, "ProgrammingTask", StringComparison.OrdinalIgnoreCase)
                ? "ProgrammingTask"
                : "Text";

    private static (string? IntentKind, string? Target) NormalizeActionExpectation(
        string? intentKind,
        string? target)
    {
        if (string.IsNullOrWhiteSpace(intentKind) && string.IsNullOrWhiteSpace(target))
        {
            return (null, null);
        }

        if (string.IsNullOrWhiteSpace(intentKind) || string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("电脑操作目标必须同时包含类型和精确目标。 ");
        }

        var normalizedKind = intentKind.Trim();
        var normalizedTarget = target.Trim();
        if (normalizedTarget.Length > 2_048)
        {
            throw new ArgumentException("电脑操作目标过长。 ");
        }

        if (string.Equals(normalizedKind, "OpenWebsite", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(normalizedTarget, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                throw new ArgumentException("网站目标必须是有效的 https 地址。 ");
            }

            return ("OpenWebsite", uri.AbsoluteUri);
        }

        if (!string.Equals(normalizedKind, "OpenApplication", StringComparison.Ordinal))
        {
            throw new ArgumentException("当前只允许为应用或网站绑定结构化操作目标。 ");
        }

        return ("OpenApplication", normalizedTarget);
    }

    private static bool MatchesExpectedAction(
        SessionTurnRecord turn,
        string? actualIntentKind,
        string? actualTarget) =>
        turn.ExpectedIntentKind is null && turn.ExpectedTarget is null
        || string.Equals(turn.ExpectedIntentKind, actualIntentKind, StringComparison.Ordinal)
        && string.Equals(turn.ExpectedTarget, actualTarget, StringComparison.Ordinal);

    private static bool MatchesPlannedExpectation(SessionTurnRecord turn) =>
        MatchesExpectedAction(turn, turn.IntentKind, turn.PlanTarget);

    private static string BuildTitle(string text) =>
        text.Length <= 36 ? text : text[..36] + "…";

    private static string FriendlyException(Exception exception) => exception switch
    {
        FileNotFoundException => "没有找到 Codex，请先安装并登录 Codex。",
        NotSupportedException => "当前 Codex 版本尚未通过兼容验证。",
        _ => "这次处理没有成功，请稍后重试。"
    };

    private sealed class ActiveSessionWork(Guid turnId)
    {
        private readonly TaskCompletionSource<Task> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid TurnId { get; } = turnId;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task Completion => _completion.Task.Unwrap();

        public void SetCompletion(Task completion) => _completion.TrySetResult(completion);
    }

    private sealed class ActiveTaskMonitor(Guid turnId, Guid taskId)
    {
        private readonly TaskCompletionSource<Task> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid TurnId { get; } = turnId;

        public Guid TaskId { get; } = taskId;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task Completion => _completion.Task.Unwrap();

        public void SetCompletion(Task completion) => _completion.TrySetResult(completion);
    }
}
