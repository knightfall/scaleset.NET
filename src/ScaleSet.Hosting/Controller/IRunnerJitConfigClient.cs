using ScaleSet.Client;

namespace ScaleSet.Hosting.Controller;

/// <summary>Generates just-in-time runner configuration for one scale set.</summary>
public interface IRunnerJitConfigClient
{
    /// <summary>Gets a short-lived runner configuration from GitHub.</summary>
    /// <param name="scaleSetId">GitHub ID of the scale set that owns the runner.</param>
    /// <param name="runnerSetting">Runner name and work folder for the registration.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
    /// <returns>The short-lived configuration when GitHub issued one.</returns>
    Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
        long scaleSetId,
        RunnerScaleSetJitRunnerSetting runnerSetting,
        CancellationToken cancellationToken = default);
}

internal interface ITrackedRunnerJitConfigClient : IRunnerJitConfigClient
{
    Task<RunnerScaleSetJitRunnerConfig?> GenerateTrackedJitConfigAsync(
        long scaleSetId, RunnerScaleSetJitRunnerSetting setting, Action onDispatch, CancellationToken cancellationToken);
}

/// <summary>Adapts the reusable ScaleSet client to the hosting provisioning seam.</summary>
public sealed class ScaleSetRunnerJitConfigClient : ITrackedRunnerJitConfigClient
{
    private readonly ScaleSetClient _client;

    /// <summary>Creates an adapter around a caller-owned client.</summary>
    /// <param name="client">Client whose lifetime is managed by the caller.</param>
    public ScaleSetRunnerJitConfigClient(ScaleSetClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public async Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
        long scaleSetId,
        RunnerScaleSetJitRunnerSetting runnerSetting,
        CancellationToken cancellationToken = default)
    {
        return await GenerateCoreAsync(scaleSetId, runnerSetting, null, cancellationToken).ConfigureAwait(false);
    }

    async Task<RunnerScaleSetJitRunnerConfig?> ITrackedRunnerJitConfigClient.GenerateTrackedJitConfigAsync(
        long scaleSetId, RunnerScaleSetJitRunnerSetting setting, Action onDispatch, CancellationToken cancellationToken) =>
        await GenerateCoreAsync(scaleSetId, setting, onDispatch, cancellationToken).ConfigureAwait(false);

    private async Task<RunnerScaleSetJitRunnerConfig?> GenerateCoreAsync(
        long scaleSetId, RunnerScaleSetJitRunnerSetting setting, Action? onDispatch, CancellationToken cancellationToken)
    {
        using var retry = ScaleSetRequestRetry.SuppressMutationReplay();
        return await _client.GenerateJitConfigAsync(scaleSetId, setting, onDispatch, cancellationToken).ConfigureAwait(false);
    }
}
