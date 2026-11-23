using ScaleSet.Client;

namespace ScaleSet.Listener;

/// <summary>Identifies one queue session within a caller-configured GitHub target.</summary>
/// <param name="Scope">Stable nonsecret identifier for the GitHub host and target.</param>
/// <param name="ScaleSetId">Positive runner scale-set identifier.</param>
/// <param name="SessionId">Exact remote session GUID.</param>
public sealed record ScaleSetListenerSessionKey(string Scope, long ScaleSetId, Guid SessionId)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Scope) || Scope.Length > 256 || Scope.Any(char.IsControl) ||
            ScaleSetId <= 0 || SessionId == Guid.Empty)
        {
            throw new ArgumentException("A bounded scope, positive scale-set ID, and session GUID are required.");
        }
    }

    /// <inheritdoc />
    public override string ToString() => "ScaleSetListenerSessionKey";
}

/// <summary>Sets the session, fixed ownership signal, and operation budgets for a pump.</summary>
/// <param name="SessionKey">Key for the exact session.</param>
/// <param name="OwnershipLost">Fixed cancellation signal for this ownership incarnation.</param>
public sealed record ScaleSetListenerPumpOptions(ScaleSetListenerSessionKey SessionKey, CancellationToken OwnershipLost)
{
    /// <summary>Gets the total poll budget, including authorization.</summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets the total handler budget, including authorization.</summary>
    public TimeSpan ProcessingTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets the budget for an independent checkpoint read or write.</summary>
    public TimeSpan CheckpointTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the shared budget for acknowledgement intent, dispatch, and settlement.</summary>
    public TimeSpan AcknowledgmentTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(SessionKey);
        SessionKey.Validate();
        ValidateTimeout(PollTimeout, nameof(PollTimeout));
        ValidateTimeout(ProcessingTimeout, nameof(ProcessingTimeout));
        ValidateTimeout(CheckpointTimeout, nameof(CheckpointTimeout));
        ValidateTimeout(AcknowledgmentTimeout, nameof(AcknowledgmentTimeout));
    }

    private static void ValidateTimeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(name, "The timeout must be positive and within the timer limit.");
        }
    }
}

/// <summary>Identifies the origin of a delivery.</summary>
public enum ScaleSetListenerDeliveryKind
{
    /// <summary>Initial statistics from the session snapshot.</summary>
    InitialStatistics = 0,
    /// <summary>A real queue message, which can have ID zero.</summary>
    Message = 1,
    /// <summary>A poll that returned no queue message.</summary>
    EmptyPoll = 2,
}

/// <summary>Describes whether the handler has safely completed processing.</summary>
public enum ScaleSetListenerDeliveryDisposition
{
    /// <summary>Business effects are durable or safely recoverable by the consumer.</summary>
    Completed = 0,
    /// <summary>Yield without acknowledging the delivery.</summary>
    Deferred = 1,
}

/// <summary>Identifies the durable phase of one pending delivery.</summary>
public enum ScaleSetListenerPendingPhase
{
    /// <summary>The snapshot is saved and processing can be resumed.</summary>
    Received = 0,
    /// <summary>Processing completed and only acknowledgement work remains.</summary>
    Processed = 1,
    /// <summary>Acknowledgement may have been sent. Recovery requires evidence.</summary>
    Acknowledging = 2,
}

/// <summary>Holds an immutable, sanitized delivery and its original observation time.</summary>
public sealed record ScaleSetListenerDelivery
{
    /// <summary>Creates a normalized snapshot from typed delivery data.</summary>
    /// <param name="sessionKey">Key for the exact session.</param>
    /// <param name="kind">Origin of the observation.</param>
    /// <param name="observedAt">Original observation time.</param>
    /// <param name="message">Parsed payload for a real delivery.</param>
    /// <param name="initialStatistics">Statistics for an initialization delivery.</param>
    /// <exception cref="ArgumentNullException">The session key is null.</exception>
    /// <exception cref="InvalidDataException">The delivery kind, payload, or time is invalid.</exception>
    public ScaleSetListenerDelivery(
        ScaleSetListenerSessionKey sessionKey, ScaleSetListenerDeliveryKind kind,
        DateTimeOffset observedAt, RunnerScaleSetMessage? message = null,
        RunnerScaleSetStatistic? initialStatistics = null)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        sessionKey.Validate();
        bool valid = kind switch
        {
            ScaleSetListenerDeliveryKind.InitialStatistics => message is null && initialStatistics is not null,
            ScaleSetListenerDeliveryKind.Message => message is { MessageId: >= 0 } && initialStatistics is null,
            ScaleSetListenerDeliveryKind.EmptyPoll => message is null && initialStatistics is null,
            _ => false,
        };
        if (!valid || observedAt == default)
        {
            throw new InvalidDataException("The listener delivery kind, payload, or observation time is invalid.");
        }

        ListenerDeliverySnapshot.ValidateStatistics(initialStatistics);
        SessionKey = sessionKey;
        Kind = kind;
        ObservedAt = observedAt;
        Message = message is null ? null : ListenerDeliverySnapshot.Create(message);
        InitialStatistics = initialStatistics;
    }

    /// <summary>Gets the delivery's session key.</summary>
    public ScaleSetListenerSessionKey SessionKey { get; }

    /// <summary>Gets the delivery kind.</summary>
    public ScaleSetListenerDeliveryKind Kind { get; }

    /// <summary>Gets the original observation time, including on replay.</summary>
    public DateTimeOffset ObservedAt { get; }

    /// <summary>Gets the sanitized message for a real delivery.</summary>
    public RunnerScaleSetMessage? Message { get; }

    /// <summary>Gets the statistics for a session initialization delivery.</summary>
    public RunnerScaleSetStatistic? InitialStatistics { get; }

    /// <summary>Gets whether this handler invocation resumes a previously observed delivery.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsReplay { get; internal init; }

    /// <inheritdoc />
    public override string ToString() => $"Listener delivery {Kind}";
}

/// <summary>Identifies the immutable snapshot and durable phase of a pending delivery.</summary>
/// <param name="Delivery">Durable delivery snapshot.</param>
/// <param name="Phase">Durable execution phase.</param>
public sealed record ScaleSetListenerPendingDelivery(
    ScaleSetListenerDelivery Delivery, ScaleSetListenerPendingPhase Phase);

/// <summary>Contains the entire atomic Listener state for one session.</summary>
/// <remarks>Preserve the transition ID, exact revision, and original snapshot on every round trip.</remarks>
/// <param name="SessionKey">Key for the exact session.</param>
/// <param name="SchemaVersion">Checkpoint schema version.</param>
/// <param name="Revision">One-based revision incremented on each durable transition.</param>
/// <param name="TransitionId">Unique ID of the committed transition.</param>
/// <param name="InitialStatisticsCompleted">Whether initial statistics handling has been recorded.</param>
/// <param name="LastAcknowledgedMessageId">Highest confirmed real message ID, or null.</param>
/// <param name="Pending">The unresolved durable delivery, if present.</param>
public sealed record ScaleSetListenerCheckpoint(
    ScaleSetListenerSessionKey SessionKey, int SchemaVersion, long Revision,
    Guid TransitionId, bool InitialStatisticsCompleted,
    long? LastAcknowledgedMessageId, ScaleSetListenerPendingDelivery? Pending)
{
    /// <summary>Schema written and accepted by the pump.</summary>
    public const int CurrentSchemaVersion = 1;

    internal void Validate(ScaleSetListenerSessionKey key)
    {
        if (SessionKey != key || SchemaVersion != CurrentSchemaVersion || Revision <= 0 ||
            TransitionId == Guid.Empty || LastAcknowledgedMessageId < 0)
        {
            throw new InvalidDataException("The listener checkpoint identity, schema, revision, or cursor is invalid.");
        }

        if (InitialStatisticsCompleted)
        {
            if (Revision < 2 || (Pending is not null &&
                (Pending.Delivery is not { Kind: ScaleSetListenerDeliveryKind.Message, IsReplay: false } ||
                 Pending.Delivery.SessionKey != key || !Enum.IsDefined(Pending.Phase))))
            {
                throw new InvalidDataException("The initialized listener checkpoint has an invalid pending phase or delivery.");
            }
        }
        else if (Revision != 1 || LastAcknowledgedMessageId is not null ||
                 Pending is not { Phase: ScaleSetListenerPendingPhase.Received,
                     Delivery: { Kind: ScaleSetListenerDeliveryKind.InitialStatistics, IsReplay: false } } ||
                 Pending.Delivery.SessionKey != key)
        {
            throw new InvalidDataException("The uninitialized listener checkpoint must contain only its initial receipt.");
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"Listener checkpoint revision {Revision}";
}

/// <summary>Reports a conclusive conditional write outcome.</summary>
public enum ScaleSetListenerCheckpointWriteResult
{
    /// <summary>The entire checkpoint was committed.</summary>
    Committed = 0,
    /// <summary>The revision or ownership condition failed and no mutation occurred.</summary>
    Conflict = 1,
}

/// <summary>Provides consistent reads and atomic, ownership-fenced checkpoint mutations.</summary>
public interface IScaleSetListenerCheckpointStore
{
    /// <summary>Reads the latest committed checkpoint or returns null for an absent key.</summary>
    /// <param name="key">Exact session key.</param>
    /// <param name="cancellationToken">Bounded operation token.</param>
    Task<ScaleSetListenerCheckpoint?> ReadAsync(ScaleSetListenerSessionKey key, CancellationToken cancellationToken);

    /// <summary>Applies the checkpoint if the exact revision and ownership conditions hold.</summary>
    /// <remarks>A null revision requires absence. Throw for an uncertain result and preserve the original failure type.</remarks>
    /// <param name="key">Exact session key.</param>
    /// <param name="expectedRevision">Required revision or null to require absence.</param>
    /// <param name="checkpoint">Entire new checkpoint to commit.</param>
    /// <param name="cancellationToken">Bounded operation token.</param>
    Task<ScaleSetListenerCheckpointWriteResult> WriteAsync(
        ScaleSetListenerSessionKey key, long? expectedRevision,
        ScaleSetListenerCheckpoint checkpoint, CancellationToken cancellationToken);
}

/// <summary>Processes a typed delivery with consumer-owned admission and replay safety.</summary>
public interface IScaleSetListenerDeliveryHandler
{
    /// <summary>Processes the delivery and reports completion or explicit deferral.</summary>
    /// <param name="delivery">Typed delivery with its original observation time.</param>
    /// <param name="cancellationToken">Bounded operation token.</param>
    Task<ScaleSetListenerDeliveryDisposition> HandleAsync(
        ScaleSetListenerDelivery delivery, CancellationToken cancellationToken);
}
