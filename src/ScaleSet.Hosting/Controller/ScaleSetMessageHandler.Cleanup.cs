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

            bool absent = await _provisioner.ReconcileCompletedAsync(operation.Identity, cancellationToken).ConfigureAwait(false);
            if (absent)
            {
                ReleaseCapacityLease(_capacity, operation.Identity.OperationId);
            }
        }
    }

    private static void ReleaseCapacityLease(ScaleSetCapacityContext capacity, Guid operationId)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            CapacityReservationLease? lease = capacity.DemandRegistry.GetCapacityLease(operationId);
            if (lease is null)
            {
                return;
            }

            CapacityReleaseStatus status = capacity.Coordinator.TryRelease(lease.Handle, lease.Revision);
            if (status is CapacityReleaseStatus.Released or CapacityReleaseStatus.AlreadyReleased)
            {
                _ = capacity.DemandRegistry.RemoveCapacityLease(operationId, lease);
                return;
            }

            if (status == CapacityReleaseStatus.StaleRevision)
            {
                continue;
            }

            if (status == CapacityReleaseStatus.StaleHandle)
            {
                CapacityReservationLease? refreshed = capacity.DemandRegistry.GetCapacityLease(operationId);
                if (refreshed is not null && refreshed.Handle.Generation != lease.Handle.Generation)
                {
                    continue;
                }
            }

            throw new InvalidOperationException(
                $"Capacity for completed runner operation '{operationId}' could not be released: {status}.");
        }

        throw new InvalidOperationException($"Capacity for completed runner operation '{operationId}' changed repeatedly during release.");
    }
}
