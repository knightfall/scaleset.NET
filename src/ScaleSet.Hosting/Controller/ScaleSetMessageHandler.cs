using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Client;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;
using ScaleSet.Listener;

namespace ScaleSet.Hosting.Controller;

/// <summary>
/// Handles one scale set's queue messages, acquiring offered jobs before
/// provisioning and applying only matching GitHub activity evidence.
/// </summary>
public sealed partial class ScaleSetMessageHandler : IScaleSetMessageHandler
{
    private readonly RunnerOwnershipScope _scope;
    private readonly IMessageSessionClient _session;
    private readonly ScaleSetRunnerProvisioner _provisioner;
    private readonly RunnerLifecycleManager _lifecycle;
    private readonly IRunnerStateJournal _journal;
    private readonly ILogger<ScaleSetMessageHandler> _logger;
    private int _latestAssignedJobs = -1;

    /// <summary>Creates a message handler bound to one ownership scope and its durable journal.</summary>
    public ScaleSetMessageHandler(
        RunnerOwnershipScope scope,
        IMessageSessionClient session,
        ScaleSetRunnerProvisioner provisioner,
        RunnerLifecycleManager lifecycle,
        IRunnerStateJournal journal,
        ILogger<ScaleSetMessageHandler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Scope != scope)
        {
            throw new ArgumentException("The journal must match the message-handler ownership scope.", nameof(journal));
        }

        _scope = scope;
        _session = session;
        _provisioner = provisioner;
        _lifecycle = lifecycle;
        _journal = journal;
        _logger = logger ?? NullLogger<ScaleSetMessageHandler>.Instance;
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
        if (message?.Statistics is { } statistics)
        {
            if (statistics.TotalAssignedJobs < 0)
            {
                throw new InvalidDataException("Scale-set statistics contained a negative assigned-job count.");
            }

            Volatile.Write(ref _latestAssignedJobs, statistics.TotalAssignedJobs);
        }

        if (message is null)
        {
            return;
        }

        await AcquireAndProvisionAsync(message.JobAvailableMessages, cancellationToken).ConfigureAwait(false);
        foreach (JobAssignedMessage assigned in message.JobAssignedMessages)
        {
            await ApplyActivityAsync(
                assigned.RunnerRequestId,
                RunnerActivityEvidence.JobAssigned,
                runnerId: null,
                runnerName: null,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (JobStartedMessage started in message.JobStartedMessages)
        {
            await ApplyActivityAsync(
                started.RunnerRequestId,
                RunnerActivityEvidence.JobAssigned,
                started.RunnerId,
                started.RunnerName,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (JobCompletedMessage completed in message.JobCompletedMessages)
        {
            await ApplyActivityAsync(
                completed.RunnerRequestId,
                RunnerActivityEvidence.JobCompleted,
                completed.RunnerId,
                completed.RunnerName,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AcquireAndProvisionAsync(
        IReadOnlyList<JobAvailableMessage> messages,
        CancellationToken cancellationToken)
    {
        long[] offeredIds = messages
            .Select(static message => message.RunnerRequestId)
            .Where(static requestId => requestId > 0)
            .Distinct()
            .ToArray();
        if (offeredIds.Length == 0)
        {
            return;
        }

        IReadOnlyList<long> acquiredIds = await _session.AcquireJobsAsync(offeredIds, cancellationToken).ConfigureAwait(false);
        HashSet<long> acquired = (acquiredIds ?? Array.Empty<long>())
            .Where(offeredIds.Contains)
            .ToHashSet();

        foreach (long requestId in offeredIds)
        {
            if (acquired.Remove(requestId))
            {
                RunnerIdentity identity = RunnerRequestIdentityFactory.Create(_scope, requestId);
                await _provisioner.ProvisionAsync(identity, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyActivityAsync(
        long runnerRequestId,
        RunnerActivityEvidence evidence,
        long? runnerId,
        string? runnerName,
        CancellationToken cancellationToken)
    {
        if (runnerRequestId <= 0 || (runnerId is <= 0) || (runnerName is not null && string.IsNullOrWhiteSpace(runnerName)))
        {
            return;
        }

        RunnerIdentity identity = RunnerRequestIdentityFactory.Create(_scope, runnerRequestId);
        RunnerOperationState? operation = await _journal.FindAsync(identity.OperationId, cancellationToken).ConfigureAwait(false);
        if (operation is null
            || operation.Identity.Scope != _scope
            || operation.Identity.OperationId != identity.OperationId
            || !string.Equals(operation.Identity.RunnerName, identity.RunnerName, StringComparison.Ordinal))
        {
            return;
        }

        if ((runnerId is long expectedRunnerId && operation.Identity.GitHubRunnerId != expectedRunnerId)
            || (runnerName is not null && !string.Equals(operation.Identity.RunnerName, runnerName, StringComparison.Ordinal)))
        {
            LogConflictingActivity(_logger, runnerRequestId, _scope.ScaleSetKey);
            return;
        }

        if (operation.Stage is not (RunnerLifecycleStage.Starting or RunnerLifecycleStage.Idle
            or RunnerLifecycleStage.Busy or RunnerLifecycleStage.Completed or RunnerLifecycleStage.Unknown))
        {
            return;
        }

        await _lifecycle.RecordActivityAsync(identity, evidence, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Ignored conflicting activity for runner request {RunnerRequestId} in scale set {ScaleSetKey}.")]
    private static partial void LogConflictingActivity(ILogger logger, long runnerRequestId, string scaleSetKey);
}
