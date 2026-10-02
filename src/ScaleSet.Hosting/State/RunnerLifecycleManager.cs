using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.Telemetry;

namespace ScaleSet.Hosting.State;

/// <summary>
/// Applies replay-safe lifecycle transitions and adopts discovered resources
/// conservatively after a restart.
/// </summary>
public sealed partial class RunnerLifecycleManager : IDisposable
{
    private readonly IRunnerStateJournal _journal;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RunnerLifecycleManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public RunnerLifecycleManager(
        IRunnerStateJournal journal,
        TimeProvider timeProvider,
        ILogger<RunnerLifecycleManager> logger)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _journal = journal;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Durably reserves an operation before GenerateJitConfig or any provider side effect.
    /// Replaying the same reservation returns its existing state.
    /// </summary>
    public async Task<RunnerOperationState> ReserveAsync(
        RunnerIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        EnsureScope(identity);
        ThrowIfDisposed();

        using Activity? activity = StartActivity("scaleset.runner.reserve");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RunnerOperationState? existing = await _journal.FindAsync(identity.OperationId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                EnsureSameOperation(existing.Identity, identity);
                return existing;
            }

            RunnerOperationState reserved = new(
                identity,
                RunnerLifecycleStage.Reserved,
                revision: 0,
                _timeProvider.GetUtcNow());

            try
            {
                await _journal.WriteAsync(reserved, expectedRevision: -1, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // A second manager may have reserved the same operation through
                // the same journal between the lookup and the compare-and-write.
                RunnerOperationState? raced = await _journal.FindAsync(identity.OperationId, cancellationToken).ConfigureAwait(false);
                if (raced is null)
                {
                    throw;
                }

                EnsureSameOperation(raced.Identity, identity);
                return raced;
            }

            OperationReserved(_logger, identity.OperationId, identity.Scope.ScaleSetKey);
            activity?.SetTag("runner.lifecycle.stage", "reserved");
            return reserved;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Associates the GitHub runner ID returned after JIT generation with the
    /// already-reserved operation. The association is idempotent and one-way.
    /// </summary>
    public async Task<RunnerOperationState> AssociateGitHubRunnerAsync(
        RunnerIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        EnsureScope(identity);
        ThrowIfDisposed();
        if (identity.GitHubRunnerId is null)
        {
            throw new ArgumentException("A positive GitHub runner ID is required for association.", nameof(identity));
        }

        using Activity? activity = StartActivity("scaleset.runner.identity.associate");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RunnerOperationState current = await _journal.FindAsync(identity.OperationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The runner operation has not been reserved or adopted.");
            EnsureSameOperation(current.Identity, identity);

            if (current.Identity.GitHubRunnerId == identity.GitHubRunnerId)
            {
                return current;
            }

            if (current.Identity.GitHubRunnerId is not null)
            {
                throw new InvalidOperationException("A runner operation cannot be rebound to a different GitHub runner ID.");
            }

            if (IsTerminal(current))
            {
                throw new InvalidOperationException("A terminal runner operation cannot be associated with a GitHub runner ID.");
            }

            if (current.Stage == RunnerLifecycleStage.Reserved)
            {
                throw new InvalidOperationException("A GitHub runner ID can only be associated after JIT issuance.");
            }

            if (current.Stage != RunnerLifecycleStage.JitIssued)
            {
                throw new InvalidOperationException("Associate the GitHub runner ID after JIT issuance and before provider creation.");
            }

            RunnerOperationState next = new(
                identity,
                current.Stage,
                current.Revision + 1,
                _timeProvider.GetUtcNow(),
                current.ProviderResourceId,
                current.LastObservedProviderStatus);
            await _journal.WriteAsync(next, current.Revision, cancellationToken).ConfigureAwait(false);
            GitHubRunnerAssociated(_logger, identity.OperationId);
            activity?.SetTag("runner.lifecycle.identity_linked", true);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Persists the next lifecycle stage. Reserved is written before JIT
    /// generation; JitIssued records a successful JIT response; callers must
    /// await Creating, Starting, and Removing transitions before their matching
    /// provider side effects. Idle and Busy require authoritative runner/job
    /// evidence; provider Running status alone is never converted into either stage.
    /// </summary>
    public Task<RunnerOperationState> TransitionAsync(
        RunnerIdentity identity,
        RunnerLifecycleStage nextStage,
        string? providerResourceId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(nextStage))
        {
            throw new ArgumentOutOfRangeException(nameof(nextStage), nextStage, "The lifecycle stage is not defined.");
        }

        if (nextStage is RunnerLifecycleStage.Idle or RunnerLifecycleStage.Busy
            or RunnerLifecycleStage.Completed or RunnerLifecycleStage.Gone)
        {
            throw new InvalidOperationException("Use activity evidence or ConfirmGoneAsync for observed lifecycle states.");
        }

        return PersistTransitionAsync(identity, nextStage, providerResourceId, cancellationToken);
    }

    /// <summary>
    /// Applies an authoritative job/activity signal. Provider resource status
    /// cannot be supplied here and therefore cannot establish Idle or Busy.
    /// </summary>
    public Task<RunnerOperationState> RecordActivityAsync(
        RunnerIdentity identity,
        RunnerActivityEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RunnerLifecycleStage stage = evidence switch
        {
            RunnerActivityEvidence.ConfirmedIdle => RunnerLifecycleStage.Idle,
            RunnerActivityEvidence.JobAssigned => RunnerLifecycleStage.Busy,
            RunnerActivityEvidence.JobCompleted => RunnerLifecycleStage.Completed,
            _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, "The activity evidence is not defined."),
        };

        return PersistTransitionAsync(identity, stage, providerResourceId: null, cancellationToken);
    }

    private async Task<RunnerOperationState> PersistTransitionAsync(
        RunnerIdentity identity,
        RunnerLifecycleStage nextStage,
        string? providerResourceId,
        CancellationToken cancellationToken,
        RunnerResource? terminalResource = null,
        RunnerProviderCapabilities? providerCapabilities = null,
        bool providerAbsenceConfirmed = false)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ThrowIfDisposed();

        if (providerResourceId is not null && string.IsNullOrWhiteSpace(providerResourceId))
        {
            throw new ArgumentException("The provider resource ID must be null or non-empty.", nameof(providerResourceId));
        }

        EnsureScope(identity);
        using Activity? activity = StartActivity("scaleset.runner.transition");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RunnerOperationState current = await _journal.FindAsync(identity.OperationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The runner operation has not been reserved or adopted.");
            EnsureSameOperation(current.Identity, identity);

            if (terminalResource is not null)
            {
                ValidateTerminalRemovalEvidence(current, terminalResource, providerCapabilities);
                if (IsTerminal(current))
                {
                    throw new InvalidDataException("Provider resource evidence contradicts a retained terminal operation tombstone.");
                }
            }

            if (current.Identity.GitHubRunnerId is null && identity.GitHubRunnerId is not null)
            {
                throw new InvalidOperationException("Associate the GitHub runner ID in its own durable revision before advancing lifecycle state.");
            }

            if (nextStage == RunnerLifecycleStage.Creating && current.Identity.GitHubRunnerId is null)
            {
                throw new InvalidOperationException("Associate the GitHub runner ID before persisting provider creation intent.");
            }

            if (current.Stage == nextStage
                && (providerResourceId is null || providerResourceId == current.ProviderResourceId))
            {
                return current;
            }

            if (IsTerminal(current) && nextStage != current.Stage)
            {
                // Terminal records act as tombstones: delayed or duplicate
                // messages cannot recreate or resurrect an operation.
                return current;
            }

            if (current.Stage != RunnerLifecycleStage.Unknown
                && current.Stage != RunnerLifecycleStage.Failed
                && nextStage != RunnerLifecycleStage.Unknown
                && IsEarlierInLifecycle(nextStage, current.Stage))
            {
                // Ignore a delayed event that would move the operation backward.
                return current;
            }

            bool terminalResourceAllowsRemoval = terminalResource is not null
                && nextStage == RunnerLifecycleStage.Removing;
            bool confirmedAbsenceAllowsGone = providerAbsenceConfirmed && nextStage == RunnerLifecycleStage.Gone;
            if (!CanTransition(current, nextStage) && !terminalResourceAllowsRemoval && !confirmedAbsenceAllowsGone)
            {
                throw new InvalidOperationException($"The runner operation cannot transition from {current.Stage} to {nextStage}.");
            }

            string? resourceId = providerResourceId ?? current.ProviderResourceId;
            if (current.ProviderResourceId is not null
                && resourceId != current.ProviderResourceId)
            {
                throw new InvalidOperationException("A runner operation cannot change its provider resource ID after discovery.");
            }

            if (nextStage == RunnerLifecycleStage.Failed && resourceId is not null)
            {
                throw new InvalidOperationException("A resource-bearing operation must be removed or reconciled before it can be marked failed.");
            }

            RunnerOperationState next = new(
                identity.GitHubRunnerId is null ? current.Identity : identity,
                nextStage,
                current.Revision + 1,
                _timeProvider.GetUtcNow(),
                resourceId,
                terminalResource?.Status
                    ?? (providerAbsenceConfirmed ? RunnerResourceStatus.Missing : current.LastObservedProviderStatus));

            await _journal.WriteAsync(next, current.Revision, cancellationToken).ConfigureAwait(false);
            OperationTransitioned(_logger, identity.OperationId, current.Stage, nextStage);
            activity?.SetTag("runner.lifecycle.stage", nextStage.ToString().ToLowerInvariant());
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records cleanup intent for a resource only when scoped provider evidence
    /// proves it is stopped or was created but never started. Running and
    /// Unknown resources remain protected.
    /// </summary>
    public async Task<RunnerOperationState> BeginRemovalAsync(
        RunnerResource resource,
        RunnerProviderCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(capabilities);
        ThrowIfDisposed();
        EnsureScope(resource.Identity);

        if (resource.Status == RunnerResourceStatus.Missing)
        {
            return await ConfirmGoneAsync(resource.Identity, cancellationToken).ConfigureAwait(false);
        }

        if (!capabilities.SupportsOwnershipScopedDiscovery)
        {
            throw new InvalidOperationException("Provider ownership-scoped discovery is required before removing a recovered resource.");
        }

        if (resource.Status is (RunnerResourceStatus.Exited or RunnerResourceStatus.Dead)
            && !capabilities.ProvidesTerminalStateEvidence)
        {
            throw new InvalidOperationException("The provider does not advertise terminal-state evidence.");
        }

        if (resource.Status == RunnerResourceStatus.Created
            && (resource.StartedAt is not null || resource.FinishedAt is not null))
        {
            throw new InvalidOperationException("A Created resource with start or finish timestamps is contradictory and cannot be removed automatically.");
        }

        if (resource.Status is not (RunnerResourceStatus.Created or RunnerResourceStatus.Exited or RunnerResourceStatus.Dead))
        {
            throw new InvalidOperationException("The provider has not supplied safe removal evidence for this resource.");
        }

        if (resource.Status == RunnerResourceStatus.Created)
        {
            RunnerOperationState? previous = await _journal.FindAsync(resource.Identity.OperationId, cancellationToken).ConfigureAwait(false);
            if (previous is not null
                && (previous.Stage is RunnerLifecycleStage.Idle or RunnerLifecycleStage.Busy or RunnerLifecycleStage.Completed
                    || previous.LastObservedProviderStatus is RunnerResourceStatus.Running
                        or RunnerResourceStatus.Exited or RunnerResourceStatus.Dead or RunnerResourceStatus.Missing))
            {
                throw new InvalidDataException("Created status contradicts previously observed runner activity or provider state.");
            }
        }

        await AdoptDiscoveredAsync(resource, cancellationToken).ConfigureAwait(false);

        return await PersistTransitionAsync(
            resource.Identity,
            RunnerLifecycleStage.Removing,
            resource.ProviderResourceId,
            cancellationToken,
            resource,
            capabilities).ConfigureAwait(false);
    }

    /// <summary>
    /// Adopts a resource found during scoped discovery. Activity is Unknown
    /// after restart even if the provider reports Running or the old journal
    /// recorded Idle/Busy.
    /// </summary>
    public async Task<RunnerOperationState> AdoptDiscoveredAsync(
        RunnerResource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ThrowIfDisposed();
        EnsureScope(resource.Identity);
        if (resource.Status == RunnerResourceStatus.Missing)
        {
            throw new ArgumentException("A missing resource cannot be adopted from discovery.", nameof(resource));
        }

        using Activity? activity = StartActivity("scaleset.runner.recovery.adopt");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RunnerOperationState? current = await _journal.FindAsync(resource.Identity.OperationId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                RunnerOperationState adopted = new(
                    resource.Identity,
                    RunnerLifecycleStage.Unknown,
                    revision: 0,
                    _timeProvider.GetUtcNow(),
                    resource.ProviderResourceId,
                    resource.Status);
                await _journal.WriteAsync(adopted, expectedRevision: -1, cancellationToken).ConfigureAwait(false);
                DiscoveredUnknown(_logger, resource.Identity.OperationId, resource.Status);
                activity?.SetTag("runner.lifecycle.stage", "unknown");
                return adopted;
            }

            EnsureSameOperation(current.Identity, resource.Identity);
            if (current.ProviderResourceId is not null && current.ProviderResourceId != resource.ProviderResourceId)
            {
                throw new InvalidDataException("Provider discovery returned a different resource ID for the same operation.");
            }

            if (current.Stage == RunnerLifecycleStage.Gone
                || (current.Stage == RunnerLifecycleStage.Failed && current.ProviderResourceId is null))
            {
                throw new InvalidDataException("Provider discovery contradicts a retained terminal operation tombstone.");
            }

            RunnerIdentity adoptedIdentity = PreserveKnownGitHubRunnerId(current.Identity, resource.Identity);

            if (current.Stage == RunnerLifecycleStage.Unknown
                && current.ProviderResourceId == resource.ProviderResourceId
                && current.LastObservedProviderStatus == resource.Status
                && current.Identity == adoptedIdentity)
            {
                return current;
            }

            RunnerOperationState next = new(
                adoptedIdentity,
                RunnerLifecycleStage.Unknown,
                current.Revision + 1,
                _timeProvider.GetUtcNow(),
                resource.ProviderResourceId,
                resource.Status);
            await _journal.WriteAsync(next, current.Revision, cancellationToken).ConfigureAwait(false);
            DiscoveredUnknown(_logger, resource.Identity.OperationId, resource.Status);
            activity?.SetTag("runner.lifecycle.stage", "unknown");
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records provider absence after InspectAsync returns null or Missing is observed.</summary>
    public Task<RunnerOperationState> ConfirmGoneAsync(
        RunnerIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // Call only after InspectAsync returns null or the provider reports
        // Missing. The transition gate serializes the resulting tombstone.
        return PersistTransitionAsync(
            identity,
            RunnerLifecycleStage.Gone,
            providerResourceId: null,
            cancellationToken,
            providerAbsenceConfirmed: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.Dispose();
        }
    }

    private static Activity? StartActivity(string name)
    {
        Activity? activity = ScaleSetTelemetrySources.StartOperation(name);
        activity?.SetTag("scaleset.component", "runner_lifecycle");
        return activity;
    }

    private static bool CanTransition(RunnerOperationState current, RunnerLifecycleStage next) =>
        current.Stage switch
        {
            RunnerLifecycleStage.Reserved => next is RunnerLifecycleStage.JitIssued or RunnerLifecycleStage.Failed or RunnerLifecycleStage.Unknown,
            RunnerLifecycleStage.JitIssued => next is RunnerLifecycleStage.Creating or RunnerLifecycleStage.Failed or RunnerLifecycleStage.Unknown,
            RunnerLifecycleStage.Creating => next is RunnerLifecycleStage.Starting or RunnerLifecycleStage.Failed or RunnerLifecycleStage.Unknown,
            RunnerLifecycleStage.Starting => next is RunnerLifecycleStage.Idle or RunnerLifecycleStage.Busy or RunnerLifecycleStage.Completed or RunnerLifecycleStage.Unknown,
            RunnerLifecycleStage.Idle => next is RunnerLifecycleStage.Busy or RunnerLifecycleStage.Completed or RunnerLifecycleStage.Removing or RunnerLifecycleStage.Unknown,
            RunnerLifecycleStage.Busy => next is RunnerLifecycleStage.Completed or RunnerLifecycleStage.Unknown,
            RunnerLifecycleStage.Completed => next is RunnerLifecycleStage.Removing or RunnerLifecycleStage.Unknown,
            RunnerLifecycleStage.Removing => next is RunnerLifecycleStage.Gone or RunnerLifecycleStage.Unknown,
            RunnerLifecycleStage.Unknown => next is RunnerLifecycleStage.Idle or RunnerLifecycleStage.Busy
                or RunnerLifecycleStage.Completed or RunnerLifecycleStage.Gone,
            RunnerLifecycleStage.Failed => current.ProviderResourceId is not null
                && next is (RunnerLifecycleStage.Removing or RunnerLifecycleStage.Unknown),
            RunnerLifecycleStage.Gone => false,
            _ => false,
        };

    private static bool IsTerminal(RunnerOperationState state) =>
        state.Stage == RunnerLifecycleStage.Gone
        || (state.Stage == RunnerLifecycleStage.Failed && state.ProviderResourceId is null);

    private static bool IsEarlierInLifecycle(RunnerLifecycleStage candidate, RunnerLifecycleStage current)
    {
        int candidateOrder = LifecycleOrder(candidate);
        int currentOrder = LifecycleOrder(current);
        return candidateOrder >= 0 && currentOrder >= 0 && candidateOrder < currentOrder;
    }

    private static int LifecycleOrder(RunnerLifecycleStage stage) => stage switch
    {
        RunnerLifecycleStage.Reserved => 0,
        RunnerLifecycleStage.JitIssued => 1,
        RunnerLifecycleStage.Creating => 2,
        RunnerLifecycleStage.Starting => 3,
        RunnerLifecycleStage.Idle => 4,
        RunnerLifecycleStage.Busy => 5,
        RunnerLifecycleStage.Completed => 6,
        RunnerLifecycleStage.Removing => 7,
        RunnerLifecycleStage.Gone => 8,
        RunnerLifecycleStage.Failed => 9,
        _ => -1,
    };

    private static void ValidateTerminalRemovalEvidence(
        RunnerOperationState current,
        RunnerResource resource,
        RunnerProviderCapabilities? capabilities)
    {
        if (capabilities?.SupportsOwnershipScopedDiscovery != true)
        {
            throw new InvalidOperationException("Provider ownership-scoped discovery is required before removing a recovered resource.");
        }

        EnsureSameOperation(current.Identity, resource.Identity);
        if (current.ProviderResourceId is not null && current.ProviderResourceId != resource.ProviderResourceId)
        {
            throw new InvalidDataException("Provider evidence refers to a different resource ID for this operation.");
        }

        bool safeCreatedResource = resource.Status == RunnerResourceStatus.Created
            && resource.StartedAt is null
            && resource.FinishedAt is null
            && current.Stage is (RunnerLifecycleStage.Reserved or RunnerLifecycleStage.JitIssued
                or RunnerLifecycleStage.Creating or RunnerLifecycleStage.Starting
                or RunnerLifecycleStage.Unknown or RunnerLifecycleStage.Removing)
            && current.LastObservedProviderStatus is (null or RunnerResourceStatus.Created or RunnerResourceStatus.Unknown);
        bool terminalResource = resource.Status is (RunnerResourceStatus.Exited or RunnerResourceStatus.Dead)
            && capabilities.ProvidesTerminalStateEvidence;

        if (!safeCreatedResource && !terminalResource)
        {
            throw new InvalidOperationException("The provider resource is not proven safe to remove.");
        }
    }

    private void EnsureScope(RunnerIdentity identity)
    {
        if (identity.Scope != _journal.Scope)
        {
            throw new InvalidOperationException("The runner operation belongs to a different ownership scope.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static void EnsureSameOperation(RunnerIdentity existing, RunnerIdentity requested)
    {
        bool same = existing.Scope == requested.Scope
            && existing.OperationId == requested.OperationId
            && existing.RunnerName.Equals(requested.RunnerName, StringComparison.Ordinal)
            && (existing.GitHubRunnerId is null || requested.GitHubRunnerId is null || existing.GitHubRunnerId == requested.GitHubRunnerId);
        if (!same)
        {
            throw new InvalidOperationException("An operation ID is already bound to a different runner identity.");
        }
    }

    private static RunnerIdentity PreserveKnownGitHubRunnerId(RunnerIdentity existing, RunnerIdentity discovered) =>
        discovered.GitHubRunnerId is null && existing.GitHubRunnerId is not null
            ? existing
            : discovered;

    [LoggerMessage(4100, LogLevel.Information, "Reserved runner operation {OperationId} for scale set {ScaleSetKey}.")]
    private static partial void OperationReserved(ILogger logger, Guid operationId, string scaleSetKey);

    [LoggerMessage(4101, LogLevel.Information, "Runner operation {OperationId} transitioned from {PreviousStage} to {NextStage}.")]
    private static partial void OperationTransitioned(
        ILogger logger,
        Guid operationId,
        RunnerLifecycleStage previousStage,
        RunnerLifecycleStage nextStage);

    [LoggerMessage(4102, LogLevel.Information, "Discovered runner operation {OperationId} was adopted as Unknown from provider status {ProviderStatus}.")]
    private static partial void DiscoveredUnknown(ILogger logger, Guid operationId, RunnerResourceStatus providerStatus);

    [LoggerMessage(4103, LogLevel.Information, "Associated GitHub runner ID with operation {OperationId}.")]
    private static partial void GitHubRunnerAssociated(ILogger logger, Guid operationId);
}
