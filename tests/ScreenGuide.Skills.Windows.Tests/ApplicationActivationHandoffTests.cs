using System.Diagnostics;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class ApplicationActivationHandoffTests
{
    private static readonly DateTimeOffset AttemptTime =
        new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TrustedCalculatorLauncherAcceptsExactPackageHandoff()
    {
        var window = TrustedCalculatorWindow(200, 2000, AttemptTime.AddMilliseconds(20));
        var runtime = new ScriptedActivationRuntime([window]);
        var launcher = new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(300));

        var result = launcher.OpenApplicationVisible(Calculator(), CancellationToken.None);

        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(Calculator().LaunchTarget, runtime.StartedFileName);
        Assert.Equal(window.Process.ProcessId, result.ProcessId);
        Assert.Equal(window.Handle.ToInt64(), result.WindowHandle);
        Assert.Equal(1, runtime.ActivationCount);
    }

    [Fact]
    public void OrdinarySingleProcessApplicationStillUsesOwnedWindow()
    {
        var process = new ApplicationProcessSnapshot(
            300,
            "notepad",
            AttemptTime.AddMilliseconds(20),
            true,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"),
            null,
            null,
            null);
        var window = new ApplicationWindowSnapshot(new IntPtr(3000), "无标题 - 记事本", process);
        var runtime = new ScriptedActivationRuntime([window]);
        var launcher = new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(300));

        var result = launcher.OpenApplicationVisible(
            new KnownDesktopApplication("notepad", "记事本", process.ExecutablePath!),
            CancellationToken.None);

        Assert.Equal(process.ProcessId, result.ProcessId);
        Assert.Equal(window.Handle.ToInt64(), result.WindowHandle);
    }

    [Fact]
    public void TrustedCalculatorWindowMayAppearLaterWithoutRelaunch()
    {
        var window = TrustedCalculatorWindow(201, 2001, AttemptTime.AddMilliseconds(120));
        var runtime = new ScriptedActivationRuntime([], [], [window]);
        var launcher = new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(500));

        var result = launcher.OpenApplicationVisible(Calculator(), CancellationToken.None);

        Assert.Equal(window.Process.ProcessId, result.ProcessId);
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(2, runtime.WaitCount);
    }

    [Fact]
    public void CancellationStopsSingleWaitAndRejectsLateCalculatorWindow()
    {
        using var cancellation = new CancellationTokenSource();
        var window = TrustedCalculatorWindow(202, 2002, AttemptTime.AddMilliseconds(120));
        var runtime = new ScriptedActivationRuntime([], [window])
        {
            BeforeWait = cancellation.Cancel
        };
        var launcher = new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(500));

        Assert.Throws<OperationCanceledException>(() =>
            launcher.OpenApplicationVisible(Calculator(), cancellation.Token));
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(0, runtime.ActivationCount);
    }

    [Fact]
    public void CancellationDuringActivationRejectsSelectedCalculatorWindow()
    {
        using var cancellation = new CancellationTokenSource();
        var window = TrustedCalculatorWindow(210, 2010, AttemptTime.AddMilliseconds(20));
        var runtime = new ScriptedActivationRuntime([window])
        {
            BeforeActivate = cancellation.Cancel
        };
        var launcher = new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(300));

        Assert.Throws<OperationCanceledException>(() =>
            launcher.OpenApplicationVisible(Calculator(), cancellation.Token));
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(1, runtime.ActivationCount);
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("same-name-wrong-path")]
    [InlineData("wrong-family")]
    [InlineData("wrong-full-name")]
    [InlineData("wrong-aumid")]
    [InlineData("not-running")]
    public void UntrustedCalculatorHandoffsFailClosed(string mutation)
    {
        var trusted = TrustedCalculatorWindow(203, 2003, AttemptTime.AddMilliseconds(20));
        var changed = mutation switch
        {
            "unrelated" => trusted with
            {
                Process = trusted.Process with
                {
                    ProcessName = "unrelated",
                    ExecutablePath = @"C:\Untrusted\unrelated.exe",
                    PackageFamilyName = null,
                    PackageFullName = null,
                    ApplicationUserModelId = null
                }
            },
            "same-name-wrong-path" => trusted with
            {
                Process = trusted.Process with { ExecutablePath = @"C:\Untrusted\CalculatorApp.exe" }
            },
            "wrong-family" => trusted with
            {
                Process = trusted.Process with { PackageFamilyName = "Contoso.Calculator_8wekyb3d8bbwe" }
            },
            "wrong-full-name" => trusted with
            {
                Process = trusted.Process with { PackageFullName = "Contoso.Calculator_1.0.0.0_x64__8wekyb3d8bbwe" }
            },
            "wrong-aumid" => trusted with
            {
                Process = trusted.Process with { ApplicationUserModelId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!Other" }
            },
            "not-running" => trusted with { Process = trusted.Process with { IsRunning = false } },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        var runtime = new ScriptedActivationRuntime([changed]);
        var launcher = new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(250));

        var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
            launcher.OpenApplicationVisible(Calculator(), CancellationToken.None));

        Assert.Equal(InstalledApplicationErrorCodes.TargetChanged, error.Code);
        Assert.Equal(0, runtime.ActivationCount);
    }

    [Fact]
    public void MultipleTrustedCalculatorWindowsAreRejectedAsAmbiguous()
    {
        var runtime = new ScriptedActivationRuntime(
            [
                TrustedCalculatorWindow(204, 2004, AttemptTime.AddMilliseconds(20)),
                TrustedCalculatorWindow(205, 2005, AttemptTime.AddMilliseconds(30))
            ]);
        var launcher = new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(250));

        var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
            launcher.OpenApplicationVisible(Calculator(), CancellationToken.None));

        Assert.Equal(InstalledApplicationErrorCodes.Ambiguous, error.Code);
        Assert.Equal(0, runtime.ActivationCount);
    }

    [Fact]
    public void CalculatorIdentityWithNonSystemLauncherIsRejectedBeforeStart()
    {
        var runtime = new ScriptedActivationRuntime(
            [TrustedCalculatorWindow(209, 2009, AttemptTime.AddMilliseconds(20))]);
        var launcher = new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(250));
        var untrusted = new KnownDesktopApplication(
            "calculator",
            "计算器",
            @"C:\Untrusted\calc.exe");

        var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
            launcher.OpenApplicationVisible(untrusted, CancellationToken.None));

        Assert.Equal(InstalledApplicationErrorCodes.TargetChanged, error.Code);
        Assert.Equal(0, runtime.StartCount);
        Assert.Equal(0, runtime.ActivationCount);
    }

    [Fact]
    public void OldTrustedCalculatorWindowCannotImpersonateCurrentAttempt()
    {
        var old = TrustedCalculatorWindow(206, 2006, AttemptTime.AddMinutes(-1));
        var runtime = new ScriptedActivationRuntime([old]) { BeforeStartWindows = [old] };
        var launcher = new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(250));

        var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
            launcher.OpenApplicationVisible(Calculator(), CancellationToken.None));

        Assert.Equal(InstalledApplicationErrorCodes.TargetChanged, error.Code);
        Assert.Equal(0, runtime.ActivationCount);
    }

    [Fact]
    public void CalculatorWindowArrivingAfterTimeoutIsNotAccepted()
    {
        var late = TrustedCalculatorWindow(207, 2007, AttemptTime.AddSeconds(2));
        var runtime = new ScriptedActivationRuntime([], [], [], [], [late]);
        var launcher = new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(250));

        var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
            launcher.OpenApplicationVisible(Calculator(), CancellationToken.None));

        Assert.Equal(InstalledApplicationErrorCodes.TargetChanged, error.Code);
        Assert.Equal(0, runtime.ActivationCount);
    }

    [Fact]
    public void CalculatorWindowIdentityChangeBeforeActivationFailsClosed()
    {
        var candidate = TrustedCalculatorWindow(208, 2008, AttemptTime.AddMilliseconds(20));
        var runtime = new ScriptedActivationRuntime([candidate])
        {
            ReadWindowOverride = _ => candidate with
            {
                Process = candidate.Process with { ProcessId = candidate.Process.ProcessId + 1 }
            }
        };
        var launcher = new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(250));

        var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
            launcher.OpenApplicationVisible(Calculator(), CancellationToken.None));

        Assert.Equal(InstalledApplicationErrorCodes.TargetChanged, error.Code);
        Assert.Equal(0, runtime.ActivationCount);
    }

    private static KnownDesktopApplication Calculator() => new(
        "calculator",
        "计算器",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "calc.exe"));

    private static ApplicationWindowSnapshot TrustedCalculatorWindow(
        int processId,
        long handle,
        DateTimeOffset startedAtUtc)
    {
        const string packageFullName =
            "Microsoft.WindowsCalculator_11.2606.0.0_x64__8wekyb3d8bbwe";
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps",
            packageFullName,
            "CalculatorApp.exe");
        return new ApplicationWindowSnapshot(
            new IntPtr(handle),
            "计算器",
            new ApplicationProcessSnapshot(
                processId,
                "CalculatorApp",
                startedAtUtc,
                true,
                path,
                "Microsoft.WindowsCalculator_8wekyb3d8bbwe",
                packageFullName,
                "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"));
    }

    private sealed class ScriptedActivationRuntime(
        params IReadOnlyList<ApplicationWindowSnapshot>[] frames) : IApplicationActivationRuntime
    {
        private readonly IReadOnlyList<ApplicationWindowSnapshot>[] _frames = frames.Length == 0
            ? [[]]
            : frames;
        private bool _started;
        private int _frameIndex;
        private long _timestamp;

        public IReadOnlyList<ApplicationWindowSnapshot> BeforeStartWindows { get; init; } = [];

        public Action? BeforeWait { get; init; }

        public Action? BeforeActivate { get; init; }

        public Func<IntPtr, ApplicationWindowSnapshot?>? ReadWindowOverride { get; init; }

        public int StartCount { get; private set; }

        public int WaitCount { get; private set; }

        public int ActivationCount { get; private set; }

        public string? StartedFileName { get; private set; }

        public DateTimeOffset UtcNow => AttemptTime.AddTicks(_timestamp);

        public long GetTimestamp() => _timestamp;

        public TimeSpan GetElapsedTime(long startingTimestamp) =>
            TimeSpan.FromTicks(_timestamp - startingTimestamp);

        public ApplicationProcessLaunch? Start(ProcessStartInfo startInfo)
        {
            _started = true;
            StartCount++;
            StartedFileName = startInfo.FileName;
            return new ApplicationProcessLaunch(100, Path.GetFileNameWithoutExtension(startInfo.FileName));
        }

        public IReadOnlyList<ApplicationWindowSnapshot> EnumerateVisibleWindows() =>
            !_started
                ? BeforeStartWindows
                : _frames[Math.Min(_frameIndex, _frames.Length - 1)];

        public ApplicationWindowSnapshot? ReadWindow(IntPtr handle)
        {
            if (ReadWindowOverride is not null)
            {
                return ReadWindowOverride(handle);
            }

            return EnumerateVisibleWindows().SingleOrDefault(item => item.Handle == handle);
        }

        public bool TryActivate(IntPtr handle, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ActivationCount++;
            BeforeActivate?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        public void Wait(TimeSpan delay, CancellationToken cancellationToken)
        {
            BeforeWait?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            WaitCount++;
            _timestamp += delay.Ticks;
            _frameIndex++;
        }
    }
}
