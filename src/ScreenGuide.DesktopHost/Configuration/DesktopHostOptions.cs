namespace ScreenGuide.DesktopHost.Configuration;

public sealed class DesktopHostOptions
{
    public const string DataDirectoryEnvironmentVariable = "SCREEN_GUIDE_DATA_DIRECTORY";

    public DesktopHostOptions(string dataDirectory, string? codexExecutablePath = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Desktop Host 数据目录不能为空。", nameof(dataDirectory));
        }

        DataDirectory = Path.GetFullPath(dataDirectory.Trim());
        CodexExecutablePath = string.IsNullOrWhiteSpace(codexExecutablePath)
            ? null
            : Path.GetFullPath(codexExecutablePath.Trim());
    }

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "state", "tasking.db");

    public string CodexDataDirectory => Path.Combine(DataDirectory, "codex");

    public string EvidenceDataDirectory => Path.Combine(DataDirectory, "evidence");

    public string? CodexExecutablePath { get; }

    public static DesktopHostOptions FromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new DesktopHostOptions(configured);
        }

        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("无法确定当前用户的本地应用数据目录。");
        }

        return new DesktopHostOptions(
            Path.Combine(localApplicationData, "ScreenGuide", "V01"));
    }
}
