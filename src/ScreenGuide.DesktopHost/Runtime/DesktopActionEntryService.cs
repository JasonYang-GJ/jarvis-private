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
        CancellationToken cancellationToken = default) =>
        await ExecuteCoreAsync(request, null, null, cancellationToken).ConfigureAwait(false);

    internal async Task<DesktopActionResultDto> ExecuteBoundApplicationAsync(
        ExecuteDesktopActionRequestDto request,
        string expectedTargetBinding,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(request.ActionKind, "OpenApplication", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(expectedTargetBinding))
        {
            throw new ArgumentException("绑定应用入口缺少有效的应用目标。", nameof(request));
        }

        return await ExecuteCoreAsync(
            request,
            null,
            expectedTargetBinding,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<DesktopActionResultDto> ExecuteTrustedWindowAsync(
        ExecuteDesktopActionRequestDto request,
        ForegroundWindowSnapshot trustedWindow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustedWindow);
        if (request.ActionKind is not ("SearchForeground" or "DescribeForeground"))
        {
            throw new ArgumentException("可信窗口入口只接受窗口范围操作。", nameof(request));
        }

        return await ExecuteCoreAsync(request, trustedWindow, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DesktopActionResultDto> ExecuteCoreAsync(
        ExecuteDesktopActionRequestDto request,
        ForegroundWindowSnapshot? trustedWindow,
        string? expectedApplicationTargetBinding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Confirmed)
        {
            throw new UnauthorizedAccessException("需要你在界面中确认本次桌面操作。");
        }

        var device = RequireStartedHost().LocalDevice!;
        var normalized = Normalize(request, trustedWindow, expectedApplicationTargetBinding);
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
                confirmed = true,
                authorizationSource = NormalizeAuthorizationSource(request.AuthorizationSource)
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
                normalized.ExecutionTarget,
                normalized.WindowHandle,
                normalized.WindowTitle,
                normalized.Argument,
                normalized.WindowIdentity,
                normalized.ApplicationTargetBinding)),
            [new SkillResourceScope(
                normalized.ScopeType,
                null,
                normalized.ScopeValue,
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
                authorizationSource = NormalizeAuthorizationSource(request.AuthorizationSource),
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
                    authorizationSource = NormalizeAuthorizationSource(request.AuthorizationSource),
                    processId = result.ProcessId,
                    status = result.Status.ToString(),
                    verificationStatus = normalized.Capability is
                        WindowsDesktopCapabilities.OpenApplication or
                        WindowsDesktopCapabilities.SearchForeground or
                        WindowsDesktopCapabilities.DescribeForeground or
                        WindowsDesktopCapabilities.OpenWebsiteSecure or
                        WindowsDesktopCapabilities.OpenWebsiteInApplication
                            ? "ExecutionVerified"
                            : "LaunchRequested",
                    resultSummary = final?.Summary
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
                    authorizationSource = NormalizeAuthorizationSource(request.AuthorizationSource),
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

    private static string NormalizeAuthorizationSource(string? value) =>
        string.Equals(value, "ExplicitVoice", StringComparison.Ordinal)
            ? "ExplicitVoice"
            : "VisibleConfirmation";

    private NormalizedDesktopAction Normalize(
        ExecuteDesktopActionRequestDto request,
        ForegroundWindowSnapshot? trustedWindow,
        string? expectedApplicationTargetBinding)
    {
        if (string.Equals(request.ActionKind, "OpenApplication", StringComparison.Ordinal))
        {
            var application = adapter.Applications.FirstOrDefault(item =>
                string.Equals(item.Id, request.Target, StringComparison.Ordinal));
            if (application is null)
            {
                throw new UnauthorizedAccessException("这个应用不在当前允许打开的清单中。");
            }

            var currentBinding = InstalledApplicationCatalog.CreateTargetBinding(application);
            if (expectedApplicationTargetBinding is not null
                && !InstalledApplicationCatalog.TargetBindingMatches(
                    application,
                    expectedApplicationTargetBinding))
            {
                throw new InstalledApplicationResolutionException(
                    InstalledApplicationErrorCodes.TargetChanged,
                    "应用目标在确认后发生变化，本次没有打开任何程序；请重新确认。");
            }

            if (!InstalledApplicationCatalog.IsAllowedLaunchTarget(application))
            {
                throw new InstalledApplicationResolutionException(
                    InstalledApplicationErrorCodes.TargetNotAllowed,
                    "这个应用目标不符合安全启动要求，本次没有打开任何程序。");
            }

            return new NormalizedDesktopAction(
                request.ActionKind,
                WindowsDesktopCapabilities.OpenApplication,
                "Application",
                application.Id,
                application.Id,
                application.Id,
                ApplicationTargetBinding: currentBinding);
        }

        if (string.Equals(request.ActionKind, "OpenWebsite", StringComparison.Ordinal))
        {
            var website = SafeWebsitePolicy.RequireHttps(request.Target);
            return new NormalizedDesktopAction(
                request.ActionKind,
                WindowsDesktopCapabilities.OpenWebsiteSecure,
                "Website",
                website.AbsoluteUri,
                SafeWebsitePolicy.ForAudit(website),
                SafeWebsitePolicy.ForAudit(website));
        }

        if (string.Equals(request.ActionKind, "OpenWebsiteInApplication", StringComparison.Ordinal))
        {
            var website = SafeWebsitePolicy.RequireHttps(request.Target);
            var application = adapter.Applications.FirstOrDefault(item =>
                string.Equals(item.Id, request.ApplicationId, StringComparison.Ordinal));
            if (application is null)
            {
                throw new UnauthorizedAccessException("指定浏览器不在当前允许打开的清单中。");
            }

            var auditWebsite = SafeWebsitePolicy.ForAudit(website);
            return new NormalizedDesktopAction(
                request.ActionKind,
                WindowsDesktopCapabilities.OpenWebsiteInApplication,
                "ApplicationWebsite",
                application.Id,
                $"{application.Id}|{auditWebsite}",
                $"{application.Id}:{auditWebsite}",
                Argument: website.AbsoluteUri);
        }

        if (string.Equals(request.ActionKind, "OpenFile", StringComparison.Ordinal))
        {
            var fullPath = Path.GetFullPath(request.Target);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("刚才选择的文件已经不存在。", fullPath);
            }

            return new NormalizedDesktopAction(
                request.ActionKind,
                WindowsDesktopCapabilities.OpenFile,
                "File",
                fullPath,
                fullPath,
                Path.GetFileName(fullPath));
        }

        if (string.Equals(request.ActionKind, "SearchForeground", StringComparison.Ordinal))
        {
            var query = request.Target.Trim();
            if (query.Length is < 1 or > 200)
            {
                throw new ArgumentException("搜索操作缺少有效内容。", nameof(request));
            }

            var identity = RequireTrustedWindow(request, trustedWindow);

            return new NormalizedDesktopAction(
                request.ActionKind,
                WindowsDesktopCapabilities.SearchForeground,
                "Window",
                query,
                identity.WindowHandle.ToString(),
                identity.WindowTitle,
                identity.WindowHandle,
                identity.WindowTitle,
                WindowIdentity: identity);
        }

        if (string.Equals(request.ActionKind, "DescribeForeground", StringComparison.Ordinal))
        {
            var identity = RequireTrustedWindow(request, trustedWindow);

            return new NormalizedDesktopAction(
                request.ActionKind,
                WindowsDesktopCapabilities.DescribeForeground,
                "Window",
                identity.WindowHandle.ToString(),
                identity.WindowHandle.ToString(),
                identity.WindowTitle,
                identity.WindowHandle,
                identity.WindowTitle,
                WindowIdentity: identity);
        }

        throw new UnauthorizedAccessException("这个桌面操作尚未开放。");
    }

    private static ForegroundWindowSnapshot RequireTrustedWindow(
        ExecuteDesktopActionRequestDto request,
        ForegroundWindowSnapshot? trustedWindow)
    {
        if (trustedWindow is null)
        {
            throw new WindowIdentityException(
                WindowIdentityErrorCodes.Missing,
                "窗口操作必须通过 Host 会话中的可信窗口身份执行。 ");
        }

        if (request.WindowHandle != trustedWindow.WindowHandle
            || !string.Equals(request.WindowTitle, trustedWindow.WindowTitle, StringComparison.Ordinal))
        {
            throw new WindowIdentityException(
                WindowIdentityErrorCodes.Changed,
                "窗口操作请求与 Host 冻结的窗口身份不一致。 ");
        }

        return trustedWindow;
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
        string ScopeValue,
        string AuditTarget,
        long? WindowHandle = null,
        string? WindowTitle = null,
        string? Argument = null,
        ForegroundWindowSnapshot? WindowIdentity = null,
        string? ApplicationTargetBinding = null);
}
