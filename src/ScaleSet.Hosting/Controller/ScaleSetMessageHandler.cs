using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Client;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;
using ScaleSet.Listener;

namespace ScaleSet.Hosting.Controller;

/// <summary>Shared process-wide capacity services for one scale-set message handler.</summary>
public sealed class ScaleSetCapacityContext
{
    /// <summary>Creates the capacity context for one configured scale set.</summary>
    /// <param name="scaleSet">Configuration for the set, including its scaling limits and runner resource profile.</param>
    /// <param name="coordinator">Shared ledger that enforces the host's capacity limits.</param>
    /// <param name="demandRegistry">Coordinator's registry of demand and capacity leases.</param>
    /// <param name="setListenerMaximum">Callback that updates the listener's advertised maximum.</param>
    public ScaleSetCapacityContext(
        ScaleSetOptions scaleSet,
        HostCapacityCoordinator coordinator,
        ScaleSetDemandRegistry demandRegistry,
        Action<int> setListenerMaximum)
    {
        ArgumentNullException.ThrowIfNull(scaleSet);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(demandRegistry);
        ArgumentNullException.ThrowIfNull(setListenerMaximum);
        ArgumentException.ThrowIfNullOrWhiteSpace(scaleSet.Key);
        if (ScaleSetCapacityDefinition.FromOptions(scaleSet) != coordinator.GetDefinition(scaleSet.Key!))
        {
            throw new ArgumentException("The scale-set scaling limits and runner profile must match the shared capacity coordinator.", nameof(scaleSet));
        }

        ScaleSet = scaleSet;
        Coordinator = coordinator;
        DemandRegistry = demandRegistry;
        SetListenerMaximum = setListenerMaximum;
        Resources = CapacityResourceQuantities.FromRunnerProfile(scaleSet.RunnerProfile);
    }

    /// <summary>Gets the per-scale-set policy and resource profile.</summary>
    public ScaleSetOptions ScaleSet { get; }

    /// <summary>Gets the host-wide, concurrency-safe capacity ledger.</summary>
    public HostCapacityCoordinator Coordinator { get; }

    /// <summary>Gets the latest statistics shared among set workers.</summary>
    public ScaleSetDemandRegistry DemandRegistry { get; }

    /// <summary>Updates this set's GitHub queue-advertised capacity.</summary>
    public Action<int> SetListenerMaximum { get; }

    /// <summary>Gets this set's configured runner resource quantities.</summary>
    public CapacityResourceQuantities Resources { get; }
}

/// <summary>
/// Handles one scale set's queue messages, acquiring offered jobs before
/// provisioning and applying only matching GitHub activity evidence.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The callback gate uses only managed asynchronous waits and must remain releasable by in-flight callbacks.")]
public sealed partial class ScaleSetMessageHandler : IScaleSetMessageHandler
{
    private readonly RunnerOwnershipScope _scope;
    private readonly IMessageSessionClient _session;
    private readonly ScaleSetRunnerProvisioner _provisioner;
    private readonly RunnerLifecycleManager _lifecycle;
    private readonly IRunnerStateJournal _journal;
    private readonly ILogger<ScaleSetMessageHandler> _logger;
    private readonly ScaleSetCapacityContext _capacity;
    private readonly SemaphoreSlim _callbackGate = new(1, 1);
    private int _latestAssignedJobs = -1;

    /// <summary>Creates a message handler bound to one ownership scope, its durable journal, and the host capacity ledger.</summary>
    public ScaleSetMessageHandler(
        RunnerOwnershipScope scope,
        IMessageSessionClient session,
        ScaleSetRunnerProvisioner provisioner,
        RunnerLifecycleManager lifecycle,
        IRunnerStateJournal journal,
        ScaleSetCapacityContext capacity,
        ILogger<ScaleSetMessageHandler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(capacity);
        if (journal.Scope != scope)
        {
            throw new ArgumentException("The journal must match the message-handler ownership scope.", nameof(journal));
        }

        if (!string.Equals(capacity.ScaleSet.Key, scope.ScaleSetKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The capacity context must match the message-handler scale-set key.", nameof(capacity));
        }

        _scope = scope;
        _session = session;
        _provisioner = provisioner;
        _lifecycle = lifecycle;
        _journal = journal;
        _logger = logger ?? NullLogger<ScaleSetMessageHandler>.Instance;
        _capacity = capacity;
    }

    /// <summary>Gets the latest assigned-job count, or null until statistics arrive.</summary>
    public int? LatestAssignedJobs
    {
        get
        {
            int value = Volatile.Read(ref _latestAssignedJobs);
            return value < 0 ? null : value;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Exceptions propagate to <see cref="ScaleSetListener"/>. Its callback-before-ack
    /// contract leaves a failed message unacknowledged for safe redelivery.
    /// </remarks>
    public async Task HandleAsync(RunnerScaleSetMessage? message, CancellationToken cancellationToken)
    {
        using IDisposable callbackLease = EnterCallback();
        await _callbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await HandleSerializedAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _callbackGate.Release();
        }
    }

    private async Task HandleSerializedAsync(RunnerScaleSetMessage? message, CancellationToken cancellationToken)
    {
        if (message?.Statistics is { } statistics)
        {
            if (statistics.TotalAssignedJobs < 0 || statistics.TotalIdleRunners < 0)
            {
                throw new InvalidDataException("Scale-set statistics contained a negative runner or job count.");
            }

            Volatile.Write(ref _latestAssignedJobs, statistics.TotalAssignedJobs);
        }

        await HandleCapacityAwareAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
