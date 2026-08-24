namespace ScreenGuide.DesktopHost.Configuration;

public sealed class DesktopHostOptions
{
    public const string DataDirectoryEnvironmentVariable = "SCREEN_GUIDE_DATA_DIRECTORY";
    public const string PipeNameEnvironmentVariable = "SCREEN_GUIDE_PIPE_NAME";
    public const string CodexExecutableEnvironmentVariable = "SCREEN_GUIDE_CODEX_PATH";

    public DesktopHostOptions(
        string dataDirectory,
        string? codexExecutablePath = null,
        string? pipeName = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Desktop Host 数据目录不能为空。", nameof(dataDirectory));
        }

        DataDirectory = Path.GetFullPath(dataDirectory.Trim());
        CodexExecutablePath = string.IsNullOrWhiteSpace(codexExecutablePath)
            ? null
            : Path.GetFullPath(codexExecutablePath.Trim());
        PipeName = string.IsNullOrWhiteSpace(pipeName)
            ? DesktopProtocol.DesktopIpcEndpoint.CurrentUserPipeName()
            : pipeName.Trim();
    }

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "state", "tasking.db");

    public string CodexDataDirectory => Path.Combine(DataDirectory, "codex");

    public string EvidenceDataDirectory => Path.Combine(DataDirectory, "evidence");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public string SecretsDirectory => Path.Combine(DataDirectory, "secrets");

    public string AiSettingsPath => Path.Combine(DataDirectory, "settings", "ai-settings.json");

    public string PromptRegistryDirectory => Path.Combine(
        AppContext.BaseDirectory,
        "prompts",
        "runtime");

    public string? CodexExecutablePath { get; }

    public string PipeName { get; }

    public static DesktopHostOptions FromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new DesktopHostOptions(
                configured,
                Environment.GetEnvironmentVariable(CodexExecutableEnvironmentVariable),
                pipeName: Environment.GetEnvironmentVariable(PipeNameEnvironmentVariable));
        }

        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("无法确定当前用户的本地应用数据目录。");
        }

        return new DesktopHostOptions(
            Path.Combine(localApplicationData, "ScreenGuide", "V01"),
            Environment.GetEnvironmentVariable(CodexExecutableEnvironmentVariable),
            pipeName: Environment.GetEnvironmentVariable(PipeNameEnvironmentVariable));
    }
}
