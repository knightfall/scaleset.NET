using System.Diagnostics;
using ScaleSet.Client;

namespace ScaleSet.Listener;

/// <summary>Drives checkpointed message processing for one exact session and ownership incarnation.</summary>
/// <remarks>
/// The caller owns every collaborator and the session lifecycle. Drive a session through only one
/// pump or legacy listener at a time. Checkpointing requires a fenced store and replay-safe handler.
/// A reconstructed pump can continue with the same still-open session client. Session replacement
/// requires a fresh key. The caller retains unresolved checkpoints for reconciliation.
/// </remarks>
public sealed class ScaleSetListenerPump
{
    private readonly ListenerStepEngine _engine;

    /// <summary>Creates a pump with caller-supplied checkpoint storage, delivery handling, and authorization.</summary>
    /// <param name="session">Initialized, caller-owned session client bound to one remote session.</param>
    /// <param name="options">Exact session identity, fixed ownership signal, and finite operation budgets.</param>
    /// <param name="checkpoints">Consumer-owned consistent store with conditional, ownership-fenced writes.</param>
    /// <param name="handler">Consumer-owned handler with replay-safe effects and current admission checks.</param>
    /// <param name="policy">Consumer-owned authorization policy for the fixed ownership incarnation.</param>
    /// <param name="timeProvider">Clock and timers for observations and deadlines.</param>
    /// <exception cref="ArgumentNullException">A required collaborator is null.</exception>
    /// <exception cref="ArgumentException">The session key is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An operation budget falls outside the timer range.</exception>
    public ScaleSetListenerPump(
        IMessageSessionClient session,
        ScaleSetListenerPumpOptions options,
        IScaleSetListenerCheckpointStore checkpoints,
        IScaleSetListenerDeliveryHandler handler,
        IScaleSetListenerExecutionPolicy policy,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(policy);
        options.Validate();
        timeProvider ??= TimeProvider.System;
        _engine = new ListenerStepEngine(session, options, checkpoints, handler,
            new ListenerOperations(policy, timeProvider, options.OwnershipLost), timeProvider);
    }

    /// <summary>Resumes pending work or polls and handles at most one new delivery.</summary>
    /// <param name="stoppingToken">
    /// Graceful stop signal. Completed handling may finish bounded persistence and acknowledgement
    /// while the fixed ownership signal remains valid.
    /// </param>
    /// <returns>The outcome, including deferral, checkpoint conflict, or acknowledgement requiring recovery.</returns>
    /// <remarks>
    /// A failed callback or transport request propagates its failure. The pump awaits all work it
    /// started before releasing the step, including a callback that takes time to observe cancellation.
    /// A failed acknowledgement can leave a durable intent that causes a later step to require recovery.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Another step is active or the client session no longer matches.</exception>
    /// <exception cref="OperationCanceledException">The step was canceled by graceful stop, ownership loss, or a deadline.</exception>
    /// <exception cref="TimeoutException">The budget or permit expired before an operation was authorized or completed.</exception>
    /// <exception cref="InvalidDataException">The checkpoint or delivery has an invalid identity, schema, or payload.</exception>
    public async Task<ScaleSetListenerStepResult> StepAsync(CancellationToken stoppingToken = default)
    {
        using var activity = ScaleSetListener.ListenerActivitySource.StartActivity("scaleset.listener.step");
        try
        {
            var result = await _engine.StepAsync(stoppingToken).ConfigureAwait(false);
            activity?.SetTag("scaleset.listener.outcome", result.Status.ToString());
            return result;
        }
        catch (OperationCanceledException)
        {
            activity?.SetTag("scaleset.listener.outcome", "canceled");
            throw;
        }
        catch
        {
            activity?.SetTag("scaleset.listener.outcome", "failed");
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }
}
