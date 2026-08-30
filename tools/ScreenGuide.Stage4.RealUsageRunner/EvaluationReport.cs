using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenGuide.Stage4.RealUsageRunner;

public sealed record EvaluationEnvironment(
    string WindowsBuild,
    string ProcessArchitecture,
    bool CapabilityAvailable,
    string MicrophoneBucket);

public sealed class EvaluationReport
{
    private static readonly Regex ExactShaPattern = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex WindowsBuildPattern = new("^[0-9]{1,6}(\\.[0-9]{1,6}){0,3}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedDiagnosticCandidateIds = new(StringComparer.Ordinal)
    {
        "current-cn",
        "common-cn",
        "ascii-token",
        "mixed-token"
    };

    private EvaluationReport(
        string exactSha,
        string mode,
        string stage,
        EvaluationEnvironment environment,
        EvaluationAggregate aggregate,
        bool cleanupConfirmed,
        VisionDiagnosticSummary visionDiagnostic)
    {
        ExactSha = exactSha;
        Mode = mode;
        Stage = stage;
        Environment = environment;
        Aggregate = aggregate;
        CleanupConfirmed = cleanupConfirmed;
        VisionDiagnostic = visionDiagnostic;
    }

    public string ContractVersion => "s4-r2.usage-evaluation.v2";

    public string ExactSha { get; }

    public string Mode { get; }

    public string Stage { get; }

    public EvaluationEnvironment Environment { get; }

    public EvaluationAggregate Aggregate { get; }

    public bool CleanupConfirmed { get; }

    public VisionDiagnosticSummary VisionDiagnostic { get; }

    public int NetworkRequests => 0;

    public int ProviderRequests => 0;

    public static EvaluationReport Create(
        string mode,
        string exactSha,
        string stage,
        EvaluationAggregate aggregate,
        EvaluationEnvironment environment,
        bool cleanupConfirmed,
        VisionDiagnosticSummary? visionDiagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(environment);
        var validatedSha = exactSha ?? string.Empty;
        if (!ExactShaPattern.IsMatch(validatedSha))
        {
            throw new ArgumentException("必须提供 40 位小写 exact SHA。", nameof(exactSha));
        }

        if (mode is not ("voice" or "voice-diagnostic" or "vision" or "vision-diagnostic" or "vision-identity-change"))
        {
            throw new ArgumentException("评测模式不受支持。", nameof(mode));
        }

        if (stage is not ("completed" or "failed" or "cancelled" or "blocked"))
        {
            throw new ArgumentException("评测终态不受支持。", nameof(stage));
        }

        return new EvaluationReport(
            validatedSha,
            mode,
            stage,
            Sanitize(environment),
            Sanitize(aggregate),
            cleanupConfirmed,
            SanitizeVisionDiagnostic(visionDiagnostic));
    }

    private static EvaluationEnvironment Sanitize(EvaluationEnvironment environment)
    {
        var windowsBuild = environment.WindowsBuild ?? string.Empty;
        return new(
            WindowsBuildPattern.IsMatch(windowsBuild)
                ? windowsBuild
                : "unknown",
            environment.ProcessArchitecture is "x64" or "x86" or "arm64"
                ? environment.ProcessArchitecture
                : "unknown",
            environment.CapabilityAvailable,
            environment.MicrophoneBucket is "none" or "one" or "multiple" or "not_applicable"
                ? environment.MicrophoneBucket
                : "unknown");
    }

    private static EvaluationAggregate Sanitize(EvaluationAggregate aggregate) =>
        aggregate with
        {
            ErrorCounts = aggregate.ErrorCounts
                .GroupBy(item => StableEvaluationErrors.Normalize(item.Key), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(item => Math.Max(0, item.Value)),
                    StringComparer.Ordinal)
        };

    private static VisionDiagnosticSummary SanitizeVisionDiagnostic(
        VisionDiagnosticSummary? diagnostic)
    {
        if (diagnostic is null)
        {
            return VisionDiagnosticSummary.Empty;
        }

        var candidates = diagnostic.Candidates
            .Take(4)
            .Select(item =>
            {
                var candidateId = item.CandidateId ?? string.Empty;
                return new VisionCandidateMatchSummary(
                    AllowedDiagnosticCandidateIds.Contains(candidateId)
                        ? candidateId
                        : "unknown",
                    Math.Clamp(item.ExpectedLength, 0, 256),
                    Math.Clamp(item.BestEditDistance, 0, 256));
            })
            .ToArray();
        return new VisionDiagnosticSummary(
            Math.Clamp(diagnostic.SampleCount, 0, 1),
            Math.Clamp(diagnostic.CompactTextLength, 0, 4_000),
            candidates);
    }
}

public static class SafeEvaluationReportWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string Serialize(EvaluationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Options);
    }
}
