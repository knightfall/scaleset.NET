using ScaleSet.Client;

namespace ScaleSet.Hosting.Controller;

/// <summary>Generates just-in-time runner configuration for one scale set.</summary>
public interface IRunnerJitConfigClient
{
    /// <summary>Gets a short-lived runner configuration from GitHub.</summary>
    Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
        long scaleSetId,
        RunnerScaleSetJitRunnerSetting runnerSetting,
        CancellationToken cancellationToken = default);
}

/// <summary>Adapts the reusable ScaleSet client to the hosting provisioning seam.</summary>
public sealed class ScaleSetRunnerJitConfigClient : IRunnerJitConfigClient
{
    private readonly ScaleSetClient _client;

    /// <summary>Creates an adapter around a caller-owned client.</summary>
    public ScaleSetRunnerJitConfigClient(ScaleSetClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public async Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
        long scaleSetId,
        RunnerScaleSetJitRunnerSetting runnerSetting,
        CancellationToken cancellationToken = default) =>
        await _client.GenerateJitConfigAsync(scaleSetId, runnerSetting, cancellationToken).ConfigureAwait(false);
}
