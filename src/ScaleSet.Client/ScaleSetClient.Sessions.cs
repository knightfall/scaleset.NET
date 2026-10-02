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
}
