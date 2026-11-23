using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Client;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;
using ScaleSet.Hosting.Telemetry;

namespace ScaleSet.Hosting.Controller;

/// <summary>The outcome of one durable runner provisioning operation.</summary>
/// <param name="Resource">Provider resource associated with the operation.</param>
/// <param name="Stage">Durable lifecycle stage after provisioning or adoption.</param>
/// <param name="Recovered">Whether an existing provider resource was adopted.</param>
public sealed record RunnerProvisioningResult(RunnerResource Resource, RunnerLifecycleStage Stage, bool Recovered);

/// <summary>
/// Reserves, configures, creates, and starts one runner under a stable identity.
/// Recovered resources are adopted as activity-unknown and are never recreated.
/// </summary>
/// <remarks>
/// A journaled Starting resource that inspection confirms is Created without
/// start or finish timestamps gets one StartAsync retry. Other recovered
/// resources are adopted as activity-unknown without a start retry because the
/// lifecycle graph cannot safely advance Unknown to Starting.
/// </remarks>
public sealed partial class ScaleSetRunnerProvisioner : IDisposable
{
    private static readonly OperationGateRegistry OperationGates = new();

    private readonly long _scaleSetId;
    private readonly string _workFolder;
    private readonly IRunnerJitConfigClient _jitConfigClient;
    private readonly IRunnerProvider _provider;
    private readonly IRunnerStateJournal _journal;
    private readonly RunnerLifecycleManager _lifecycle;
    private readonly ILogger<ScaleSetRunnerProvisioner> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ScaleSetTelemetryMetrics? _telemetry;
    private readonly ScaleSetTelemetryProvider _telemetryProvider;

    /// <summary>Creates a provisioner bound to one scale set, provider profile, and state journal.</summary>
    /// <param name="scaleSetId">GitHub ID that matches the journal's scale set.</param>
    /// <param name="workFolder">Runner work folder to include in the JIT request.</param>
    /// <param name="jitConfigClient">Client used to issue JIT configurations for new runners.</param>
    /// <param name="provider">Caller-owned runner provider with ownership-scoped discovery.</param>
    /// <param name="journal">Caller-owned durable state for the scale set.</param>
    /// <param name="logger">Optional application logger.</param>
    /// <param name="timeProvider">Optional clock for lifecycle timestamps.</param>
    public ScaleSetRunnerProvisioner(
        long scaleSetId,
        string workFolder,
        IRunnerJitConfigClient jitConfigClient,
        IRunnerProvider provider,
        IRunnerStateJournal journal,
        ILogger<ScaleSetRunnerProvisioner>? logger = null,
        TimeProvider? timeProvider = null)
        : this(scaleSetId, workFolder, jitConfigClient, provider, journal, logger, timeProvider, null, ScaleSetTelemetryProvider.Other)
    {
    }

    /// <summary>Creates a provisioner that records completed provisioning attempts in the host meter.</summary>
    /// <param name="scaleSetId">GitHub ID that matches the journal's scale set.</param>
    /// <param name="workFolder">Runner work folder to include in the JIT request.</param>
    /// <param name="jitConfigClient">Client used to issue JIT configurations for new runners.</param>
    /// <param name="provider">Caller-owned runner provider with ownership-scoped discovery.</param>
    /// <param name="journal">Caller-owned durable state for the scale set.</param>
    /// <param name="logger">Optional application logger.</param>
    /// <param name="timeProvider">Optional clock for lifecycle timestamps.</param>
    /// <param name="telemetry">Optional host-owned operation meter.</param>
    /// <param name="telemetryProvider">Finite metric dimension for the configured runner backend.</param>
    public ScaleSetRunnerProvisioner(
        long scaleSetId,
        string workFolder,
        IRunnerJitConfigClient jitConfigClient,
        IRunnerProvider provider,
        IRunnerStateJournal journal,
        ILogger<ScaleSetRunnerProvisioner>? logger,
        TimeProvider? timeProvider,
        ScaleSetTelemetryMetrics? telemetry,
        ScaleSetTelemetryProvider telemetryProvider)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleSetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workFolder);
        ArgumentNullException.ThrowIfNull(jitConfigClient);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Scope.ScaleSetId != scaleSetId)
        {
            throw new ArgumentException("The journal scope must match the configured scale-set ID.", nameof(journal));
        }

        if (!provider.Capabilities.SupportsOwnershipScopedDiscovery)
        {
            throw new ArgumentException("The provider must support ownership-scoped discovery.", nameof(provider));
        }

        _scaleSetId = scaleSetId;
        _workFolder = workFolder;
        _jitConfigClient = jitConfigClient;
        _provider = provider;
        _journal = journal;
        _logger = logger ?? NullLogger<ScaleSetRunnerProvisioner>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _telemetry = telemetry;
        _telemetryProvider = telemetryProvider;
        _lifecycle = new RunnerLifecycleManager(journal, _timeProvider, NullLogger<RunnerLifecycleManager>.Instance);
    }

    /// <summary>
    /// Provision one stable operation. Existing provider state is inspected or
    /// discovered before any JIT or create request is issued. JIT configuration
    /// is held only in memory and is never written to the journal or diagnostics.
    /// </summary>
    /// <param name="identity">Stable identity for the runner operation, in the journal's ownership scope.</param>
    /// <param name="cancellationToken">Token used to cancel provisioning.</param>
    /// <exception cref="ArgumentException">The identity is outside the configured ownership scope.</exception>
    /// <exception cref="InvalidOperationException">The operation is ambiguous or already terminal and cannot safely be replayed.</exception>
    /// <exception cref="InvalidDataException">Provider discovery contradicts the journal or returns conflicting resources.</exception>
    public async Task<RunnerProvisioningResult> ProvisionAsync(
        RunnerIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Scope != _journal.Scope)
        {
            throw new ArgumentException("The runner identity must match the journal ownership scope.", nameof(identity));
        }

        if (identity.Scope.ScaleSetId != _scaleSetId)
        {
            throw new ArgumentException("The runner identity must match the configured scale-set ID.", nameof(identity));
        }

        using OperationLease operationLease = await OperationGates.AcquireAsync(identity.Scope, identity.OperationId, cancellationToken).ConfigureAwait(false);
        if (operationLease.TryGetResult(out RunnerProvisioningResult? completed))
        {
            EnsureSameStableIdentity(completed!.Resource.Identity, identity);
            return completed;
        }

        long startedAt = Stopwatch.GetTimestamp();
        ScaleSetTelemetryOutcome outcome = ScaleSetTelemetryOutcome.Failure;
        try
        {
            RunnerProvisioningResult result = await ProvisionCoreAsync(identity, cancellationToken).ConfigureAwait(false);
            operationLease.Publish(result);
            outcome = ScaleSetTelemetryOutcome.Success;
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = ScaleSetTelemetryOutcome.Cancelled;
            throw;
        }
        finally
        {
            _telemetry?.RecordOperation(
                ScaleSetTelemetryOperation.Provision,
                outcome,
                Stopwatch.GetElapsedTime(startedAt),
                identity.Scope.ScaleSetKey,
                _telemetryProvider);
        }
    }

    private async Task<RunnerProvisioningResult> ProvisionCoreAsync(
        RunnerIdentity identity,
        CancellationToken cancellationToken)
    {
        using Activity? activity = ScaleSetTelemetrySources.StartOperation("scaleset.runner.provision");
        activity?.SetTag("scaleset.id", _scaleSetId);
        activity?.SetTag("scaleset.key", identity.Scope.ScaleSetKey);

        RunnerOperationState? previous = await _journal.FindAsync(identity.OperationId, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            EnsureSameStableIdentity(previous.Identity, identity);
            RunnerProvisioningResult? recovered = await TryRecoverExistingAsync(previous, cancellationToken).ConfigureAwait(false);
            if (recovered is not null)
            {
                activity?.SetTag("scaleset.recovered", true);
                return recovered;
            }

            throw await MarkUncertainAndDescribeAsync(previous, cancellationToken).ConfigureAwait(false);
        }

        if (identity.GitHubRunnerId is not null)
        {
            throw new ArgumentException("A new runner operation must not include a GitHub runner ID; JIT generation supplies it.", nameof(identity));
        }

        RunnerResource? preexisting = await FindOwnedOperationAsync(identity, cancellationToken).ConfigureAwait(false);
        if (preexisting is not null)
        {
            RunnerOperationState adopted = await AdoptOrTombstoneAsync(preexisting, identity, cancellationToken).ConfigureAwait(false);
            if (preexisting.Status == RunnerResourceStatus.Missing)
            {
                throw new InvalidOperationException("The operation has a retained provider-absence tombstone and cannot be recreated.");
            }

            activity?.SetTag("scaleset.recovered", true);
            return new RunnerProvisioningResult(preexisting, adopted.Stage, Recovered: true);
        }

        await _lifecycle.ReserveAsync(identity, cancellationToken).ConfigureAwait(false);

        RunnerScaleSetJitRunnerConfig? jit;
        try
        {
            jit = await _jitConfigClient.GenerateJitConfigAsync(
                _scaleSetId,
                new RunnerScaleSetJitRunnerSetting { Name = identity.RunnerName, WorkFolder = _workFolder },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _lifecycle.TransitionAsync(identity, RunnerLifecycleStage.Unknown, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (jit?.Runner is null || jit.Runner.Id <= 0 || string.IsNullOrWhiteSpace(jit.EncodedJitConfig))
        {
            await _lifecycle.TransitionAsync(identity, RunnerLifecycleStage.Unknown, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("GitHub returned an incomplete JIT runner configuration.");
        }

        if (!string.Equals(jit.Runner.Name, identity.RunnerName, StringComparison.Ordinal))
        {
            await _lifecycle.TransitionAsync(identity, RunnerLifecycleStage.Unknown, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw new InvalidDataException("GitHub returned a JIT runner with a different name.");
        }

        await _lifecycle.TransitionAsync(identity, RunnerLifecycleStage.JitIssued, cancellationToken: cancellationToken).ConfigureAwait(false);
        RunnerIdentity linkedIdentity = new(
            identity.Scope,
            identity.OperationId,
            identity.RunnerName,
            jit.Runner.Id,
            identity.CapacityResources);
        await _lifecycle.AssociateGitHubRunnerAsync(linkedIdentity, cancellationToken).ConfigureAwait(false);
        await _lifecycle.TransitionAsync(linkedIdentity, RunnerLifecycleStage.Creating, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Creating is durable before the provider call. A retry must discover
        // this operation before considering any further create request.
        RunnerResource created = await _provider.CreateAsync(
            new RunnerCreateRequest(linkedIdentity, jit.EncodedJitConfig),
            cancellationToken).ConfigureAwait(false);

        ValidateProviderResource(created, linkedIdentity);
        RunnerOperationState starting = await _lifecycle.TransitionAsync(
            linkedIdentity,
            RunnerLifecycleStage.Starting,
            created.ProviderResourceId,
            cancellationToken).ConfigureAwait(false);

        // Starting and its provider ID are durable before StartAsync. Recovery
        // inspects this exact resource and does not replay StartAsync blindly.
        RunnerResource started = await _provider.StartAsync(created, cancellationToken).ConfigureAwait(false);

        ValidateProviderResource(started, linkedIdentity);
        if (!string.Equals(started.ProviderResourceId, created.ProviderResourceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The provider changed a runner resource ID while starting it.");
        }

        ProvisioningReachedProviderStart(_logger, identity.OperationId, identity.Scope.ScaleSetKey);
        activity?.SetTag("scaleset.provision.outcome", "started");
        return new RunnerProvisioningResult(started, starting.Stage, Recovered: false);
    }

    private async Task<RunnerProvisioningResult?> TryRecoverExistingAsync(
        RunnerOperationState previous,
        CancellationToken cancellationToken)
    {
        if (previous.Stage is RunnerLifecycleStage.Gone or RunnerLifecycleStage.Failed)
        {
            throw new InvalidOperationException("A terminal runner operation cannot be provisioned again.");
        }

        if (previous.ProviderResourceId is not null)
        {
            RunnerResource probe = new(
                previous.ProviderResourceId,
                previous.Identity,
                previous.LastObservedProviderStatus ?? RunnerResourceStatus.Unknown);
            RunnerResource? inspected = await _provider.InspectAsync(probe, cancellationToken).ConfigureAwait(false);
            if (inspected is null || inspected.Status == RunnerResourceStatus.Missing)
            {
                await _lifecycle.ConfirmGoneAsync(previous.Identity, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("The journaled provider resource is absent; its operation is tombstoned and will not be recreated.");
            }

            if (!string.Equals(inspected.ProviderResourceId, previous.ProviderResourceId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Provider inspection returned a different resource ID than the journaled operation.");
            }

            ValidateProviderResource(inspected, previous.Identity);
            if (previous.Stage == RunnerLifecycleStage.Starting
                && inspected.Status == RunnerResourceStatus.Created)
            {
                if (inspected.StartedAt is not null || inspected.FinishedAt is not null)
                {
                    throw new InvalidDataException("A Created provider resource has contradictory start or finish timestamps.");
                }

                RunnerResource resumed;
                try
                {
                    resumed = await _provider.StartAsync(inspected, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // This recovery attempt is made once per durable Starting
                    // state. Even if its result is ambiguous, do not replay it
                    // again without new authoritative reconciliation.
                    await _lifecycle.TransitionAsync(
                        previous.Identity,
                        RunnerLifecycleStage.Unknown,
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    throw;
                }

                try
                {
                    ValidateProviderResource(resumed, previous.Identity);
                    if (!string.Equals(resumed.ProviderResourceId, previous.ProviderResourceId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The provider changed a journaled resource ID while resuming its start.");
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or ArgumentNullException)
                {
                    await _lifecycle.TransitionAsync(
                        previous.Identity,
                        RunnerLifecycleStage.Unknown,
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                if (resumed.Status == RunnerResourceStatus.Missing)
                {
                    await _lifecycle.ConfirmGoneAsync(previous.Identity, cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException("The provider resource disappeared while resuming its start; it will not be recreated.");
                }

                RunnerOperationState resumedState = await _lifecycle.AdoptDiscoveredAsync(resumed, cancellationToken).ConfigureAwait(false);
                return new RunnerProvisioningResult(resumed, resumedState.Stage, Recovered: true);
            }

            RunnerOperationState adopted = await _lifecycle.AdoptDiscoveredAsync(inspected, cancellationToken).ConfigureAwait(false);
            return new RunnerProvisioningResult(inspected, adopted.Stage, Recovered: true);
        }

        RunnerResource? discovered = await FindOwnedOperationAsync(previous.Identity, cancellationToken).ConfigureAwait(false);
        if (discovered is not null)
        {
            RunnerOperationState adopted = await AdoptOrTombstoneAsync(discovered, previous.Identity, cancellationToken).ConfigureAwait(false);
            if (discovered.Status != RunnerResourceStatus.Missing)
            {
                return new RunnerProvisioningResult(discovered, adopted.Stage, Recovered: true);
            }

            throw new InvalidOperationException("Provider discovery confirms the operation is absent; it will not be recreated.");
        }

        return null;
    }

    private async Task<RunnerResource?> FindOwnedOperationAsync(
        RunnerIdentity identity,
        CancellationToken cancellationToken)
    {
        ImmutableArray<RunnerResource> resources = await _provider.DiscoverAsync(identity.Scope, cancellationToken).ConfigureAwait(false);
        RunnerResource[] matches = resources
            .Where(resource => resource.Identity.OperationId == identity.OperationId)
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidDataException("Provider discovery returned multiple resources for one runner operation.");
        }

        if (matches.Length == 0)
        {
            return null;
        }

        RunnerResource match = matches[0];
        ValidateProviderResource(match, identity);
        return match;
    }

    private async Task<RunnerOperationState> AdoptOrTombstoneAsync(
        RunnerResource resource,
        RunnerIdentity identity,
        CancellationToken cancellationToken)
    {
        if (resource.Status == RunnerResourceStatus.Missing)
        {
            RunnerOperationState? existing = await _journal.FindAsync(identity.OperationId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                await _lifecycle.ReserveAsync(identity, cancellationToken).ConfigureAwait(false);
            }

            return await _lifecycle.ConfirmGoneAsync(identity, cancellationToken).ConfigureAwait(false);
        }

        return await _lifecycle.AdoptDiscoveredAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InvalidOperationException> MarkUncertainAndDescribeAsync(
        RunnerOperationState previous,
        CancellationToken cancellationToken)
    {
        if (previous.Stage is not (RunnerLifecycleStage.Unknown or RunnerLifecycleStage.Gone or RunnerLifecycleStage.Failed))
        {
            try
            {
                previous = await _lifecycle.TransitionAsync(
                    previous.Identity,
                    RunnerLifecycleStage.Unknown,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Keep the durable earlier intent. The caller still receives a
                // fail-closed result instead of an unsafe replay.
            }
        }

        return new InvalidOperationException(
            $"Runner operation {previous.Identity.OperationId} is {previous.Stage} without a discoverable provider resource. " +
            "Its prior JIT/provider outcome is uncertain; reconcile it before retrying.");
    }

    private static void EnsureSameStableIdentity(RunnerIdentity current, RunnerIdentity requested)
    {
        if (current.Scope != requested.Scope
            || current.OperationId != requested.OperationId
            || !string.Equals(current.RunnerName, requested.RunnerName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The operation ID is already bound to a different runner identity.");
        }
    }

    private static void ValidateProviderResource(RunnerResource resource, RunnerIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Identity.Scope != expected.Scope
            || resource.Identity.OperationId != expected.OperationId
            || !string.Equals(resource.Identity.RunnerName, expected.RunnerName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The provider returned a resource outside the requested ownership operation.");
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Runner provisioning reached provider start for operation {OperationId} in scale set {ScaleSetKey}.")]
    private static partial void ProvisioningReachedProviderStart(
        ILogger logger,
        Guid operationId,
        string scaleSetKey);

    private readonly record struct OperationKey(RunnerOwnershipScope Scope, Guid OperationId);

    private sealed class OperationGateRegistry
    {
        private readonly object _sync = new();
        private readonly Dictionary<OperationKey, OperationGateEntry> _entries = [];

        public async ValueTask<OperationLease> AcquireAsync(
            RunnerOwnershipScope scope,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            OperationKey key = new(scope, operationId);
            OperationGateEntry entry;
            lock (_sync)
            {
                if (!_entries.TryGetValue(key, out entry!))
                {
                    entry = new OperationGateEntry();
                    _entries.Add(key, entry);
                }

                entry.ReferenceCount++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new OperationLease(this, key, entry);
            }
            catch
            {
                ReleaseReference(key, entry);
                throw;
            }
        }

        public void Release(OperationLease lease)
        {
            lease.Entry.Semaphore.Release();
            ReleaseReference(lease.Key, lease.Entry);
        }

        private void ReleaseReference(OperationKey key, OperationGateEntry entry)
        {
            lock (_sync)
            {
                entry.ReferenceCount--;
                if (entry.ReferenceCount == 0)
                {
                    if (_entries.TryGetValue(key, out OperationGateEntry? current)
                        && ReferenceEquals(current, entry))
                    {
                        _entries.Remove(key);
                    }

                    entry.Semaphore.Dispose();
                }
            }
        }

    }

    private sealed class OperationGateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }

        public RunnerProvisioningResult? Result { get; set; }
    }

    private sealed class OperationLease(
        OperationGateRegistry registry,
        OperationKey key,
        OperationGateEntry entry) : IDisposable
    {
        private int _disposed;

        public OperationKey Key { get; } = key;

        public OperationGateEntry Entry { get; } = entry;

        public bool TryGetResult(out RunnerProvisioningResult? result)
        {
            result = Entry.Result;
            return result is not null;
        }

        public void Publish(RunnerProvisioningResult result) => Entry.Result = result;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                registry.Release(this);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lifecycle.Dispose();
}
