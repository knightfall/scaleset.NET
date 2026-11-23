namespace ScaleSet.Listener;

/// <summary>Identifies an individually authorized Listener operation.</summary>
internal enum ScaleSetListenerOperation
{
    /// <summary>Read the latest checkpoint.</summary>
    ReadCheckpoint = 0,
    /// <summary>Persist a delivery before processing.</summary>
    PersistReceived = 1,
    /// <summary>Poll the queue with the confirmed cursor.</summary>
    Poll = 2,
    /// <summary>Invoke the delivery handler with fresh authorization.</summary>
    HandleDelivery = 3,
    /// <summary>Record completed handling or finish initialization.</summary>
    PersistProcessed = 4,
    /// <summary>Persist acknowledgement intent before dispatch.</summary>
    PersistAcknowledging = 5,
    /// <summary>Dispatch queue acknowledgement.</summary>
    Acknowledge = 6,
    /// <summary>Atomically settle a confirmed acknowledgement.</summary>
    PersistSettled = 7,
}

/// <summary>Identifies an operation without exposing the delivery payload or credentials.</summary>
/// <param name="SessionKey">Exact session key.</param>
/// <param name="Operation">Operation being authorized.</param>
/// <param name="Revision">Observed checkpoint revision, if known.</param>
/// <param name="DeliveryKind">Kind of the relevant observation, if known.</param>
/// <param name="MessageId">Real message identifier, if applicable.</param>
internal sealed record ScaleSetListenerOperationContext(
    ScaleSetListenerSessionKey SessionKey, ScaleSetListenerOperation Operation,
    long? Revision, ScaleSetListenerDeliveryKind? DeliveryKind, long? MessageId);

/// <summary>Grants a bounded permit or defers an operation.</summary>
internal sealed class ScaleSetListenerAuthorization
{
    private ScaleSetListenerAuthorization(
        DateTimeOffset? validUntil, int? maxCapacity, DateTimeOffset? retryAt, CancellationToken ownershipLost)
    {
        ValidUntil = validUntil;
        OwnershipLost = ownershipLost;
        MaxCapacity = maxCapacity;
        RetryAt = retryAt;
    }

    /// <summary>Gets the latest time the authorized operation may run. Null indicates deferral.</summary>
    public DateTimeOffset? ValidUntil { get; }

    /// <summary>Gets the same fixed ownership signal supplied to the pump.</summary>
    public CancellationToken OwnershipLost { get; }

    /// <summary>Gets the current advertised poll capacity, supplied only for polls.</summary>
    public int? MaxCapacity { get; }

    /// <summary>Gets the optional scheduling hint supplied on deferral.</summary>
    public DateTimeOffset? RetryAt { get; }

    /// <summary>Grants permission for the current ownership incarnation.</summary>
    /// <param name="validUntil">Finite UTC permit deadline.</param>
    /// <param name="maxCapacity">Nonnegative advertised capacity for a poll.</param>
    /// <param name="ownershipLost">Fixed ownership signal from the pump options.</param>
    /// <exception cref="ArgumentOutOfRangeException">The validity deadline or capacity is invalid.</exception>
    public static ScaleSetListenerAuthorization Allow(
        DateTimeOffset validUntil, int? maxCapacity = null, CancellationToken ownershipLost = default)
    {
        if (validUntil == default || validUntil == DateTimeOffset.MaxValue || maxCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(validUntil), "A finite permit and nonnegative poll capacity are required.");
        }

        return new(validUntil, maxCapacity, null, ownershipLost);
    }

    /// <summary>Defers the operation and optionally suggests when to retry.</summary>
    /// <param name="retryAt">Optional hint for supervisor scheduling.</param>
    public static ScaleSetListenerAuthorization Defer(DateTimeOffset? retryAt = null) =>
        new(null, null, retryAt, default);
}

/// <summary>Authorizes each Listener operation under one fixed ownership incarnation.</summary>
internal interface IScaleSetListenerExecutionPolicy
{
    /// <summary>Returns an allowed permit or explicit deferral within the supplied operation budget.</summary>
    /// <param name="context">Nonsecret identity and stage for the operation.</param>
    /// <param name="cancellationToken">Bounded policy evaluation token.</param>
    Task<ScaleSetListenerAuthorization> AuthorizeAsync(
        ScaleSetListenerOperationContext context, CancellationToken cancellationToken);
}

/// <summary>Identifies the outcome of one serialized step.</summary>
internal enum ScaleSetListenerStepStatus
{
    /// <summary>Initial statistics were handled and their completion committed.</summary>
    Initialized = 0,
    /// <summary>The empty poll observation was handled.</summary>
    EmptyPoll = 1,
    /// <summary>Acknowledgement succeeded and its settlement committed.</summary>
    MessageAcknowledged = 2,
    /// <summary>The policy or handler deferred work.</summary>
    Deferred = 3,
    /// <summary>Acknowledgement may have been sent and requires supervisor resolution.</summary>
    RecoveryRequired = 4,
    /// <summary>The revision or ownership condition failed. Retire this pump.</summary>
    CheckpointConflict = 5,
}

/// <summary>Reports a step outcome and optional nonsecret delivery or scheduling information.</summary>
/// <param name="Status">Result of the step.</param>
/// <param name="MessageId">Real message associated with the result, if applicable.</param>
/// <param name="PendingPhase">Durable pending phase, if available.</param>
/// <param name="RetryAt">Optional hint for scheduling a later step.</param>
internal sealed record ScaleSetListenerStepResult(
    ScaleSetListenerStepStatus Status, long? MessageId = null,
    ScaleSetListenerPendingPhase? PendingPhase = null, DateTimeOffset? RetryAt = null);

internal interface IListenerOperations
{
    IListenerOperationBudget StartBudget(TimeSpan timeout, CancellationToken stoppingToken);
}

internal interface IListenerOperationBudget : IDisposable
{
    Task<T> RunAsync<T>(
        ScaleSetListenerOperationContext context,
        Func<ScaleSetListenerAuthorization, CancellationToken, Task<T>> operation,
        Func<T, bool>? finishOnStop = null);
}

internal sealed class ListenerDeferredException(DateTimeOffset? retryAt) : Exception
{
    internal DateTimeOffset? RetryAt { get; } = retryAt;
}
