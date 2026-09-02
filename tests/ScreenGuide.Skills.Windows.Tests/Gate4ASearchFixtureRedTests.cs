using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ScreenGuide.Stage4.RealUsageRunner;
using Yuanshu.Gate4ASearchFixture;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class Gate4ASearchFixtureAcceptanceTests
{
    [Fact]
    public void ExternalPwshUiaClientFindsOneEditFromExactCompiledFixtureFormHwnd()
    {
        using var fixture = CompiledFixture.Start();

        var observation = PwshUiaProbe.Inspect(fixture.WindowHandle);

        Assert.Equal(fixture.WindowHandle, observation.RootWindowHandle);
        Assert.Equal(fixture.Process.Id, observation.RootProcessId);
        Assert.Equal(1, observation.EditCandidateCount);
    }

    [Fact]
    public void ExternalCompiledUiaClientValidatesStableWritableEditFromExactFormHwnd()
    {
        using var fixture = CompiledFixture.Start();
        var inspector = new Gate4ASearchFixtureInspector();
        var samples = new List<Gate4ASearchFixtureObservation>();
        for (var sample = 0; sample < Gate4ASearchFixtureValidator.RequiredStableSamples; sample++)
        {
            samples.Add(inspector.Inspect(fixture.WindowHandle));
            Thread.Sleep(25);
        }

        var result = new Gate4ASearchFixtureValidator().Validate(
            samples,
            fixture.WindowHandle,
            fixture.Process.Id);

        Assert.True(result.Passed, result.Code);
        Assert.Equal("gate4a_fixture_ready", result.Code);
        var edit = Assert.Single(samples[0].EditCandidates);
        Assert.Equal("ControlType.Edit", edit.ControlType);
        Assert.Equal("Gate4ASearchBox", edit.AutomationId);
        Assert.Equal("搜索框", edit.AccessibleName);
        Assert.True(edit.ValuePatternSupported);
        Assert.False(edit.IsReadOnly);
        Assert.NotEqual(0, edit.NativeWindowHandle);
        Assert.NotEmpty(edit.RuntimeId);
        Assert.True(edit.Bounds.HasArea);
    }

    [Theory]
    [InlineData("zero_edit", "fixture_edit_missing")]
    [InlineData("multiple_edits", "fixture_edit_ambiguous")]
    [InlineData("wrong_root", "fixture_root_identity_mismatch")]
    [InlineData("wrong_owner", "fixture_edit_owner_mismatch")]
    [InlineData("readonly", "fixture_edit_readonly")]
    [InlineData("password", "fixture_edit_password")]
    [InlineData("offscreen", "fixture_edit_offscreen")]
    [InlineData("disabled", "fixture_edit_disabled")]
    [InlineData("missing_value_pattern", "fixture_edit_value_pattern_missing")]
    [InlineData("identity_change", "fixture_edit_identity_changed")]
    [InlineData("bounds_change", "fixture_edit_identity_changed")]
    public void ValidatorFailsClosedForUnsafeOrChangedEdit(
        string scenario,
        string expectedCode)
    {
        const long rootWindowHandle = 100;
        const int processId = 200;
        var samples = ValidSamples(rootWindowHandle, processId);

        samples = Mutate(samples, scenario);
        var result = new Gate4ASearchFixtureValidator().Validate(
            samples,
            rootWindowHandle,
            processId);

        Assert.False(result.Passed);
        Assert.Equal(expectedCode, result.Code);
    }

    [Fact]
    public void SubmissionReceiptContainsOnlyCountAndTextLength()
    {
        Assert.Equal("SubmitCount=1;TextLength=3", SubmissionReceipt.Format(1, 3));
    }

    private static IReadOnlyList<Gate4ASearchFixtureObservation> ValidSamples(
        long rootWindowHandle,
        int processId)
    {
        var rootBounds = new Gate4ARectangle(10, 20, 560, 170);
        var edit = new Gate4AEditCandidate(
            processId,
            101,
            rootWindowHandle,
            [42, 101],
            "Gate4ASearchBox",
            "搜索框",
            "ControlType.Edit",
            IsEnabled: true,
            IsOffscreen: false,
            IsPassword: false,
            ValuePatternSupported: true,
            IsReadOnly: false,
            new Gate4ARectangle(34, 74, 500, 30));
        return Enumerable.Range(0, Gate4ASearchFixtureValidator.RequiredStableSamples)
            .Select(_ => new Gate4ASearchFixtureObservation(
                rootWindowHandle,
                processId,
                [42, 100],
                rootBounds,
                [edit]))
            .ToArray();
    }

    private static IReadOnlyList<Gate4ASearchFixtureObservation> Mutate(
        IReadOnlyList<Gate4ASearchFixtureObservation> source,
        string scenario)
    {
        var samples = source.ToArray();
        var first = samples[0];
        var edit = first.EditCandidates[0];
        switch (scenario)
        {
            case "zero_edit":
                samples[0] = first with { EditCandidates = [] };
                break;
            case "multiple_edits":
                samples[0] = first with { EditCandidates = [edit, edit with { NativeWindowHandle = 102 }] };
                break;
            case "wrong_root":
                samples[0] = first with { RootWindowHandle = 999 };
                break;
            case "wrong_owner":
                samples[0] = WithEdit(first, edit with { OwnerRootWindowHandle = 999 });
                break;
            case "readonly":
                samples[0] = WithEdit(first, edit with { IsReadOnly = true });
                break;
            case "password":
                samples[0] = WithEdit(first, edit with { IsPassword = true });
                break;
            case "offscreen":
                samples[0] = WithEdit(first, edit with { IsOffscreen = true });
                break;
            case "disabled":
                samples[0] = WithEdit(first, edit with { IsEnabled = false });
                break;
            case "missing_value_pattern":
                samples[0] = WithEdit(first, edit with { ValuePatternSupported = false });
                break;
            case "identity_change":
                samples[1] = WithEdit(samples[1], edit with { RuntimeId = [42, 999] });
                break;
            case "bounds_change":
                samples[1] = WithEdit(
                    samples[1],
                    edit with { Bounds = edit.Bounds with { Left = edit.Bounds.Left + 1 } });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        return samples;
    }

    private static Gate4ASearchFixtureObservation WithEdit(
        Gate4ASearchFixtureObservation sample,
        Gate4AEditCandidate edit) =>
        sample with { EditCandidates = [edit] };

    private static class PwshUiaProbe
    {
        public static Observation Inspect(long windowHandle)
        {
            var script = """
                $ErrorActionPreference='Stop'
                Add-Type -AssemblyName UIAutomationClient
                Add-Type -AssemblyName UIAutomationTypes
                $handle=[Int64]$env:YUANSHU_GATE4A_ROOT_HWND
                $root=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]::new($handle))
                $condition=[System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Edit)
                $edits=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition)
                [ordered]@{
                    RootWindowHandle=$root.Current.NativeWindowHandle
                    RootProcessId=$root.Current.ProcessId
                    EditCandidateCount=$edits.Count
                }|ConvertTo-Json -Compress
                """;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var process = Process.Start(new ProcessStartInfo(
                "pwsh.exe",
                $"-NoProfile -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment =
                {
                    ["YUANSHU_GATE4A_ROOT_HWND"] = windowHandle.ToString()
                }
            }) ?? throw new InvalidOperationException("pwsh UIA probe did not start.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10000) || process.ExitCode != 0)
            {
                throw new InvalidOperationException($"pwsh UIA probe failed: {error}");
            }

            return JsonSerializer.Deserialize<Observation>(output)
                ?? throw new InvalidDataException("pwsh UIA probe returned no observation.");
        }

        public sealed record Observation(
            long RootWindowHandle,
            int RootProcessId,
            int EditCandidateCount);
    }

    private sealed class CompiledFixture : IDisposable
    {
        private const string ReadyTitle = "Yuanshu Gate4A Compiled Fixture Ready";
        private readonly Process _process;

        private CompiledFixture(Process process, long windowHandle)
        {
            _process = process;
            WindowHandle = windowHandle;
        }

        public Process Process => _process;

        public long WindowHandle { get; }

        public static CompiledFixture Start()
        {
            var executable = ResolveExecutable();
            var process = Process.Start(new ProcessStartInfo(
                executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Compiled fixture did not start.");

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException("Compiled fixture exited before its window was ready.");
                }

                var windows = NativeMethods.VisibleWindowsForProcess(process.Id);
                if (windows.Count == 1
                    && string.Equals(
                        NativeMethods.WindowText(windows[0]),
                        ReadyTitle,
                        StringComparison.Ordinal))
                {
                    return new CompiledFixture(process, windows[0].ToInt64());
                }

                Thread.Sleep(50);
            }

            process.Kill(entireProcessTree: true);
            process.Dispose();
            throw new TimeoutException("Compiled fixture window was not ready.");
        }

        private static string ResolveExecutable()
        {
            var configuration = AppContext.BaseDirectory.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";
            for (var current = new DirectoryInfo(AppContext.BaseDirectory);
                 current is not null;
                 current = current.Parent)
            {
                var executable = Path.Combine(
                    current.FullName,
                    "tools",
                    "Yuanshu.Gate4ASearchFixture",
                    "bin",
                    configuration,
                    "net10.0-windows10.0.19041.0",
                    "Yuanshu.Gate4ASearchFixture.exe");
                if (File.Exists(executable))
                {
                    return executable;
                }
            }

            throw new FileNotFoundException("Compiled Gate4A fixture executable was not built.");
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                NativeMethods.PostMessage(
                    new IntPtr(WindowHandle),
                    0x0010,
                    IntPtr.Zero,
                    IntPtr.Zero);
                if (!_process.WaitForExit(5000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }

            _process.Dispose();
        }
    }

    private static class NativeMethods
    {
        internal delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

        internal static IReadOnlyList<IntPtr> VisibleWindowsForProcess(int processId)
        {
            var result = new List<IntPtr>();
            EnumWindows((windowHandle, parameter) =>
            {
                GetWindowThreadProcessId(windowHandle, out var candidateProcessId);
                if (candidateProcessId == processId && IsWindowVisible(windowHandle))
                {
                    result.Add(windowHandle);
                }

                return true;
            }, IntPtr.Zero);
            return result;
        }

        internal static string WindowText(IntPtr windowHandle)
        {
            var length = GetWindowTextLength(windowHandle);
            var value = new StringBuilder(length + 1);
            _ = GetWindowText(windowHandle, value, value.Capacity);
            return value.ToString();
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out int processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr windowHandle, StringBuilder value, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            IntPtr windowHandle,
            uint message,
            IntPtr wParam,
            IntPtr lParam);
    }
}
