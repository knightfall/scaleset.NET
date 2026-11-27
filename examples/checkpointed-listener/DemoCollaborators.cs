using System.Text.Json;
using ScaleSet.Client;
using ScaleSet.Listener;

namespace ScaleSet.CheckpointedListener.Example;

/// <summary>Provides a process-local fence for this example’s store and ledger.</summary>
internal sealed class DemoOwnership : IDisposable
{
    private readonly CancellationTokenSource _lost = new();

    internal object Gate { get; } = new();

    internal CancellationToken Token => _lost.Token;

    internal bool IsCurrent { get; private set; } = true;

    internal void Revoke()
    {
        lock (Gate)
        {
            IsCurrent = false;
        }

        _lost.Cancel();
    }

    public void Dispose() => _lost.Dispose();
}

internal sealed class DemoCheckpointStore(DemoOwnership ownership) : IScaleSetListenerCheckpointStore
{
    private readonly Dictionary<ScaleSetListenerSessionKey, string> _records = [];

    public Task<ScaleSetListenerCheckpoint?> ReadAsync(ScaleSetListenerSessionKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Latest(key));
    }

    public Task<ScaleSetListenerCheckpointWriteResult> WriteAsync(
        ScaleSetListenerSessionKey key, long? expectedRevision, ScaleSetListenerCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        lock (ownership.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ownership.IsCurrent || Latest(key)?.Revision != expectedRevision)
            {
                return Task.FromResult(ScaleSetListenerCheckpointWriteResult.Conflict);
            }

            string snapshot = JsonSerializer.Serialize(checkpoint);
            if (snapshot.Contains("demo-secret", StringComparison.Ordinal) ||
                snapshot.Contains("https://queue-fixture.actions.githubusercontent.com", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Session credentials or a queue URL entered the checkpoint.");
            }

            _records[key] = snapshot;
            return Task.FromResult(ScaleSetListenerCheckpointWriteResult.Committed);
        }
    }

    internal ScaleSetListenerCheckpoint? Latest(ScaleSetListenerSessionKey key)
    {
        lock (ownership.Gate)
        {
            return _records.TryGetValue(key, out var json)
                ? JsonSerializer.Deserialize<ScaleSetListenerCheckpoint>(json)
                : null;
        }
    }
}

internal sealed class DemoPolicy(DemoOwnership ownership) : IScaleSetListenerExecutionPolicy
{
    internal DateTimeOffset? CooldownUntil { get; set; }
    internal bool PauseAcknowledgement { get; set; }
    internal int Capacity { get; set; }

    public Task<ScaleSetListenerAuthorization> AuthorizeAsync(
        ScaleSetListenerOperationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        if (context.Operation == ScaleSetListenerOperation.Poll && CooldownUntil is { } retryAt && retryAt > now)
        {
            return Task.FromResult(ScaleSetListenerAuthorization.Defer(retryAt));
        }

        if (context.Operation == ScaleSetListenerOperation.PersistAcknowledging && PauseAcknowledgement)
        {
            return Task.FromResult(ScaleSetListenerAuthorization.Defer());
        }

        return Task.FromResult(ScaleSetListenerAuthorization.Allow(now.AddMinutes(1),
            context.Operation == ScaleSetListenerOperation.Poll ? Capacity : null, ownership.Token));
    }
}

internal sealed class DemoDeliveryHandler(
    DemoSession session, DemoCheckpointStore store, DemoOwnership ownership, DemoPolicy policy)
    : IScaleSetListenerDeliveryHandler
{
    private readonly HashSet<long> _reserved = [];

    internal DateTimeOffset LastObservation { get; private set; }
    internal bool LastWasReplay { get; private set; }
    internal int RealDeliveries { get; private set; }
    internal int Assigned { get; private set; }

    public async Task<ScaleSetListenerDeliveryDisposition> HandleAsync(
        ScaleSetListenerDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.Message is not { } message)
        {
            return ScaleSetListenerDeliveryDisposition.Completed;
        }

        RealDeliveries++;
        LastObservation = delivery.ObservedAt;
        LastWasReplay = delivery.IsReplay;
        if (message.Statistics is { } statistics)
        {
            Assigned = statistics.TotalAssignedJobs;
        }

        if (store.Latest(delivery.SessionKey)?.Pending?.Phase != ScaleSetListenerPendingPhase.Received)
        {
            throw new InvalidOperationException("The delivery must be durable before handling.");
        }

        long[] reserved;
        lock (ownership.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ownership.IsCurrent || policy.Capacity == 0)
            {
                return ScaleSetListenerDeliveryDisposition.Deferred;
            }

            var available = Math.Max(0, policy.Capacity - _reserved.Count);
            reserved = message.JobAvailableMessages.Select(job => job.RunnerRequestId)
                .Distinct().Where(request => !_reserved.Contains(request)).Take(available).ToArray();
            _reserved.UnionWith(reserved);
        }

        if (reserved.Length != 0)
        {
            _ = await session.AcquireJobsAsync(reserved, cancellationToken);
        }

        return ScaleSetListenerDeliveryDisposition.Completed;
    }
}

internal sealed class DemoSession : IMessageSession
{
    private readonly Queue<RunnerScaleSetMessage> _messages = new();
    private PollBarrier? _nextBarrier;

    public RunnerScaleSetSession Session { get; } = new()
    {
        SessionId = Guid.NewGuid(),
        Statistics = new RunnerScaleSetStatistic(),
        MessageQueueUrl = "https://queue-fixture.actions.githubusercontent.com/demo",
        MessageQueueAccessToken = "demo-secret",
    };

    internal int Polls { get; private set; }
    internal int AdvertisedCapacity { get; private set; }
    internal long LastCursor { get; private set; }
    internal int Acquisitions { get; private set; }
    internal int AcknowledgeAttempts { get; private set; }
    internal bool LoseAcknowledgement { get; set; }
    internal bool Closed { get; private set; }

    internal void Offer(long messageId, int assigned, long requestId) => _messages.Enqueue(new()
    {
        MessageId = messageId,
        Statistics = new() { TotalAssignedJobs = assigned },
        JobAvailableMessages = [new() { RunnerRequestId = requestId, AcquireJobUrl = "https://queue-fixture.actions.githubusercontent.com/?sig=demo-secret" }],
    });

    internal PollBarrier HoldNextPoll() => _nextBarrier = new();

    public async Task<RunnerScaleSetMessage?> GetMessageAsync(long lastMessageId, int maxCapacity, CancellationToken cancellationToken)
    {
        Polls++;
        LastCursor = lastMessageId;
        AdvertisedCapacity = maxCapacity;
        if (_nextBarrier is { } barrier)
        {
            _nextBarrier = null;
            barrier.Entered.SetResult();
            await barrier.Release.Task.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _messages.TryDequeue(out var message) ? message : null;
    }

    public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AcknowledgeAttempts++;
        return LoseAcknowledgement
            ? Task.FromException(new IOException("The scripted acknowledgement response was lost."))
            : Task.CompletedTask;
    }

    public Task<IReadOnlyList<long>> AcquireJobsAsync(IReadOnlyList<long> runnerRequestIds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Acquisitions += runnerRequestIds.Count;
        return Task.FromResult(runnerRequestIds);
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Closed = true;
        return Task.CompletedTask;
    }

    internal sealed class PollBarrier
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
