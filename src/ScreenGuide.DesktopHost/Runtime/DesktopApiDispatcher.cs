using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using ScreenGuide.Agent.Codex;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class DesktopApiDispatcher(
    LocalTaskEntryService taskEntry,
    DesktopHostState hostState,
    DesktopHostOptions options,
    CodexDiagnosticsService codexDiagnostics,
    ProjectInspector projectInspector,
    DesktopActionEntryService desktopActions,
    IHostApplicationLifetime applicationLifetime)
{
    public async Task<DesktopApiResponse> DispatchAsync(
        DesktopApiRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.ProtocolVersion < DesktopProtocolVersion.MinimumSupported
                || request.ProtocolVersion > DesktopProtocolVersion.Current)
            {
                return new DesktopApiResponse(
                    request.RequestId,
                    false,
                    Error: new DesktopApiError(
                        "protocol_version_unsupported",
                        "Desktop Client 与 Desktop Host 版本不兼容。",
                        $"Requested protocol version: {request.ProtocolVersion}; supported: "
                        + $"{DesktopProtocolVersion.MinimumSupported}-{DesktopProtocolVersion.Current}."));
            }

            var payload = request.Method switch
            {
                DesktopApiMethods.Ping or DesktopApiMethods.GetSystemStatus =>
                    DesktopProtocolJson.ToElement(await SystemStatusAsync(cancellationToken)
                        .ConfigureAwait(false)),
                DesktopApiMethods.GetDashboard =>
                    DesktopProtocolJson.ToElement(await DashboardAsync(cancellationToken)
                        .ConfigureAwait(false)),
                DesktopApiMethods.ListProjects =>
                    DesktopProtocolJson.ToElement(await ListProjectsAsync(cancellationToken)
                        .ConfigureAwait(false)),
                DesktopApiMethods.AddProject =>
                    DesktopProtocolJson.ToElement(await AddProjectAsync(
                        Deserialize<AddProjectRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.RevokeProject =>
                    DesktopProtocolJson.ToElement(await RevokeProjectAsync(
                        Deserialize<RevokeProjectRequestDto>(request).ProjectId,
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.ListTasks =>
                    DesktopProtocolJson.ToElement(await ListTasksAsync(
                        Deserialize<ListTasksRequest>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.GetTask =>
                    DesktopProtocolJson.ToElement(await GetTaskAsync(
                        Deserialize<TaskIdRequest>(request).TaskId,
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.CreateTask =>
                    DesktopProtocolJson.ToElement(await CreateTaskAsync(
                        Deserialize<CreateTaskRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.CancelTask =>
                    DesktopProtocolJson.ToElement(await CancelTaskAsync(
                        Deserialize<CancelTaskRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.ContinueTask =>
                    DesktopProtocolJson.ToElement(await ContinueTaskAsync(
                        Deserialize<ContinueTaskRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.ClearHistory =>
                    DesktopProtocolJson.ToElement(await ClearHistoryAsync(
                        Deserialize<ClearHistoryRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.ListDesktopApplications =>
                    DesktopProtocolJson.ToElement(desktopActions.GetApplications()),
                DesktopApiMethods.ExecuteDesktopAction =>
                    DesktopProtocolJson.ToElement(await desktopActions.ExecuteAsync(
                        Deserialize<ExecuteDesktopActionRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.Shutdown => Shutdown(),
                _ => throw new NotSupportedException("当前 Desktop Host 不支持这个操作。")
            };
            return new DesktopApiResponse(request.RequestId, true, payload);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DesktopApiResponse(
                request.RequestId,
                false,
                Error: DesktopApiErrors.FromException(exception));
        }
    }

    private async Task<SystemStatusDto> SystemStatusAsync(CancellationToken cancellationToken)
    {
        var state = hostState.Snapshot;
        var codex = await codexDiagnostics.CheckAsync(cancellationToken).ConfigureAwait(false);
        var schemaVersion = await taskEntry.GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false);
        return new SystemStatusDto(
            state.IsStarted,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0",
            ProcessStartTime.Value,
            state.LocalDevice?.Id.ToString("D") ?? string.Empty,
            state.IsStarted ? "Ready" : "Offline",
            options.DataDirectory,
            options.LogsDirectory,
            new CodexStatusDto(
                codex.IsInstalled,
                codex.IsCompatible,
                codex.Version,
                codex.Message),
            DesktopProtocolVersion.Current,
            schemaVersion);
    }

    private async Task<DashboardDto> DashboardAsync(CancellationToken cancellationToken)
    {
        var system = await SystemStatusAsync(cancellationToken).ConfigureAwait(false);
        var projects = await taskEntry.GetAuthorizedProjectsAsync(cancellationToken)
            .ConfigureAwait(false);
        var tasks = await ListTasksAsync(new ListTasksRequest(), cancellationToken)
            .ConfigureAwait(false);
        var today = DateTimeOffset.Now.Date;
        var active = tasks.Where(task => IsStatus(task.Status, "Pending", "Running", "CancellationRequested"))
            .ToArray();
        var waiting = tasks.Where(task => IsStatus(task.Status, "WaitingForUser")).ToArray();
        return new DashboardDto(
            system,
            projects.Count,
            active.Length,
            waiting.Length,
            tasks.Count(task => task.CompletedAtUtc?.ToLocalTime().Date == today
                                && IsStatus(task.Status, "Succeeded")),
            tasks.Count(task => IsStatus(task.Status, "Failed", "Interrupted")),
            active,
            waiting,
            tasks.Take(8).ToArray());
    }

    private async Task<ProjectDto[]> ListProjectsAsync(CancellationToken cancellationToken)
    {
        var projects = await taskEntry.GetProjectsAsync(true, cancellationToken)
            .ConfigureAwait(false);
        var tasks = await taskEntry.GetTasksAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var results = new List<ProjectDto>();
        foreach (var project in projects)
        {
            var inspection = await projectInspector.InspectAsync(project.RootPath, cancellationToken)
                .ConfigureAwait(false);
            var projectTasks = tasks.Where(task => task.ProjectId == project.Id).ToArray();
            results.Add(new ProjectDto(
                project.Id,
                project.Name,
                project.RootPath,
                project.AuthorizationState.ToString(),
                inspection.IsGitRepository,
                inspection.HasChanges,
                inspection.Summary,
                projectTasks.Length,
                projectTasks.Any(DesktopApiMapper.IsActive),
                project.AuthorizedAtUtc));
        }

        return results.ToArray();
    }

    private async Task<ProjectDto> AddProjectAsync(
        AddProjectRequestDto request,
        CancellationToken cancellationToken)
    {
        var project = await taskEntry.AddProjectAsync(
            request.RootPath,
            request.Name,
            cancellationToken).ConfigureAwait(false);
        var inspection = await projectInspector.InspectAsync(project.RootPath, cancellationToken)
            .ConfigureAwait(false);
        return new ProjectDto(
            project.Id,
            project.Name,
            project.RootPath,
            project.AuthorizationState.ToString(),
            inspection.IsGitRepository,
            inspection.HasChanges,
            inspection.Summary,
            0,
            false,
            project.AuthorizedAtUtc);
    }

    private async Task<ProjectDto> RevokeProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var project = await taskEntry.RevokeProjectAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        var inspection = await projectInspector.InspectAsync(project.RootPath, cancellationToken)
            .ConfigureAwait(false);
        return new ProjectDto(
            project.Id,
            project.Name,
            project.RootPath,
            project.AuthorizationState.ToString(),
            inspection.IsGitRepository,
            inspection.HasChanges,
            inspection.Summary,
            0,
            false,
            project.AuthorizedAtUtc);
    }

    private async Task<int> ClearHistoryAsync(
        ClearHistoryRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!request.Confirmed)
        {
            throw new InvalidOperationException("清理历史记录需要用户确认。");
        }

        return await taskEntry.ClearTerminalHistoryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TaskSummaryDto[]> ListTasksAsync(
        ListTasksRequest request,
        CancellationToken cancellationToken)
    {
        var tasks = await taskEntry.GetTasksAsync(request.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        var results = new List<TaskSummaryDto>();
        foreach (var task in tasks)
        {
            if (!string.IsNullOrWhiteSpace(request.Status)
                && !string.Equals(task.Status.ToString(), request.Status, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    (await taskEntry.GetTaskDetailsAsync(task.Id, cancellationToken).ConfigureAwait(false))
                    ?.Evidence?.VerificationStatus.ToString(),
                    request.Status,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var details = await taskEntry.GetTaskDetailsAsync(task.Id, cancellationToken)
                .ConfigureAwait(false);
            if (details is not null)
            {
                results.Add(DesktopApiMapper.Summary(details.Task, details.Project, details.Evidence));
            }
        }

        return results.ToArray();
    }

    private async Task<TaskDetailsDto?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var details = await taskEntry.GetTaskDetailsAsync(taskId, cancellationToken).ConfigureAwait(false);
        return details is null ? null : DesktopApiMapper.Details(details);
    }

    private async Task<CommandResultDto> CreateTaskAsync(
        CreateTaskRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await taskEntry.CreateTaskAsync(
            new CreateLocalTaskRequest(
                request.ProjectId,
                request.Instruction,
                request.Title,
                request.WorkingDirectoryRelativePath,
                request.IdempotencyKey),
            cancellationToken).ConfigureAwait(false);
        return new CommandResultDto(result.TaskId, result.WasDuplicate);
    }

    private async Task<CommandResultDto> CancelTaskAsync(
        CancelTaskRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await taskEntry.CancelTaskAsync(
            request.TaskId,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        return new CommandResultDto(result.TaskId, result.WasDuplicate);
    }

    private async Task<CommandResultDto> ContinueTaskAsync(
        ContinueTaskRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await taskEntry.ContinueTaskAsync(
            request.TaskId,
            request.Response,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        return new CommandResultDto(result.TaskId, result.WasDuplicate);
    }

    private JsonElement Shutdown()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            applicationLifetime.StopApplication();
        });
        return DesktopProtocolJson.ToElement(true);
    }

    private static T Deserialize<T>(DesktopApiRequest request) =>
        request.Payload.Deserialize<T>(DesktopProtocolJson.Options)
        ?? throw new InvalidDataException("请求内容无效。");

    private static bool IsStatus(string actual, params string[] expected) =>
        expected.Contains(actual, StringComparer.OrdinalIgnoreCase);

    private static class ProcessStartTime
    {
        public static DateTimeOffset Value { get; } = DateTimeOffset.UtcNow;
    }
}

internal static class DesktopApiErrors
{
    public static DesktopApiError FromException(Exception exception)
    {
        var (code, userMessage) = exception switch
        {
            UnauthorizedAccessException when ContainsAny(
                exception.Message,
                "桌面", "应用", "操作", "确认", "清单") =>
                ("desktop_action_not_authorized", exception.Message),
            UnauthorizedAccessException => ("project_not_authorized", "这个项目没有授权，无法执行任务。"),
            DirectoryNotFoundException => ("project_missing", "项目目录不存在，请重新选择项目。"),
            FileNotFoundException => ("codex_not_found", "没有找到 Codex，请先安装并登录 Codex。"),
            NotSupportedException when exception.Message.Contains("版本", StringComparison.Ordinal) =>
                ("codex_version_incompatible", "当前 Codex 版本尚未通过兼容验证。"),
            InvalidOperationException when exception.Message.Contains("不是 Git", StringComparison.Ordinal) =>
                ("project_not_git", "选择的目录不是 Git 项目。"),
            InvalidOperationException when ContainsAny(exception.Message, "登录", "login", "authentication", "401") =>
                ("codex_login_required", "Codex 登录已经失效。"),
            InvalidOperationException when ContainsAny(exception.Message, "网络", "network", "connection") =>
                ("network_unavailable", "当前网络不可用，Codex 无法继续。"),
            InvalidOperationException when exception.Message.Contains("启动", StringComparison.OrdinalIgnoreCase) =>
                ("codex_start_failed", "Codex 没有成功启动。"),
            InvalidDataException => ("data_invalid", "本地任务数据无法读取。"),
            InvalidOperationException => ("operation_invalid", FriendlyInvalidOperation(exception.Message)),
            ArgumentException => ("input_invalid", exception.Message),
            _ when exception.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) =>
                ("database_unavailable", "本地任务数据库无法打开。"),
            _ => ("host_error", "Desktop Host 执行操作时遇到错误。")
        };
        return new DesktopApiError(code, userMessage, Technical(exception));
    }

    private static string FriendlyInvalidOperation(string message) =>
        message.Contains("不是 Git 项目", StringComparison.Ordinal)
            ? "请选择一个 Git 项目目录。"
            : message.Contains("正在运行", StringComparison.Ordinal)
                ? "项目仍有任务正在运行，完成或取消任务后再删除授权。"
                : message.Contains("WaitingForUser", StringComparison.Ordinal)
            ? "任务当前不需要补充指令。"
            : message.Contains("任务不存在", StringComparison.Ordinal)
                ? "没有找到这个任务。"
                : message.Contains("项目不存在", StringComparison.Ordinal)
                    ? "没有找到这个项目。"
                    : "当前状态下不能执行这个操作。";

    private static string Technical(Exception exception)
    {
        var message = exception.Message.ReplaceLineEndings(" ").Trim();
        if (message.Length > 500)
        {
            message = message[..500];
        }

        return $"{exception.GetType().Name}: {message}";
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
