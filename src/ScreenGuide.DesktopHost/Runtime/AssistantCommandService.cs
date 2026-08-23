using System.Collections.Concurrent;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Runtime;

/// <summary>
/// The provider-neutral entry for V0.2. Planning never executes an action. A ready action plan
/// is held in memory for two minutes and can be consumed exactly once after visible confirmation.
/// </summary>
public sealed class AssistantCommandService(
    IIntentPlanner planner,
    IForegroundWindowContextProvider foregroundWindows,
    IInstalledApplicationCatalog applications,
    LocalTaskEntryService tasks,
    ConversationService conversations,
    DesktopActionEntryService desktopActions,
    WindowUnderstandingService windowUnderstanding,
    TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<Guid, PendingPlan> _plans = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _windowObservations =
        new(StringComparer.Ordinal);

    public async Task<AssistantIntentPlanDto> PlanAsync(
        PlanAssistantCommandRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selectedFile = NormalizeSelectedFile(request.SelectedFilePath);
        var inputModality = NormalizeInputModality(request.InputModality);
        var project = await FindAuthorizedProjectAsync(request.SelectedProjectId, cancellationToken)
            .ConfigureAwait(false);
        var foreground = foregroundWindows.GetLastExternalWindow();
        var context = new IntentPlanningContext(
            selectedFile,
            project?.Id,
            project?.Name,
            foreground is null
                ? null
                : new ForegroundApplicationContext(
                    foreground.WindowHandle,
                    foreground.WindowTitle,
                    foreground.ProcessName),
            request.ForegroundObservationConsent);
        var plan = planner.Plan(request.Text, context);
        RemoveExpired();
        if (plan.Readiness == IntentPlanReadiness.Ready)
        {
            _plans[plan.Id] = new PendingPlan(
                plan,
                foreground,
                request.ForegroundObservationConsent,
                inputModality);
        }

        return Map(plan, foreground);
    }

    public async Task<AssistantCommandResultDto> ExecuteAsync(
        ExecuteAssistantCommandRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_plans.TryRemove(request.PlanId, out var pending))
        {
            throw new InvalidOperationException("这条操作计划已经失效或处理过，请重新说一次。 ");
        }

        var plan = pending.Plan;
        if (plan.ExpiresAtUtc is not null && plan.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException("这条操作确认已超时，请重新说一次。 ");
        }

        if (plan.Readiness != IntentPlanReadiness.Ready)
        {
            throw new InvalidOperationException("当前还缺少必要信息，不能执行。 ");
        }

        var explicitlyAuthorizedByVoice =
            string.Equals(pending.InputModality, "Voice", StringComparison.Ordinal)
            && string.Equals(request.AuthorizationSource, "ExplicitVoice", StringComparison.Ordinal)
            && IsDirectVoiceAction(plan.Kind);
        if (plan.RequiresConfirmation && !request.Confirmed && !explicitlyAuthorizedByVoice)
        {
            throw new UnauthorizedAccessException("需要你在界面中确认这一次电脑操作。 ");
        }

        var effectiveRequest = explicitlyAuthorizedByVoice
            ? request with { Confirmed = true }
            : request;

        return plan.Kind switch
        {
            UniversalIntentKind.Conversation => await ExecuteConversationAsync(plan, effectiveRequest, cancellationToken)
                .ConfigureAwait(false),
            UniversalIntentKind.CodingTask => await ExecuteCodingTaskAsync(plan, effectiveRequest, cancellationToken)
                .ConfigureAwait(false),
            UniversalIntentKind.OpenApplication => await ExecuteApplicationAsync(plan, effectiveRequest, cancellationToken)
                .ConfigureAwait(false),
            UniversalIntentKind.OpenWebsite => await ExecuteWebsiteAsync(
                plan, effectiveRequest, cancellationToken).ConfigureAwait(false),
            UniversalIntentKind.OpenFile => await ExecuteDesktopAsync(
                plan, effectiveRequest, "OpenFile", plan.Target!, null, cancellationToken).ConfigureAwait(false),
            UniversalIntentKind.SearchForeground => await ExecuteDesktopAsync(
                plan, effectiveRequest, "SearchForeground", plan.Target!, pending.Foreground, cancellationToken)
                .ConfigureAwait(false),
            UniversalIntentKind.DescribeForeground => await ExecuteWindowUnderstandingAsync(
                plan, effectiveRequest, pending.Foreground, pending.ObservationConsent, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new NotSupportedException("当前版本还不能执行这类请求。")
        };
    }

    public bool CancelWindowObservation(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return false;
        }

        if (!_windowObservations.TryGetValue(operationId, out var cancellation))
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task<AssistantCommandResultDto> ExecuteConversationAsync(
        IntentPlan plan,
        ExecuteAssistantCommandRequestDto request,
        CancellationToken cancellationToken)
    {
        var title = plan.OriginalText.Length <= 36
            ? plan.OriginalText
            : plan.OriginalText[..36] + "…";
        var conversation = await conversations.CreateAsync(title, cancellationToken)
            .ConfigureAwait(false);
        var result = await conversations.SendAsync(
                conversation.Id,
                plan.OriginalText,
                request.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        return new AssistantCommandResultDto(
            plan.Kind.ToString(),
            "Processing",
            "已开始回答，不会操作电脑。",
            "NotApplicable",
            ConversationId: result.ConversationId,
            WasDuplicate: result.WasDuplicate);
    }

    private async Task<AssistantCommandResultDto> ExecuteCodingTaskAsync(
        IntentPlan plan,
        ExecuteAssistantCommandRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(plan.Target, out var projectId))
        {
            throw new InvalidDataException("编程任务缺少有效的授权项目。 ");
        }

        var result = await tasks.CreateTaskAsync(
                new CreateLocalTaskRequest(
                    projectId,
                    plan.OriginalText,
                    IdempotencyKey: request.IdempotencyKey),
                cancellationToken)
            .ConfigureAwait(false);
        return new AssistantCommandResultDto(
            plan.Kind.ToString(),
            "Processing",
            "编程任务已交给授权项目中的编程助手，执行过程和结果证据会持续记录。",
            "Pending",
            TaskId: result.TaskId,
            WasDuplicate: result.WasDuplicate);
    }

    private Task<AssistantCommandResultDto> ExecuteApplicationAsync(
        IntentPlan plan,
        ExecuteAssistantCommandRequestDto request,
        CancellationToken cancellationToken)
    {
        var application = applications.FindByDisplayName(plan.Target ?? string.Empty)
            ?? applications.FindBrowser(plan.Target ?? string.Empty)
            ?? throw new InvalidOperationException(
                $"没有唯一识别到已安装的“{plan.Target}”，因此没有打开任何程序。 ");
        return ExecuteDesktopAsync(
            plan, request, "OpenApplication", application.Id, null, cancellationToken);
    }

    private Task<AssistantCommandResultDto> ExecuteWebsiteAsync(
        IntentPlan plan,
        ExecuteAssistantCommandRequestDto request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.PreferredApplicationName))
        {
            return ExecuteDesktopAsync(
                plan, request, "OpenWebsite", plan.Target!, null, cancellationToken);
        }

        var browser = applications.FindBrowser(plan.PreferredApplicationName)
            ?? throw new InvalidOperationException(
                $"没有找到已安装的“{plan.PreferredApplicationName}”，因此没有打开网站。 ");
        return ExecuteDesktopAsync(
            plan,
            request,
            "OpenWebsiteInApplication",
            plan.Target!,
            null,
            cancellationToken,
            browser.Id);
    }

    private async Task<AssistantCommandResultDto> ExecuteDesktopAsync(
        IntentPlan plan,
        ExecuteAssistantCommandRequestDto request,
        string actionKind,
        string target,
        ForegroundWindowSnapshot? foreground,
        CancellationToken cancellationToken,
        string? applicationId = null)
    {
        if (actionKind is "SearchForeground" or "DescribeForeground" && foreground is null)
        {
            throw new InvalidOperationException("目标窗口已经不可用，请重新切回目标软件。 ");
        }

        var result = await desktopActions.ExecuteAsync(
                new ExecuteDesktopActionRequestDto(
                    actionKind,
                    target,
                    request.Confirmed,
                    request.IdempotencyKey,
                    foreground?.WindowHandle,
                    foreground?.WindowTitle,
                    applicationId,
                    request.AuthorizationSource),
                cancellationToken)
            .ConfigureAwait(false);
        var verification = actionKind is "OpenApplication" or "SearchForeground" or "DescribeForeground"
            or "OpenWebsite" or "OpenWebsiteInApplication"
            ? "ExecutionVerified"
            : "LaunchRequested";
        var verifiedFacts = actionKind switch
        {
            "SearchForeground" => new[] { "内容已写入唯一识别的搜索框", "已向该搜索框提交 Enter" },
            "DescribeForeground" => new[] { "只读取了目标单个窗口公开给 Windows 的控件名称" },
            "OpenApplication" => new[] { "应用窗口已经显示在用户眼前" },
            "OpenWebsite" => new[] { "浏览器窗口已经显示在前台" },
            "OpenWebsiteInApplication" => new[] { "指定浏览器窗口已经显示在前台" },
            _ => new[] { "Windows 已接受本次启动请求" }
        };
        var unverifiedFacts = actionKind switch
        {
            "SearchForeground" => new[] { "搜索结果页面是否加载完成尚未验证" },
            "DescribeForeground" => Array.Empty<string>(),
            "OpenWebsite" or "OpenWebsiteInApplication" => new[] { "网页内容是否完全加载尚未验证" },
            "OpenApplication" => new[] { "应用内部内容是否完全加载尚未验证" },
            "OpenFile" => new[] { "文件是否完成加载尚未验证" },
            _ => new[] { "最终界面状态尚未验证" }
        };
        var evidence = new AssistantActionEvidenceDto(
            Guid.NewGuid(),
            verification,
            result.Message,
            verifiedFacts,
            unverifiedFacts,
            timeProvider.GetUtcNow());
        return new AssistantCommandResultDto(
            plan.Kind.ToString(),
            result.Succeeded ? "Completed" : "Failed",
            result.Message,
            verification,
            CommandId: result.CommandId,
            WasDuplicate: result.WasDuplicate,
            Evidence: evidence);
    }

    private async Task<ProjectRecord?> FindAuthorizedProjectAsync(
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        if (projectId is null)
        {
            return null;
        }

        return (await tasks.GetAuthorizedProjectsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(project => project.Id == projectId.Value)
            ?? throw new UnauthorizedAccessException("所选项目没有授权或已经失效。 ");
    }

    private async Task<AssistantCommandResultDto> ExecuteWindowUnderstandingAsync(
        IntentPlan plan,
        ExecuteAssistantCommandRequestDto request,
        ForegroundWindowSnapshot? foreground,
        bool observationConsent,
        CancellationToken cancellationToken)
    {
        if (foreground is null)
        {
            throw new InvalidOperationException("目标窗口已经不可用，请重新切回目标软件。 ");
        }

        if (!request.Confirmed || !observationConsent)
        {
            throw new UnauthorizedAccessException("需要你在界面中明确同意并确认本次窗口查看。 ");
        }

        var operationId = request.IdempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new InvalidDataException("窗口查看缺少本次操作编号。 ");
        }

        using var observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_windowObservations.TryAdd(operationId, observationCancellation))
        {
            throw new InvalidOperationException("同一个窗口查看操作已经在进行中。 ");
        }

        WindowUnderstandingResult result;
        try
        {
            result = await windowUnderstanding.DescribeAsync(
                    foreground,
                    explicitConsent: true,
                    observationCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _windowObservations.TryRemove(operationId, out _);
        }
        var evidence = new AssistantActionEvidenceDto(
            Guid.NewGuid(),
            "ObservationCompleted",
            result.UserSummary,
            [
                "只读取了确认卡中显示的单个目标窗口",
                "画面只在内存中用于本机分析并已清理",
                "未截取整个桌面，也未把图像发送到云端"
            ],
            result.Limitations,
            timeProvider.GetUtcNow());
        return new AssistantCommandResultDto(
            plan.Kind.ToString(),
            "Completed",
            result.UserSummary,
            "ObservationCompleted",
            Evidence: evidence);
    }

    private static string? NormalizeSelectedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("刚才选择的文件已经不存在。", fullPath);
        }

        return fullPath;
    }

    private static string NormalizeInputModality(string? value) =>
        string.Equals(value, "Voice", StringComparison.OrdinalIgnoreCase)
            ? "Voice"
            : "Text";

    private static bool IsDirectVoiceAction(UniversalIntentKind kind) =>
        kind is UniversalIntentKind.OpenApplication
            or UniversalIntentKind.OpenWebsite
            or UniversalIntentKind.SearchForeground;

    private static AssistantIntentPlanDto Map(
        IntentPlan plan,
        ForegroundWindowSnapshot? foreground) =>
        new(
            plan.Id,
            plan.Kind.ToString(),
            plan.Readiness.ToString(),
            plan.UserSummary,
            plan.RequiresConfirmation,
            plan.ConfirmationText,
            plan.MissingContext,
            plan.ExpiresAtUtc,
            foreground is null
                ? null
                : new ForegroundApplicationDto(
                    foreground.WindowHandle,
                    foreground.WindowTitle,
                    foreground.ProcessName,
                    foreground.ObservedAtUtc));

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var pair in _plans)
        {
            if (pair.Value.Plan.ExpiresAtUtc is not null
                && pair.Value.Plan.ExpiresAtUtc <= now)
            {
                _plans.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record PendingPlan(
        IntentPlan Plan,
        ForegroundWindowSnapshot? Foreground,
        bool ObservationConsent,
        string InputModality);
}
