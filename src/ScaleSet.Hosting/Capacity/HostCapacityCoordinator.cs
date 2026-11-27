using System.Collections.ObjectModel;
using System.Diagnostics;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.Scaling;

namespace ScaleSet.Hosting.Capacity;

/// <summary>
/// Thread-safe host-capacity ledger and deterministic multi-scale-set scheduler.
/// Planning is immutable; capacity is held only by an atomic reservation call.
/// </summary>
public sealed class HostCapacityCoordinator
{
    private const int MaximumRecentReleases = 4096;
    private readonly object _sync = new();
    private readonly HostCapacityLimits _limits;
    private readonly ScaleSetCapacityDefinition[] _definitions;
    private readonly Dictionary<string, ScaleSetCapacityDefinition> _definitionsByKey;
    private readonly Dictionary<CapacityOperationKey, Occupant> _occupants = new();
    private readonly Dictionary<CapacityOperationKey, ReleasedOperation> _released = new();
    private readonly LinkedList<CapacityOperationKey> _releaseOrder = new();
    private readonly Dictionary<Guid, CapacityOperationKey> _operationIds = new();
    private readonly Guid _coordinatorId = Guid.NewGuid();
    private long _version;
    private long _nextGeneration;
    private int _roundRobinCursor;

    /// <summary>Gets the process-wide demand and capacity-lease registry shared by all scale-set handlers.</summary>
    public ScaleSetDemandRegistry DemandRegistry { get; } = new();

    /// <summary>Creates a coordinator from capacity-neutral definitions.</summary>
    public HostCapacityCoordinator(
        HostCapacityLimits limits,
        IEnumerable<ScaleSetCapacityDefinition> scaleSets)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(scaleSets);

        _limits = limits;
        _definitions = scaleSets
            .Select(static definition => definition ?? throw new ArgumentException("A scale-set definition cannot be null."))
            .OrderBy(static definition => definition.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static definition => definition.Key, StringComparer.Ordinal)
            .ToArray();

        _definitionsByKey = new Dictionary<string, ScaleSetCapacityDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (ScaleSetCapacityDefinition definition in _definitions)
        {
            if (!_definitionsByKey.TryAdd(definition.Key, definition))
            {
                throw new ArgumentException($"Scale-set key '{definition.Key}' is configured more than once.", nameof(scaleSets));
            }
        }

        CreationConcurrencyGate = new CreationConcurrencyGate(limits.CreationConcurrency);
    }

    /// <summary>Creates a coordinator from the accepted autoscaler options.</summary>
    public HostCapacityCoordinator(HostCapacityOptions hostCapacity, IEnumerable<ScaleSetOptions> scaleSets)
        : this(
            HostCapacityLimits.FromOptions(hostCapacity),
            scaleSets?.Select(ScaleSetCapacityDefinition.FromOptions)
                ?? throw new ArgumentNullException(nameof(scaleSets)))
    {
    }

    /// <summary>Gets the host-wide creation gate shared by all configured scale sets.</summary>
    public CreationConcurrencyGate CreationConcurrencyGate { get; }

    internal ScaleSetCapacityDefinition GetDefinition(string scaleSetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scaleSetKey);
        return _definitionsByKey.TryGetValue(scaleSetKey, out ScaleSetCapacityDefinition? definition)
            ? definition
            : throw new KeyNotFoundException($"Scale set '{scaleSetKey}' is not configured in this capacity coordinator.");
    }

    /// <summary>Calculates feasible capacities without reserving any slots.</summary>
    /// <param name="assignedJobsByScaleSet">Latest assigned-job count per local scale-set key.</param>
    public CapacityPlan CreatePlan(IReadOnlyDictionary<string, int>? assignedJobsByScaleSet = null)
    {
        Dictionary<string, int> assignedJobs = CopyAndValidateAssignedJobs(assignedJobsByScaleSet);
        using Activity? activity = CapacityTelemetry.StartActivity("plan");

        lock (_sync)
        {
            HostCapacitySnapshot snapshot = CreateSnapshotLocked();
            List<ScaleSetDemand> demands = new(_definitions.Length);
            foreach (ScaleSetCapacityDefinition definition in _definitions)
            {
                int assigned = assignedJobs.GetValueOrDefault(definition.Key);
                int desired = ScalingDecision.GetDesiredRunnerCount(
                    definition.Policy,
                    assigned,
                    definition.MinimumRunners,
                    definition.MaximumRunners);
                long current = snapshot.ScaleSets.TryGetValue(definition.Key, out ScaleSetCapacityUsageSnapshot? setUsage)
                    ? setUsage.Usage.RunnerCount
                    : 0;
                long desiredAdditional = Math.Max(0L, (long)desired - current);
                long maximumAdditional = Math.Max(0L, (long)definition.MaximumRunners - current);
                demands.Add(new ScaleSetDemand(definition, assigned, desired, current, desiredAdditional, maximumAdditional));
            }

            int[] allocations = AllocateFeasibleCapacity(demands);
            long totalPermits = SumCounts(allocations);
            long totalFeasibleFleetSize = SaturatingAdd(snapshot.Usage.RunnerCount, totalPermits);
            int[] advertisedMaximums = AllocateAdvertisedMaximums(
                demands,
                allocations,
                snapshot,
                totalFeasibleFleetSize,
                IsHostCapacityExceeded(snapshot.Usage));
            Dictionary<string, ScaleSetCapacityPlan> setPlans = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < demands.Count; index++)
            {
                ScaleSetDemand demand = demands[index];
                int reservable = allocations[index];
                int advertised = advertisedMaximums[index];
                setPlans.Add(
                    demand.Definition.Key,
                    new ScaleSetCapacityPlan(
                        demand.Definition.Key,
                        demand.AssignedJobs,
                        demand.DesiredTotal,
                        demand.CurrentRunners,
                        demand.DesiredAdditional,
                        reservable,
                        advertised));
            }

            activity?.SetTag("outcome", "success");
            CapacityTelemetry.RecordCapacityOperation("plan", "success");
            return new CapacityPlan(_coordinatorId, _version, _roundRobinCursor, snapshot, setPlans);
        }
    }

    /// <summary>Returns a consistent snapshot of all adopted resources and active reservations.</summary>
    public HostCapacitySnapshot CaptureSnapshot()
    {
        using Activity? activity = CapacityTelemetry.StartActivity("snapshot");
        return ReadOccupancy();
    }

    internal HostCapacitySnapshot ReadOccupancy()
    {
        lock (_sync)
        {
            return CreateSnapshotLocked();
        }
    }

    /// <summary>Attempts to reserve one operation under a previously calculated plan.</summary>
    public CapacityReservationAttempt TryReserve(CapacityPlan plan, CapacityReservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CapacityReservationBatchAttempt batch = TryReserveBatch(plan, [request]);
        CapacityReservationLease? lease = batch.Leases.Count == 0 ? null : batch.Leases[0];
        return new CapacityReservationAttempt(batch.Status, lease?.Handle, lease?.Revision ?? 0);
    }

    /// <summary>
    /// Atomically reserves all new operations in the batch. A stale plan never reserves capacity;
    /// callers recalculate and retry after any concurrent ledger change.
    /// </summary>
    public CapacityReservationBatchAttempt TryReserveBatch(
        CapacityPlan plan,
        IEnumerable<CapacityReservationRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(requests);

        CapacityReservationRequest[] materialized = requests
            .Select(static request => request ?? throw new ArgumentException("A reservation request cannot be null."))
            .ToArray();
        using Activity? activity = CapacityTelemetry.StartActivity("reserve");

        lock (_sync)
        {
            CapacityReservationBatchAttempt Fail(CapacityReservationStatus status)
            {
                activity?.SetTag("outcome", ToOutcome(status));
                CapacityTelemetry.RecordCapacityOperation("reserve", ToOutcome(status));
                return new CapacityReservationBatchAttempt(status, Array.Empty<CapacityReservationLease>());
            }

            if (plan.CoordinatorId != _coordinatorId)
            {
                return Fail(CapacityReservationStatus.InvalidPlan);
            }

            if (materialized.Length == 0)
            {
                return new CapacityReservationBatchAttempt(
                    CapacityReservationStatus.AlreadyReserved,
                    Array.Empty<CapacityReservationLease>());
            }

            Dictionary<CapacityOperationKey, ReservationGroup> groups = new();
            CapacityOperationKey[] inputKeys = new CapacityOperationKey[materialized.Length];
            for (int index = 0; index < materialized.Length; index++)
            {
                CapacityReservationRequest request = materialized[index];
                CapacityOperationKey key = CapacityOperationKey.From(request.Identity);
                inputKeys[index] = key;

                if (!_definitionsByKey.TryGetValue(request.Identity.Scope.ScaleSetKey, out ScaleSetCapacityDefinition? definition)
                    || request.Resources != definition.RunnerResources)
                {
                    return Fail(CapacityReservationStatus.IdentityConflict);
                }

                if (_operationIds.TryGetValue(request.Identity.OperationId, out CapacityOperationKey operationKey)
                    && operationKey != key)
                {
                    return Fail(CapacityReservationStatus.IdentityConflict);
                }

                if (groups.TryGetValue(key, out ReservationGroup? existingGroup))
                {
                    if (!SameStableIdentity(existingGroup.Request.Identity, request.Identity)
                        || existingGroup.Request.Resources != request.Resources)
                    {
                        return Fail(CapacityReservationStatus.IdentityConflict);
                    }

                    continue;
                }

                ReservationGroup group = new(request);
                groups.Add(key, group);

                if (_occupants.TryGetValue(key, out Occupant? occupant))
                {
                    if (!SameStableIdentity(occupant.Identity, request.Identity)
                        || occupant.Resources != request.Resources)
                    {
                        return Fail(CapacityReservationStatus.IdentityConflict);
                    }

                    group.Existing = occupant;
                }
                else if (_released.TryGetValue(key, out ReleasedOperation? released))
                {
                    if (!SameStableIdentity(released.Identity, request.Identity)
                        || released.Resources != request.Resources)
                    {
                        return Fail(CapacityReservationStatus.IdentityConflict);
                    }

                    return Fail(CapacityReservationStatus.OperationAlreadyReleased);
                }
            }

            ReservationGroup[] newGroups = groups.Values.Where(static group => group.Existing is null).ToArray();
            if (newGroups.Length == 0)
            {
                CapacityReservationLease[] existingLeases = MapInputLeases(materialized.Length, inputKeys, groups);
                activity?.SetTag("outcome", "already_reserved");
                CapacityTelemetry.RecordCapacityOperation("reserve", "already_reserved");
                return new CapacityReservationBatchAttempt(CapacityReservationStatus.AlreadyReserved, existingLeases);
            }

            if (plan.StateVersion != _version)
            {
                return Fail(CapacityReservationStatus.StalePlan);
            }

            Dictionary<string, int> newCountBySet = new(StringComparer.OrdinalIgnoreCase);
            foreach (ReservationGroup group in newGroups)
            {
                string scaleSetKey = group.Request.Identity.Scope.ScaleSetKey;
                newCountBySet[scaleSetKey] = checked(newCountBySet.GetValueOrDefault(scaleSetKey) + 1);
            }

            foreach ((string scaleSetKey, int count) in newCountBySet)
            {
                if (!plan.ScaleSets.TryGetValue(scaleSetKey, out ScaleSetCapacityPlan? setPlan)
                    || count > setPlan.ReservableAdditionalRunners)
                {
                    return Fail(CapacityReservationStatus.InsufficientCapacity);
                }
            }

            CapacityResourceQuantities[] newResources = newGroups
                .Select(static group => group.Request.Resources)
                .ToArray();
            if (!CanFitBatch(CreateAggregate(_occupants.Values), newResources))
            {
                return Fail(CapacityReservationStatus.InsufficientCapacity);
            }

            if (!TryGetNextVersion(out long nextVersion)
                || newGroups.Length > long.MaxValue - _nextGeneration)
            {
                return Fail(CapacityReservationStatus.VersionExhausted);
            }

            int start = _definitions.Length == 0 ? 0 : plan.RoundRobinStartIndex % _definitions.Length;
            Dictionary<string, int> orderByKey = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < _definitions.Length; index++)
            {
                int absolute = (start + index) % _definitions.Length;
                orderByKey[_definitions[absolute].Key] = index;
            }

            ReservationGroup[] orderedNewGroups = newGroups
                .OrderBy(group => orderByKey[group.Request.Identity.Scope.ScaleSetKey])
                .ThenBy(group => group.Request.Identity.OperationId)
                .ToArray();

            foreach (ReservationGroup group in orderedNewGroups)
            {
                long generation = ++_nextGeneration;
                Occupant occupant = new(
                    group.Request.Identity,
                    group.Request.Resources,
                    CapacityOccupancyState.Reserved,
                    generation,
                    revision: 0);
                _occupants.Add(CapacityOperationKey.From(group.Request.Identity), occupant);
                _operationIds[group.Request.Identity.OperationId] = CapacityOperationKey.From(group.Request.Identity);
                group.Existing = occupant;
            }

            int lastSetIndex = _definitions.Length == 0
                ? 0
                : Array.FindIndex(_definitions, definition => StringComparer.OrdinalIgnoreCase.Equals(
                    definition.Key,
                    orderedNewGroups[^1].Request.Identity.Scope.ScaleSetKey));
            if (_definitions.Length > 0)
            {
                _roundRobinCursor = (lastSetIndex + 1) % _definitions.Length;
            }

            _version = nextVersion;
            CapacityReservationLease[] leases = MapInputLeases(materialized.Length, inputKeys, groups);
            activity?.SetTag("outcome", "reserved");
            CapacityTelemetry.RecordCapacityOperation("reserve", "reserved");
            return new CapacityReservationBatchAttempt(CapacityReservationStatus.Reserved, leases);
        }
    }

    /// <summary>Adopts an existing resource conservatively and idempotently.</summary>
    public CapacityInventoryAdoptionAttempt AdoptInventory(CapacityInventoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        using Activity? activity = CapacityTelemetry.StartActivity("inventory.adopt");

        lock (_sync)
        {
            CapacityOperationKey key = CapacityOperationKey.From(item.Identity);
            CapacityInventoryAdoptionAttempt Result(CapacityInventoryAdoptionStatus status, Occupant? occupant)
            {
                activity?.SetTag("outcome", ToOutcome(status));
                CapacityTelemetry.RecordCapacityOperation("inventory_adopt", ToOutcome(status));
                return new CapacityInventoryAdoptionAttempt(
                    status,
                    occupant is null ? null : CreateHandle(occupant),
                    occupant?.Revision ?? 0);
            }

            if (_operationIds.TryGetValue(item.Identity.OperationId, out CapacityOperationKey operationKey)
                && operationKey != key)
            {
                return Result(CapacityInventoryAdoptionStatus.IdentityConflict, null);
            }

            if (_occupants.TryGetValue(key, out Occupant? current))
            {
                if (!SameStableIdentity(current.Identity, item.Identity))
                {
                    return Result(CapacityInventoryAdoptionStatus.IdentityConflict, null);
                }

                CapacityResourceQuantities merged = CapacityResourceQuantities.MergeObservation(
                    current.Resources,
                    item.Resources,
                    out bool resourceConflict);
                bool changed = current.State != item.State
                    || current.Resources != merged;
                if (!changed)
                {
                    return Result(CapacityInventoryAdoptionStatus.AlreadyAdopted, current);
                }

                if (!TryIncrementRevision(current.Revision, out long newRevision)
                    || !TryGetNextVersion(out long nextVersion))
                {
                    return Result(CapacityInventoryAdoptionStatus.VersionExhausted, current);
                }

                current.Resources = merged;
                current.State = resourceConflict ? CapacityOccupancyState.Unknown : item.State;
                current.Revision = newRevision;
                _version = nextVersion;
                return Result(
                    resourceConflict ? CapacityInventoryAdoptionStatus.ResourceConflict : CapacityInventoryAdoptionStatus.Updated,
                    current);
            }

            if (_released.TryGetValue(key, out ReleasedOperation? released)
                && !SameStableIdentity(released.Identity, item.Identity))
            {
                return Result(CapacityInventoryAdoptionStatus.IdentityConflict, null);
            }

            if (_nextGeneration == long.MaxValue || !TryGetNextVersion(out long adoptedVersion))
            {
                return Result(CapacityInventoryAdoptionStatus.VersionExhausted, null);
            }

            CapacityResourceQuantities resources = item.Resources;
            bool hadConflict = false;
            RunnerIdentity adoptedIdentity = item.Identity;
            if (_released.TryGetValue(key, out released))
            {
                resources = CapacityResourceQuantities.MergeObservation(released.Resources, item.Resources, out hadConflict);
                adoptedIdentity = released.Identity;
                _releaseOrder.Remove(released.Order);
                _released.Remove(key);
            }

            Occupant adopted = new(
                adoptedIdentity,
                resources,
                hadConflict ? CapacityOccupancyState.Unknown : item.State,
                ++_nextGeneration,
                revision: 0);
            _occupants.Add(key, adopted);
            _operationIds[item.Identity.OperationId] = key;
            _version = adoptedVersion;
            return Result(
                hadConflict ? CapacityInventoryAdoptionStatus.ResourceConflict : CapacityInventoryAdoptionStatus.Adopted,
                adopted);
        }
    }

    /// <summary>Changes lifecycle intent while keeping the same capacity generation.</summary>
    public CapacityStateUpdateAttempt TryUpdateState(
        CapacityReservationHandle handle,
        long expectedRevision,
        CapacityOccupancyState state)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "The capacity state is not defined.");
        }

        using Activity? activity = CapacityTelemetry.StartActivity("state.update");
        lock (_sync)
        {
            CapacityOperationKey key = CapacityOperationKey.From(handle.Identity);
            if (!_occupants.TryGetValue(key, out Occupant? occupant))
            {
                CapacityStateUpdateStatus missingStatus = _released.TryGetValue(key, out ReleasedOperation? released)
                    && released.Generation == handle.Generation
                    && released.Revision == expectedRevision
                    && released.Resources == handle.Resources
                    && SameStableIdentity(released.Identity, handle.Identity)
                    ? CapacityStateUpdateStatus.AlreadyReleased
                    : CapacityStateUpdateStatus.StaleHandle;
                CapacityTelemetry.RecordCapacityOperation("state_update", ToOutcome(missingStatus));
                return new CapacityStateUpdateAttempt(missingStatus, 0);
            }

            if (!HandleMatches(occupant, handle))
            {
                CapacityTelemetry.RecordCapacityOperation("state_update", "stale_handle");
                return new CapacityStateUpdateAttempt(CapacityStateUpdateStatus.StaleHandle, occupant.Revision);
            }

            if (occupant.Revision != expectedRevision)
            {
                CapacityTelemetry.RecordCapacityOperation("state_update", "stale_revision");
                return new CapacityStateUpdateAttempt(CapacityStateUpdateStatus.StaleRevision, occupant.Revision);
            }

            if (occupant.State == state)
            {
                CapacityTelemetry.RecordCapacityOperation("state_update", "already_current");
                return new CapacityStateUpdateAttempt(CapacityStateUpdateStatus.AlreadyCurrent, occupant.Revision);
            }

            if (!TryIncrementRevision(occupant.Revision, out long nextRevision)
                || !TryGetNextVersion(out long nextVersion))
            {
                CapacityTelemetry.RecordCapacityOperation("state_update", "version_exhausted");
                return new CapacityStateUpdateAttempt(CapacityStateUpdateStatus.RevisionExhausted, occupant.Revision);
            }

            occupant.State = state;
            occupant.Revision = nextRevision;
            _version = nextVersion;
            CapacityTelemetry.RecordCapacityOperation("state_update", "updated");
            return new CapacityStateUpdateAttempt(CapacityStateUpdateStatus.Updated, occupant.Revision);
        }
    }

    /// <summary>Releases capacity only for the current handle generation and capacity revision.</summary>
    /// <remarks>Only recent release receipts are cached. Use the durable operation journal for replay protection.</remarks>
    public CapacityReleaseStatus TryRelease(CapacityReservationHandle handle, long expectedRevision) =>
        TryReleaseCore(handle, expectedRevision, retainTombstone: true);

    internal CapacityReleaseStatus ReleaseUnusedPermit(CapacityReservationHandle handle, long expectedRevision) =>
        TryReleaseCore(handle, expectedRevision, retainTombstone: false);

    private CapacityReleaseStatus TryReleaseCore(
        CapacityReservationHandle handle, long expectedRevision, bool retainTombstone)
    {
        ArgumentNullException.ThrowIfNull(handle);
        using Activity? activity = CapacityTelemetry.StartActivity("release");

        lock (_sync)
        {
            CapacityOperationKey key = CapacityOperationKey.From(handle.Identity);
            if (!_occupants.TryGetValue(key, out Occupant? occupant))
            {
                CapacityReleaseStatus missingStatus = _released.TryGetValue(key, out ReleasedOperation? released)
                    && released.Generation == handle.Generation
                    && released.Revision == expectedRevision
                    && released.Resources == handle.Resources
                    && SameStableIdentity(released.Identity, handle.Identity)
                    ? CapacityReleaseStatus.AlreadyReleased
                    : CapacityReleaseStatus.StaleHandle;
                CapacityTelemetry.RecordCapacityOperation("release", ToOutcome(missingStatus));
                return missingStatus;
            }

            if (!HandleMatches(occupant, handle))
            {
                CapacityTelemetry.RecordCapacityOperation("release", "stale_handle");
                return CapacityReleaseStatus.StaleHandle;
            }

            if (occupant.Revision != expectedRevision)
            {
                CapacityTelemetry.RecordCapacityOperation("release", "stale_revision");
                return CapacityReleaseStatus.StaleRevision;
            }

            if (!TryGetNextVersion(out long nextVersion))
            {
                CapacityTelemetry.RecordCapacityOperation("release", "version_exhausted");
                return CapacityReleaseStatus.VersionExhausted;
            }

            _occupants.Remove(key);
            if (retainTombstone)
            {
                _released.Add(key, new ReleasedOperation(
                    occupant.Identity, occupant.Resources, occupant.Generation, occupant.Revision, _releaseOrder.AddLast(key)));
                if (_released.Count > MaximumRecentReleases)
                {
                    CapacityOperationKey expired = _releaseOrder.First!.Value;
                    _releaseOrder.RemoveFirst();
                    _released.Remove(expired);
                    _operationIds.Remove(expired.OperationId);
                }
            }
            else
            {
                _operationIds.Remove(occupant.Identity.OperationId);
            }
            _version = nextVersion;
            CapacityTelemetry.RecordCapacityOperation("release", "released");
            return CapacityReleaseStatus.Released;
        }
    }

    private Dictionary<string, int> CopyAndValidateAssignedJobs(IReadOnlyDictionary<string, int>? assignedJobsByScaleSet)
    {
        Dictionary<string, int> result = new(StringComparer.OrdinalIgnoreCase);
        if (assignedJobsByScaleSet is null)
        {
            return result;
        }

        foreach ((string key, int count) in assignedJobsByScaleSet)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (!_definitionsByKey.ContainsKey(key))
            {
                throw new ArgumentException($"Scale set '{key}' is not configured.", nameof(assignedJobsByScaleSet));
            }

            if (!result.TryAdd(key, count))
            {
                throw new ArgumentException($"Scale-set key '{key}' appears more than once.", nameof(assignedJobsByScaleSet));
            }
        }

        return result;
    }

    private HostCapacitySnapshot CreateSnapshotLocked()
    {
        CapacityAccumulator total = new();
        Dictionary<string, CapacityAccumulator> byScaleSet = new(StringComparer.OrdinalIgnoreCase);
        foreach (Occupant occupant in _occupants.Values)
        {
            total.Add(occupant.Resources, occupant.State);
            string key = occupant.Identity.Scope.ScaleSetKey;
            if (!byScaleSet.TryGetValue(key, out CapacityAccumulator? accumulator))
            {
                accumulator = new CapacityAccumulator();
                byScaleSet.Add(key, accumulator);
            }

            accumulator.Add(occupant.Resources, occupant.State);
        }

        Dictionary<string, ScaleSetCapacityUsageSnapshot> setSnapshots = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, CapacityAccumulator accumulator) in byScaleSet)
        {
            setSnapshots.Add(key, new ScaleSetCapacityUsageSnapshot(key, accumulator.ToSnapshot()));
        }

        return new HostCapacitySnapshot(
            _version,
            total.ToSnapshot(),
            new ReadOnlyDictionary<string, ScaleSetCapacityUsageSnapshot>(setSnapshots),
            CreationConcurrencyGate.ActiveLeases,
            _limits.CreationConcurrency);
    }

    private int[] AllocateFeasibleCapacity(IReadOnlyList<ScaleSetDemand> demands)
    {
        int[] allocations = new int[demands.Count];
        if (demands.Count == 0)
        {
            return allocations;
        }

        long[] remaining = demands.Select(static demand => demand.MaximumAdditional).ToArray();
        CapacityAccumulator used = CreateAggregate(_occupants.Values);
        int[] order = new int[demands.Count];
        bool[] active = new bool[demands.Count];
        for (int index = 0; index < demands.Count; index++)
        {
            active[index] = remaining[index] > 0;
        }

        int start = _roundRobinCursor % demands.Count;
        while (true)
        {
            int orderedCount = 0;
            for (int offset = 0; offset < demands.Count; offset++)
            {
                int index = (start + offset) % demands.Count;
                if (active[index] && remaining[index] > 0 && CanFitOne(used, demands[index].Definition.RunnerResources))
                {
                    order[orderedCount++] = index;
                }
                else if (active[index])
                {
                    // Occupancy only increases during planning, so an infeasible request
                    // cannot become feasible until the next plan observes a release.
                    active[index] = false;
                }
            }

            if (orderedCount == 0)
            {
                break;
            }

            long fullRounds = remaining[order[0]];
            for (int offset = 1; offset < orderedCount; offset++)
            {
                fullRounds = Math.Min(fullRounds, remaining[order[offset]]);
            }

            if (_limits.MaxTotalRunners is int maxRunners)
            {
                long available = used.RunnerCount >= maxRunners ? 0 : maxRunners - used.RunnerCount;
                fullRounds = Math.Min(fullRounds, available / orderedCount);
            }

            if (_limits.CpuBudget is decimal cpuBudget)
            {
                if (!TrySumCpu(demands, order, orderedCount, out decimal cpuPerRound))
                {
                    fullRounds = 0;
                }
                else if (cpuPerRound > 0)
                {
                    if (used.UnknownCpuCount > 0 || used.CpuOverflowed || used.CpuUsed > cpuBudget)
                    {
                        fullRounds = 0;
                    }
                    else
                    {
                        decimal availableCpu = cpuBudget - used.CpuUsed;
                        long cpuLimitedRounds = FloorToLong(availableCpu / cpuPerRound);
                        if (cpuLimitedRounds > 0)
                        {
                            try
                            {
                                if (checked(cpuPerRound * cpuLimitedRounds) > availableCpu)
                                {
                                    cpuLimitedRounds--;
                                }
                            }
                            catch (OverflowException)
                            {
                                cpuLimitedRounds--;
                            }
                        }

                        fullRounds = Math.Min(fullRounds, cpuLimitedRounds);
                    }
                }
            }

            if (_limits.MemoryBudgetMiB is long memoryBudget)
            {
                if (!TrySumMemory(demands, order, orderedCount, out long memoryPerRound))
                {
                    fullRounds = 0;
                }
                else if (memoryPerRound > 0)
                {
                    if (used.UnknownMemoryCount > 0 || used.MemoryOverflowed || used.MemoryUsed > memoryBudget)
                    {
                        fullRounds = 0;
                    }
                    else
                    {
                        long availableMemory = memoryBudget - used.MemoryUsed;
                        fullRounds = Math.Min(fullRounds, availableMemory / memoryPerRound);
                    }
                }
            }

            if (fullRounds > 0)
            {
                foreach (int index in order.Take(orderedCount))
                {
                    allocations[index] = checked(allocations[index] + (int)fullRounds);
                    remaining[index] -= fullRounds;
                    used.Add(demands[index].Definition.RunnerResources, CapacityOccupancyState.Reserved, fullRounds);
                    if (remaining[index] == 0)
                    {
                        active[index] = false;
                    }
                }

                // A complete round returns the cursor to its starting position.
                continue;
            }

            bool madeProgress = false;
            for (int offset = 0; offset < orderedCount; offset++)
            {
                int index = order[offset];
                if (remaining[index] == 0 || !CanFitOne(used, demands[index].Definition.RunnerResources))
                {
                    active[index] = false;
                    continue;
                }

                allocations[index] = checked(allocations[index] + 1);
                remaining[index]--;
                used.Add(demands[index].Definition.RunnerResources, CapacityOccupancyState.Reserved, 1);
                start = (index + 1) % demands.Count;
                madeProgress = true;
                if (remaining[index] == 0)
                {
                    active[index] = false;
                }
            }

            if (!madeProgress)
            {
                break;
            }
        }

        return allocations;
    }

    private int[] AllocateAdvertisedMaximums(
        IReadOnlyList<ScaleSetDemand> demands,
        int[] permits,
        HostCapacitySnapshot snapshot,
        long totalFeasibleFleetSize,
        bool hostCapacityExceeded)
    {
        // Busy, Unknown, Removing, and Failed entries remain in fleet accounting but are
        // not free queue slots. Assigned jobs cover active work; known usable/pending entries
        // and feasible new permits supply the remaining safe queue capacity.
        int[] advertised = new int[demands.Count];
        if (hostCapacityExceeded || demands.Count == 0 || totalFeasibleFleetSize == 0)
        {
            return advertised;
        }

        long[] candidates = new long[demands.Count];
        for (int index = 0; index < demands.Count; index++)
        {
            ScaleSetDemand demand = demands[index];
            if (demand.Definition.MaximumRunners == 0)
            {
                continue;
            }

            long knownCapacity = snapshot.ScaleSets.TryGetValue(demand.Definition.Key, out ScaleSetCapacityUsageSnapshot? usage)
                ? GetKnownUsableAndPendingCount(usage.Usage)
                : 0;
            long candidate = SaturatingAdd(demand.AssignedJobs, knownCapacity);
            candidate = SaturatingAdd(candidate, permits[index]);
            candidates[index] = Math.Min((long)demand.Definition.MaximumRunners, candidate);
        }

        long allocatedTotal = 0;
        int start = _definitions.Length == 0 ? 0 : _roundRobinCursor % _definitions.Length;
        int[] order = new int[demands.Count];
        while (allocatedTotal < totalFeasibleFleetSize)
        {
            int orderCount = 0;
            for (int offset = 0; offset < demands.Count; offset++)
            {
                int index = (start + offset) % demands.Count;
                if (candidates[index] > advertised[index])
                {
                    order[orderCount++] = index;
                }
            }

            if (orderCount == 0)
            {
                break;
            }

            long fleetHeadroom = totalFeasibleFleetSize - allocatedTotal;
            long fullRounds = fleetHeadroom / orderCount;
            for (int offset = 0; offset < orderCount; offset++)
            {
                int index = order[offset];
                fullRounds = Math.Min(fullRounds, candidates[index] - advertised[index]);
            }

            if (fullRounds > 0)
            {
                foreach (int index in order.Take(orderCount))
                {
                    advertised[index] = checked(advertised[index] + (int)fullRounds);
                }

                allocatedTotal = checked(allocatedTotal + checked(fullRounds * orderCount));
                continue;
            }

            bool madeProgress = false;
            for (int offset = 0; offset < orderCount && allocatedTotal < totalFeasibleFleetSize; offset++)
            {
                int index = order[offset];
                if (advertised[index] >= candidates[index])
                {
                    continue;
                }

                advertised[index]++;
                allocatedTotal++;
                start = (index + 1) % demands.Count;
                madeProgress = true;
            }

            if (!madeProgress)
            {
                break;
            }
        }

        return advertised;
    }

    private bool IsHostCapacityExceeded(CapacityUsageSnapshot usage)
    {
        if (_limits.MaxTotalRunners is int maxTotalRunners
            && (maxTotalRunners == 0 || usage.RunnerCount > maxTotalRunners))
        {
            return true;
        }

        if (_limits.CpuBudget is decimal cpuBudget
            && (usage.CpuOverflowed || (usage.KnownCpuCoresUsed is decimal cpuUsed && cpuUsed > cpuBudget)))
        {
            return true;
        }

        return _limits.MemoryBudgetMiB is long memoryBudget
            && (usage.MemoryOverflowed || (usage.KnownMemoryMiBUsed is long memoryUsed && memoryUsed > memoryBudget));
    }

    private static long GetKnownUsableAndPendingCount(CapacityUsageSnapshot usage)
    {
        long count = 0;
        foreach (CapacityOccupancyState state in new[]
        {
            CapacityOccupancyState.Reserved,
            CapacityOccupancyState.JitIssued,
            CapacityOccupancyState.Creating,
            CapacityOccupancyState.Starting,
            CapacityOccupancyState.Idle,
        })
        {
            count = SaturatingAdd(count, usage.StateCounts.GetValueOrDefault(state));
        }

        return count;
    }

    private static long SumCounts(IReadOnlyList<int> counts)
    {
        long total = 0;
        foreach (int count in counts)
        {
            total = SaturatingAdd(total, count);
        }

        return total;
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private bool CanFitOne(CapacityAccumulator used, CapacityResourceQuantities resources)
    {
        if (_limits.MaxTotalRunners is int maxRunners && used.RunnerCount >= maxRunners)
        {
            return false;
        }

        if (_limits.CpuBudget is decimal cpuBudget && resources.CpuCores is decimal cpu)
        {
            if (cpu > 0 && (used.UnknownCpuCount > 0 || used.CpuOverflowed))
            {
                return false;
            }

            if (used.CpuUsed > cpuBudget || cpu > cpuBudget - used.CpuUsed)
            {
                return false;
            }
        }

        if (_limits.MemoryBudgetMiB is long memoryBudget && resources.MemoryMiB is long memory)
        {
            if (memory > 0 && (used.UnknownMemoryCount > 0 || used.MemoryOverflowed))
            {
                return false;
            }

            if (used.MemoryUsed > memoryBudget || memory > memoryBudget - used.MemoryUsed)
            {
                return false;
            }
        }

        return true;
    }

    private bool CanFitBatch(CapacityAccumulator used, CapacityResourceQuantities[] resources)
    {
        if (_limits.MaxTotalRunners is int maxRunners
            && (used.RunnerCount > maxRunners || resources.Length > maxRunners - used.RunnerCount))
        {
            return false;
        }

        if (_limits.CpuBudget is decimal cpuBudget)
        {
            decimal addedCpu = 0;
            try
            {
                foreach (CapacityResourceQuantities quantity in resources)
                {
                    if (quantity.CpuCores is not decimal cpu)
                    {
                        return false;
                    }

                    addedCpu = checked(addedCpu + cpu);
                }
            }
            catch (OverflowException)
            {
                return false;
            }

            if (addedCpu > 0 && (used.UnknownCpuCount > 0 || used.CpuOverflowed))
            {
                return false;
            }

            if (used.CpuUsed > cpuBudget || addedCpu > cpuBudget - used.CpuUsed)
            {
                return false;
            }
        }

        if (_limits.MemoryBudgetMiB is long memoryBudget)
        {
            long addedMemory = 0;
            try
            {
                foreach (CapacityResourceQuantities quantity in resources)
                {
                    if (quantity.MemoryMiB is not long memory)
                    {
                        return false;
                    }

                    addedMemory = checked(addedMemory + memory);
                }
            }
            catch (OverflowException)
            {
                return false;
            }

            if (addedMemory > 0 && (used.UnknownMemoryCount > 0 || used.MemoryOverflowed))
            {
                return false;
            }

            if (used.MemoryUsed > memoryBudget || addedMemory > memoryBudget - used.MemoryUsed)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TrySumCpu(
        IReadOnlyList<ScaleSetDemand> demands,
        int[] indices,
        int count,
        out decimal sum)
    {
        sum = 0;
        try
        {
            for (int offset = 0; offset < count; offset++)
            {
                sum = checked(sum + demands[indices[offset]].Definition.RunnerResources.CpuCores!.Value);
            }

            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TrySumMemory(
        IReadOnlyList<ScaleSetDemand> demands,
        int[] indices,
        int count,
        out long sum)
    {
        sum = 0;
        try
        {
            for (int offset = 0; offset < count; offset++)
            {
                sum = checked(sum + demands[indices[offset]].Definition.RunnerResources.MemoryMiB!.Value);
            }

            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static long FloorToLong(decimal value)
    {
        if (value <= 0)
        {
            return 0;
        }

        decimal floored = decimal.Floor(value);
        return floored >= long.MaxValue ? long.MaxValue : (long)floored;
    }

    private static CapacityAccumulator CreateAggregate(IEnumerable<Occupant> occupants)
    {
        CapacityAccumulator accumulator = new();
        foreach (Occupant occupant in occupants)
        {
            accumulator.Add(occupant.Resources, occupant.State);
        }

        return accumulator;
    }

    private CapacityReservationLease[] MapInputLeases(
        int inputCount,
        IReadOnlyList<CapacityOperationKey> inputKeys,
        IReadOnlyDictionary<CapacityOperationKey, ReservationGroup> groups)
    {
        CapacityReservationLease[] leases = new CapacityReservationLease[inputCount];
        for (int index = 0; index < inputCount; index++)
        {
            Occupant occupant = groups[inputKeys[index]].Existing
                ?? _occupants[inputKeys[index]];
            leases[index] = new CapacityReservationLease(CreateHandle(occupant), occupant.Revision);
        }

        return leases;
    }

    private static CapacityReservationHandle CreateHandle(Occupant occupant) =>
        new(occupant.Identity, occupant.Resources, occupant.Generation);

    private static bool HandleMatches(Occupant occupant, CapacityReservationHandle handle) =>
        occupant.Generation == handle.Generation
        && occupant.Resources == handle.Resources
        && SameStableIdentity(occupant.Identity, handle.Identity);

    private static bool SameStableIdentity(RunnerIdentity left, RunnerIdentity right) =>
        left.Scope == right.Scope
        && left.OperationId == right.OperationId
        && StringComparer.Ordinal.Equals(left.RunnerName, right.RunnerName);

    private static bool TryIncrementRevision(long current, out long next)
    {
        if (current == long.MaxValue)
        {
            next = current;
            return false;
        }

        next = current + 1;
        return true;
    }

    private bool TryGetNextVersion(out long next)
    {
        if (_version == long.MaxValue)
        {
            next = _version;
            return false;
        }

        next = _version + 1;
        return true;
    }

    private static string ToOutcome(CapacityReservationStatus status) => status switch
    {
        CapacityReservationStatus.Reserved => "reserved",
        CapacityReservationStatus.AlreadyReserved => "already_reserved",
        CapacityReservationStatus.StalePlan => "stale_plan",
        CapacityReservationStatus.InsufficientCapacity => "insufficient_capacity",
        CapacityReservationStatus.IdentityConflict => "identity_conflict",
        CapacityReservationStatus.OperationAlreadyReleased => "operation_already_released",
        CapacityReservationStatus.InvalidPlan => "invalid_plan",
        CapacityReservationStatus.VersionExhausted => "version_exhausted",
        _ => "other",
    };

    private static string ToOutcome(CapacityInventoryAdoptionStatus status) => status switch
    {
        CapacityInventoryAdoptionStatus.Adopted => "adopted",
        CapacityInventoryAdoptionStatus.AlreadyAdopted => "already_adopted",
        CapacityInventoryAdoptionStatus.Updated => "updated",
        CapacityInventoryAdoptionStatus.ResourceConflict => "resource_conflict",
        CapacityInventoryAdoptionStatus.IdentityConflict => "identity_conflict",
        CapacityInventoryAdoptionStatus.VersionExhausted => "version_exhausted",
        _ => "other",
    };

    private static string ToOutcome(CapacityStateUpdateStatus status) => status switch
    {
        CapacityStateUpdateStatus.Updated => "updated",
        CapacityStateUpdateStatus.AlreadyCurrent => "already_current",
        CapacityStateUpdateStatus.StaleHandle => "stale_handle",
        CapacityStateUpdateStatus.StaleRevision => "stale_revision",
        CapacityStateUpdateStatus.AlreadyReleased => "already_released",
        CapacityStateUpdateStatus.RevisionExhausted => "revision_exhausted",
        _ => "other",
    };

    private static string ToOutcome(CapacityReleaseStatus status) => status switch
    {
        CapacityReleaseStatus.Released => "released",
        CapacityReleaseStatus.AlreadyReleased => "already_released",
        CapacityReleaseStatus.StaleHandle => "stale_handle",
        CapacityReleaseStatus.StaleRevision => "stale_revision",
        CapacityReleaseStatus.VersionExhausted => "version_exhausted",
        _ => "other",
    };

    private sealed record ScaleSetDemand(
        ScaleSetCapacityDefinition Definition,
        int AssignedJobs,
        int DesiredTotal,
        long CurrentRunners,
        long DesiredAdditional,
        long MaximumAdditional);

    private sealed class ReservationGroup(CapacityReservationRequest request)
    {
        public CapacityReservationRequest Request { get; } = request;

        public Occupant? Existing { get; set; }
    }

    private sealed class Occupant(
        RunnerIdentity identity,
        CapacityResourceQuantities resources,
        CapacityOccupancyState state,
        long generation,
        long revision)
    {
        public RunnerIdentity Identity { get; set; } = identity;

        public CapacityResourceQuantities Resources { get; set; } = resources;

        public CapacityOccupancyState State { get; set; } = state;

        public long Generation { get; } = generation;

        public long Revision { get; set; } = revision;
    }

    private sealed record ReleasedOperation(
        RunnerIdentity Identity,
        CapacityResourceQuantities Resources,
        long Generation,
        long Revision,
        LinkedListNode<CapacityOperationKey> Order);

    private readonly record struct CapacityOperationKey(
        string ControllerId,
        string ScaleSetKey,
        long ScaleSetId,
        Guid OperationId)
    {
        public static CapacityOperationKey From(RunnerIdentity identity) => new(
            identity.Scope.ControllerId,
            identity.Scope.ScaleSetKey,
            identity.Scope.ScaleSetId,
            identity.OperationId);
    }

    private sealed class CapacityAccumulator
    {
        private readonly Dictionary<CapacityOccupancyState, long> _stateCounts =
            Enum.GetValues<CapacityOccupancyState>().ToDictionary(static state => state, static _ => 0L);

        public long RunnerCount { get; private set; }

        public decimal CpuUsed { get; private set; }

        public long MemoryUsed { get; private set; }

        public long UnknownCpuCount { get; private set; }

        public long UnknownMemoryCount { get; private set; }

        public bool CpuOverflowed { get; private set; }

        public bool MemoryOverflowed { get; private set; }

        public void Add(
            CapacityResourceQuantities resources,
            CapacityOccupancyState state,
            long count = 1)
        {
            RunnerCount = checked(RunnerCount + count);
            _stateCounts[state] = checked(_stateCounts.GetValueOrDefault(state) + count);
            AddResource(resources.CpuCores, count);
            AddMemory(resources.MemoryMiB, count);
        }

        public CapacityUsageSnapshot ToSnapshot()
        {
            Dictionary<CapacityOccupancyState, long> states = new(_stateCounts);
            return new CapacityUsageSnapshot(
                RunnerCount,
                UnknownCpuCount == 0 && !CpuOverflowed ? CpuUsed : null,
                UnknownMemoryCount == 0 && !MemoryOverflowed ? MemoryUsed : null,
                CpuOverflowed ? null : CpuUsed,
                MemoryOverflowed ? null : MemoryUsed,
                UnknownCpuCount,
                UnknownMemoryCount,
                CpuOverflowed,
                MemoryOverflowed,
                new ReadOnlyDictionary<CapacityOccupancyState, long>(states));
        }

        private void AddResource(decimal? cpu, long count)
        {
            if (!cpu.HasValue)
            {
                UnknownCpuCount = checked(UnknownCpuCount + count);
                return;
            }

            if (CpuOverflowed)
            {
                return;
            }

            try
            {
                CpuUsed = checked(CpuUsed + (cpu.Value * count));
            }
            catch (OverflowException)
            {
                CpuUsed = decimal.MaxValue;
                CpuOverflowed = true;
            }
        }

        private void AddMemory(long? memory, long count)
        {
            if (!memory.HasValue)
            {
                UnknownMemoryCount = checked(UnknownMemoryCount + count);
                return;
            }

            if (MemoryOverflowed)
            {
                return;
            }

            try
            {
                MemoryUsed = checked(MemoryUsed + checked(memory.Value * count));
            }
            catch (OverflowException)
            {
                MemoryUsed = long.MaxValue;
                MemoryOverflowed = true;
            }
        }
    }
}
