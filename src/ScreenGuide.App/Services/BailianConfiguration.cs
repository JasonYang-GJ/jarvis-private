using System.IO;
using System.Text.Json;

namespace ScreenGuide.App.Services;

internal sealed record BailianConfiguration(
    string ApiHost,
    string TextModel,
    string VisionModel,
    string VoiceModel,
    string VoiceName)
{
    public const string DefaultApiHost = "https://dashscope.aliyuncs.com";
    public const string DefaultTextModel = "deepseek-v4-flash";
    public const string DefaultVisionModel = "qwen3.7-plus";
    public const string DefaultVoiceModel = "qwen-audio-3.0-tts-flash";
    public const string DefaultVoiceName = "longanlingxi";

    public static BailianConfiguration Default { get; } = new(
        DefaultApiHost,
        DefaultTextModel,
        DefaultVisionModel,
        DefaultVoiceModel,
        DefaultVoiceName);

    public Uri ChatCompletionsUri => new($"{ApiHost.TrimEnd('/')}/compatible-mode/v1/chat/completions");

    public Uri SpeechWebSocketUri
    {
        get
        {
            var source = new Uri(ApiHost);
            var builder = new UriBuilder(source)
            {
                Scheme = source.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
                Port = -1,
                Path = "/api-ws/v1/inference",
                Query = string.Empty
            };
            return builder.Uri;
        }
    }
}

internal sealed class BailianConfigurationStore
{
    private const string CredentialTarget = "ScreenGuideTeacher/BailianApiKey";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly WindowsCredentialStore _credentials = new();
    private readonly string _configurationPath;

    public BailianConfigurationStore()
    {
        var configurationDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenGuideTeacher",
            "config");
        _configurationPath = Path.Combine(configurationDirectory, "bailian.json");
    }

    public BailianConfiguration Load()
    {
        try
        {
            if (!File.Exists(_configurationPath))
            {
                return BailianConfiguration.Default;
            }

            var configuration = JsonSerializer.Deserialize<BailianConfiguration>(File.ReadAllText(_configurationPath));
            return configuration is null ? BailianConfiguration.Default : Normalize(configuration);
        }
        catch
        {
            return BailianConfiguration.Default;
        }
    }

    public void Save(BailianConfiguration configuration, string apiKey)
    {
        var normalized = Normalize(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(_configurationPath)!);
        File.WriteAllText(_configurationPath, JsonSerializer.Serialize(normalized, JsonOptions));
        _credentials.Save(CredentialTarget, apiKey.Trim());
    }

    public string? ReadApiKey() => _credentials.Read(CredentialTarget);

    private static BailianConfiguration Normalize(BailianConfiguration configuration)
    {
        var host = string.IsNullOrWhiteSpace(configuration.ApiHost)
            ? BailianConfiguration.DefaultApiHost
            : configuration.ApiHost.Trim().TrimEnd('/');

        if (!Uri.TryCreate(host, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("百炼 API Host 必须是完整的 http 或 https 地址。");
        }

        var configuredVisionModel = string.IsNullOrWhiteSpace(configuration.VisionModel)
            ? BailianConfiguration.DefaultVisionModel
            : configuration.VisionModel.Trim();
        if (configuredVisionModel.Equals("qwen3-vl-flash", StringComparison.OrdinalIgnoreCase))
        {
            configuredVisionModel = BailianConfiguration.DefaultVisionModel;
        }

        return configuration with
        {
            ApiHost = host,
            TextModel = string.IsNullOrWhiteSpace(configuration.TextModel)
                ? BailianConfiguration.DefaultTextModel
                : configuration.TextModel.Trim(),
            VisionModel = configuredVisionModel,
            VoiceModel = string.IsNullOrWhiteSpace(configuration.VoiceModel)
                ? BailianConfiguration.DefaultVoiceModel
                : configuration.VoiceModel.Trim(),
            VoiceName = string.IsNullOrWhiteSpace(configuration.VoiceName)
                ? BailianConfiguration.DefaultVoiceName
                : configuration.VoiceName.Trim()
        };
    }
}
