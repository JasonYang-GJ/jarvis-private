using System.Text.Json;
using ScreenGuide.Core.Security;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Runtime;

/// <summary>
/// 当前用户桌面操作的唯一 Host 入口。只接受 UI 明确确认的一次性安全启动请求。
/// </summary>
public sealed class DesktopActionEntryService(
    ILocalTaskStore store,
    DesktopHostState hostState,
    WindowsDesktopSkillAdapter adapter,
    CapabilityPolicyEngine policy,
    TimeProvider timeProvider)
{
    public IReadOnlyList<DesktopApplicationDto> GetApplications() =>
        adapter.Applications
            .Select(item => new DesktopApplicationDto(item.Id, item.DisplayName))
            .ToArray();

    public async Task<DesktopActionResultDto> ExecuteAsync(
        ExecuteDesktopActionRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Confirmed)
        {
            throw new UnauthorizedAccessException("需要你在界面中确认本次桌面操作。");
        }

        var device = RequireStartedHost().LocalDevice!;
        var normalized = Normalize(request);
        var now = timeProvider.GetUtcNow();
        var command = new CommandRecord
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = device.Id,
            ProjectId = null,
            TaskId = null,
            IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                ? $"desktop-action-{Guid.NewGuid():N}"
                : request.IdempotencyKey.Trim(),
            CommandType = CommandType.ExecuteDesktopAction,
            PayloadJson = JsonSerializer.Serialize(new
            {
                actionKind = normalized.ActionKind,
                target = normalized.AuditTarget,
                confirmed = true
            }),
            ReceivedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(2),
            Status = CommandStatus.Received
        };
        var registration = await store.RegisterCommandAsync(command, cancellationToken)
            .ConfigureAwait(false);
        if (!registration.Accepted)
        {
            return new DesktopActionResultDto(
                registration.Command.Id,
                null,
                false,
                "这个操作请求已经处理过，不会重复执行。",
                true);
        }

        var invocationId = Guid.NewGuid();
        var invocation = new SkillInvocationRequest(
            Guid.Empty,
            invocationId,
            adapter.Descriptor.Id,
            normalized.Capability,
            JsonSerializer.Serialize(new WindowsDesktopActionInput(
                normalized.ActionKind,
                normalized.ExecutionTarget)),
            [new SkillResourceScope(
                normalized.ScopeType,
                null,
                normalized.AuditTarget,
                "Execute")],
            SkillAuthorizationOrigin.ExplicitUser,
            command.Id);
        var decision = policy.AuthorizeOnce(adapter.Descriptor, invocation);
        await AppendAuditAsync(
            device.Id,
            command.Id,
            decision.IsAllowed ? "DesktopActionAuthorizationAllowed" : "DesktopActionAuthorizationDenied",
            decision.IsAllowed ? AuditOutcome.Success : AuditOutcome.Rejected,
            new
            {
                skillId = adapter.Descriptor.Id,
                normalized.Capability,
                normalized.ActionKind,
                target = normalized.AuditTarget,
                decision.Code
            },
            cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await store.CompleteCommandAsync(
                command.Id,
                CommandStatus.Rejected,
                timeProvider.GetUtcNow(),
                decision.Code,
                cancellationToken).ConfigureAwait(false);
            throw new UnauthorizedAccessException(decision.UserMessage);
        }

        try
        {
            var result = await adapter.StartAsync(invocation, cancellationToken).ConfigureAwait(false);
            var final = await adapter.GetFinalResultAsync(result.Run, cancellationToken)
                .ConfigureAwait(false);
            var succeeded = result.Status == SkillExecutionStatus.Succeeded
                            && final?.Succeeded == true;
            await store.CompleteCommandAsync(
                command.Id,
                succeeded ? CommandStatus.Processed : CommandStatus.Failed,
                timeProvider.GetUtcNow(),
                succeeded ? null : "desktop_action_failed",
                cancellationToken).ConfigureAwait(false);
            await AppendAuditAsync(
                device.Id,
                command.Id,
                "DesktopActionExecuted",
                succeeded ? AuditOutcome.Success : AuditOutcome.Failed,
                new
                {
                    invocationId,
                    skillId = adapter.Descriptor.Id,
                    normalized.Capability,
                    normalized.ActionKind,
                    target = normalized.AuditTarget,
                    processId = result.ProcessId,
                    status = result.Status.ToString()
                },
                cancellationToken).ConfigureAwait(false);
            return new DesktopActionResultDto(
                command.Id,
                invocationId,
                succeeded,
                final?.Summary ?? "Windows 没有返回可确认的启动结果。",
                false,
                result.ProcessId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await store.CompleteCommandAsync(
                command.Id,
                CommandStatus.Failed,
                timeProvider.GetUtcNow(),
                exception.GetType().Name,
                cancellationToken).ConfigureAwait(false);
            await AppendAuditAsync(
                device.Id,
                command.Id,
                "DesktopActionExecuted",
                AuditOutcome.Failed,
                new
                {
                    invocationId,
                    skillId = adapter.Descriptor.Id,
                    normalized.Capability,
                    normalized.ActionKind,
                    target = normalized.AuditTarget,
                    error = exception.GetType().Name
                },
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private DesktopHostSnapshot RequireStartedHost()
    {
        var snapshot = hostState.Snapshot;
        if (!snapshot.IsStarted || snapshot.LocalDevice is null)
        {
            throw new InvalidOperationException("Desktop Host 尚未启动。");
        }

        return snapshot;
    }

    private NormalizedDesktopAction Normalize(ExecuteDesktopActionRequestDto request)
    {
        if (string.Equals(request.ActionKind, "OpenApplication", StringComparison.Ordinal))
        {
            var application = adapter.Applications.FirstOrDefault(item =>
                string.Equals(item.Id, request.Target, StringComparison.Ordinal));
            if (application is null)
            {
                throw new UnauthorizedAccessException("这个应用不在当前允许打开的清单中。");
            }

            return new NormalizedDesktopAction(
                request.ActionKind,
                WindowsDesktopCapabilities.OpenApplication,
                "Application",
                application.Id,
                application.Id);
        }

        if (string.Equals(request.ActionKind, "OpenWebsite", StringComparison.Ordinal))
        {
            var website = SafeWebsitePolicy.RequireHttps(request.Target);
            return new NormalizedDesktopAction(
                request.ActionKind,
                WindowsDesktopCapabilities.OpenWebsite,
                "Website",
                website.AbsoluteUri,
                SafeWebsitePolicy.ForAudit(website));
        }

        throw new UnauthorizedAccessException("这个桌面操作尚未开放。");
    }

    private Task AppendAuditAsync(
        Guid deviceId,
        Guid commandId,
        string action,
        AuditOutcome outcome,
        object details,
        CancellationToken cancellationToken) =>
        store.AppendAuditAsync(
            new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                OccurredAtUtc = timeProvider.GetUtcNow(),
                ActorDeviceId = deviceId,
                Action = action,
                EntityType = "Command",
                EntityId = commandId.ToString("D"),
                Outcome = outcome,
                DetailsJson = JsonSerializer.Serialize(details)
            },
            cancellationToken);

    private sealed record NormalizedDesktopAction(
        string ActionKind,
        string Capability,
        string ScopeType,
        string ExecutionTarget,
        string AuditTarget);
}
