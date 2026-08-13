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
    public void Start_RequiresAnExplicitWindowTitle()
    {
        var session = new ObservationSession();

        Assert.Throws<ArgumentException>(() => session.Start("   "));
        Assert.False(session.CanCapture);
    }

    [Fact]
    public void Stop_RemovesCapturePermissionAndWindowReference()
    {
        var session = new ObservationSession();
        session.Start("GitHub Desktop");

        session.Stop();

        Assert.Equal(ObservationStatus.Stopped, session.Status);
        Assert.False(session.CanCapture);
        Assert.Null(session.TargetWindowTitle);
    }
}
