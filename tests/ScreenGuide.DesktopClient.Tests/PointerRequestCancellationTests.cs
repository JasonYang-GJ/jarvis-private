using ScreenGuide.DesktopClient.Services;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class PointerRequestCancellationTests
{
    [Fact]
    public async Task ClosingWaitsForHostCancellationEvenAfterConfirmCallbackHasReturned()
    {
        var guard = new PointerRequestCancellation();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await guard.BindAsync(async token => { Interlocked.Increment(ref calls); await acknowledged.Task.WaitAsync(token); }, default);
        var closing = guard.CancelAsync();
        Assert.False(closing.IsCompleted);
        var duplicate = guard.CancelAsync();
        acknowledged.SetResult();
        await Task.WhenAll(closing, duplicate);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LateSubmissionResponseCancelsItsExactTurnInsteadOfRebindingRevokedGesture()
    {
        var guard = new PointerRequestCancellation();
        using var operation = new CancellationTokenSource();
        operation.Cancel();
        var oldCount = 0;
        var newCount = 0;
        await guard.BindAsync(_ => { newCount++; return Task.CompletedTask; }, default);
        await guard.BindAsync(_ => { oldCount++; return Task.CompletedTask; }, operation.Token);
        Assert.Equal(1, oldCount);
        Assert.Equal(0, newCount);
        await guard.CancelAsync();
        Assert.Equal(1, newCount);
    }

    [Fact]
    public async Task FailedCancellationIsVisibleAndCanBeRetried()
    {
        var guard = new PointerRequestCancellation();
        var count = 0;
        await guard.BindAsync(_ => ++count == 1 ? Task.FromException(new IOException("fake IPC unavailable")) : Task.CompletedTask, default);
        await Assert.ThrowsAsync<IOException>(() => guard.CancelAsync());
        await guard.CancelAsync();
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task FailedLateReplyCancellationRemainsInTheWaitAndRetryChain()
    {
        var guard = new PointerRequestCancellation();
        var calls = 0;
        using var revoked = new CancellationTokenSource();
        revoked.Cancel();
        await Assert.ThrowsAsync<IOException>(() => guard.BindAsync(
            _ => ++calls == 1 ? Task.FromException(new IOException("fake lost acknowledgement")) : Task.CompletedTask,
            revoked.Token));
        await guard.CancelAsync();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailedOldCancellationAndNewBindingAreBothRetriedWithoutLosingEither()
    {
        var guard = new PointerRequestCancellation();
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCalls = 0;
        var newCalls = 0;
        await guard.BindAsync(_ => ++oldCalls == 1 ? failure.Task : Task.CompletedTask, default);
        var oldCancellation = guard.CancelAsync();
        await guard.BindAsync(_ => { newCalls++; return Task.CompletedTask; }, default);
        failure.SetException(new IOException("fake old IPC failure"));
        await Assert.ThrowsAsync<IOException>(() => oldCancellation);
        await guard.CancelAsync();
        Assert.Equal(2, oldCalls);
        Assert.Equal(1, newCalls);
    }
}
