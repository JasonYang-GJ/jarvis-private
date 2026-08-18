using Microsoft.Extensions.Logging.Abstractions;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopStabilityTests
{
    [Fact]
    public void RedactorRemovesSecretsAndPersonalWindowsPath()
    {
        var value = SensitiveDataRedactor.Redact(
            "api_key=secret-value Authorization: Bearer abc.def C:\\Users\\Alice\\project");

        Assert.DoesNotContain("secret-value", value, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def", value, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", value, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", value, StringComparison.Ordinal);
        Assert.Contains("[USER]", value, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupFailureIsSanitizedAndClassifiedAsDatabaseFailure()
    {
        var root = NewRoot();
        try
        {
            var options = new DesktopHostOptions(root);
            HostStartupStatusStore.Record(
                options,
                new InvalidOperationException("database failed at C:\\Users\\Alice\\state"));

            var payload = File.ReadAllText(HostStartupStatusStore.GetPath(options));
            Assert.Contains("database_unavailable", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("Alice", payload, StringComparison.Ordinal);

            HostStartupStatusStore.Clear(options);
            Assert.False(File.Exists(HostStartupStatusStore.GetPath(options)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void MaintenanceRemovesOnlyStaleTemporaryAndBaselineData()
    {
        var root = NewRoot();
        try
        {
            var options = new DesktopHostOptions(root);
            var oldTemp = Path.Combine(root, "temp", "old.tmp");
            var recentTemp = Path.Combine(root, "temp", "recent.tmp");
            var oldBaseline = Path.Combine(options.EvidenceDataDirectory, "baselines", Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(Path.GetDirectoryName(oldTemp)!);
            Directory.CreateDirectory(oldBaseline);
            File.WriteAllText(oldTemp, "old");
            File.WriteAllText(recentTemp, "recent");
            File.WriteAllText(Path.Combine(oldBaseline, "manifest.json"), "{}");
            File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddDays(-3));
            Directory.SetLastWriteTimeUtc(oldBaseline, DateTime.UtcNow.AddDays(-3));

            var maintenance = new RuntimeDataMaintenance(
                options,
                TimeProvider.System,
                NullLogger<RuntimeDataMaintenance>.Instance);
            maintenance.CleanupStaleFiles();

            Assert.False(File.Exists(oldTemp));
            Assert.True(File.Exists(recentTemp));
            Assert.False(Directory.Exists(oldBaseline));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-stability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
