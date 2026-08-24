using Microsoft.Extensions.Logging;
using ScreenGuide.DesktopHost.Configuration;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class RuntimeDataMaintenance(
    DesktopHostOptions options,
    TimeProvider timeProvider,
    ILogger<RuntimeDataMaintenance> logger)
{
    public void CleanupStaleFiles()
    {
        CleanupFiles(Path.Combine(options.DataDirectory, "temp"), TimeSpan.FromDays(1));
        CleanupFiles(options.SecretsDirectory, TimeSpan.FromDays(1));
        CleanupDirectories(
            Path.Combine(options.EvidenceDataDirectory, "baselines"),
            TimeSpan.FromDays(2));
    }

    private void CleanupFiles(string root, TimeSpan maximumAge)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories))
        {
            TryDeleteFile(file, maximumAge);
        }
    }

    private void CleanupDirectories(string root, TimeSpan maximumAge)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                var lastWrite = Directory.GetLastWriteTimeUtc(directory);
                if (timeProvider.GetUtcNow() - lastWrite > maximumAge)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not remove a stale evidence baseline: {ErrorType}.", exception.GetType().Name);
            }
        }
    }

    private void TryDeleteFile(string file, TimeSpan maximumAge)
    {
        try
        {
            if (timeProvider.GetUtcNow() - File.GetLastWriteTimeUtc(file) > maximumAge)
            {
                File.Delete(file);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove a stale temporary file: {ErrorType}.", exception.GetType().Name);
        }
    }
}
