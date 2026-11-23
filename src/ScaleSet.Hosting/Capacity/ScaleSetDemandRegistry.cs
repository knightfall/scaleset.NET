using ScaleSet.Hosting.Providers;

namespace ScaleSet.Hosting.Capacity;

/// <summary>Thread-safe latest assigned-job observations shared by scale-set workers.</summary>
public sealed class ScaleSetDemandRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _assignedJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _idleRunners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, CapacityReservationLease> _capacityLeases = new();
    private readonly HashSet<Guid> _admissionPermits = [];

    /// <summary>
    /// Serializes demand updates, host-wide planning, and reservations shared
    /// by every scale-set message handler.
    /// </summary>
    public SemaphoreSlim PlanningGate { get; } = new(1, 1);

    /// <summary>Records the latest valid assigned-job count for a configured scale set.</summary>
    public void Update(string scaleSetKey, int assignedJobs, int? idleRunnerCount = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scaleSetKey);
        ArgumentOutOfRangeException.ThrowIfNegative(assignedJobs);
        if (idleRunnerCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(idleRunnerCount), idleRunnerCount, "The idle runner count must be zero or greater when supplied.");
        }

        lock (_sync)
        {
            _assignedJobs[scaleSetKey] = assignedJobs;
            if (idleRunnerCount is int idle)
            {
                _idleRunners[scaleSetKey] = idle;
            }
        }
    }

    /// <summary>Gets the latest count, or null until that set has reported statistics.</summary>
    public int? Get(string scaleSetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scaleSetKey);
        lock (_sync)
        {
            return _assignedJobs.TryGetValue(scaleSetKey, out int assignedJobs) ? assignedJobs : null;
        }
    }

    /// <summary>Gets the latest authoritative idle-runner count, or null until statistics arrive.</summary>
    public int? GetIdleRunnerCount(string scaleSetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scaleSetKey);
        lock (_sync)
        {
            return _idleRunners.TryGetValue(scaleSetKey, out int idleRunners) ? idleRunners : null;
        }
    }

    /// <summary>Copies all known counts for one consistent host-wide capacity plan input.</summary>
    public IReadOnlyDictionary<string, int> Snapshot()
    {
        lock (_sync)
        {
            return new Dictionary<string, int>(_assignedJobs, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Stores the current generation and revision for one capacity occupant.</summary>
    /// <remarks>
    /// Re-registering the same stable operation refreshes its lease. Reusing an
    /// operation ID for a different scope, name, or resource profile is rejected.
    /// </remarks>
    public void StoreCapacityLease(CapacityReservationLease lease, bool isAdmissionPermit = false)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(lease.Handle);

        Guid operationId = lease.Handle.Identity.OperationId;
        lock (_sync)
        {
            if (_capacityLeases.TryGetValue(operationId, out CapacityReservationLease? current)
                && !SameStableIdentity(current.Handle.Identity, lease.Handle.Identity))
            {
                throw new InvalidOperationException("A capacity lease operation ID is already associated with a different runner identity.");
            }

            if (current is null
                || lease.Handle.Generation > current.Handle.Generation
                || (lease.Handle.Generation == current.Handle.Generation && lease.Revision >= current.Revision))
            {
                _capacityLeases[operationId] = lease;
                if (isAdmissionPermit)
                {
                    _admissionPermits.Add(operationId);
                }
                else
                {
                    _admissionPermits.Remove(operationId);
                }
            }
        }
    }

    /// <summary>Releases the captured lease after the owner confirms cleanup. A stale lease cannot release a newer generation.</summary>
    /// <param name="coordinator">Coordinator that issued the lease.</param>
    /// <param name="lease">Lease captured before collecting the cleanup evidence.</param>
    public CapacityReleaseStatus TryReleaseConfirmedAbsent(HostCapacityCoordinator coordinator, CapacityReservationLease lease)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(lease);
        CapacityReleaseStatus status = coordinator.TryRelease(lease.Handle, lease.Revision);
        if (status is CapacityReleaseStatus.Released or CapacityReleaseStatus.AlreadyReleased)
        {
            _ = RemoveCapacityLease(lease.Handle.Identity.OperationId, lease);
        }

        return status;
    }

    /// <summary>Gets the latest registered generation and revision for an operation.</summary>
    public CapacityReservationLease? GetCapacityLease(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("The operation ID must not be empty.", nameof(operationId));
        }

        lock (_sync)
        {
            return _capacityLeases.TryGetValue(operationId, out CapacityReservationLease? lease) ? lease : null;
        }
    }

    /// <summary>Gets unmaterialized, host-capacity-backed queue permits for one scale set.</summary>
    public IReadOnlyList<CapacityReservationLease> GetAdmissionPermits(string scaleSetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scaleSetKey);
        lock (_sync)
        {
            return _admissionPermits
                .Select(operationId => _capacityLeases[operationId])
                .Where(lease => string.Equals(
                    lease.Handle.Identity.Scope.ScaleSetKey,
                    scaleSetKey,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(static lease => lease.Handle.Identity.OperationId)
                .ToArray();
        }
    }

    /// <summary>Marks a held admission permit as an actual provisioning operation.</summary>
    public bool MarkCapacityLeaseProvisioning(CapacityReservationLease expectedLease)
    {
        ArgumentNullException.ThrowIfNull(expectedLease);
        ArgumentNullException.ThrowIfNull(expectedLease.Handle);
        Guid operationId = expectedLease.Handle.Identity.OperationId;
        lock (_sync)
        {
            if (!_capacityLeases.TryGetValue(operationId, out CapacityReservationLease? current)
                || current.Revision != expectedLease.Revision
                || !SameLeaseHandle(current.Handle, expectedLease.Handle))
            {
                return false;
            }

            return _admissionPermits.Remove(operationId);
        }
    }

    /// <summary>
    /// Advances a registered lease revision if it still matches the supplied
    /// handle generation and expected revision.
    /// </summary>
    public bool TryUpdateCapacityLease(
        CapacityReservationLease expectedLease,
        long newRevision)
    {
        ArgumentNullException.ThrowIfNull(expectedLease);
        ArgumentNullException.ThrowIfNull(expectedLease.Handle);
        ArgumentOutOfRangeException.ThrowIfNegative(newRevision);
        if (newRevision < expectedLease.Revision)
        {
            throw new ArgumentOutOfRangeException(nameof(newRevision), newRevision, "A capacity lease revision cannot move backwards.");
        }

        Guid operationId = expectedLease.Handle.Identity.OperationId;
        lock (_sync)
        {
            if (!_capacityLeases.TryGetValue(operationId, out CapacityReservationLease? current)
                || current.Revision != expectedLease.Revision
                || !SameLeaseHandle(current.Handle, expectedLease.Handle))
            {
                return false;
            }

            _capacityLeases[operationId] = new CapacityReservationLease(current.Handle, newRevision);
            return true;
        }
    }

    /// <summary>Removes a capacity lease only when its generation and revision still match.</summary>
    public bool RemoveCapacityLease(Guid operationId, CapacityReservationLease expectedLease)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("The operation ID must not be empty.", nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(expectedLease);
        ArgumentNullException.ThrowIfNull(expectedLease.Handle);
        if (expectedLease.Handle.Identity.OperationId != operationId)
        {
            throw new ArgumentException("The expected capacity lease must match the operation ID.", nameof(expectedLease));
        }

        lock (_sync)
        {
            if (!_capacityLeases.TryGetValue(operationId, out CapacityReservationLease? current)
                || current.Revision != expectedLease.Revision
                || !SameLeaseHandle(current.Handle, expectedLease.Handle))
            {
                return false;
            }

            _admissionPermits.Remove(operationId);
            return _capacityLeases.Remove(operationId);
        }
    }

    private static bool SameStableIdentity(RunnerIdentity left, RunnerIdentity right) =>
        left.OperationId == right.OperationId
        && left.Scope == right.Scope
        && string.Equals(left.RunnerName, right.RunnerName, StringComparison.Ordinal);

    private static bool SameLeaseHandle(CapacityReservationHandle left, CapacityReservationHandle right) =>
        left.Generation == right.Generation
        && left.Resources == right.Resources
        && SameStableIdentity(left.Identity, right.Identity);
}
