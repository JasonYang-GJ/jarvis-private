namespace ScreenGuide.Stage4.RealUsageRunner;

public sealed record VisionDiagnosticCandidate(string Id, string Text);

public static class VisionEvaluationContract
{
    public const string FormTitle = "元枢本机单窗口评测";
    public const string ChangedFormTitle = "元枢本机单窗口评测（身份已变化）";
    public const string FormalCanary = "这是无个人数据的本机单窗口评测标记";
    public const int DiagnosticWindowWidth = 960;
    public const int DiagnosticWindowHeight = 600;
    public const float DiagnosticFontSize = 34;

    public static IReadOnlyList<VisionDiagnosticCandidate> DiagnosticCandidates { get; } =
    [
        new("common-cn", "今天学习中文"),
        new("digits-token", "86428642"),
        new("latin-token", "VISION4827"),
        new("mixed-token", "元枢4827")
    ];

    public static string DiagnosticWindowText => string.Join(
        Environment.NewLine,
        DiagnosticCandidates.Select((candidate, index) => $"{index + 1}. {candidate.Text}"));
}
