using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Client;

namespace ScaleSet.Listener;

/// <summary>Runs one serial message-processing and acknowledgment loop.</summary>
/// <remarks>The caller owns session creation, closure and recovery after failure.</remarks>
public sealed partial class ScaleSetListener
{
    /// <summary>Activity source used for polling and message processing spans.</summary>
    public const string ActivitySourceName = "ScaleSet.Listener";

    /// <summary>Identifier of the synthetic message carrying the session's initial statistics.</summary>
    public const long InitialMessageId = -1;

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    internal static ActivitySource ListenerActivitySource => ActivitySource;
    private readonly IMessageSessionClient _client;
    private readonly long _scaleSetId;
    private readonly TimeSpan _acknowledgmentTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScaleSetListener> _logger;
    private int _maxRunners;
    private int _running;

    /// <summary>Creates a listener for an initialized message session.</summary>
    /// <param name="client">Caller-owned session used to poll and acknowledge messages.</param>
    /// <param name="options">Scale-set ID, initial capacity, and acknowledgment timeout.</param>
    /// <param name="logger">Optional application logger.</param>
    /// <param name="timeProvider">Optional clock for acknowledgment deadlines.</param>
    /// <exception cref="ArgumentOutOfRangeException">The scale-set ID, initial capacity, or acknowledgment timeout is invalid.</exception>
    public ScaleSetListener(
        IMessageSessionClient client,
        ScaleSetListenerOptions options,
        ILogger<ScaleSetListener>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ScaleSetId);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRunners);
        if (options.AcknowledgmentTimeout <= TimeSpan.Zero || options.AcknowledgmentTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Acknowledgment timeout must be positive and finite.");
        }

        _client = client;
        _scaleSetId = options.ScaleSetId;
        _maxRunners = options.MaxRunners;
        _acknowledgmentTimeout = options.AcknowledgmentTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ScaleSetListener>.Instance;
    }

    /// <summary>Gets the maximum runner capacity that the next poll advertises.</summary>
    public int MaxRunners => Volatile.Read(ref _maxRunners);

    /// <summary>Updates the maximum runner capacity that the next poll advertises.</summary>
    /// <param name="count">Non-negative runner capacity.</param>
    public void SetMaxRunners(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Volatile.Write(ref _maxRunners, count);
    }

    /// <summary>Processes messages serially until cancellation or failure.</summary>
    /// <remarks>Each real message is acknowledged only after the handler completes successfully.</remarks>
    /// <param name="handler">Callback for initial statistics, real messages, and empty polls.</param>
    /// <param name="cancellationToken">Token used to stop polling and cancel the active callback.</param>
    /// <exception cref="InvalidOperationException">A run is active, or the session is missing its identity or initial statistics.</exception>
    public async Task RunAsync(IScaleSetMessageHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("A listener cannot have multiple active runs.");
        }

        try
        {
            RunnerScaleSetSession session = _client.Session;
            if (session.SessionId == Guid.Empty || session.Statistics is null)
            {
                throw new InvalidOperationException("An initialized message session with statistics is required.");
            }

            await HandleAsync(handler, new RunnerScaleSetMessage
            {
                MessageId = InitialMessageId,
                Statistics = session.Statistics,
            }, cancellationToken).ConfigureAwait(false);

            long lastMessageId = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RunnerScaleSetMessage? message;
                using (Activity? activity = ActivitySource.StartActivity("scaleset.message.poll"))
                {
                    activity?.SetTag("scaleset.id", _scaleSetId);
                    message = await _client.GetMessageAsync(lastMessageId, MaxRunners, cancellationToken).ConfigureAwait(false);
                }

                await HandleAsync(handler, message, cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    continue;
                }

                lastMessageId = Math.Max(lastMessageId, message.MessageId);
                using var acknowledgment = new CancellationTokenSource(_acknowledgmentTimeout, _timeProvider);
                // A successful handler commits work even if shutdown has since cancelled its token.
                await _client.DeleteMessageAsync(message.MessageId, acknowledgment.Token).ConfigureAwait(false);
                MessageAcknowledged(_logger, message.MessageId, _scaleSetId);
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task HandleAsync(IScaleSetMessageHandler handler, RunnerScaleSetMessage? message, CancellationToken cancellationToken)
    {
        using Activity? activity = ActivitySource.StartActivity("scaleset.message.process");
        activity?.SetTag("scaleset.id", _scaleSetId);
        activity?.SetTag("scaleset.message.id", message?.MessageId);
        await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Acknowledged scale-set message {MessageId} for {ScaleSetId}")]
    private static partial void MessageAcknowledged(ILogger logger, long messageId, long scaleSetId);
}
