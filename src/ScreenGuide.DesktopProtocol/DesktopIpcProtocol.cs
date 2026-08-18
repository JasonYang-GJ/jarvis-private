using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenGuide.DesktopProtocol;

public static class DesktopProtocolVersion
{
    public const int Current = 2;

    public const int MinimumSupported = 1;
}

public static class DesktopApiMethods
{
    public const string Ping = "system.ping";
    public const string GetSystemStatus = "system.status";
    public const string GetDashboard = "dashboard.get";
    public const string ListProjects = "projects.list";
    public const string AddProject = "projects.add";
    public const string RevokeProject = "projects.revoke";
    public const string ListTasks = "tasks.list";
    public const string GetTask = "tasks.get";
    public const string CreateTask = "tasks.create";
    public const string CancelTask = "tasks.cancel";
    public const string ContinueTask = "tasks.continue";
    public const string ClearHistory = "tasks.clear-history";
    public const string ListDesktopApplications = "desktop.applications.list";
    public const string ExecuteDesktopAction = "desktop.action.execute";
    public const string Shutdown = "system.shutdown";
}

public static class DesktopIpcEndpoint
{
    public static string CurrentUserPipeName()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法识别当前 Windows 用户。");
        return $"ScreenGuide.DesktopHost.V01.{sid.Replace('-', '_')}";
    }

    public static string CurrentUserMutexName(string component)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法识别当前 Windows 用户。");
        return $"Local\\ScreenGuide.{component}.V01.{sid.Replace('-', '_')}";
    }
}

public static class DesktopProtocolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Options);
}

public sealed record DesktopApiRequest(
    string RequestId,
    string Method,
    JsonElement Payload,
    int ProtocolVersion = DesktopProtocolVersion.Current);

public sealed record DesktopApiError(string Code, string UserMessage, string? TechnicalDetail = null);

public sealed record DesktopApiResponse(
    string RequestId,
    bool Success,
    JsonElement? Payload = null,
    DesktopApiError? Error = null,
    int ProtocolVersion = DesktopProtocolVersion.Current);

public sealed class DesktopApiException(DesktopApiError error) : Exception(error.UserMessage)
{
    public DesktopApiError Error { get; } = error;
}

public static class DesktopIpcFraming
{
    public const int MaximumMessageBytes = 4 * 1024 * 1024;

    public static async Task WriteAsync<T>(
        PipeStream stream,
        T message,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, DesktopProtocolJson.Options);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC 消息超过大小限制。");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(
        PipeStream stream,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC 消息长度无效。");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, DesktopProtocolJson.Options)
            ?? throw new InvalidDataException("IPC 消息内容无效。");
    }
}
