namespace ScreenGuide.Skills.Windows.Tests;

public sealed class DesktopSearchSafetyTests
{
    [Fact]
    public void UniqueStrongCandidateWritesExactQueryAndSubmitsOnce()
    {
        var expected = Window();
        var driver = new FakeSearchDriver(
        [
            Control("primary", name: "地址和搜索栏")
        ]);
        var executor = Executor(expected, driver);

        var result = executor.Search(expected, "元枢");

        Assert.True(result.Verified);
        Assert.Equal("元枢", driver.Value("primary"));
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(1, driver.SubmitCount);
        Assert.Equal(1, driver.ActivationCount);
        Assert.DoesNotContain("地址和搜索栏", result.TechnicalDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("元枢", result.TechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void UniqueHighestConfidenceCandidateWinsWithoutTryingAnotherField()
    {
        var expected = Window();
        var driver = new FakeSearchDriver(
        [
            Control("primary", name: "地址和搜索栏"),
            Control("secondary", automationId: "search")
        ]);

        Executor(expected, driver).Search(expected, "周杰伦");

        Assert.Equal("周杰伦", driver.Value("primary"));
        Assert.Equal(string.Empty, driver.Value("secondary"));
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(1, driver.SubmitCount);
    }

    [Fact]
    public void TiedStrongCandidatesFailClosedBeforeWriting()
    {
        var expected = Window();
        var driver = new FakeSearchDriver(
        [
            Control("one", name: "搜索框"),
            Control("two", labeledBy: "搜索框")
        ]);

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.CandidateAmbiguous, exception.Code);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void WeakGenericEditFailsClosedBeforeWriting()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("generic", name: "编辑")]);

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.CandidateAmbiguous, exception.Code);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("offscreen")]
    [InlineData("password")]
    [InlineData("readonly")]
    [InlineData("unsupported")]
    [InlineData("out-of-window")]
    [InlineData("empty-bounds")]
    [InlineData("missing-runtime")]
    public void UnsafeCandidatesAreRejectedBeforeWriting(string scenario)
    {
        var expected = Window();
        var snapshot = Control("unsafe", name: "搜索框").Snapshot;
        snapshot = scenario switch
        {
            "disabled" => snapshot with { IsEnabled = false },
            "offscreen" => snapshot with { IsOffscreen = true },
            "password" => snapshot with { IsPassword = true },
            "readonly" => snapshot with { IsReadOnly = true },
            "unsupported" => snapshot with { SupportsValuePattern = false },
            "out-of-window" => snapshot with { OwnerWindowHandle = 43 },
            "empty-bounds" => snapshot with { Bounds = new DesktopSearchBounds(0, 0, 0, 0) },
            "missing-runtime" => snapshot with { RuntimeId = string.Empty },
            _ => throw new InvalidOperationException()
        };
        var driver = new FakeSearchDriver([new DesktopSearchControl("unsafe", snapshot)]);

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, exception.Code);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void LabeledByProvidesStrongSearchEvidence()
    {
        var expected = Window();
        var driver = new FakeSearchDriver(
        [
            Control("labeled", name: "编辑", labeledBy: "站内搜索框")
        ]);

        Executor(expected, driver).Search(expected, "今日头条");

        Assert.Equal("今日头条", driver.Value("labeled"));
        Assert.Equal(1, driver.SubmitCount);
    }

    [Fact]
    public void WindowIdentityChangeImmediatelyBeforeWriteFailsClosed()
    {
        var expected = Window();
        var changed = expected with { WindowTitle = "另一个窗口" };
        var foreground = new SequencedForegroundProvider(expected, expected, expected, changed);
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")]);
        var executor = new SafeForegroundSearchExecutor(foreground, driver);

        var exception = Assert.Throws<WindowIdentityException>(
            () => executor.Search(expected, "天气"));

        Assert.Equal(WindowIdentityErrorCodes.Changed, exception.Code);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ForegroundWindowChangeImmediatelyBeforeWriteFailsClosed()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")]);
        driver.OnRead = (read, snapshot) =>
        {
            if (read == 1)
            {
                driver.ForegroundWindowHandle = 43;
            }

            return snapshot;
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, exception.Code);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ElementBoundsChangeImmediatelyBeforeWriteFailsClosed()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            OnRead = (read, snapshot) => read == 2
                ? snapshot with { Bounds = new DesktopSearchBounds(11, 10, 400, 30) }
                : snapshot
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, exception.Code);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ElementRuntimeIdChangeAfterWriteFailsClosedBeforeSubmit()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            OnRead = (read, snapshot) => read == 3
                ? snapshot with { RuntimeId = "replacement" }
                : snapshot
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void FocusLossAfterWriteFailsClosedBeforeSubmit()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            OnRead = (read, snapshot) => read == 3
                ? snapshot with { HasKeyboardFocus = false }
                : snapshot
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.FocusChanged, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void FocusLossImmediatelyBeforeEnterFailsClosed()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            OnRead = (read, snapshot) => read == 4
                ? snapshot with { HasKeyboardFocus = false }
                : snapshot
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.FocusChanged, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ForegroundChangeInsideSubmitBoundaryPreventsEnter()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            BeforeSubmitSend = (current, _) => current.ForegroundWindowHandle = 43
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void FocusChangeInsideSubmitBoundaryPreventsEnter()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            BeforeSubmitSend = (current, control) => current.Mutate(
                control,
                snapshot => snapshot with { HasKeyboardFocus = false })
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.FocusChanged, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ExactValueChangeInsideSubmitBoundaryPreventsEnter()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            BeforeSubmitSend = (current, control) => current.Mutate(
                control,
                snapshot => snapshot with { Value = "changed" })
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.WriteVerificationFailed, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void OwnershipChangeInsideSubmitBoundaryPreventsEnter()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            BeforeSubmitSend = (current, control) => current.Mutate(
                control,
                snapshot => snapshot with { OwnerWindowHandle = 43 })
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ExactValueMismatchFailsClosedBeforeSubmit()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            OnRead = (read, snapshot) => read == 3
                ? snapshot with { Value = "different" }
                : snapshot
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.WriteVerificationFailed, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Theory]
    [InlineData("天气\r\n明天")]
    [InlineData("天气\t明天")]
    [InlineData("天气\u0001明天")]
    public void ExecutionBoundaryRejectsControlCharactersBeforeActivation(string query)
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")]);

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, query));

        Assert.Equal(DesktopSearchErrorCodes.InvalidQuery, exception.Code);
        Assert.Equal(0, driver.ActivationCount);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ExecutionBoundaryRejectsOverTwoHundredCharactersBeforeActivation()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")]);

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, new string('长', 201)));

        Assert.Equal(DesktopSearchErrorCodes.InvalidQuery, exception.Code);
        Assert.Equal(0, driver.ActivationCount);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    private static SafeForegroundSearchExecutor Executor(
        ForegroundWindowSnapshot expected,
        FakeSearchDriver driver) =>
        new(new StableForegroundProvider(expected), driver);

    private static ForegroundWindowSnapshot Window() =>
        new(
            42,
            "合成浏览器",
            "fake-browser",
            101,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 0, 0, 1, TimeSpan.Zero));

    private static DesktopSearchControl Control(
        string id,
        string name = "",
        string automationId = "",
        string helpText = "",
        string labeledBy = "") =>
        new(
            id,
            new DesktopSearchControlSnapshot(
                RuntimeId: id,
                OwnerWindowHandle: 42,
                Name: name,
                AutomationId: automationId,
                HelpText: helpText,
                LabeledBy: labeledBy,
                IsEnabled: true,
                IsOffscreen: false,
                IsPassword: false,
                IsReadOnly: false,
                SupportsValuePattern: true,
                HasKeyboardFocus: false,
                Bounds: new DesktopSearchBounds(10, 10, 400, 30),
                Value: string.Empty));

    private sealed class StableForegroundProvider(ForegroundWindowSnapshot snapshot)
        : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot? GetLastExternalWindow() => snapshot;

        public ForegroundWindowSnapshot? ResolveWindow(long windowHandle) =>
            windowHandle == snapshot.WindowHandle ? snapshot : null;
    }

    private sealed class SequencedForegroundProvider(params ForegroundWindowSnapshot[] snapshots)
        : IForegroundWindowContextProvider
    {
        private int _index;

        public ForegroundWindowSnapshot? GetLastExternalWindow() => snapshots[0];

        public ForegroundWindowSnapshot? ResolveWindow(long windowHandle)
        {
            var index = Math.Min(_index++, snapshots.Length - 1);
            return snapshots[index];
        }
    }

    private sealed class FakeSearchDriver(IReadOnlyList<DesktopSearchControl> controls)
        : IDesktopSearchAutomationDriver
    {
        private readonly Dictionary<string, DesktopSearchControlSnapshot> _states =
            controls.ToDictionary(
                control => (string)control.NativeReference,
                control => control.Snapshot,
                StringComparer.Ordinal);
        private int _readCount;

        public Func<int, DesktopSearchControlSnapshot, DesktopSearchControlSnapshot>? OnRead { get; set; }

        public Action<FakeSearchDriver, DesktopSearchControl>? BeforeSubmitSend { get; init; }

        public long ForegroundWindowHandle { get; set; } = 42;

        public int ActivationCount { get; private set; }

        public int SetValueCount { get; private set; }

        public int SubmitCount { get; private set; }

        public string Value(string id) => _states[id].Value;

        public void Mutate(
            DesktopSearchControl control,
            Func<DesktopSearchControlSnapshot, DesktopSearchControlSnapshot> mutation)
        {
            var id = (string)control.NativeReference;
            _states[id] = mutation(_states[id]);
        }

        public bool IsWindow(long windowHandle) => windowHandle == 42;

        public bool TryActivate(long windowHandle)
        {
            ActivationCount++;
            return windowHandle == 42;
        }

        public long GetForegroundWindowHandle() => ForegroundWindowHandle;

        public IReadOnlyList<DesktopSearchControl> Discover(long windowHandle) => controls;

        public DesktopSearchControlSnapshot Read(DesktopSearchControl control)
        {
            var id = (string)control.NativeReference;
            var current = _states[id];
            _readCount++;
            current = OnRead?.Invoke(_readCount, current) ?? current;
            _states[id] = current;
            return current;
        }

        public void Focus(DesktopSearchControl control)
        {
            var id = (string)control.NativeReference;
            _states[id] = _states[id] with { HasKeyboardFocus = true };
        }

        public void SetValue(DesktopSearchControl control, string value)
        {
            SetValueCount++;
            var id = (string)control.NativeReference;
            _states[id] = _states[id] with { Value = value };
        }

        public void SubmitEnter(
            DesktopSearchControl control,
            Action validateAtSendBoundary)
        {
            BeforeSubmitSend?.Invoke(this, control);
            validateAtSendBoundary();
            SubmitCount++;
        }
    }
}
