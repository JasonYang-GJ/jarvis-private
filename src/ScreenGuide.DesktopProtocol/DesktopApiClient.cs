using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace ScreenGuide.DesktopProtocol;

public interface IDesktopApiClient
{
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    Task<SystemStatusDto> GetSystemStatusAsync(CancellationToken cancellationToken = default);

    Task<DashboardDto> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectDto>> ListProjectsAsync(CancellationToken cancellationToken = default);

    Task<ProjectDto> AddProjectAsync(
        AddProjectRequestDto request,
        CancellationToken cancellationToken = default);

    Task<ProjectDto> RevokeProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskSummaryDto>> ListTasksAsync(
        Guid? projectId = null,
        string? status = null,
        CancellationToken cancellationToken = default);

    Task<TaskDetailsDto?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<CommandResultDto> CreateTaskAsync(
        CreateTaskRequestDto request,
        CancellationToken cancellationToken = default);

    Task<CommandResultDto> CancelTaskAsync(
        Guid taskId,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    Task<CommandResultDto> ContinueTaskAsync(
        Guid taskId,
        string response,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    Task<int> ClearHistoryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DesktopApplicationDto>> ListDesktopApplicationsAsync(
        CancellationToken cancellationToken = default);

    Task<DesktopActionResultDto> ExecuteDesktopActionAsync(
        ExecuteDesktopActionRequestDto request,
        CancellationToken cancellationToken = default);

    Task ShutdownHostAsync(CancellationToken cancellationToken = default);
}

public sealed class DesktopApiClient(
    string? pipeName = null,
    TimeSpan? connectTimeout = null) : IDesktopApiClient
{
    private readonly string _pipeName = pipeName
        ?? Environment.GetEnvironmentVariable("SCREEN_GUIDE_PIPE_NAME")
        ?? DesktopIpcEndpoint.CurrentUserPipeName();
    private readonly TimeSpan _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await CallAsync<EmptyRequest, SystemStatusDto>(
                DesktopApiMethods.Ping,
                new EmptyRequest(),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return false;
        }
    }

    public Task<SystemStatusDto> GetSystemStatusAsync(CancellationToken cancellationToken = default) =>
        CallAsync<EmptyRequest, SystemStatusDto>(
            DesktopApiMethods.GetSystemStatus,
            new EmptyRequest(),
            cancellationToken);

    public Task<DashboardDto> GetDashboardAsync(CancellationToken cancellationToken = default) =>
        CallAsync<EmptyRequest, DashboardDto>(
            DesktopApiMethods.GetDashboard,
            new EmptyRequest(),
            cancellationToken);

    public async Task<IReadOnlyList<ProjectDto>> ListProjectsAsync(
        CancellationToken cancellationToken = default) =>
        await CallAsync<EmptyRequest, ProjectDto[]>(
            DesktopApiMethods.ListProjects,
            new EmptyRequest(),
            cancellationToken).ConfigureAwait(false);

    public Task<ProjectDto> AddProjectAsync(
        AddProjectRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<AddProjectRequestDto, ProjectDto>(
            DesktopApiMethods.AddProject,
            request,
            cancellationToken);

    public Task<ProjectDto> RevokeProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        CallAsync<RevokeProjectRequestDto, ProjectDto>(
            DesktopApiMethods.RevokeProject,
            new RevokeProjectRequestDto(projectId),
            cancellationToken);

    public async Task<IReadOnlyList<TaskSummaryDto>> ListTasksAsync(
        Guid? projectId = null,
        string? status = null,
        CancellationToken cancellationToken = default) =>
        await CallAsync<ListTasksRequest, TaskSummaryDto[]>(
            DesktopApiMethods.ListTasks,
            new ListTasksRequest(projectId, status),
            cancellationToken).ConfigureAwait(false);

    public Task<TaskDetailsDto?> GetTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        CallAsync<TaskIdRequest, TaskDetailsDto?>(
            DesktopApiMethods.GetTask,
            new TaskIdRequest(taskId),
            cancellationToken);

    public Task<CommandResultDto> CreateTaskAsync(
        CreateTaskRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<CreateTaskRequestDto, CommandResultDto>(
            DesktopApiMethods.CreateTask,
            request,
            cancellationToken);

    public Task<CommandResultDto> CancelTaskAsync(
        Guid taskId,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        CallAsync<CancelTaskRequestDto, CommandResultDto>(
            DesktopApiMethods.CancelTask,
            new CancelTaskRequestDto(taskId, idempotencyKey),
            cancellationToken);

    public Task<CommandResultDto> ContinueTaskAsync(
        Guid taskId,
        string response,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        CallAsync<ContinueTaskRequestDto, CommandResultDto>(
            DesktopApiMethods.ContinueTask,
            new ContinueTaskRequestDto(taskId, response, idempotencyKey),
            cancellationToken);

    public Task<int> ClearHistoryAsync(CancellationToken cancellationToken = default) =>
        CallAsync<ClearHistoryRequestDto, int>(
            DesktopApiMethods.ClearHistory,
            new ClearHistoryRequestDto(true),
            cancellationToken);

    public async Task<IReadOnlyList<DesktopApplicationDto>> ListDesktopApplicationsAsync(
        CancellationToken cancellationToken = default) =>
        await CallAsync<EmptyRequest, DesktopApplicationDto[]>(
            DesktopApiMethods.ListDesktopApplications,
            new EmptyRequest(),
            cancellationToken).ConfigureAwait(false);

    public Task<DesktopActionResultDto> ExecuteDesktopActionAsync(
        ExecuteDesktopActionRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<ExecuteDesktopActionRequestDto, DesktopActionResultDto>(
            DesktopApiMethods.ExecuteDesktopAction,
            request,
            cancellationToken);

    public Task ShutdownHostAsync(CancellationToken cancellationToken = default) =>
        CallAsync<EmptyRequest, bool>(
            DesktopApiMethods.Shutdown,
            new EmptyRequest(),
            cancellationToken);

    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        string method,
        TRequest payload,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Desktop Host 连接超时。");
        }

        var requestId = Guid.NewGuid().ToString("N");
        await DesktopIpcFraming.WriteAsync(
            pipe,
            new DesktopApiRequest(
                requestId,
                method,
                DesktopProtocolJson.ToElement(payload)),
            cancellationToken).ConfigureAwait(false);
        var response = await DesktopIpcFraming.ReadAsync<DesktopApiResponse>(
            pipe,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(requestId, response.RequestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Desktop Host 返回了不匹配的响应。");
        }

        if (response.ProtocolVersion < DesktopProtocolVersion.MinimumSupported
            || response.ProtocolVersion > DesktopProtocolVersion.Current)
        {
            throw new NotSupportedException($"Desktop Host 协议版本 {response.ProtocolVersion} 不受支持。");
        }

        if (!response.Success)
        {
            throw new DesktopApiException(
                response.Error ?? new DesktopApiError("unknown", "Desktop Host 请求失败。"));
        }

        if (response.Payload is null)
        {
            return default!;
        }

        return response.Payload.Value.Deserialize<TResponse>(DesktopProtocolJson.Options)!;
    }
}
