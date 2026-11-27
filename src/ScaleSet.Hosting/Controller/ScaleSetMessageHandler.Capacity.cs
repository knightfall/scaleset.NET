using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using ScaleSet.Client;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;

namespace ScaleSet.Hosting.Controller;

public sealed partial class ScaleSetMessageHandler
{
    private const int MaximumStalePlanAttempts = 3;
    private int _lastAdvertisedMaximum = -1;

    private async Task HandleCapacityAwareAsync(
        RunnerScaleSetMessage? message,
        CancellationToken cancellationToken)
    {
        ScaleSetCapacityContext capacity = _capacity;
        bool admitted = TryEnterAdmission(out IDisposable? admissionLease);
        using (admissionLease)
        {
            RunnerScaleSetStatistic? statistics = message?.Statistics;
            long[] offers = admitted && statistics is not null && message is not null
                ? GetUnacquiredOffers(message.JobAvailableMessages)
                : Array.Empty<long>();

            bool primaryFailed = false;
            try
            {
                CapacityReservationLease[] reservations;
                int maximumAcquisitions;
                await capacity.DemandRegistry.PlanningGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (statistics is not null)
                    {
                        capacity.DemandRegistry.Update(
                            _scope.ScaleSetKey,
                            statistics.TotalAssignedJobs,
                            statistics.TotalIdleRunners);
                    }

                    if (message is not null)
                    {
                        RecordAssignments(message.JobAssignedMessages);
                    }

                    (reservations, maximumAcquisitions) = PlanAndReserveCapacity(capacity, admitted, offers.Length);
                }
                finally
                {
                    capacity.DemandRegistry.PlanningGate.Release();
                }

                // Match the external listener contract: acquire offered work before
                // consuming activity events, and only while this callback has a
                // fresh statistics snapshot and the controller admits new work.
                // Capacity reservations above are internal permits; JIT generation
                // and provider work remain after acquisition and activity handling.
                if (admitted && statistics is not null && message is not null)
                {
                    int availableRequests = Math.Min(maximumAcquisitions, GetAvailableAdmissionCount(statistics.TotalAssignedJobs));
                    await AcquireCapacityJobsAsync(
                        offers,
                        availableRequests,
                        cancellationToken).ConfigureAwait(false);
                }

                if (message is not null)
                {
                    RecordAssignments(message.JobAssignedMessages);
                    foreach (JobAssignedMessage assigned in message.JobAssignedMessages)
                    {
                        LogJobAssignedWithoutRunner(_logger, assigned.RunnerRequestId, _scope.ScaleSetKey);
                    }

                    foreach (JobStartedMessage started in message.JobStartedMessages)
                    {
                        await ApplyCapacityActivityAsync(
                            started.RunnerRequestId,
                            started.RunnerId,
                            started.RunnerName,
                            RunnerActivityEvidence.JobAssigned,
                            cancellationToken).ConfigureAwait(false);
                    }

                    foreach (JobCompletedMessage completed in message.JobCompletedMessages)
                    {
                        await ApplyCapacityActivityAsync(
                            completed.RunnerRequestId,
                            completed.RunnerId,
                            completed.RunnerName,
                            RunnerActivityEvidence.JobCompleted,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                await ProvisionCapacityReservationsAsync(capacity, reservations, cancellationToken).ConfigureAwait(false);
                await ReconcileCompletedOperationsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                primaryFailed = true;
                throw;
            }
            finally
            {
                if (admitted)
                {
                    admissionLease?.Dispose();
                    FinishAdmission(primaryFailed);
                }
            }
        }
    }

    private async Task AcquireCapacityJobsAsync(
        IReadOnlyList<long> offers,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount <= 0)
        {
            return;
        }

        long[] offeredIds = offers.Take(maximumCount).ToArray();
        if (offeredIds.Length == 0)
        {
            return;
        }

        IReadOnlyList<long> response = await _session.AcquireJobsAsync(offeredIds, cancellationToken).ConfigureAwait(false);
        HashSet<long> offered = offeredIds.ToHashSet();
        long[] acquiredIds = (response ?? Array.Empty<long>())
            .Where(offered.Contains)
            .Distinct()
            .ToArray();
        RecordAcquiredRequests(acquiredIds);
    }

    private (CapacityReservationLease[] Reservations, int MaximumAcquisitions) PlanAndReserveCapacity(
        ScaleSetCapacityContext capacity,
        bool admitted,
        int offeredJobCount)
    {
        for (int attempt = 0; attempt < MaximumStalePlanAttempts; attempt++)
        {
            CapacityPlan plan = capacity.Coordinator.CreatePlan(capacity.DemandRegistry.Snapshot());
            ScaleSetCapacityPlan scaleSetPlan = plan.GetScaleSet(_scope.ScaleSetKey);
            if (!admitted)
            {
                SetListenerMaximumIfChanged(capacity, 0);
                return ([], 0);
            }

            long existingPermits = capacity.DemandRegistry.GetAdmissionPermits(_scope.ScaleSetKey).Count;
            long actualRunners = Math.Max(0, scaleSetPlan.CurrentRunners - existingPermits);
            long desiredToProvision = Math.Max(0L, (long)scaleSetPlan.DesiredTotalRunners - actualRunners);
            long knownCapacity = GetKnownCapacityCount(plan, _scope.ScaleSetKey);
            long currentlyAdvertisedRequests = Math.Max(0L, (long)scaleSetPlan.AdvertisedMaximum - scaleSetPlan.AssignedJobs);
            long offerCapacity = Math.Min(offeredJobCount, currentlyAdvertisedRequests);
            long uncommittedDemand = GetUncommittedDemand(plan, scaleSetPlan.AssignedJobs);
            long knownFreeRequests = Math.Max(0L, knownCapacity - uncommittedDemand);
            long permitsNeededForOffers = offerCapacity == 0
                ? 0
                : Math.Max(0L, uncommittedDemand + offerCapacity - knownCapacity);
            long permitsNeededForDemand = Math.Max(0L, desiredToProvision - existingPermits);
            long requestedCount = Math.Min(
                scaleSetPlan.ReservableAdditionalRunners,
                Math.Max(permitsNeededForOffers, permitsNeededForDemand));

            if (requestedCount <= 0)
            {
                SetListenerMaximumIfChanged(capacity, scaleSetPlan.AdvertisedMaximum);
                return (SelectPermitsToProvision(capacity, desiredToProvision),
                    checked((int)Math.Min(offerCapacity, knownFreeRequests)));
            }

            int reservationCount = checked((int)requestedCount);
            CapacityReservationRequest[] requests = CreateCapacityReservations(capacity, reservationCount);
            CapacityReservationBatchAttempt result = capacity.Coordinator.TryReserveBatch(plan, requests);
            if (result.Status == CapacityReservationStatus.StalePlan)
            {
                continue;
            }

            if (result.Status is not (CapacityReservationStatus.Reserved or CapacityReservationStatus.AlreadyReserved))
            {
                throw new InvalidOperationException(
                    $"The host capacity coordinator could not reserve runners for scale set '{_scope.ScaleSetKey}': {result.Status}.");
            }

            foreach (CapacityReservationLease lease in result.Leases)
            {
                capacity.DemandRegistry.StoreCapacityLease(lease, isAdmissionPermit: true);
            }

            CapacityPlan updatedPlan = capacity.Coordinator.CreatePlan(capacity.DemandRegistry.Snapshot());
            SetListenerMaximumIfChanged(
                capacity,
                updatedPlan.GetScaleSet(_scope.ScaleSetKey).AdvertisedMaximum);
            long updatedFreeRequests = Math.Max(0L, GetKnownCapacityCount(updatedPlan, _scope.ScaleSetKey)
                - GetUncommittedDemand(updatedPlan, scaleSetPlan.AssignedJobs));
            return (SelectPermitsToProvision(capacity, desiredToProvision),
                checked((int)Math.Min(offerCapacity, updatedFreeRequests)));
        }

        LogStalePlan(_logger, _scope.ScaleSetKey, MaximumStalePlanAttempts);
        throw new InvalidOperationException(
            $"The host capacity plan remained stale after {MaximumStalePlanAttempts} reservation attempts for scale set '{_scope.ScaleSetKey}'.");
    }

    private void FinishAdmission(bool primaryFailed)
    {
        SemaphoreSlim gate = _capacity.DemandRegistry.PlanningGate;
        // After primary work completes, gate contention or shutdown must not prevent acknowledgment.
        if (!gate.Wait(0))
        {
            lock (_drainSync)
            {
                _admissionTrimPending = true;
                StartDeferredSettlementUnderLock();
            }

            return;
        }

        try
        {
            lock (_drainSync)
            {
                _admissionTrimPending = true;
                SettleAdmissionTrimUnderGate();
            }
        }
        catch (Exception exception) when (primaryFailed)
        {
            LogAdmissionTrimFailed(_logger, _scope.ScaleSetKey, exception.GetType().Name);
        }
        finally
        {
            gate.Release();
        }
    }

    private void TrimUnneededAdmissionPermitsUnderGate()
    {
        var registry = _capacity.DemandRegistry;
        IReadOnlyList<CapacityReservationLease> permits = registry.GetAdmissionPermits(_scope.ScaleSetKey);
        if (permits.Count == 0)
        {
            return;
        }

        int needed = 0;
        if (!_drainStarted)
        {
            CapacityPlan plan = _capacity.Coordinator.CreatePlan(registry.Snapshot());
            long materializedCapacity = Math.Max(0, GetKnownCapacityCount(plan, _scope.ScaleSetKey) - permits.Count);
            long demand = GetUncommittedDemand(plan, plan.GetScaleSet(_scope.ScaleSetKey).AssignedJobs);
            needed = (int)Math.Clamp(demand - materializedCapacity, 0, permits.Count);
        }

        foreach (CapacityReservationLease permit in permits.Skip(needed))
        {
            ReleaseKnownUnusedPermit(permit, retainTombstone: false);
        }
    }

    private long GetUncommittedDemand(CapacityPlan plan, int assignedJobs)
    {
        long busyRunners = plan.Snapshot.ScaleSets.TryGetValue(_scope.ScaleSetKey, out var occupancy)
            ? occupancy.Usage.StateCounts.GetValueOrDefault(CapacityOccupancyState.Busy)
            : 0;
        long uncommittedAssignments = Math.Max(0L, assignedJobs - busyRunners);
        lock (_drainSync)
        {
            // Only a matching assignment event proves that an acquisition is already in aggregate demand.
            long overlap = Math.Min(uncommittedAssignments, _assignedAcquiredRequests.Count);
            return uncommittedAssignments + _pendingAcquiredRequests.Count - overlap;
        }
    }

    private void ReleaseKnownUnusedPermit(CapacityReservationLease initialLease, bool retainTombstone)
    {
        var registry = _capacity.DemandRegistry;
        Guid operationId = initialLease.Handle.Identity.OperationId;
        CapacityReservationLease? previousAttempt = null;
        for (int attempt = 0; attempt < MaximumStalePlanAttempts; attempt++)
        {
            CapacityReservationLease? lease = registry.GetCapacityLease(operationId);
            if (lease is null || lease.Handle.Generation != initialLease.Handle.Generation)
            {
                return;
            }

            if (lease == previousAttempt)
            {
                break;
            }

            CapacityReleaseStatus status = retainTombstone
                ? registry.TryReleaseConfirmedAbsent(_capacity.Coordinator, lease)
                : registry.ReleaseUnusedPermit(_capacity.Coordinator, lease);
            if (status is CapacityReleaseStatus.Released or CapacityReleaseStatus.AlreadyReleased)
            {
                return;
            }

            if (status is not (CapacityReleaseStatus.StaleHandle or CapacityReleaseStatus.StaleRevision))
            {
                throw new InvalidOperationException($"The capacity ledger could not release an unused reservation: {status}.");
            }

            previousAttempt = lease;
        }

        throw new InvalidOperationException("The capacity lease changed without an available current revision.");
    }

    private CapacityReservationLease[] SelectPermitsToProvision(
        ScaleSetCapacityContext capacity,
        long desiredToProvision)
    {
        if (desiredToProvision <= 0)
        {
            return Array.Empty<CapacityReservationLease>();
        }

        return capacity.DemandRegistry.GetAdmissionPermits(_scope.ScaleSetKey)
            .Take(checked((int)Math.Min(desiredToProvision, int.MaxValue)))
            .ToArray();
    }

    private static long GetKnownCapacityCount(CapacityPlan plan, string scaleSetKey)
    {
        if (!plan.Snapshot.ScaleSets.TryGetValue(scaleSetKey, out ScaleSetCapacityUsageSnapshot? scaleSet))
        {
            return 0;
        }

        CapacityUsageSnapshot usage = scaleSet.Usage;
        return usage.StateCounts.GetValueOrDefault(CapacityOccupancyState.Reserved)
            + usage.StateCounts.GetValueOrDefault(CapacityOccupancyState.JitIssued)
            + usage.StateCounts.GetValueOrDefault(CapacityOccupancyState.Creating)
            + usage.StateCounts.GetValueOrDefault(CapacityOccupancyState.Starting)
            + usage.StateCounts.GetValueOrDefault(CapacityOccupancyState.Idle);
    }

    private int GetAvailableAdmissionCount(int assignedJobs)
    {
        int currentMaximum = Volatile.Read(ref _lastAdvertisedMaximum);
        return currentMaximum <= assignedJobs ? 0 : currentMaximum - assignedJobs;
    }

    private CapacityReservationRequest[] CreateCapacityReservations(
        ScaleSetCapacityContext capacity,
        int count)
    {
        CapacityReservationRequest[] requests = new CapacityReservationRequest[count];
        HashSet<Guid> operationIds = new();
        for (int index = 0; index < count; index++)
        {
            Guid operationId;
            do
            {
                operationId = Guid.NewGuid();
            }
            while (operationId == Guid.Empty || !operationIds.Add(operationId));

            RunnerIdentity identity = new(
                _scope,
                operationId,
                $"ss-{operationId:N}",
                capacityResources: capacity.Resources);
            requests[index] = new CapacityReservationRequest(identity, capacity.Resources);
        }

        return requests;
    }

    private async Task ProvisionCapacityReservationsAsync(
        ScaleSetCapacityContext capacity,
        IReadOnlyList<CapacityReservationLease> reservations,
        CancellationToken cancellationToken)
    {
        Exception? firstFailure = null;
        foreach (CapacityReservationLease reservation in reservations)
        {
            bool invokedProvisioner = false;
            try
            {
                _ = capacity.DemandRegistry.MarkCapacityLeaseProvisioning(reservation);
                await using CreationConcurrencyLease creationLease =
                    await capacity.Coordinator.CreationConcurrencyGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
                SetCapacityLeaseState(capacity, reservation, CapacityOccupancyState.JitIssued);
                invokedProvisioner = true;
                RunnerProvisioningResult result = await _provisioner.ProvisionAsync(
                    reservation.Handle.Identity,
                    cancellationToken).ConfigureAwait(false);
                SetCapacityLeaseState(capacity, reservation, result.Stage == RunnerLifecycleStage.Starting
                    ? CapacityOccupancyState.Starting
                    : CapacityOccupancyState.Unknown);
            }
            catch (Exception exception)
            {
                try
                {
                    RunnerOperationState? durable = invokedProvisioner
                        ? await _journal.FindAsync(reservation.Handle.Identity.OperationId, CancellationToken.None).ConfigureAwait(false)
                        : null;
                    if (durable is null or { Stage: RunnerLifecycleStage.Failed, ProviderResourceId: null })
                    {
                        ReleaseOrDeferUnusedPermit(reservation, retainTombstone: durable is not null);
                    }
                    else
                    {
                        SetCapacityLeaseState(capacity, reservation, CapacityOccupancyState.Unknown);
                    }
                }
                catch (Exception stateException)
                {
                    LogCapacityStateUpdateFailed(
                        _logger,
                        reservation.Handle.Identity.OperationId,
                        _scope.ScaleSetKey,
                        stateException.GetType().Name);
                }

                firstFailure ??= exception;
            }
        }

        if (firstFailure is not null)
        {
            ExceptionDispatchInfo.Throw(firstFailure);
        }
    }

    private void ReleaseOrDeferUnusedPermit(CapacityReservationLease reservation, bool retainTombstone)
    {
        SemaphoreSlim gate = _capacity.DemandRegistry.PlanningGate;
        if (!gate.Wait(0))
        {
            DeferPermitRelease(reservation, retainTombstone);
            return;
        }

        try
        {
            ReleaseKnownUnusedPermit(reservation, retainTombstone);
        }
        catch
        {
            DeferPermitRelease(reservation, retainTombstone);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void SetCapacityLeaseState(
        ScaleSetCapacityContext capacity,
            CapacityReservationLease initialLease,
        CapacityOccupancyState state)
    {
        CapacityReservationLease lease = initialLease;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            lease = capacity.DemandRegistry.GetCapacityLease(lease.Handle.Identity.OperationId) ?? lease;
            if (state != CapacityOccupancyState.Reserved)
            {
                _ = capacity.DemandRegistry.MarkCapacityLeaseProvisioning(lease);
            }

            CapacityStateUpdateAttempt update = capacity.Coordinator.TryUpdateState(
                lease.Handle,
                lease.Revision,
                state);
            if (update.Status is CapacityStateUpdateStatus.Updated or CapacityStateUpdateStatus.AlreadyCurrent)
            {
                if (!capacity.DemandRegistry.TryUpdateCapacityLease(lease, update.CurrentRevision))
                {
                    CapacityReservationLease? refreshed = capacity.DemandRegistry.GetCapacityLease(lease.Handle.Identity.OperationId);
                    if (refreshed is not null)
                    {
                        lease = refreshed;
                    }
                }

                return;
            }

            if (update.Status == CapacityStateUpdateStatus.StaleRevision)
            {
                _ = capacity.DemandRegistry.TryUpdateCapacityLease(lease, update.CurrentRevision);
                continue;
            }

            if (update.Status == CapacityStateUpdateStatus.StaleHandle)
            {
                CapacityReservationLease? refreshed = capacity.DemandRegistry.GetCapacityLease(lease.Handle.Identity.OperationId);
                if (refreshed is not null && refreshed.Handle.Generation != lease.Handle.Generation)
                {
                    lease = refreshed;
                    continue;
                }
            }

            throw new InvalidOperationException(
                $"The capacity lease for runner operation '{lease.Handle.Identity.OperationId}' could not be updated: {update.Status}.");
        }

        throw new InvalidOperationException(
            $"The capacity lease for runner operation '{initialLease.Handle.Identity.OperationId}' changed repeatedly while setting {state}.");
    }

    private async Task ApplyCapacityActivityAsync(
        long runnerRequestId,
        long runnerId,
        string? runnerName,
        RunnerActivityEvidence evidence,
        CancellationToken cancellationToken)
    {
        if (evidence == RunnerActivityEvidence.JobCompleted)
        {
            RecordJobCompleted(runnerRequestId);
        }

        RunnerOperationState? operation = await ResolveCapacityActivityAsync(
            runnerRequestId,
            runnerId,
            runnerName,
            cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return;
        }

        RunnerOperationState updated = await _lifecycle.RecordActivityAsync(
            operation.Identity,
            evidence,
            cancellationToken).ConfigureAwait(false);
        CapacityOccupancyState capacityState = updated.Stage switch
        {
            RunnerLifecycleStage.Starting => CapacityOccupancyState.Starting,
            RunnerLifecycleStage.Idle => CapacityOccupancyState.Idle,
            RunnerLifecycleStage.Busy => CapacityOccupancyState.Busy,
            RunnerLifecycleStage.Completed => CapacityOccupancyState.Unknown,
            RunnerLifecycleStage.Unknown => CapacityOccupancyState.Unknown,
            _ => CapacityOccupancyState.Unknown,
        };
        CapacityReservationLease? capacityLease = _capacity.DemandRegistry.GetCapacityLease(operation.Identity.OperationId);
        if (capacityLease is not null)
        {
            SetCapacityLeaseState(_capacity, capacityLease, capacityState);
        }

        if (updated.Stage == RunnerLifecycleStage.Busy && evidence == RunnerActivityEvidence.JobAssigned)
        {
            RecordJobStarted(runnerRequestId);
        }
    }

    private async Task<RunnerOperationState?> ResolveCapacityActivityAsync(
        long runnerRequestId,
        long runnerId,
        string? runnerName,
        CancellationToken cancellationToken)
    {
        if (runnerId <= 0 || string.IsNullOrWhiteSpace(runnerName))
        {
            LogMissingCapacityActivity(_logger, runnerRequestId, runnerId, _scope.ScaleSetKey);
            return null;
        }

        IReadOnlyList<RunnerOperationState> operations = await _journal.ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        RunnerOperationState[] idMatches = operations
            .Where(operation => operation.Identity.Scope == _scope && operation.Identity.GitHubRunnerId == runnerId)
            .ToArray();
        RunnerOperationState[] nameMatches = operations
            .Where(operation => operation.Identity.Scope == _scope
                && string.Equals(operation.Identity.RunnerName, runnerName, StringComparison.Ordinal))
            .ToArray();
        RunnerOperationState[] exactMatches = operations
            .Where(operation => operation.Identity.Scope == _scope
                && operation.Identity.GitHubRunnerId == runnerId
                && string.Equals(operation.Identity.RunnerName, runnerName, StringComparison.Ordinal))
            .ToArray();

        if (exactMatches.Length != 1)
        {
            if (exactMatches.Length == 0 && idMatches.Length == 0 && nameMatches.Length == 0)
            {
                LogMissingCapacityActivity(_logger, runnerRequestId, runnerId, _scope.ScaleSetKey);
            }
            else
            {
                LogConflictingCapacityActivity(_logger, runnerRequestId, runnerId, runnerName, _scope.ScaleSetKey);
            }

            return null;
        }

        return exactMatches[0];
    }

    private void SetListenerMaximumIfChanged(ScaleSetCapacityContext capacity, int maximum)
    {
        int effectiveMaximum = IsDraining ? 0 : maximum;
        int previous = Interlocked.Exchange(ref _lastAdvertisedMaximum, effectiveMaximum);
        if (previous != effectiveMaximum)
        {
            capacity.SetListenerMaximum(effectiveMaximum);
        }

        // Close the narrow race in which BeginDrain sets the listener maximum
        // to zero between the first state read and this update.
        if (effectiveMaximum != 0 && IsDraining)
        {
            Interlocked.Exchange(ref _lastAdvertisedMaximum, 0);
            capacity.SetListenerMaximum(0);
        }
    }

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Observed JobAssigned activity without runner identity for request {RunnerRequestId} in scale set {ScaleSetKey}.")]
    private static partial void LogJobAssignedWithoutRunner(ILogger logger, long runnerRequestId, string scaleSetKey);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Ignored activity with missing runner identity for request {RunnerRequestId}, runner {RunnerId}, in scale set {ScaleSetKey}.")]
    private static partial void LogMissingCapacityActivity(ILogger logger, long runnerRequestId, long runnerId, string scaleSetKey);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "Ignored conflicting activity identity for request {RunnerRequestId}, runner {RunnerId} named {RunnerName}, in scale set {ScaleSetKey}.")]
    private static partial void LogConflictingCapacityActivity(
        ILogger logger,
        long runnerRequestId,
        long runnerId,
        string runnerName,
        string scaleSetKey);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Could not update capacity state for runner operation {OperationId} in scale set {ScaleSetKey}: {FailureType}.")]
    private static partial void LogCapacityStateUpdateFailed(
        ILogger logger,
        Guid operationId,
        string scaleSetKey,
        string failureType);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Warning,
        Message = "Could not settle admission permits for scale set {ScaleSetKey}. Failure type {ExceptionType}.")]
    private static partial void LogAdmissionTrimFailed(ILogger logger, string scaleSetKey, string exceptionType);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Capacity planning for scale set {ScaleSetKey} remained stale after {AttemptCount} reservation attempts.")]
    private static partial void LogStalePlan(ILogger logger, string scaleSetKey, int attemptCount);
}
