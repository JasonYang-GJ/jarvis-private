using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopStabilityTests
{
    public const string CanaryApiKey = "sk-yuanshuStage2CANARY1234567890";

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

    [Theory]
    [InlineData("{\"api_key\":\"sk-yuanshuStage2CANARY1234567890\",\"error\":\"invalid\"}")]
    [InlineData("Authorization: Bearer sk-yuanshuStage2CANARY1234567890")]
    [InlineData("provider rejected sk-yuanshuStage2CANARY1234567890")]
    [InlineData("'token' = 'sk-yuanshuStage2CANARY1234567890'")]
    [InlineData("https://api.deepseek.com/error?api_key=sk-yuanshuStage2CANARY1234567890&code=401")]
    public void RedactorRemovesProviderCredentialFromCommonFailureShapes(string failure)
    {
        var redacted = SensitiveDataRedactor.Redact(failure);

        Assert.DoesNotContain(CanaryApiKey, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactorKeepsOrdinarySafeErrorReadable()
    {
        const string safeMessage = "Provider request failed with local code provider_unavailable.";

        Assert.Equal(safeMessage, SensitiveDataRedactor.Redact(safeMessage));
    }

    [Fact]
    public void HostLogDoesNotPersistExceptionMessageButKeepsReadableEvent()
    {
        var root = NewRoot();
        const string rawMarker = "RAW_PROVIDER_LOG_RESPONSE";
        try
        {
            using var provider = new RollingFileLoggerProvider(root);
            var logger = provider.CreateLogger("ProviderSafetyTest");

            logger.LogError(
                new InvalidOperationException($"{rawMarker}: {CanaryApiKey}"),
                "Provider request failed with a safe local summary.");
            var payload = File.ReadAllText(Path.Combine(root, "desktop-host.log"));

            Assert.Contains("Provider request failed with a safe local summary.", payload, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", payload, StringComparison.Ordinal);
            Assert.DoesNotContain(rawMarker, payload, StringComparison.Ordinal);
            Assert.DoesNotContain(CanaryApiKey, payload, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
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
    public void StartupCrashFileDoesNotPersistProviderResponseOrCredential()
    {
        var root = NewRoot();
        try
        {
            var options = new DesktopHostOptions(root);
            const string rawMarker = "RAW_PROVIDER_STARTUP_RESPONSE";

            HostStartupStatusStore.Record(
                options,
                new InvalidOperationException($"{rawMarker}: {CanaryApiKey}"));
            var payload = File.ReadAllText(HostStartupStatusStore.GetPath(options));

            Assert.Contains("InvalidOperationException", payload, StringComparison.Ordinal);
            Assert.DoesNotContain(rawMarker, payload, StringComparison.Ordinal);
            Assert.DoesNotContain(CanaryApiKey, payload, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UnhandledFailureAuditDoesNotPersistProviderResponseOrCredential()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        const string rawMarker = "RAW_PROVIDER_BODY_SHOULD_NOT_CROSS_BOUNDARY";

        await HostFailureRecorder.TryRecordUnhandledAsync(
            host.Services,
            new InvalidOperationException($"{rawMarker}: {CanaryApiKey}"));
        await using var store = await environment.OpenStoreAsync();
        var audit = Assert.Single(
            await store.GetAuditLogAsync(),
            item => item.Action == "HostUnhandledException");
        await host.StopAsync();

        Assert.Contains("InvalidOperationException", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(rawMarker, audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryApiKey, audit.DetailsJson, StringComparison.Ordinal);
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
