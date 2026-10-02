using ScaleSet.Hosting.Capacity;

namespace ScaleSet.Hosting.Tests.Capacity;

public sealed class CreationConcurrencyGateTests
{
    [Fact]
    public async Task AcquireAsync_CanceledWaiterDoesNotConsumePermit()
    {
        CreationConcurrencyGate gate = new(maximumLeases: 1);
        CreationConcurrencyLease first = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        using CancellationTokenSource cancellation = new();
        Task<CreationConcurrencyLease> waiting = gate.AcquireAsync(cancellation.Token).AsTask();

        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, gate.ActiveLeases);

        first.Dispose();
        await using CreationConcurrencyLease next = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, gate.ActiveLeases);
        await next.DisposeAsync();
        Assert.Equal(0, gate.ActiveLeases);
    }

    [Fact]
    public async Task AcquireAsync_BoundsConcurrentCreationsAndDisposalIsIdempotent()
    {
        CreationConcurrencyGate gate = new(maximumLeases: 2);
        CreationConcurrencyLease first = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        CreationConcurrencyLease second = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        Task<CreationConcurrencyLease> thirdTask = gate.AcquireAsync(TestContext.Current.CancellationToken).AsTask();

        Assert.Equal(2, gate.ActiveLeases);
        Assert.False(thirdTask.IsCompleted);

        first.Dispose();
        await using CreationConcurrencyLease third = await thirdTask;
        Assert.Equal(2, gate.ActiveLeases);
        first.Dispose();
        second.Dispose();
        await third.DisposeAsync();
        Assert.Equal(0, gate.ActiveLeases);
    }
}
