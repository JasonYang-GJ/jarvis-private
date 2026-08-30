using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class SessionOperationGateRegistryTests
{
    [Fact]
    public async Task TenThousandUniqueSessionAndTurnKeysReturnToIdle()
    {
        var registry = new SessionOperationGateRegistry();

        for (var index = 0; index < 10_000; index++)
        {
            await using var sessionLease = await registry.AcquireSessionAsync(Guid.NewGuid());
            await using var turnLease = await registry.AcquireTurnAsync(Guid.NewGuid());
        }

        Assert.Equal(0, registry.SessionGateCount);
        Assert.Equal(0, registry.TurnGateCount);
    }

    [Fact]
    public async Task SameKeySerializesHoldersAndCountsWaitingReferences()
    {
        var registry = new SessionOperationGateRegistry();
        var sessionId = Guid.NewGuid();
        var first = await registry.AcquireSessionAsync(sessionId);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(async () =>
        {
            await using var lease = await registry.AcquireSessionAsync(sessionId);
            secondEntered.SetResult();
            await releaseSecond.Task;
        });

        Assert.True(SpinWait.SpinUntil(
            () => registry.GetSessionReferenceCount(sessionId) == 2,
            TimeSpan.FromSeconds(2)));
        Assert.False(secondEntered.Task.IsCompleted);

        await first.DisposeAsync();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseSecond.SetResult();
        await second;

        Assert.Equal(0, registry.SessionGateCount);
    }

    [Fact]
    public async Task DifferentKeysCanBeHeldInParallel()
    {
        var registry = new SessionOperationGateRegistry();
        await using var first = await registry.AcquireTurnAsync(Guid.NewGuid());

        var second = await registry.AcquireTurnAsync(Guid.NewGuid())
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, registry.TurnGateCount);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task CancelledWaiterRemovesItsReferenceWithoutDisturbingHolder()
    {
        var registry = new SessionOperationGateRegistry();
        var turnId = Guid.NewGuid();
        var holder = await registry.AcquireTurnAsync(turnId);
        using var cancellation = new CancellationTokenSource();
        var waiter = registry.AcquireTurnAsync(turnId, cancellation.Token).AsTask();

        Assert.True(SpinWait.SpinUntil(
            () => registry.GetTurnReferenceCount(turnId) == 2,
            TimeSpan.FromSeconds(2)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Equal(1, registry.GetTurnReferenceCount(turnId));
        await holder.DisposeAsync();
        Assert.Equal(0, registry.TurnGateCount);
    }

    [Fact]
    public async Task ExceptionInsideLeaseStillReturnsRegistryToIdle()
    {
        var registry = new SessionOperationGateRegistry();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var lease = await registry.AcquireSessionAsync(Guid.NewGuid());
            throw new InvalidOperationException("test-only");
        });

        Assert.Equal(0, registry.SessionGateCount);
    }

    [Fact]
    public async Task DuplicateReleaseIsIdempotent()
    {
        var registry = new SessionOperationGateRegistry();
        var lease = await registry.AcquireTurnAsync(Guid.NewGuid());

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(0, registry.TurnGateCount);
    }

    [Fact]
    public async Task ReleaseAcquireRaceNeverCreatesASecondEffectiveGateForTheSameKey()
    {
        var registry = new SessionOperationGateRegistry();
        var sessionId = Guid.NewGuid();

        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            var first = await registry.AcquireSessionAsync(sessionId);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = Task.Run(async () =>
            {
                await start.Task;
                await first.DisposeAsync();
            });
            var acquire = Task.Run(async () =>
            {
                await start.Task;
                return await registry.AcquireSessionAsync(sessionId);
            });

            start.SetResult();
            await release;
            var second = await acquire;
            var third = registry.AcquireSessionAsync(sessionId).AsTask();
            Assert.True(SpinWait.SpinUntil(
                () => registry.GetSessionReferenceCount(sessionId) == 2,
                TimeSpan.FromSeconds(2)));
            Assert.False(third.IsCompleted);

            await second.DisposeAsync();
            var thirdLease = await third.WaitAsync(TimeSpan.FromSeconds(2));
            await thirdLease.DisposeAsync();
            Assert.Equal(0, registry.SessionGateCount);
        }
    }
}
