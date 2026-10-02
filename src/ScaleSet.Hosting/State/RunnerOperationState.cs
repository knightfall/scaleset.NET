using ScaleSet.Hosting.Providers;

namespace ScaleSet.Hosting.State;

/// <summary>Non-secret lifecycle stages for one stable runner operation.</summary>
public enum RunnerLifecycleStage
{
    Reserved = 0,
    JitIssued = 1,
    Creating = 2,
    Starting = 3,
    Idle = 4,
    Busy = 5,
    Completed = 6,
    Removing = 7,
    Gone = 8,
    Failed = 9,
    Unknown = 10,
}

/// <summary>Authoritative activity evidence that can resolve a runner's job state.</summary>
public enum RunnerActivityEvidence
{
    ConfirmedIdle = 0,
    JobAssigned = 1,
    JobCompleted = 2,
}

/// <summary>Durable, non-secret state for one runner operation.</summary>
public sealed record RunnerOperationState
{
    public RunnerOperationState(
        RunnerIdentity identity,
        RunnerLifecycleStage stage,
        long revision,
        DateTimeOffset updatedAt,
        string? providerResourceId = null,
        RunnerResourceStatus? lastObservedProviderStatus = null)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "The lifecycle stage is not defined.");
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "The revision must not be negative.");
        }

        if (providerResourceId is not null && string.IsNullOrWhiteSpace(providerResourceId))
        {
            throw new ArgumentException("The provider resource ID must be null or non-empty.", nameof(providerResourceId));
        }

        if (lastObservedProviderStatus is not null && !Enum.IsDefined(lastObservedProviderStatus.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(lastObservedProviderStatus), lastObservedProviderStatus, "The provider status is not defined.");
        }

        Identity = identity;
        Stage = stage;
        Revision = revision;
        UpdatedAt = updatedAt.ToUniversalTime();
        ProviderResourceId = providerResourceId;
        LastObservedProviderStatus = lastObservedProviderStatus;
    }

    public RunnerIdentity Identity { get; }

    public RunnerLifecycleStage Stage { get; }

    /// <summary>Gets the monotonically increasing per-operation journal revision.</summary>
    public long Revision { get; }

    public DateTimeOffset UpdatedAt { get; }

    public string? ProviderResourceId { get; }

    public RunnerResourceStatus? LastObservedProviderStatus { get; }
}
