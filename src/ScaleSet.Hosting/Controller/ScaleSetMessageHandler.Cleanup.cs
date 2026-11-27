using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.State;

namespace ScaleSet.Hosting.Controller;

public sealed partial class ScaleSetMessageHandler
{
    internal static async Task<IReadOnlyList<RunnerOperationState>> ReadTrackedOperationsAsync(
        IRunnerStateJournal journal, ScaleSetDemandRegistry registry, CancellationToken cancellationToken)
    {
        var active = await journal.ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        Guid[] trackedIds = registry.GetTrackedOperationIds(journal.Scope.ScaleSetKey);
        if (trackedIds.Length == 0)
        {
            return active;
        }

        HashSet<Guid> activeIds = active.Select(static operation => operation.Identity.OperationId).ToHashSet();
        var operations = active.ToBuilder();
        foreach (Guid id in trackedIds)
        {
            if (!activeIds.Contains(id) && await journal.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } operation)
            {
                operations.Add(operation);
            }
        }

        return operations.ToImmutable();
    }

    private async Task ReconcileCompletedOperationsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RunnerOperationState> operations = await ReadTrackedOperationsAsync(
            _journal, _capacity.DemandRegistry, cancellationToken).ConfigureAwait(false);

        foreach (RunnerOperationState operation in operations)
        {
            if (operation.Stage is not (RunnerLifecycleStage.Completed or RunnerLifecycleStage.Removing or RunnerLifecycleStage.Gone))
            {
                continue;
            }

            CapacityReservationLease? lease = _capacity.DemandRegistry.GetCapacityLease(operation.Identity.OperationId);
            if (operation.Stage == RunnerLifecycleStage.Gone && lease is null)
            {
                continue;
            }

            bool absent = await _provisioner.ReconcileCompletedAsync(operation.Identity, cancellationToken).ConfigureAwait(false);
            if (absent && lease is not null
                && _capacity.DemandRegistry.TryReleaseConfirmedAbsent(_capacity.Coordinator, lease) == CapacityReleaseStatus.VersionExhausted)
            {
                throw new InvalidOperationException("The capacity ledger cannot record the confirmed runner absence.");
            }
        }
    }
}
