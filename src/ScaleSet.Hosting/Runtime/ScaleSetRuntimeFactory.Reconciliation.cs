using ScaleSet.Client;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Controller;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;

namespace ScaleSet.Hosting.Runtime;

public sealed partial class ScaleSetRuntimeFactory
{
    private sealed partial class ScaleSetRuntime
    {
        private async Task ReconcileSteadyStateAsync(CancellationToken cancellationToken)
        {
            FileRunnerStateJournal journal = _journal!;
            var operations = await ScaleSetMessageHandler.ReadTrackedOperationsAsync(
                journal, _capacityCoordinator.DemandRegistry, cancellationToken).ConfigureAwait(false);
            if (_handler?.IsDraining == true)
            {
                foreach (RunnerOperationState operation in operations)
                {
                    if (operation.Stage is RunnerLifecycleStage.Completed or RunnerLifecycleStage.Removing)
                    {
                        CapacityReservationLease? lease = _capacityCoordinator.DemandRegistry.GetCapacityLease(operation.Identity.OperationId);
                        bool gone = await _provisioner!.ReconcileCompletedAsync(operation.Identity, cancellationToken).ConfigureAwait(false);
                        ReleaseConfirmedCapacity(gone, lease);
                    }
                }

                return;
            }

            Dictionary<Guid, RunnerResource> inventory = await ReadOwnedInventoryAsync(journal.Scope, cancellationToken).ConfigureAwait(false);
            Dictionary<Guid, RunnerOperationState> byOperation = operations.ToDictionary(static operation => operation.Identity.OperationId);
            foreach (RunnerResource resource in inventory.Values)
            {
                RunnerOperationState? previous = byOperation.GetValueOrDefault(resource.Identity.OperationId)
                    ?? await journal.FindAsync(resource.Identity.OperationId, cancellationToken).ConfigureAwait(false);
                if (previous is not null)
                {
                    EnsureResourceMatchesJournal(resource, previous, journal.Scope);
                    if (IRunnerStateJournal.IsTerminal(previous))
                    {
                        throw new InvalidDataException("Scoped inventory contradicts a durable runner absence.");
                    }

                    if (previous.ProviderResourceId is not null)
                    {
                        continue;
                    }
                }

                RunnerOperationState adopted = await _lifecycle!.AdoptDiscoveredAsync(resource, cancellationToken).ConfigureAwait(false);
                AdoptCapacity(adopted.Identity, GetObservedCapacityResources(resource, adopted.Identity), CapacityOccupancyState.Unknown);
                byOperation[adopted.Identity.OperationId] = adopted;
            }

            foreach (RunnerOperationState operation in byOperation.Values)
            {
                CapacityReservationLease? lease = _capacityCoordinator.DemandRegistry.GetCapacityLease(operation.Identity.OperationId);
                if (operation.Stage == RunnerLifecycleStage.Failed || (operation.Stage == RunnerLifecycleStage.Gone && lease is null))
                {
                    continue;
                }

                inventory.TryGetValue(operation.Identity.OperationId, out RunnerResource? resource);
                if (resource is null && operation.ProviderResourceId is { } providerId)
                {
                    resource = await InspectOwnedResourceAsync(operation, providerId, cancellationToken).ConfigureAwait(false);
                }

                bool gone = operation.Stage switch
                {
                    RunnerLifecycleStage.Gone => resource is null
                        ? true
                        : throw new InvalidDataException("Provider inspection contradicts a durable runner absence."),
                    RunnerLifecycleStage.Completed => await _provisioner!
                        .ReconcileCompletedAsync(operation.Identity, cancellationToken).ConfigureAwait(false),
                    _ => await ReconcileInactiveRunnerAsync(operation, resource, cancellationToken).ConfigureAwait(false),
                };

                ReleaseConfirmedCapacity(gone, lease);
            }
        }

        private void ReleaseConfirmedCapacity(bool gone, CapacityReservationLease? lease)
        {
            if (gone && lease is not null
                && _capacityCoordinator.DemandRegistry.TryReleaseConfirmedAbsent(_capacityCoordinator, lease) == CapacityReleaseStatus.VersionExhausted)
            {
                throw new InvalidOperationException("The capacity ledger cannot record the confirmed runner absence.");
            }
        }

        private async Task<Dictionary<Guid, RunnerResource>> ReadOwnedInventoryAsync(
            RunnerOwnershipScope scope,
            CancellationToken cancellationToken)
        {
            var discovered = await _provider.DiscoverAsync(scope, cancellationToken).ConfigureAwait(false);
            Dictionary<Guid, RunnerResource> inventory = new();
            HashSet<string> providerIds = new(StringComparer.Ordinal);
            foreach (RunnerResource resource in discovered)
            {
                if (resource.Identity.Scope != scope)
                {
                    throw new InvalidDataException("Provider discovery returned a resource outside the configured ownership scope.");
                }

                if (resource.Status != RunnerResourceStatus.Missing
                    && (!inventory.TryAdd(resource.Identity.OperationId, resource) || !providerIds.Add(resource.ProviderResourceId)))
                {
                    throw new InvalidDataException("Provider discovery returned duplicate resource or operation identities.");
                }
            }

            return inventory;
        }

        private async Task<RunnerResource?> InspectOwnedResourceAsync(
            RunnerOperationState operation,
            string providerResourceId,
            CancellationToken cancellationToken)
        {
            RunnerResource probe = new(
                providerResourceId, operation.Identity, operation.LastObservedProviderStatus ?? RunnerResourceStatus.Unknown);
            RunnerResource? inspected = await _provider.InspectAsync(probe, cancellationToken).ConfigureAwait(false);
            if (inspected is null)
            {
                return null;
            }

            EnsureResourceMatchesJournal(inspected, operation, operation.Identity.Scope);
            if (inspected.ProviderResourceId != providerResourceId)
            {
                throw new InvalidDataException("Provider inspection returned a different resource for a durable runner operation.");
            }

            return inspected.Status == RunnerResourceStatus.Missing ? null : inspected;
        }

        private async Task<bool> ReconcileInactiveRunnerAsync(
            RunnerOperationState operation,
            RunnerResource? resource,
            CancellationToken cancellationToken)
        {
            bool canRetryStart = resource is not null && IsStartingCreatedRecovery(operation, resource);
            bool neverStarted = resource is { Status: RunnerResourceStatus.Created, StartedAt: null, FinishedAt: null }
                && operation.Stage is not (RunnerLifecycleStage.Busy or RunnerLifecycleStage.Idle)
                && operation.LastObservedProviderStatus is null or RunnerResourceStatus.Created or RunnerResourceStatus.Unknown;
            bool terminal = resource is { Status: RunnerResourceStatus.Exited or RunnerResourceStatus.Dead }
                && _provider.Capabilities.ProvidesTerminalStateEvidence;
            if (resource is not null && !terminal && !neverStarted)
            {
                // A running resource, or an unconfirmed provider state, can
                // still be assigned work even if aggregate idle demand is zero.
                return false;
            }

            (RegistrationState registration, RunnerReference? reference) = await ReadRegistrationAsync(
                operation.Identity, cancellationToken).ConfigureAwait(false);
            if (registration == RegistrationState.Present)
            {
                if (canRetryStart)
                {
                    // Reuse the durable resource and its existing registration.
                    // The provisioner checks its start intent before retrying.
                    _ = await _provisioner!.ProvisionAsync(operation.Identity, cancellationToken).ConfigureAwait(false);
                    return false;
                }

                if (operation.Identity.GitHubRunnerId is null)
                {
                    var identity = operation.Identity;
                    operation = await _lifecycle!.AssociateRecoveredGitHubRunnerAsync(
                        new RunnerIdentity(identity.Scope, identity.OperationId, identity.RunnerName, reference!.Id, identity.CapacityResources),
                        cancellationToken).ConfigureAwait(false);
                }

                if (!await TryRemoveInactiveRegistrationAsync(reference!.Id, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }
            else if (registration != RegistrationState.Absent)
            {
                return false;
            }

            if (resource is not null)
            {
                _ = await _lifecycle!.BeginRemovalAsync(resource, _provider.Capabilities, cancellationToken).ConfigureAwait(false);
                await _provider.RemoveAsync(resource, force: false, cancellationToken).ConfigureAwait(false);
                if (await InspectOwnedResourceAsync(operation, resource.ProviderResourceId, cancellationToken).ConfigureAwait(false) is not null)
                {
                    return false;
                }
            }

            _ = await _lifecycle!.ConfirmGoneAsync(operation.Identity, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task<(RegistrationState State, RunnerReference? Reference)> ReadRegistrationAsync(
            RunnerIdentity identity, CancellationToken cancellationToken)
        {
            if (identity.GitHubRunnerId is null && _client is not IScaleSetRunnerNameLookup)
            {
                return (RegistrationState.Unconfirmed, null);
            }

            RunnerReference? registration;
            try
            {
                registration = identity.GitHubRunnerId is { } runnerId
                    ? await _client.GetRunnerAsync(runnerId, cancellationToken).ConfigureAwait(false)
                    : await ((IScaleSetRunnerNameLookup)_client).FindRunnerByNameAsync(identity.RunnerName, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (ScaleSetApiException exception) when (identity.GitHubRunnerId is not null && RegistrationNotFound(exception))
            {
                return (RegistrationState.Absent, null);
            }

            if (registration is null)
            {
                return (RegistrationState.Absent, null);
            }

            if (registration.Id <= 0 || (identity.GitHubRunnerId is { } expectedId && registration.Id != expectedId)
                || !string.Equals(registration.Name, identity.RunnerName, StringComparison.Ordinal)
                || (registration.RunnerScaleSetId > 0 && registration.RunnerScaleSetId != identity.Scope.ScaleSetId))
            {
                throw new InvalidDataException("GitHub returned a runner registration that conflicts with its durable identity.");
            }

            return (registration.RunnerScaleSetId == identity.Scope.ScaleSetId
                ? RegistrationState.Present
                : RegistrationState.Unconfirmed, registration);
        }

        private async Task<bool> TryRemoveInactiveRegistrationAsync(long runnerId, CancellationToken cancellationToken)
        {
            try
            {
                await _client.RemoveRunnerAsync(runnerId, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (ScaleSetApiException exception) when (RegistrationNotFound(exception))
            {
                return true;
            }
            catch (ScaleSetApiException exception) when (exception.DomainError == ScaleSetDomainError.JobStillRunning)
            {
                return false;
            }
        }

        private static bool RegistrationNotFound(ScaleSetApiException exception) =>
            exception is { StatusCode: 404, DomainError: ScaleSetDomainError.AgentNotFound };

        private enum RegistrationState
        {
            Unconfirmed = 0,
            Present = 1,
            Absent = 2,
        }
    }
}
