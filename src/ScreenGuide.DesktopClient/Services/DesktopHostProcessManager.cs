using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

public sealed class DesktopHostProcessManager(IDesktopApiClient apiClient)
{
    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (await apiClient.PingAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var executable = LocateHostExecutable();
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        });
        process?.Dispose();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            if (await apiClient.PingAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        var startupFailure = TryReadStartupFailure();
        if (startupFailure is not null)
        {
            throw startupFailure;
        }

        throw new TimeoutException("Desktop Host 没有在预期时间内启动。");
    }

    public static string LocateHostExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("SCREEN_GUIDE_DESKTOP_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var installed = Path.Combine(AppContext.BaseDirectory, "ScreenGuide.DesktopHost.exe");
        if (File.Exists(installed))
        {
            return installed;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ScreenGuide.slnx")))
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var development = Path.Combine(
                        directory.FullName,
                        "src",
                        "ScreenGuide.DesktopHost",
                        "bin",
                        configuration,
                        "net10.0-windows10.0.19041.0",
                        "ScreenGuide.DesktopHost.exe");
                    if (File.Exists(development))
                    {
                        return development;
                    }
                }

                break;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("没有找到元枢后台服务程序。");
    }

    private static DesktopHostStartupException? TryReadStartupFailure()
    {
        try
        {
            var configured = Environment.GetEnvironmentVariable("SCREEN_GUIDE_DATA_DIRECTORY");
            var dataDirectory = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ScreenGuide",
                    "V01")
                : Path.GetFullPath(configured);
            var path = Path.Combine(dataDirectory, "state", "host-startup-error.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new DesktopHostStartupException(
                root.GetProperty("Code").GetString() ?? "host_startup_failed",
                root.GetProperty("UserMessage").GetString() ?? "Desktop Host 启动失败。",
                root.GetProperty("TechnicalDetail").GetString() ?? "No technical detail.");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }
}
