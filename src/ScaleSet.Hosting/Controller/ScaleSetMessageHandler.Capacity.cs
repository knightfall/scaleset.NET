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
            int offeredJobCount = statistics is null || message is null
                ? 0
                : message.JobAvailableMessages
                    .Select(static job => job.RunnerRequestId)
                    .Where(static requestId => requestId > 0)
                    .Distinct()
                    .Count();

            IReadOnlyList<CapacityReservationLease> reservations;
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

                reservations = PlanAndReserveCapacity(capacity, admitted, offeredJobCount);
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
                int availableRequests = GetAvailableAdmissionCount(statistics.TotalAssignedJobs);
                await AcquireCapacityJobsAsync(
                    message.JobAvailableMessages,
                    availableRequests,
                    cancellationToken).ConfigureAwait(false);
            }

            if (message is not null)
            {
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
        }
    }

    private async Task AcquireCapacityJobsAsync(
        IReadOnlyList<JobAvailableMessage> messages,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount <= 0)
        {
            return;
        }

        long[] offeredIds = messages
            .Select(static message => message.RunnerRequestId)
            .Where(static requestId => requestId > 0)
            .Distinct()
            .Take(maximumCount)
            .ToArray();
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

    private CapacityReservationLease[] PlanAndReserveCapacity(
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
                return Array.Empty<CapacityReservationLease>();
            }

            long existingPermits = capacity.DemandRegistry.GetAdmissionPermits(_scope.ScaleSetKey).Count;
            long actualRunners = Math.Max(0, scaleSetPlan.CurrentRunners - existingPermits);
            long desiredToProvision = Math.Max(0L, (long)scaleSetPlan.DesiredTotalRunners - actualRunners);
            long knownCapacity = GetKnownCapacityCount(plan, _scope.ScaleSetKey);
            long currentlyAdvertisedRequests = Math.Max(0L, (long)scaleSetPlan.AdvertisedMaximum - scaleSetPlan.AssignedJobs);
            long offerCapacity = Math.Min(offeredJobCount, currentlyAdvertisedRequests);
            long knownFreeRequests = Math.Max(0L, knownCapacity - scaleSetPlan.AssignedJobs);
            long permitsNeededForOffers = Math.Max(0L, offerCapacity - knownFreeRequests);
            long permitsNeededForDemand = Math.Max(0L, desiredToProvision - existingPermits);
            long requestedCount = Math.Min(
                scaleSetPlan.ReservableAdditionalRunners,
                Math.Max(permitsNeededForOffers, permitsNeededForDemand));

            if (requestedCount <= 0)
            {
                SetListenerMaximumIfChanged(capacity, scaleSetPlan.AdvertisedMaximum);
                return SelectPermitsToProvision(capacity, desiredToProvision);
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
            return SelectPermitsToProvision(capacity, desiredToProvision);
        }

        LogStalePlan(_logger, _scope.ScaleSetKey, MaximumStalePlanAttempts);
        throw new InvalidOperationException(
            $"The host capacity plan remained stale after {MaximumStalePlanAttempts} reservation attempts for scale set '{_scope.ScaleSetKey}'.");
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
            try
            {
                await using CreationConcurrencyLease creationLease =
                    await capacity.Coordinator.CreationConcurrencyGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
                _ = capacity.DemandRegistry.MarkCapacityLeaseProvisioning(reservation);
                SetCapacityLeaseState(capacity, reservation, CapacityOccupancyState.JitIssued);
                _ = await _provisioner.ProvisionAsync(
                    reservation.Handle.Identity,
                    cancellationToken).ConfigureAwait(false);
                SetCapacityLeaseState(capacity, reservation, CapacityOccupancyState.Starting);
            }
            catch (Exception exception)
            {
                try
                {
                    SetCapacityLeaseState(capacity, reservation, CapacityOccupancyState.Unknown);
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
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
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
        else if (updated.Stage == RunnerLifecycleStage.Completed && evidence == RunnerActivityEvidence.JobCompleted)
        {
            RecordJobCompleted(runnerRequestId);
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

        IReadOnlyList<RunnerOperationState> operations = await _journal.ReadAllAsync(cancellationToken).ConfigureAwait(false);
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
        Message = "Ignored JobAssigned activity without runner identity for request {RunnerRequestId} in scale set {ScaleSetKey}.")]
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
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Capacity planning for scale set {ScaleSetKey} remained stale after {AttemptCount} reservation attempts.")]
    private static partial void LogStalePlan(ILogger logger, string scaleSetKey, int attemptCount);
}
