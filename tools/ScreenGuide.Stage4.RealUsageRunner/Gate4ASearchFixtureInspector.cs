using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ScreenGuide.Stage4.RealUsageRunner;

public sealed record Gate4ARectangle(
    double Left,
    double Top,
    double Width,
    double Height)
{
    public bool HasArea => Width > 0 && Height > 0;

    public bool Contains(Gate4ARectangle candidate) =>
        HasArea
        && candidate.HasArea
        && candidate.Left >= Left
        && candidate.Top >= Top
        && candidate.Left + candidate.Width <= Left + Width
        && candidate.Top + candidate.Height <= Top + Height;
}

public sealed record Gate4AEditCandidate(
    int ProcessId,
    long NativeWindowHandle,
    long OwnerRootWindowHandle,
    IReadOnlyList<int> RuntimeId,
    string AutomationId,
    string AccessibleName,
    string ControlType,
    bool IsEnabled,
    bool IsOffscreen,
    bool IsPassword,
    bool ValuePatternSupported,
    bool IsReadOnly,
    Gate4ARectangle Bounds);

public sealed record Gate4ASearchFixtureObservation(
    long RootWindowHandle,
    int RootProcessId,
    IReadOnlyList<int> RootRuntimeId,
    Gate4ARectangle RootBounds,
    IReadOnlyList<Gate4AEditCandidate> EditCandidates)
{
    public int EditCandidateCount => EditCandidates.Count;
}

public sealed record Gate4ASearchFixtureValidationResult(
    bool Passed,
    string Code)
{
    public static Gate4ASearchFixtureValidationResult Pass() => new(true, "gate4a_fixture_ready");

    public static Gate4ASearchFixtureValidationResult Reject(string code) => new(false, code);
}

public sealed class Gate4ASearchFixtureInspector
{
    private const uint GetRoot = 2;

    public Gate4ASearchFixtureObservation Inspect(long rootWindowHandle)
    {
        if (rootWindowHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rootWindowHandle));
        }

        var expectedRoot = new IntPtr(rootWindowHandle);
        var root = AutomationElement.FromHandle(expectedRoot)
            ?? throw new ElementNotAvailableException();
        var condition = new PropertyCondition(
            AutomationElement.ControlTypeProperty,
            ControlType.Edit);
        var edits = root.FindAll(TreeScope.Descendants, condition);
        var candidates = new List<Gate4AEditCandidate>(edits.Count);
        for (var index = 0; index < edits.Count; index++)
        {
            var edit = edits[index];
            var nativeWindowHandle = edit.Current.NativeWindowHandle;
            var valuePatternSupported = edit.TryGetCurrentPattern(
                ValuePattern.Pattern,
                out var valuePatternObject);
            var isReadOnly = valuePatternSupported
                && ((ValuePattern)valuePatternObject).Current.IsReadOnly;
            var ownerRoot = nativeWindowHandle == 0
                ? IntPtr.Zero
                : GetAncestor(new IntPtr(nativeWindowHandle), GetRoot);

            candidates.Add(new Gate4AEditCandidate(
                edit.Current.ProcessId,
                nativeWindowHandle,
                ownerRoot.ToInt64(),
                edit.GetRuntimeId(),
                edit.Current.AutomationId ?? string.Empty,
                edit.Current.Name ?? string.Empty,
                edit.Current.ControlType?.ProgrammaticName ?? string.Empty,
                edit.Current.IsEnabled,
                edit.Current.IsOffscreen,
                edit.Current.IsPassword,
                valuePatternSupported,
                isReadOnly,
                ToRectangle(edit.Current.BoundingRectangle)));
        }

        return new Gate4ASearchFixtureObservation(
            root.Current.NativeWindowHandle,
            root.Current.ProcessId,
            root.GetRuntimeId(),
            ToRectangle(root.Current.BoundingRectangle),
            candidates);
    }

    private static Gate4ARectangle ToRectangle(System.Windows.Rect rectangle) =>
        new(rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);
}

public sealed class Gate4ASearchFixtureValidator
{
    public const int RequiredStableSamples = 3;
    public const string ExpectedAutomationId = "Gate4ASearchBox";
    public const string ExpectedAccessibleName = "搜索框";
    public const string ExpectedControlType = "ControlType.Edit";

    public Gate4ASearchFixtureValidationResult Validate(
        IReadOnlyList<Gate4ASearchFixtureObservation> samples,
        long expectedRootWindowHandle,
        int expectedRootProcessId)
    {
        if (samples.Count != RequiredStableSamples)
        {
            return Gate4ASearchFixtureValidationResult.Reject("fixture_sample_count_mismatch");
        }

        Gate4AEditCandidate? firstCandidate = null;
        IReadOnlyList<int>? firstRootRuntimeId = null;
        foreach (var sample in samples)
        {
            if (sample.RootWindowHandle != expectedRootWindowHandle
                || sample.RootProcessId != expectedRootProcessId)
            {
                return Gate4ASearchFixtureValidationResult.Reject("fixture_root_identity_mismatch");
            }

            if (sample.RootRuntimeId.Count == 0 || !sample.RootBounds.HasArea)
            {
                return Gate4ASearchFixtureValidationResult.Reject("fixture_root_invalid");
            }

            firstRootRuntimeId ??= sample.RootRuntimeId;
            if (!firstRootRuntimeId.SequenceEqual(sample.RootRuntimeId))
            {
                return Gate4ASearchFixtureValidationResult.Reject("fixture_root_identity_changed");
            }

            if (sample.EditCandidateCount == 0)
            {
                return Gate4ASearchFixtureValidationResult.Reject("fixture_edit_missing");
            }

            if (sample.EditCandidateCount != 1)
            {
                return Gate4ASearchFixtureValidationResult.Reject("fixture_edit_ambiguous");
            }

            var candidate = sample.EditCandidates[0];
            var validation = ValidateCandidate(
                sample,
                candidate,
                expectedRootWindowHandle,
                expectedRootProcessId);
            if (!validation.Passed)
            {
                return validation;
            }

            firstCandidate ??= candidate;
            if (!SameIdentity(firstCandidate, candidate))
            {
                return Gate4ASearchFixtureValidationResult.Reject("fixture_edit_identity_changed");
            }
        }

        return Gate4ASearchFixtureValidationResult.Pass();
    }

    private static Gate4ASearchFixtureValidationResult ValidateCandidate(
        Gate4ASearchFixtureObservation sample,
        Gate4AEditCandidate candidate,
        long expectedRootWindowHandle,
        int expectedRootProcessId)
    {
        if (candidate.ProcessId != expectedRootProcessId
            || candidate.OwnerRootWindowHandle != expectedRootWindowHandle
            || candidate.NativeWindowHandle == 0)
        {
            return Gate4ASearchFixtureValidationResult.Reject("fixture_edit_owner_mismatch");
        }

        if (!string.Equals(candidate.ControlType, ExpectedControlType, StringComparison.Ordinal))
        {
            return Gate4ASearchFixtureValidationResult.Reject("fixture_edit_control_type_mismatch");
        }

        if (!string.Equals(candidate.AutomationId, ExpectedAutomationId, StringComparison.Ordinal)
            || !string.Equals(candidate.AccessibleName, ExpectedAccessibleName, StringComparison.Ordinal))
        {
            return Gate4ASearchFixtureValidationResult.Reject("fixture_edit_accessible_identity_mismatch");
        }

        if (candidate.RuntimeId.Count == 0)
        {
            return Gate4ASearchFixtureValidationResult.Reject("fixture_edit_runtime_id_missing");
        }

        if (!candidate.IsEnabled)
        {
            return Gate4ASearchFixtureValidationResult.Reject("fixture_edit_disabled");
        }

        if (candidate.IsOffscreen)
        {
            return Gate4ASearchFixtureValidationResult.Reject("fixture_edit_offscreen");
        }

        if (candidate.IsPassword)
        {
            return Gate4ASearchFixtureValidationResult.Reject("fixture_edit_password");
        }

        if (!candidate.ValuePatternSupported)
        {
            return Gate4ASearchFixtureValidationResult.Reject("fixture_edit_value_pattern_missing");
        }

        if (candidate.IsReadOnly)
        {
            return Gate4ASearchFixtureValidationResult.Reject("fixture_edit_readonly");
        }

        if (!sample.RootBounds.Contains(candidate.Bounds))
        {
            return Gate4ASearchFixtureValidationResult.Reject("fixture_edit_bounds_invalid");
        }

        return Gate4ASearchFixtureValidationResult.Pass();
    }

    private static bool SameIdentity(Gate4AEditCandidate expected, Gate4AEditCandidate actual) =>
        expected.ProcessId == actual.ProcessId
        && expected.NativeWindowHandle == actual.NativeWindowHandle
        && expected.OwnerRootWindowHandle == actual.OwnerRootWindowHandle
        && expected.RuntimeId.SequenceEqual(actual.RuntimeId)
        && expected.Bounds == actual.Bounds
        && string.Equals(expected.AutomationId, actual.AutomationId, StringComparison.Ordinal)
        && string.Equals(expected.AccessibleName, actual.AccessibleName, StringComparison.Ordinal)
        && string.Equals(expected.ControlType, actual.ControlType, StringComparison.Ordinal);
}
