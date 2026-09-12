namespace ScreenGuide.Voice.Windows.Tests;

public sealed class LocalVoiceModelPathsTests
{
    [Fact]
    public void IncompleteModelReportsOnlyExpectedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sg-voice-{Guid.NewGuid():N}");
        var paths = LocalVoiceModelPaths.Create(root);

        Assert.False(paths.IsComplete);
        Assert.Equal(4, paths.MissingFiles.Count);
        Assert.All(paths.MissingFiles, path => Assert.StartsWith(root, path, StringComparison.Ordinal));
    }

    [Fact]
    public void ModelRootUsesExplicitConfigurationOrTheLocalAppDataDefault()
    {
        var root = Path.GetFullPath(LocalVoiceModelPaths.ModelRoot);
        var configured = Environment.GetEnvironmentVariable(LocalVoiceModelPaths.ModelDirectoryEnvironmentVariable);
        if (!string.IsNullOrEmpty(configured))
        {
            Assert.Equal(Path.GetFullPath(configured), root);
            return;
        }
        var localAppData = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        Assert.StartsWith(localAppData, root, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("models", root, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelledContinuousStartNeverOpensMicrophoneOrRequiresModels()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sg-continuous-voice-cancel-{Guid.NewGuid():N}");
        await using var listener = new OfflineContinuousVoiceListener(
            LocalVoiceModelPaths.Create(root));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            listener.StartAsync(cancellation.Token));

        Assert.False(listener.IsListening);
    }

    [Fact]
    public async Task StoppingAnIdleContinuousListenerIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sg-continuous-voice-stop-{Guid.NewGuid():N}");
        await using var listener = new OfflineContinuousVoiceListener(
            LocalVoiceModelPaths.Create(root));

        await listener.StopAsync();
        await listener.StopAsync();

        Assert.False(listener.IsListening);
    }
}
