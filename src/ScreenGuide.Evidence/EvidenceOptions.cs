namespace ScreenGuide.Evidence;

public sealed class EvidenceOptions(string dataDirectory)
{
    public string DataDirectory { get; } = string.IsNullOrWhiteSpace(dataDirectory)
        ? throw new ArgumentException("证据数据目录不能为空。", nameof(dataDirectory))
        : Path.GetFullPath(dataDirectory.Trim());

    public long MaximumSnapshotFileBytes { get; init; } = 5 * 1024 * 1024;

    public long MaximumSnapshotTotalBytes { get; init; } = 100 * 1024 * 1024;

    public IReadOnlyList<string> VerifiedCodexVersions { get; init; } = ["0.147.0"];

    internal string BaselineDirectory(Guid taskId) =>
        Path.Combine(DataDirectory, "baselines", taskId.ToString("D"));
}
