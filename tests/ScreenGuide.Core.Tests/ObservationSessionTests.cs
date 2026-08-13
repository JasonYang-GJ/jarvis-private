using ScreenGuide.Core;

namespace ScreenGuide.Core.Tests;

public sealed class ObservationSessionTests
{
    [Fact]
    public void NewSession_CannotCapture()
    {
        var session = new ObservationSession();

        Assert.Equal(ObservationStatus.Idle, session.Status);
        Assert.False(session.CanCapture);
        Assert.Null(session.TargetWindowTitle);
    }

    [Fact]
    public void CompleteSelection_RequiresAnExplicitWindowTitle()
    {
        var session = new ObservationSession();
        session.BeginSelection();

        Assert.Throws<ArgumentException>(() => session.CompleteSelection("   "));
        Assert.False(session.CanCapture);
    }

    [Fact]
    public void CompleteSelection_GrantsConsentForChosenWindow()
    {
        var session = new ObservationSession();
        session.BeginSelection();

        session.CompleteSelection("  GitHub Desktop  ");

        Assert.Equal(ObservationStatus.Observing, session.Status);
        Assert.True(session.CanCapture);
        Assert.Equal("GitHub Desktop", session.TargetWindowTitle);
    }

    [Fact]
    public void CancelSelection_FromIdle_DoesNotGrantConsent()
    {
        var session = new ObservationSession();
        session.BeginSelection();

        session.CancelSelection();

        Assert.Equal(ObservationStatus.Idle, session.Status);
        Assert.False(session.CanCapture);
        Assert.Null(session.TargetWindowTitle);
    }

    [Fact]
    public void CancelSelection_WhenReplacingWindow_PreservesExistingConsent()
    {
        var session = CreateObservingSession("GitHub Desktop");
        session.BeginSelection();

        session.CancelSelection();

        Assert.Equal(ObservationStatus.Observing, session.Status);
        Assert.True(session.CanCapture);
        Assert.Equal("GitHub Desktop", session.TargetWindowTitle);
    }

    [Fact]
    public void Stop_RemovesConsentAndWindowReference()
    {
        var session = CreateObservingSession("GitHub Desktop");

        session.Stop();

        Assert.Equal(ObservationStatus.Stopped, session.Status);
        Assert.False(session.CanCapture);
        Assert.Null(session.TargetWindowTitle);
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        var session = CreateObservingSession("GitHub Desktop");

        session.Stop();
        session.Stop();

        Assert.Equal(ObservationStatus.Stopped, session.Status);
        Assert.False(session.CanCapture);
    }

    [Fact]
    public void Stop_DuringReplacementSelection_RemovesPreviousConsent()
    {
        var session = CreateObservingSession("GitHub Desktop");
        session.BeginSelection();

        session.Stop();

        Assert.Equal(ObservationStatus.Stopped, session.Status);
        Assert.False(session.CanCapture);
        Assert.Null(session.TargetWindowTitle);
    }

    [Fact]
    public void CompleteSelection_WithoutBeginning_Throws()
    {
        var session = new ObservationSession();

        Assert.Throws<InvalidOperationException>(() => session.CompleteSelection("GitHub Desktop"));
    }

    private static ObservationSession CreateObservingSession(string title)
    {
        var session = new ObservationSession();
        session.BeginSelection();
        session.CompleteSelection(title);
        return session;
    }
}

public sealed class CloudSharingAuthorizationTests
{
    [Fact]
    public void NewAuthorization_DoesNotAllowCloudSharing()
    {
        var authorization = new CloudSharingAuthorization();

        Assert.False(authorization.IsGranted);
        Assert.False(authorization.CanShareFrameFrom("GitHub Desktop"));
    }

    [Fact]
    public void Grant_AllowsOnlyTheExactSelectedWindow()
    {
        var authorization = new CloudSharingAuthorization();

        authorization.GrantForWindow("GitHub Desktop");

        Assert.True(authorization.CanShareFrameFrom("GitHub Desktop"));
        Assert.False(authorization.CanShareFrameFrom("浏览器"));
    }

    [Fact]
    public void Revoke_ImmediatelyBlocksCloudSharing()
    {
        var authorization = new CloudSharingAuthorization();
        authorization.GrantForWindow("GitHub Desktop");

        authorization.Revoke();

        Assert.False(authorization.IsGranted);
        Assert.False(authorization.CanShareFrameFrom("GitHub Desktop"));
    }
}
