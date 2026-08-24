using System.Text.Json;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

public static class DesktopClientFailureRecorder
{
    public const string FileName = "client-startup-error.json";

    public static void Record(Exception exception, string? dataDirectory = null)
    {
        try
        {
            var path = ResolvePath(dataDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    new
                    {
                        occurredAtUtc = DateTimeOffset.UtcNow,
                        message = "桌面界面启动失败。",
                        technicalDetail = SensitiveDataSanitizer.ExceptionType(exception)
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    public static void Clear()
    {
        var path = ResolvePath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static void RecordStage(string stage)
    {
        try
        {
            var errorPath = ResolvePath();
            Directory.CreateDirectory(Path.GetDirectoryName(errorPath)!);
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(errorPath)!, "client-startup-stage.txt"),
                stage);
        }
        catch
        {
        }
    }

    private static string ResolvePath(string? dataDirectory = null)
    {
        var configured = string.IsNullOrWhiteSpace(dataDirectory)
            ? Environment.GetEnvironmentVariable("SCREEN_GUIDE_DATA_DIRECTORY")
            : dataDirectory;
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenGuide",
                "V01")
            : Path.GetFullPath(configured);
        return Path.Combine(root, "state", FileName);
    }

}
