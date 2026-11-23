namespace ScaleSet.Client;

/// <summary>Queue operations exposed by an initialized runner scale set message session.</summary>
public interface IMessageSessionClient
{
    /// <summary>Current session snapshot.</summary>
    RunnerScaleSetSession Session { get; }

    /// <summary>Fetches the next message after the supplied cursor.</summary>
    /// <param name="lastMessageId">Last successfully processed message identifier.</param>
    /// <param name="maxCapacity">Maximum runner capacity to include in the queue request.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
    /// <returns>The next message, or <see langword="null"/> when the queue is empty.</returns>
    Task<RunnerScaleSetMessage?> GetMessageAsync(
        long lastMessageId,
        int maxCapacity,
        CancellationToken cancellationToken);

    /// <summary>Acknowledges a successfully processed queue message.</summary>
    /// <param name="messageId">Identifier to acknowledge.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
    Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken);

    /// <summary>Submits runner request identifiers for acquisition.</summary>
    /// <param name="runnerRequestIds">Runner request identifiers to acquire.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
    /// <returns>Identifiers that the service acquired.</returns>
    Task<IReadOnlyList<long>> AcquireJobsAsync(
        IReadOnlyList<long> runnerRequestIds,
        CancellationToken cancellationToken);
}

/// <summary>Queue operations and explicit closure for a session owned by the caller.</summary>
/// <remarks>Close the session before disposing the client that opened it.</remarks>
public interface IMessageSession : IMessageSessionClient
{
    /// <summary>Closes the session after admitted queue operations settle.</summary>
    /// <param name="cancellationToken">Token used to bound or cancel session cleanup.</param>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
