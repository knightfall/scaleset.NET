using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;

namespace ScaleSet.Hosting.Tests.State;

public sealed class RunnerOperationStateTests
{
    [Fact]
    public void Constructor_RejectsUndefinedStageAndNegativeRevision()
    {
        RunnerIdentity identity = CreateIdentity();

        Assert.Throws<ArgumentOutOfRangeException>(() => new RunnerOperationState(
            identity,
            (RunnerLifecycleStage)99,
            0,
            DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunnerOperationState(
            identity,
            RunnerLifecycleStage.Reserved,
            -1,
            DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Constructor_RejectsEmptyResourceIdAndUndefinedProviderEvidence()
    {
        RunnerIdentity identity = CreateIdentity();

        Assert.Throws<ArgumentException>(() => new RunnerOperationState(
            identity,
            RunnerLifecycleStage.Starting,
            0,
            DateTimeOffset.UnixEpoch,
            providerResourceId: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunnerOperationState(
            identity,
            RunnerLifecycleStage.Unknown,
            0,
            DateTimeOffset.UnixEpoch,
            lastObservedProviderStatus: (RunnerResourceStatus)99));
    }

    [Fact]
    public void Constructor_NormalizesUpdateTimeToUtc()
    {
        DateTimeOffset localOffset = new(2026, 10, 2, 12, 0, 0, TimeSpan.FromHours(10));

        RunnerOperationState state = new(
            CreateIdentity(),
            RunnerLifecycleStage.Reserved,
            0,
            localOffset);

        Assert.Equal(TimeSpan.Zero, state.UpdatedAt.Offset);
        Assert.Equal(localOffset.ToUniversalTime(), state.UpdatedAt);
    }

    private static RunnerIdentity CreateIdentity() =>
        new(new RunnerOwnershipScope("controller-a", "production", 17), Guid.NewGuid(), "runner-7");
}
