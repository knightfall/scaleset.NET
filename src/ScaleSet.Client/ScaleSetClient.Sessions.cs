using System.Text;
using System.Text.Json;

namespace ScaleSet.Client;

public sealed partial class ScaleSetClient
{
    private const string ScaleSetSessionsPath = "/_apis/runtime/runnerscalesets";

    /// <summary>Creates and initializes a message session for a runner scale set.</summary>
    /// <param name="scaleSetId">The runner scale set identifier.</param>
    /// <param name="ownerName">The owner name sent when creating the session.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
    /// <returns>A caller-owned client for the initialized message session.</returns>
    public async Task<MessageSessionClient> CreateMessageSessionAsync(
        long scaleSetId,
        string ownerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleSetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);

        var body = Encoding.UTF8.GetBytes(ScaleSetJson.Serialize(new RunnerScaleSetSession
        {
            OwnerName = ownerName,
        }));
        return await SendActionsAsync(
            HttpMethod.Post,
            $"{ScaleSetSessionsPath}/{scaleSetId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/sessions",
            body,
            query: null,
            expectedStatus: 200,
            cancellationToken,
            response => new MessageSessionClient(
                this,
                scaleSetId,
                ownerName,
                MessageSessionClient.ParseAndValidateSession(response, ownerName))).ConfigureAwait(false);
    }

    /// <summary>Retires the exact message session identified by a journaled scale-set ID and session GUID.</summary>
    /// <param name="scaleSetId">The positive identifier of the runner scale set in this client's target.</param>
    /// <param name="sessionId">The nonempty GUID of the session to retire.</param>
    /// <param name="cancellationToken">Token used to bound or cancel the request.</param>
    /// <remarks>
    /// Use this operation after the previous controller has stopped and ownership has been established.
    /// The client uses administration credentials and sends a single DELETE attempt. Successful
    /// completion confirms session retirement. A failure leaves acknowledgement and retirement
    /// decisions to the caller, including when an earlier response may have been lost.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scaleSetId"/> is zero or negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="ScaleSetApiException">The service did not confirm retirement.</exception>
    public async Task DeleteMessageSessionAsync(long scaleSetId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleSetId);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty message session ID is required.", nameof(sessionId));
        }

        using IDisposable retry = ScaleSetRequestRetry.Configure(static options => options with { MaxRetries = 0 });
        await DeleteMessageSessionCoreAsync(scaleSetId, sessionId, cancellationToken).ConfigureAwait(false);
    }

    internal Task DeleteMessageSessionCoreAsync(long scaleSetId, Guid sessionId, CancellationToken cancellationToken) =>
        SendActionsAsync(
            HttpMethod.Delete,
            $"{ScaleSetSessionsPath}/{FormatIdentifier(scaleSetId)}/sessions/{sessionId:D}",
            body: null,
            query: null,
            expectedStatus: 204,
            cancellationToken);
}
