using System.Text.Json;

namespace ScreenGuide.AI.Core;

public sealed record ChatModelRoute(
    string ProviderId,
    string ModelId);

public sealed record AiSettings(
    ChatModelRoute DefaultChatRoute);

public interface IAiSettingsStore
{
    Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        AiSettings settings,
        CancellationToken cancellationToken = default);
}

public sealed class FileAiSettingsStore : IAiSettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _settingsPath;
    private readonly AiSettings _defaultSettings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileAiSettingsStore(string settingsPath, AiSettings defaultSettings)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            throw new ArgumentException("AI 设置文件路径不能为空。", nameof(settingsPath));
        }

        _settingsPath = Path.GetFullPath(settingsPath.Trim());
        _defaultSettings = Validate(defaultSettings);
    }

    public async Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return _defaultSettings;
            }

            try
            {
                var document = JsonSerializer.Deserialize<AiSettingsDocument>(
                    await File.ReadAllTextAsync(_settingsPath, cancellationToken).ConfigureAwait(false),
                    JsonOptions) ?? throw new InvalidDataException("AI 设置文件内容为空。");
                if (document.SchemaVersion != 1)
                {
                    throw new InvalidDataException(
                        $"不支持 AI 设置 schema {document.SchemaVersion}。");
                }

                return Validate(document.Settings);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("AI 设置文件不是有效的 JSON。", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        AiSettings settings,
        CancellationToken cancellationToken = default)
    {
        var validated = Validate(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(
                    temporaryPath,
                    JsonSerializer.Serialize(new AiSettingsDocument(1, validated), JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static AiSettings Validate(AiSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.DefaultChatRoute.ProviderId)
            || string.IsNullOrWhiteSpace(settings.DefaultChatRoute.ModelId))
        {
            throw new InvalidDataException("默认聊天 Provider 和 Model 都不能为空。");
        }

        return settings with
        {
            DefaultChatRoute = new ChatModelRoute(
                settings.DefaultChatRoute.ProviderId.Trim(),
                settings.DefaultChatRoute.ModelId.Trim())
        };
    }

    private sealed record AiSettingsDocument(int SchemaVersion, AiSettings Settings);
}
