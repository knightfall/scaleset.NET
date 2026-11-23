using System.Runtime.ExceptionServices;
using System.Text.Json;
using ScaleSet.Client;

namespace ScaleSet.Listener;

internal sealed class ListenerStepEngine(
    IMessageSessionClient session,
    ScaleSetListenerPumpOptions options,
    IScaleSetListenerCheckpointStore checkpoints,
    IScaleSetListenerDeliveryHandler handler,
    IListenerOperations operations,
    TimeProvider timeProvider)
{
    private ScaleSetListenerCheckpoint? _checkpoint;
    private CheckpointWrite? _write;
    private Guid _acknowledgmentNotSent;
    private ExceptionDispatchInfo? _invalidReceipt;
    private bool _conflicted;
    private bool _sessionChanged;
    private int _running;

    private ScaleSetListenerSessionKey Key => options.SessionKey;
    private ScaleSetListenerPendingDelivery? Pending => _checkpoint?.Pending;

    internal async Task<ScaleSetListenerStepResult> StepAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("A listener pump cannot have multiple active steps.");
        }

        try
        {
            options.OwnershipLost.ThrowIfCancellationRequested();
            stoppingToken.ThrowIfCancellationRequested();
            if (_conflicted)
            {
                return DescribePending(ScaleSetListenerStepStatus.CheckpointConflict);
            }

            _invalidReceipt?.Throw();
            ValidateSession();
            ScaleSetListenerCheckpoint? saved;
            using (var budget = operations.StartBudget(options.CheckpointTimeout, stoppingToken))
            {
                saved = await InvokeAsync(budget, ScaleSetListenerOperation.ReadCheckpoint, null, null,
                    (_, token) => checkpoints.ReadAsync(Key, token)).ConfigureAwait(false);
            }

            saved?.Validate(Key);
            if (_write is { } write && SameCheckpoint(saved, write.Next))
            {
                if (AcceptWrite(write) is { } completed)
                {
                    return completed;
                }
            }
            else if ((_checkpoint is not null || _write is not null) && !SameCheckpoint(saved, _checkpoint))
            {
                return Conflict();
            }

            _checkpoint = saved;
            bool replay = Pending is not null || _write is not null;
            if (_write is { } retry)
            {
                if (retry.Operation is ScaleSetListenerOperation.PersistAcknowledging or ScaleSetListenerOperation.PersistSettled)
                {
                    return await AcknowledgeAsync().ConfigureAwait(false);
                }

                var token = retry.Operation == ScaleSetListenerOperation.PersistReceived
                    ? stoppingToken
                    : CancellationToken.None;
                using var budget = operations.StartBudget(options.CheckpointTimeout, token);
                if (await CommitAsync(budget).ConfigureAwait(false) is { } completed)
                {
                    return completed;
                }
            }

            if (Pending is { Phase: not ScaleSetListenerPendingPhase.Received })
            {
                return await AcknowledgeAsync().ConfigureAwait(false);
            }

            if (Pending is { } pending)
            {
                return await HandleAsync(pending.Delivery, replay, stoppingToken).ConfigureAwait(false);
            }

            if (_checkpoint is null)
            {
                var initial = new ScaleSetListenerDelivery(Key, ScaleSetListenerDeliveryKind.InitialStatistics,
                    timeProvider.GetUtcNow(), initialStatistics: ValidateSession().Statistics);
                PrepareReceipt(initial);
            }
            else
            {
                DateTimeOffset? emptyObservedAt;
                using (var budget = operations.StartBudget(options.PollTimeout, stoppingToken))
                {
                    emptyObservedAt = await InvokeAsync(budget, ScaleSetListenerOperation.Poll, _checkpoint.Revision, null,
                        PollAsync).ConfigureAwait(false);
                }

                if (emptyObservedAt is { } observedAt)
                {
                    var observation = new ScaleSetListenerDelivery(Key, ScaleSetListenerDeliveryKind.EmptyPoll, observedAt);
                    return await HandleAsync(observation, replay: false, stoppingToken).ConfigureAwait(false);
                }
            }

            using (var budget = operations.StartBudget(options.CheckpointTimeout, stoppingToken))
            {
                if (await CommitAsync(budget).ConfigureAwait(false) is { } outcome)
                {
                    return outcome;
                }
            }

            return await HandleAsync(Pending!.Delivery, replay: false, stoppingToken).ConfigureAwait(false);
        }
        catch (ListenerDeferredException deferred)
        {
            return DescribePending(ScaleSetListenerStepStatus.Deferred) with { RetryAt = deferred.RetryAt };
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<DateTimeOffset?> PollAsync(ScaleSetListenerAuthorization permit, CancellationToken token)
    {
        var message = await session.GetMessageAsync(_checkpoint?.LastAcknowledgedMessageId ?? 0,
            permit.MaxCapacity!.Value, token).ConfigureAwait(false);
        ValidateSession();
        var observedAt = timeProvider.GetUtcNow();
        if (message is null)
        {
            return observedAt;
        }

        try
        {
            PrepareReceipt(new ScaleSetListenerDelivery(Key, ScaleSetListenerDeliveryKind.Message, observedAt, message));
            return null;
        }
        catch (InvalidDataException failure)
        {
            // An unusable observation cannot be dropped and followed by a later poll.
            _invalidReceipt = ExceptionDispatchInfo.Capture(failure);
            throw;
        }
    }

    private void PrepareReceipt(ScaleSetListenerDelivery delivery)
    {
        var pending = new ScaleSetListenerPendingDelivery(delivery, ScaleSetListenerPendingPhase.Received);
        PrepareWrite(ScaleSetListenerOperation.PersistReceived, delivery, pending);
    }

    private async Task<ScaleSetListenerStepResult> HandleAsync(
        ScaleSetListenerDelivery observation, bool replay, CancellationToken stoppingToken)
    {
        ScaleSetListenerDeliveryDisposition decision;
        using (var budget = operations.StartBudget(options.ProcessingTimeout, stoppingToken))
        {
            decision = await InvokeAsync(budget, ScaleSetListenerOperation.HandleDelivery, _checkpoint?.Revision, observation,
                (_, token) => handler.HandleAsync(observation with { IsReplay = replay }, token),
                static disposition => disposition == ScaleSetListenerDeliveryDisposition.Completed).ConfigureAwait(false);
        }

        if (decision == ScaleSetListenerDeliveryDisposition.Deferred)
        {
            return DescribePending(ScaleSetListenerStepStatus.Deferred);
        }

        if (decision != ScaleSetListenerDeliveryDisposition.Completed)
        {
            throw new InvalidOperationException("The delivery handler returned an invalid disposition.");
        }

        if (observation.Kind == ScaleSetListenerDeliveryKind.EmptyPoll)
        {
            return new(ScaleSetListenerStepStatus.EmptyPoll);
        }

        bool initial = observation.Kind == ScaleSetListenerDeliveryKind.InitialStatistics;
        PrepareWrite(ScaleSetListenerOperation.PersistProcessed, observation,
            initial ? null : Pending! with { Phase = ScaleSetListenerPendingPhase.Processed });
        using (var budget = operations.StartBudget(options.CheckpointTimeout, CancellationToken.None))
        {
            if (await CommitAsync(budget).ConfigureAwait(false) is { } outcome)
            {
                return outcome;
            }
        }

        return await AcknowledgeAsync().ConfigureAwait(false);
    }

    private async Task<ScaleSetListenerStepResult> AcknowledgeAsync()
    {
        var delivery = Pending!.Delivery;
        if (_write is null && Pending.Phase == ScaleSetListenerPendingPhase.Acknowledging &&
            _acknowledgmentNotSent != _checkpoint!.TransitionId)
        {
            return DescribePending(ScaleSetListenerStepStatus.RecoveryRequired);
        }

        using var budget = operations.StartBudget(options.AcknowledgmentTimeout, CancellationToken.None);
        if (Pending.Phase == ScaleSetListenerPendingPhase.Processed && _write is null)
        {
            // Reserve a revision for settlement before persisting intent or sending DELETE.
            _ = checked(_checkpoint!.Revision + 2);
            PrepareWrite(ScaleSetListenerOperation.PersistAcknowledging, delivery,
                Pending with { Phase = ScaleSetListenerPendingPhase.Acknowledging });
            _acknowledgmentNotSent = _write!.Next.TransitionId;
        }

        if (_write is not null && await CommitAsync(budget).ConfigureAwait(false) is { } outcome)
        {
            return outcome;
        }

        await InvokeAsync(budget, ScaleSetListenerOperation.Acknowledge, _checkpoint!.Revision, delivery, async (_, token) =>
        {
            _acknowledgmentNotSent = Guid.Empty;
            await session.DeleteMessageAsync(delivery.Message!.MessageId, token).ConfigureAwait(false);
            ValidateSession();
            PrepareWrite(ScaleSetListenerOperation.PersistSettled, delivery, null);
            return true;
        }).ConfigureAwait(false);

        return (await CommitAsync(budget).ConfigureAwait(false))!;
    }

    private void PrepareWrite(
        ScaleSetListenerOperation operation, ScaleSetListenerDelivery observation, ScaleSetListenerPendingDelivery? pending)
    {
        long? cursor = _checkpoint?.LastAcknowledgedMessageId;
        if (operation == ScaleSetListenerOperation.PersistSettled)
        {
            long acknowledged = observation.Message!.MessageId;
            cursor = cursor is { } last ? Math.Max(last, acknowledged) : acknowledged;
        }

        var next = new ScaleSetListenerCheckpoint(Key, ScaleSetListenerCheckpoint.CurrentSchemaVersion,
            checked((_checkpoint?.Revision ?? 0) + 1), Guid.NewGuid(),
            _checkpoint?.InitialStatisticsCompleted == true || pending is null, cursor, pending);
        _write = new(operation, observation, next);
    }

    private async Task<ScaleSetListenerStepResult?> CommitAsync(IListenerOperationBudget budget)
    {
        var write = _write!;
        var result = await InvokeAsync(budget, write.Operation, _checkpoint?.Revision, write.Observation, async (_, token) =>
        {
            var result = await checkpoints.WriteAsync(Key, _checkpoint?.Revision, write.Next, token).ConfigureAwait(false);
            _conflicted = result == ScaleSetListenerCheckpointWriteResult.Conflict;
            return result;
        }).ConfigureAwait(false);

        return result switch
        {
            ScaleSetListenerCheckpointWriteResult.Committed => AcceptWrite(write),
            ScaleSetListenerCheckpointWriteResult.Conflict => Conflict(),
            _ => throw new InvalidOperationException("The checkpoint store returned an invalid write result."),
        };
    }

    private ScaleSetListenerStepResult? AcceptWrite(CheckpointWrite write)
    {
        _checkpoint = write.Next;
        _write = null;
        return write.Operation switch
        {
            ScaleSetListenerOperation.PersistSettled =>
                new(ScaleSetListenerStepStatus.MessageAcknowledged, write.Observation.Message!.MessageId),
            ScaleSetListenerOperation.PersistProcessed when write.Next.Pending is null =>
                new(ScaleSetListenerStepStatus.Initialized),
            _ => null,
        };
    }

    private Task<T> InvokeAsync<T>(
        IListenerOperationBudget budget,
        ScaleSetListenerOperation operation,
        long? revision,
        ScaleSetListenerDelivery? observation,
        Func<ScaleSetListenerAuthorization, CancellationToken, Task<T>> invoke,
        Func<T, bool>? finishOnStop = null)
    {
        var context = new ScaleSetListenerOperationContext(Key, operation, revision, observation?.Kind,
            observation?.Message?.MessageId);
        return budget.RunAsync(context, async (permit, token) =>
        {
            ValidateSession();
            token.ThrowIfCancellationRequested();
            T result = await invoke(permit, token).ConfigureAwait(false);
            ValidateSession();
            return result;
        }, finishOnStop);
    }

    private RunnerScaleSetSession ValidateSession()
    {
        var snapshot = session.Session;
        if (_sessionChanged || snapshot.SessionId != Key.SessionId ||
            (snapshot.RunnerScaleSet is { Id: > 0 } scaleSet && scaleSet.Id != Key.ScaleSetId))
        {
            _sessionChanged = true;
            throw new InvalidOperationException("The listener pump's session identity does not match the active client.");
        }

        return snapshot;
    }

    private ScaleSetListenerStepResult Conflict()
    {
        _conflicted = true;
        return DescribePending(ScaleSetListenerStepStatus.CheckpointConflict);
    }

    private ScaleSetListenerStepResult DescribePending(ScaleSetListenerStepStatus status) =>
        new(status, (Pending?.Delivery ?? _write?.Observation)?.Message?.MessageId, Pending?.Phase);

    private static bool SameCheckpoint(ScaleSetListenerCheckpoint? left, ScaleSetListenerCheckpoint? right)
    {
        if (ReferenceEquals(left, right) || left == right)
        {
            return true;
        }

        return left is not null && right is not null && left.Revision == right.Revision &&
            left.TransitionId == right.TransitionId &&
            JsonSerializer.SerializeToUtf8Bytes(left).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right));
    }

    private sealed record CheckpointWrite(
        ScaleSetListenerOperation Operation, ScaleSetListenerDelivery Observation, ScaleSetListenerCheckpoint Next);
}
