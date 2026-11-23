using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.State;

namespace ScaleSet.Hosting.Controller;

public sealed partial class ScaleSetMessageHandler
{
    private async Task ReconcileCompletedOperationsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RunnerOperationState> operations = await _journal
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false);

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
