using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenGuide.DesktopClient.Services;

public static partial class DesktopClientFailureRecorder
{
    public const string FileName = "client-startup-error.json";

    public static void Record(Exception exception)
    {
        try
        {
            var path = ResolvePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var detail = $"{exception.GetType().Name}: {exception.Message}";
            detail = WindowsUserPath().Replace(detail, "$1\\[USER]\\");
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    new
                    {
                        occurredAtUtc = DateTimeOffset.UtcNow,
                        message = "桌面界面启动失败。",
                        technicalDetail = detail
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

    private static string ResolvePath()
    {
        var configured = Environment.GetEnvironmentVariable("SCREEN_GUIDE_DATA_DIRECTORY");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenGuide",
                "V01")
            : Path.GetFullPath(configured);
        return Path.Combine(root, "state", FileName);
    }

    [GeneratedRegex("(?i)([A-Z]:\\\\Users)\\\\[^\\\\]+\\\\")]
    private static partial Regex WindowsUserPath();
}
