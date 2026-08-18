using System.Text.Json;

namespace ScreenGuide.DesktopClient.Services;

public sealed record DesktopClientSettings
{
    public bool StartWithWindows { get; init; }

    public bool RunInBackground { get; init; } = true;

    public bool CloseToTray { get; init; } = true;

    public bool NotificationsEnabled { get; init; } = true;

    public bool OnboardingCompleted { get; init; }

    public bool NotificationStateInitialized { get; init; }

    public HashSet<string> DeliveredNotificationKeys { get; init; } = [];
}

public sealed class DesktopClientSettingsStore(string? dataDirectory = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsPath = Path.Combine(
        ResolveDataDirectory(dataDirectory),
        "client-settings.json");

    public string SettingsPath => _settingsPath;

    public async Task<DesktopClientSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new DesktopClientSettings();
            }

            try
            {
                return JsonSerializer.Deserialize<DesktopClientSettings>(
                           await File.ReadAllTextAsync(_settingsPath, cancellationToken)
                               .ConfigureAwait(false),
                           JsonOptions)
                       ?? new DesktopClientSettings();
            }
            catch (JsonException)
            {
                return new DesktopClientSettings();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        DesktopClientSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var temporary = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(settings, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _settingsPath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ResolveDataDirectory(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var environment = Environment.GetEnvironmentVariable("SCREEN_GUIDE_DATA_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(environment))
        {
            return Path.GetFullPath(environment);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenGuide",
            "V01");
    }
}
