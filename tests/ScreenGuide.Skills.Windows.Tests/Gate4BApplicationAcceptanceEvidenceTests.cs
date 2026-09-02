using System.Text.Json;
using ScreenGuide.Stage4.RealUsageRunner;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class Gate4BApplicationAcceptanceEvidenceTests
{
    [Fact]
    public void VerifiedLaunchEvidenceSurvivesBackgroundHelperCleanupFailure()
    {
        var evidencePath = Path.Combine(
            Path.GetTempPath(),
            $"yuanshu-gate4b-evidence-{Guid.NewGuid():N}.json");

        try
        {
            var session = Gate4BApplicationAcceptanceSession.Start(
                evidencePath,
                new Gate4BApplicationExpectation(
                    "installed-quark",
                    "installed-quark",
                    @"D:\KuaKe\Quark",
                    "quark.exe",
                    "0123456789ABCDEF0123456789ABCDEF01234567"),
                [4100]);

            session.RecordVerifiedLaunch(new Gate4BTrustedLaunchObservation(
                CandidateCount: 1,
                CatalogId: "installed-quark",
                CanonicalTarget: "installed-quark",
                ProcessId: 4200,
                WindowHandle: 4300,
                ExecutablePath: @"D:\KuaKe\Quark\7.1.5.968\quark.exe",
                SignatureStatus: "Valid",
                SignerThumbprint: "0123456789ABCDEF0123456789ABCDEF01234567",
                WindowVisible: true,
                WindowResponsive: true,
                WindowTitleSha256: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"));

            var checkpoint = ReadEvidence(evidencePath);
            Assert.Equal("LaunchVerified", checkpoint.Status);
            Assert.Equal(4200, checkpoint.Launch!.ProcessId);
            Assert.Null(checkpoint.Cleanup);

            var completed = session.RecordCleanup(new Gate4BCleanupObservation(
                MainWindowCloseSucceeded: true,
                ObservedProcessIds: [4100, 4200, 4201],
                SurvivingProcessIds: [4201],
                ForcedSafetyCleanupProcessIds: [4201],
                CleanupIdentityAmbiguous: false));

            Assert.True(completed.AcceptancePassed);
            Assert.True(completed.Cleanup!.MainWindowCloseSucceeded);
            Assert.Equal([4201], completed.Cleanup.SurvivingNewHelperProcessIds);
            Assert.Equal([4201], completed.Cleanup.ForcedSafetyCleanupProcessIds);
            Assert.DoesNotContain(4100, completed.Cleanup.CleanupTargetProcessIds);

            var persisted = ReadEvidence(evidencePath);
            Assert.Equal(4200, persisted.Launch!.ProcessId);
            Assert.True(persisted.AcceptancePassed);
        }
        finally
        {
            File.Delete(evidencePath);
        }
    }

    [Theory]
    [InlineData("missing", "gate4b_window_missing")]
    [InlineData("multiple", "gate4b_window_ambiguous")]
    [InlineData("catalog", "gate4b_application_identity_mismatch")]
    [InlineData("canonical", "gate4b_application_identity_mismatch")]
    [InlineData("path", "gate4b_executable_untrusted")]
    [InlineData("signature", "gate4b_signature_untrusted")]
    [InlineData("signer", "gate4b_signature_untrusted")]
    [InlineData("hidden", "gate4b_window_not_ready")]
    [InlineData("preexisting", "gate4b_launch_process_preexisting")]
    public void UntrustedOrAmbiguousLaunchNeverBecomesAccepted(
        string variant,
        string expectedCode)
    {
        var evidencePath = NewEvidencePath();
        try
        {
            var session = StartSession(evidencePath);
            var observation = ValidLaunch() with
            {
                CandidateCount = variant switch
                {
                    "missing" => 0,
                    "multiple" => 2,
                    _ => 1
                },
                CatalogId = variant == "catalog" ? "different-app" : "installed-quark",
                CanonicalTarget = variant == "canonical" ? "different-app" : "installed-quark",
                ProcessId = variant == "preexisting" ? 4100 : 4200,
                ExecutablePath = variant == "path"
                    ? @"C:\Untrusted\quark.exe"
                    : @"D:\KuaKe\Quark\7.1.5.968\quark.exe",
                SignatureStatus = variant == "signature" ? "UnknownError" : "Valid",
                SignerThumbprint = variant == "signer"
                    ? "FEDCBA9876543210FEDCBA9876543210FEDCBA98"
                    : "0123456789ABCDEF0123456789ABCDEF01234567",
                WindowVisible = variant != "hidden"
            };

            var error = Assert.Throws<Gate4BEvidenceValidationException>(() =>
                session.RecordVerifiedLaunch(observation));

            Assert.Equal(expectedCode, error.Code);
            var persisted = ReadEvidence(evidencePath);
            Assert.Equal("Pending", persisted.Status);
            Assert.False(persisted.LaunchAcceptancePassed);
            Assert.Null(persisted.Launch);
        }
        finally
        {
            File.Delete(evidencePath);
        }
    }

    [Fact]
    public void CleanupAmbiguityFailsClosedWithoutErasingLaunchCheckpoint()
    {
        var evidencePath = NewEvidencePath();
        try
        {
            var session = StartSession(evidencePath);
            session.RecordVerifiedLaunch(ValidLaunch());

            var error = Assert.Throws<Gate4BEvidenceValidationException>(() =>
                session.RecordCleanup(new Gate4BCleanupObservation(
                    MainWindowCloseSucceeded: true,
                    ObservedProcessIds: [4200, 4201],
                    SurvivingProcessIds: [4201],
                    ForcedSafetyCleanupProcessIds: [],
                    CleanupIdentityAmbiguous: true)));

            Assert.Equal("gate4b_cleanup_identity_ambiguous", error.Code);
            var persisted = ReadEvidence(evidencePath);
            Assert.Equal("LaunchVerified", persisted.Status);
            Assert.Equal(4200, persisted.Launch!.ProcessId);
            Assert.Null(persisted.Cleanup);
        }
        finally
        {
            File.Delete(evidencePath);
        }
    }

    [Fact]
    public void PreExistingProcessCanNeverBecomeCleanupTarget()
    {
        var evidencePath = NewEvidencePath();
        try
        {
            var session = StartSession(evidencePath);
            session.RecordVerifiedLaunch(ValidLaunch());

            var error = Assert.Throws<Gate4BEvidenceValidationException>(() =>
                session.RecordCleanup(new Gate4BCleanupObservation(
                    MainWindowCloseSucceeded: true,
                    ObservedProcessIds: [4100, 4200],
                    SurvivingProcessIds: [4100],
                    ForcedSafetyCleanupProcessIds: [4100],
                    CleanupIdentityAmbiguous: false)));

            Assert.Equal("gate4b_cleanup_targets_preexisting_process", error.Code);
            Assert.Equal("LaunchVerified", ReadEvidence(evidencePath).Status);
        }
        finally
        {
            File.Delete(evidencePath);
        }
    }

    [Fact]
    public void MainWindowCloseFailureIsSeparateFromTrustedLaunchAcceptance()
    {
        var evidencePath = NewEvidencePath();
        try
        {
            var session = StartSession(evidencePath);
            session.RecordVerifiedLaunch(ValidLaunch());

            var result = session.RecordCleanup(new Gate4BCleanupObservation(
                MainWindowCloseSucceeded: false,
                ObservedProcessIds: [4100, 4200],
                SurvivingProcessIds: [],
                ForcedSafetyCleanupProcessIds: [],
                CleanupIdentityAmbiguous: false));

            Assert.True(result.LaunchAcceptancePassed);
            Assert.False(result.AcceptancePassed);
            Assert.Equal("MainWindowCloseFailed", result.Status);
            Assert.Equal(4200, result.Launch!.ProcessId);
        }
        finally
        {
            File.Delete(evidencePath);
        }
    }

    [Fact]
    public void UncleanedNewHelperDoesNotPassOverallAcceptance()
    {
        var evidencePath = NewEvidencePath();
        try
        {
            var session = StartSession(evidencePath);
            session.RecordVerifiedLaunch(ValidLaunch());

            var result = session.RecordCleanup(new Gate4BCleanupObservation(
                MainWindowCloseSucceeded: true,
                ObservedProcessIds: [4100, 4200, 4201],
                SurvivingProcessIds: [4201],
                ForcedSafetyCleanupProcessIds: [],
                CleanupIdentityAmbiguous: false));

            Assert.True(result.LaunchAcceptancePassed);
            Assert.False(result.AcceptancePassed);
            Assert.Equal("CleanupIncomplete", result.Status);
            Assert.False(result.Cleanup!.CleanupCompleted);
        }
        finally
        {
            File.Delete(evidencePath);
        }
    }

    [Fact]
    public void PersistedEvidenceContainsTitleHashButNeverRawWindowTitle()
    {
        var evidencePath = NewEvidencePath();
        try
        {
            var session = StartSession(evidencePath);
            session.RecordVerifiedLaunch(ValidLaunch());

            var json = File.ReadAllText(evidencePath);
            Assert.Contains(
                "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
                json,
                StringComparison.Ordinal);
            Assert.DoesNotContain("\"windowTitle\":", json, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(evidencePath);
        }
    }

    private static string NewEvidencePath() => Path.Combine(
        Path.GetTempPath(),
        $"yuanshu-gate4b-evidence-{Guid.NewGuid():N}.json");

    private static Gate4BApplicationAcceptanceSession StartSession(string evidencePath) =>
        Gate4BApplicationAcceptanceSession.Start(
            evidencePath,
            new Gate4BApplicationExpectation(
                "installed-quark",
                "installed-quark",
                @"D:\KuaKe\Quark",
                "quark.exe",
                "0123456789ABCDEF0123456789ABCDEF01234567"),
            [4100]);

    private static Gate4BTrustedLaunchObservation ValidLaunch() => new(
        CandidateCount: 1,
        CatalogId: "installed-quark",
        CanonicalTarget: "installed-quark",
        ProcessId: 4200,
        WindowHandle: 4300,
        ExecutablePath: @"D:\KuaKe\Quark\7.1.5.968\quark.exe",
        SignatureStatus: "Valid",
        SignerThumbprint: "0123456789ABCDEF0123456789ABCDEF01234567",
        WindowVisible: true,
        WindowResponsive: true,
        WindowTitleSha256: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF");

    private static Gate4BAcceptanceEvidence ReadEvidence(string path) =>
        JsonSerializer.Deserialize<Gate4BAcceptanceEvidence>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("Gate4B evidence is missing.");
}
