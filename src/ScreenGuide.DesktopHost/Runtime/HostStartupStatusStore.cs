using System.Text.Json;
using ScreenGuide.DesktopHost.Configuration;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed record HostStartupFailure(string Code, string UserMessage, string TechnicalDetail);

public static class HostStartupStatusStore
{
    public const string FileName = "host-startup-error.json";

    public static string GetPath(DesktopHostOptions options) =>
        Path.Combine(options.DataDirectory, "state", FileName);

    public static void Clear(DesktopHostOptions options)
    {
        var path = GetPath(options);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static void Record(DesktopHostOptions options, Exception exception)
    {
        var path = GetPath(options);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var code = IsDatabaseFailure(exception) ? "database_unavailable" : "host_startup_failed";
        var userMessage = code == "database_unavailable"
            ? "本地任务数据无法打开。"
            : "Desktop Host 启动失败。";
        var failure = new HostStartupFailure(
            code,
            userMessage,
            SensitiveDataRedactor.Redact($"{exception.GetType().Name}: {exception.Message}"));
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(failure));
        File.Move(temporary, path, overwrite: true);
    }

    private static bool IsDatabaseFailure(Exception exception) =>
        exception.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("database", StringComparison.OrdinalIgnoreCase)
        || exception.InnerException is not null && IsDatabaseFailure(exception.InnerException);
}
