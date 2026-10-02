using System.Collections.ObjectModel;

namespace ScaleSet.Hosting.Capacity;

/// <summary>
/// Pure feasible-capacity result for one scale set. Desired capacity is the scaling-policy
/// target; advertised capacity is the safe queue-header maximum for this plan.
/// </summary>
public sealed record ScaleSetCapacityPlan(
    string ScaleSetKey,
    int AssignedJobs,
    int DesiredTotalRunners,
    long CurrentRunners,
    long DesiredAdditionalRunners,
    int ReservableAdditionalRunners,
    int AdvertisedMaximum);

/// <summary>
/// Immutable capacity calculation tied to the coordinator version from which it was planned.
/// A plan alone does not reserve capacity; callers must commit through the coordinator.
/// </summary>
public sealed class CapacityPlan
{
    internal CapacityPlan(
        Guid coordinatorId,
        long stateVersion,
        int roundRobinStartIndex,
        HostCapacitySnapshot snapshot,
        IDictionary<string, ScaleSetCapacityPlan> scaleSets)
    {
        CoordinatorId = coordinatorId;
        StateVersion = stateVersion;
        RoundRobinStartIndex = roundRobinStartIndex;
        Snapshot = snapshot;
        ScaleSets = new ReadOnlyDictionary<string, ScaleSetCapacityPlan>(
            new Dictionary<string, ScaleSetCapacityPlan>(scaleSets, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Gets the capacity-ledger version used by the calculation.</summary>
    public long StateVersion { get; }

    /// <summary>Gets the inventory snapshot used by the calculation.</summary>
    public HostCapacitySnapshot Snapshot { get; }

    /// <summary>Gets all configured scale-set capacity results by local key.</summary>
    public IReadOnlyDictionary<string, ScaleSetCapacityPlan> ScaleSets { get; }

    /// <summary>Gets the result for a configured scale set.</summary>
    public ScaleSetCapacityPlan GetScaleSet(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return ScaleSets.TryGetValue(key, out ScaleSetCapacityPlan? capacity)
            ? capacity
            : throw new KeyNotFoundException($"Scale set '{key}' is not part of this capacity plan.");
    }

    internal Guid CoordinatorId { get; }

    internal int RoundRobinStartIndex { get; }
}
