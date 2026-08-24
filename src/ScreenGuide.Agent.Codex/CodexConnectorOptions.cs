namespace ScreenGuide.Agent.Codex;

public sealed class CodexConnectorOptions
{
    public const string ExecutableEnvironmentVariable = "SCREEN_GUIDE_CODEX_EXECUTABLE";

    public CodexConnectorOptions(string dataDirectory, string? executablePath = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Codex Connector 数据目录不能为空。", nameof(dataDirectory));
        }

        DataDirectory = Path.GetFullPath(dataDirectory.Trim());
        ExecutablePath = string.IsNullOrWhiteSpace(executablePath)
            ? null
            : Path.GetFullPath(executablePath.Trim());
    }

    public string DataDirectory { get; }

    public string? ExecutablePath { get; }

    public string SandboxMode { get; init; } = "workspace-write";

    public string? Model { get; init; }

    public TimeSpan ChatRequestTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public static CodexConnectorOptions FromDataDirectory(string dataDirectory) =>
        new(
            dataDirectory,
            Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable));
}
