using ScreenGuide.DesktopClient.Services;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class PointerGestureGateTests
{
    [Fact]
    public async Task ArmDoesNothingUntilOneExplicitGestureAndRejectsDuplicates()
    {
        var time = new Clock();
        using var gate = new PointerGestureGate(time);
        var count = 0;
        gate.Arm(time.GetUtcNow().AddSeconds(30), _ => { count++; return Task.CompletedTask; }, default);
        Assert.Equal(0, count);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => gate.ExecuteAsync()));
        Assert.Equal(1, count);
        Assert.False(gate.IsArmed);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-1)]
    public async Task ExpiryAndClockRollbackNeverExecute(int offset)
    {
        var time = new Clock();
        using var gate = new PointerGestureGate(time);
        var count = 0;
        gate.Arm(time.GetUtcNow().AddSeconds(30), _ => { count++; return Task.CompletedTask; }, default);
        time.Now = time.Now.AddSeconds(offset);
        await gate.ExecuteAsync();
        Assert.Equal(0, count);
        Assert.False(gate.IsArmed);
    }

    [Fact]
    public async Task ReplacementCancelsTheRealRunningCallbackAndLeavesOnlyNewGesture()
    {
        var time = new Clock();
        using var gate = new PointerGestureGate(time);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Arm(time.Now.AddSeconds(30), async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); }, default);
        var running = gate.ExecuteAsync();
        await entered.Task;
        var replacement = 0;
        gate.Arm(time.Now.AddSeconds(30), _ => { replacement++; return Task.CompletedTask; }, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await gate.ExecuteAsync();
        Assert.Equal(1, replacement);
    }

    [Fact]
    public async Task CancelAndDisposeRevokePendingGesture()
    {
        var time = new Clock();
        var gate = new PointerGestureGate(time);
        var count = 0;
        gate.Arm(time.Now.AddSeconds(30), _ => { count++; return Task.CompletedTask; }, default);
        gate.Cancel();
        await gate.ExecuteAsync();
        gate.Dispose();
        Assert.Throws<ObjectDisposedException>(() => gate.Arm(time.Now.AddSeconds(30), _ => Task.CompletedTask, default));
        Assert.Equal(0, count);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
