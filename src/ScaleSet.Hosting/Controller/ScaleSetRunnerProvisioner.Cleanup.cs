using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;

namespace ScaleSet.Hosting.Controller;

public sealed partial class ScaleSetRunnerProvisioner
{
    /// <summary>
    /// Removes a completed runner only after provider inspection proves it is
    /// terminal, then records absence as a durable tombstone.
    /// </summary>
    /// <returns>True only after provider absence is confirmed.</returns>
    public async Task<bool> ReconcileCompletedAsync(
        RunnerIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Scope != _journal.Scope || identity.Scope.ScaleSetId != _scaleSetId)
        {
            throw new ArgumentException("The runner identity must match the provisioner's ownership scope.", nameof(identity));
        }

        RunnerOperationState? state = await _journal.FindAsync(identity.OperationId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return false;
        }

        EnsureSameStableIdentity(state.Identity, identity);
        if (state.Stage == RunnerLifecycleStage.Gone)
        {
            return true;
        }

        if (state.Stage is not (RunnerLifecycleStage.Completed or RunnerLifecycleStage.Removing)
            || state.ProviderResourceId is null)
        {
            return false;
        }

        RunnerResource probe = new(
            state.ProviderResourceId,
            state.Identity,
            state.LastObservedProviderStatus ?? RunnerResourceStatus.Unknown);
        RunnerResource? inspected = await _provider.InspectAsync(probe, cancellationToken).ConfigureAwait(false);
        if (inspected is null || inspected.Status == RunnerResourceStatus.Missing)
        {
            await _lifecycle.ConfirmGoneAsync(state.Identity, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!string.Equals(inspected.ProviderResourceId, state.ProviderResourceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Provider inspection returned a different resource ID than the completed runner journal.");
        }

        ValidateProviderResource(inspected, state.Identity);
        if (inspected.Status is not (RunnerResourceStatus.Exited or RunnerResourceStatus.Dead))
        {
            // Job completion is not proof that the container process has exited.
            // Running, Created, and Unknown resources remain protected for a later reconciliation.
            return false;
        }

        if (state.Stage == RunnerLifecycleStage.Completed)
        {
            _ = await _lifecycle.BeginRemovalAsync(
                inspected,
                _provider.Capabilities,
                cancellationToken).ConfigureAwait(false);
        }

        await _provider.RemoveAsync(inspected, force: false, cancellationToken).ConfigureAwait(false);
        RunnerResource? afterRemoval = await _provider.InspectAsync(inspected, cancellationToken).ConfigureAwait(false);
        if (afterRemoval is null || afterRemoval.Status == RunnerResourceStatus.Missing)
        {
            await _lifecycle.ConfirmGoneAsync(state.Identity, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!string.Equals(afterRemoval.ProviderResourceId, state.ProviderResourceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Provider inspection returned a different resource ID after completed-runner cleanup.");
        }

        ValidateProviderResource(afterRemoval, state.Identity);
        return false;
    }
}
