using System.Windows.Forms;
using ScreenGuide.FakeBrowser;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class VisibleWindowActivationTests
{
    [Fact]
    public void RealApplicationLaunchPathFindsAndRaisesOwnedWindow()
    {
        var executable = Path.ChangeExtension(
            typeof(FakeBrowserMarker).Assembly.Location,
            ".exe");
        var launcher = new DesktopProcessLauncher();
        VisibleDesktopLaunchResult? result = null;
        try
        {
            result = launcher.OpenApplicationVisible(executable);

            Assert.True(result.WindowHandle > 0);
            Assert.Contains("模拟浏览器", result.WindowTitle, StringComparison.Ordinal);
        }
        finally
        {
            StopTestProcess(result?.ProcessId);
        }
    }

    [Fact]
    public void RealBrowserLaunchPathFindsAndRaisesOwnedWindow()
    {
        var browserAssembly = typeof(FakeBrowserMarker).Assembly.Location;
        var browserExecutable = Path.ChangeExtension(browserAssembly, ".exe");
        var launcher = new DesktopProcessLauncher();
        VisibleDesktopLaunchResult? result = null;
        try
        {
            result = launcher.OpenWebsiteVisible(
                browserExecutable,
                new Uri("https://example.test/"));

            Assert.True(result.WindowHandle > 0);
            Assert.Contains("模拟浏览器", result.WindowTitle, StringComparison.Ordinal);
        }
        finally
        {
            StopTestProcess(result?.ProcessId);
        }
    }

    private static void StopTestProcess(int? processId)
    {
        if (processId is null)
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId.Value);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3_000);
        }
        catch (ArgumentException)
        {
            // The test application can close itself before cleanup.
        }
    }

    [Fact]
    public async Task RealSyntheticWindowCanBeRestoredAndActivated()
    {
        var ready = new TaskCompletionSource<Form>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var form = new Form
            {
                Text = "元枢可见窗口激活测试",
                Width = 420,
                Height = 240,
                ShowInTaskbar = false
            };
            form.Shown += (_, _) =>
            {
                form.WindowState = FormWindowState.Minimized;
                ready.TrySetResult(form);
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var form = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var activated = VisibleWindowActivation.TryActivate(
                form.Handle,
                TimeSpan.FromSeconds(3));

            Assert.True(activated);
        }
        finally
        {
            form.BeginInvoke(form.Close);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task RealAccessibleSearchBarAcceptsQueryAndSubmit()
    {
        var ready = new TaskCompletionSource<(Form Form, TextBox SearchBox)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var form = new Form
            {
                Text = "元枢模拟浏览器搜索测试",
                Width = 520,
                Height = 240,
                ShowInTaskbar = false
            };
            using var search = new TextBox
            {
                AccessibleName = "地址和搜索栏",
                Width = 420,
                Left = 30,
                Top = 45
            };
            form.Controls.Add(search);
            form.Shown += (_, _) =>
            {
                form.Activate();
                ready.TrySetResult((form, search));
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var (form, search) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var foreground = new ForegroundWindowTracker();
            var expected = foreground.ResolveWindow(form.Handle.ToInt64())
                ?? throw new InvalidOperationException("未能读取合成测试窗口身份。");
            var result = new WindowsUiAutomationService(foreground).Search(expected, "抖音");
            var text = (string)form.Invoke(() => search.Text);

            Assert.True(result.Verified);
            Assert.Equal("抖音", text);
            Assert.Contains("提交", result.Summary, StringComparison.Ordinal);
        }
        finally
        {
            form.BeginInvoke(form.Close);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task SearchRejectsIdentityChangeAfterControlDiscoveryBeforeWrite()
    {
        var ready = new TaskCompletionSource<(Form Form, TextBox Search)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var form = new Form
            {
                Text = "元枢身份切换测试",
                Width = 520,
                Height = 240,
                ShowInTaskbar = false
            };
            using var search = new TextBox
            {
                AccessibleName = "地址和搜索栏",
                Width = 420,
                Left = 30,
                Top = 45
            };
            form.Controls.Add(search);
            form.Shown += (_, _) =>
            {
                form.Activate();
                ready.TrySetResult((form, search));
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var (form, search) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var tracker = new ForegroundWindowTracker();
            var expected = tracker.ResolveWindow(form.Handle.ToInt64())
                ?? throw new InvalidOperationException("未能读取合成测试窗口身份。");
            var changed = expected with
            {
                ProcessId = expected.ProcessId + 1,
                ProcessStartTimeUtc = expected.ProcessStartTimeUtc.AddSeconds(1)
            };
            var foreground = new ScriptedForegroundProvider(expected, expected, changed);

            var failure = Assert.Throws<WindowIdentityException>(() =>
                new WindowsUiAutomationService(foreground).Search(expected, "不得写入"));
            var text = (string)form.Invoke(() => search.Text);

            Assert.Equal(WindowIdentityErrorCodes.Changed, failure.Code);
            Assert.Equal(string.Empty, text);
            Assert.Equal(3, foreground.ResolveCount);
        }
        finally
        {
            form.BeginInvoke(form.Close);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    private sealed class ScriptedForegroundProvider(
        params ForegroundWindowSnapshot?[] snapshots) : IForegroundWindowContextProvider
    {
        private int _resolveCount;

        public int ResolveCount => Volatile.Read(ref _resolveCount);

        public ForegroundWindowSnapshot? GetLastExternalWindow() =>
            snapshots.Length == 0 ? null : snapshots[^1];

        public ForegroundWindowSnapshot? ResolveWindow(long windowHandle)
        {
            var index = Interlocked.Increment(ref _resolveCount) - 1;
            return snapshots.Length == 0
                ? null
                : snapshots[Math.Min(index, snapshots.Length - 1)];
        }
    }
}
