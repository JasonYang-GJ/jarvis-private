using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenGuide.Stage4.RealUsageRunner;

public sealed record Gate4BApplicationExpectation(
    string CatalogId,
    string CanonicalTarget,
    string TrustedExecutableRoot,
    string ExpectedExecutableName,
    string ExpectedSignerThumbprint);

public sealed record Gate4BTrustedLaunchObservation(
    int CandidateCount,
    string CatalogId,
    string CanonicalTarget,
    int ProcessId,
    long WindowHandle,
    string ExecutablePath,
    string SignatureStatus,
    string SignerThumbprint,
    bool WindowVisible,
    bool WindowResponsive,
    string WindowTitleSha256);

public sealed record Gate4BCleanupObservation(
    bool MainWindowCloseSucceeded,
    IReadOnlyList<int> ObservedProcessIds,
    IReadOnlyList<int> SurvivingProcessIds,
    IReadOnlyList<int> ForcedSafetyCleanupProcessIds,
    bool CleanupIdentityAmbiguous);

public sealed record Gate4BLaunchEvidence(
    string CatalogId,
    string CanonicalTarget,
    int ProcessId,
    long WindowHandle,
    string ExecutablePath,
    string SignatureStatus,
    string SignerThumbprint,
    string WindowTitleSha256,
    DateTimeOffset VerifiedAtUtc);

public sealed record Gate4BCleanupEvidence(
    bool MainWindowCloseSucceeded,
    IReadOnlyList<int> SurvivingNewHelperProcessIds,
    IReadOnlyList<int> ForcedSafetyCleanupProcessIds,
    IReadOnlyList<int> CleanupTargetProcessIds,
    bool CleanupCompleted,
    DateTimeOffset RecordedAtUtc);

public sealed record Gate4BAcceptanceEvidence(
    string Status,
    bool LaunchAcceptancePassed,
    bool AcceptancePassed,
    Gate4BLaunchEvidence? Launch,
    Gate4BCleanupEvidence? Cleanup);

public sealed class Gate4BEvidenceValidationException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class Gate4BApplicationAcceptanceSession
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly Regex Sha256Pattern = new(
        "^[0-9A-F]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ThumbprintPattern = new(
        "^[0-9A-F]{40}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string evidencePath;
    private readonly Gate4BApplicationExpectation expectation;
    private readonly HashSet<int> baselineProcessIds;
    private readonly TimeProvider timeProvider;
    private Gate4BAcceptanceEvidence evidence;

    private Gate4BApplicationAcceptanceSession(
        string evidencePath,
        Gate4BApplicationExpectation expectation,
        IEnumerable<int> baselineProcessIds,
        TimeProvider timeProvider)
    {
        this.evidencePath = Path.GetFullPath(evidencePath);
        this.expectation = Normalize(expectation);
        this.baselineProcessIds = baselineProcessIds.ToHashSet();
        this.timeProvider = timeProvider;

        if (this.baselineProcessIds.Any(processId => processId <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(baselineProcessIds),
                "Baseline process identifiers must be positive.");
        }

        evidence = new Gate4BAcceptanceEvidence(
            "Pending",
            LaunchAcceptancePassed: false,
            AcceptancePassed: false,
            Launch: null,
            Cleanup: null);
        Persist();
    }

    public static Gate4BApplicationAcceptanceSession Start(
        string evidencePath,
        Gate4BApplicationExpectation expectation,
        IEnumerable<int> baselineProcessIds,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidencePath);
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(baselineProcessIds);

        return new Gate4BApplicationAcceptanceSession(
            evidencePath,
            expectation,
            baselineProcessIds,
            timeProvider ?? TimeProvider.System);
    }

    public Gate4BAcceptanceEvidence RecordVerifiedLaunch(
        Gate4BTrustedLaunchObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (evidence.Launch is not null)
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_launch_evidence_already_recorded",
                "Trusted launch evidence can only be recorded once.");
        }

        ValidateLaunch(observation);
        evidence = new Gate4BAcceptanceEvidence(
            "LaunchVerified",
            LaunchAcceptancePassed: true,
            AcceptancePassed: false,
            Launch: new Gate4BLaunchEvidence(
                observation.CatalogId,
                observation.CanonicalTarget,
                observation.ProcessId,
                observation.WindowHandle,
                Path.GetFullPath(observation.ExecutablePath),
                observation.SignatureStatus,
                observation.SignerThumbprint,
                observation.WindowTitleSha256,
                timeProvider.GetUtcNow()),
            Cleanup: null);
        Persist();
        return evidence;
    }

    public Gate4BAcceptanceEvidence RecordCleanup(Gate4BCleanupObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (evidence.Launch is null)
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_launch_evidence_missing",
                "Cleanup cannot be recorded before trusted launch evidence.");
        }

        if (evidence.Cleanup is not null)
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_cleanup_evidence_already_recorded",
                "Cleanup evidence can only be recorded once.");
        }

        if (observation.CleanupIdentityAmbiguous)
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_cleanup_identity_ambiguous",
                "Cleanup process identity is ambiguous; no process may be selected automatically.");
        }

        var observed = RequirePositiveDistinct(observation.ObservedProcessIds, nameof(observation.ObservedProcessIds));
        var surviving = RequirePositiveDistinct(observation.SurvivingProcessIds, nameof(observation.SurvivingProcessIds));
        var forced = RequirePositiveDistinct(
            observation.ForcedSafetyCleanupProcessIds,
            nameof(observation.ForcedSafetyCleanupProcessIds));
        if (!surviving.IsSubsetOf(observed))
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_cleanup_observation_invalid",
                "Every surviving process must be present in the observed process snapshot.");
        }

        var survivingNew = surviving.Except(baselineProcessIds).Order().ToArray();
        if (forced.Overlaps(baselineProcessIds))
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_cleanup_targets_preexisting_process",
                "Pre-existing processes must never become cleanup targets.");
        }

        if (!forced.IsSubsetOf(survivingNew))
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_cleanup_target_not_observed",
                "Forced cleanup targets must be surviving processes created by this run.");
        }

        var cleanupCompleted = survivingNew.All(forced.Contains);
        var acceptancePassed = observation.MainWindowCloseSucceeded && cleanupCompleted;
        var cleanup = new Gate4BCleanupEvidence(
            observation.MainWindowCloseSucceeded,
            survivingNew,
            forced.Order().ToArray(),
            forced.Order().ToArray(),
            cleanupCompleted,
            timeProvider.GetUtcNow());
        evidence = evidence with
        {
            Status = acceptancePassed
                ? survivingNew.Length == 0
                    ? "Accepted"
                    : "AcceptedWithBackgroundCleanup"
                : observation.MainWindowCloseSucceeded
                    ? "CleanupIncomplete"
                    : "MainWindowCloseFailed",
            AcceptancePassed = acceptancePassed,
            Cleanup = cleanup
        };
        Persist();
        return evidence;
    }

    private void ValidateLaunch(Gate4BTrustedLaunchObservation observation)
    {
        if (observation.CandidateCount == 0)
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_window_missing",
                "No trusted application window was found.");
        }

        if (observation.CandidateCount != 1)
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_window_ambiguous",
                "The trusted application window is not unique.");
        }

        if (!string.Equals(observation.CatalogId, expectation.CatalogId, StringComparison.Ordinal)
            || !string.Equals(
                observation.CanonicalTarget,
                expectation.CanonicalTarget,
                StringComparison.Ordinal))
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_application_identity_mismatch",
                "The observed application does not match the frozen Catalog identity.");
        }

        if (observation.ProcessId <= 0 || observation.WindowHandle <= 0)
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_window_identity_invalid",
                "The trusted process and window identifiers must be positive.");
        }

        if (baselineProcessIds.Contains(observation.ProcessId))
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_launch_process_preexisting",
                "A pre-existing process cannot prove a launch from this acceptance run.");
        }

        if (!observation.WindowVisible || !observation.WindowResponsive)
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_window_not_ready",
                "The trusted application window must be visible and responsive.");
        }

        var executablePath = Path.GetFullPath(observation.ExecutablePath);
        var trustedPrefix = Path.TrimEndingDirectorySeparator(expectation.TrustedExecutableRoot)
                            + Path.DirectorySeparatorChar;
        if (!executablePath.StartsWith(trustedPrefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(executablePath),
                expectation.ExpectedExecutableName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_executable_untrusted",
                "The window-owning executable is outside the frozen trusted target.");
        }

        if (!string.Equals(observation.SignatureStatus, "Valid", StringComparison.Ordinal)
            || !string.Equals(
                observation.SignerThumbprint,
                expectation.ExpectedSignerThumbprint,
                StringComparison.Ordinal))
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_signature_untrusted",
                "The window-owning executable does not have the required trusted signature evidence.");
        }

        if (!Sha256Pattern.IsMatch(observation.WindowTitleSha256))
        {
            throw new Gate4BEvidenceValidationException(
                "gate4b_title_hash_invalid",
                "The redacted window-title hash must be a full SHA-256 value.");
        }
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(evidencePath)
                        ?? throw new InvalidOperationException("The evidence path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(evidencePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(evidence, JsonOptions));
            File.Move(temporaryPath, evidencePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static Gate4BApplicationExpectation Normalize(
        Gate4BApplicationExpectation expectation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectation.CatalogId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectation.CanonicalTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectation.TrustedExecutableRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectation.ExpectedExecutableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectation.ExpectedSignerThumbprint);
        if (!ThumbprintPattern.IsMatch(expectation.ExpectedSignerThumbprint))
        {
            throw new ArgumentException(
                "The expected signer thumbprint must be a full uppercase SHA-1 value.",
                nameof(expectation));
        }

        return expectation with
        {
            TrustedExecutableRoot = Path.GetFullPath(expectation.TrustedExecutableRoot)
        };
    }

    private static HashSet<int> RequirePositiveDistinct(
        IReadOnlyList<int> processIds,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        if (processIds.Any(processId => processId <= 0)
            || processIds.Distinct().Count() != processIds.Count)
        {
            throw new ArgumentException(
                "Process identifiers must be positive and unique.",
                parameterName);
        }

        return processIds.ToHashSet();
    }
}
