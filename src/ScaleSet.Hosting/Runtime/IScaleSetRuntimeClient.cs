using Microsoft.Extensions.Logging;
using ScaleSet.Client;

namespace ScaleSet.Hosting.Runtime;

/// <summary>Control-plane calls the autoscaler runtime needs from one GitHub target.</summary>
public interface IScaleSetRuntimeClient : IDisposable
{
    /// <summary>Finds the configured runner group.</summary>
    Task<RunnerGroup> GetRunnerGroupByNameAsync(string name, CancellationToken cancellationToken);

    /// <summary>Finds a named scale set in a runner group.</summary>
    Task<RunnerScaleSet?> FindScaleSetAsync(long runnerGroupId, string name, CancellationToken cancellationToken);

    /// <summary>Creates a scale set during a clean first start.</summary>
    Task<RunnerScaleSet> CreateScaleSetAsync(RunnerScaleSet scaleSet, CancellationToken cancellationToken);

    /// <summary>Looks up the exact runner registration in the scale set's GitHub target.</summary>
    Task<RunnerReference?> GetRunnerAsync(long runnerId, CancellationToken cancellationToken);

    /// <summary>Removes a runner registration when the GitHub service permits it.</summary>
    Task RemoveRunnerAsync(long runnerId, CancellationToken cancellationToken);

    /// <summary>Gets a short-lived runner configuration for the resolved scale set.</summary>
    Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
        long scaleSetId,
        RunnerScaleSetJitRunnerSetting runnerSetting,
        CancellationToken cancellationToken);

    /// <summary>Opens a message session for the resolved scale-set identity.</summary>
    Task<IMessageSession> CreateMessageSessionAsync(long scaleSetId, string ownerName, CancellationToken cancellationToken);
}

/// <summary>Creates a production ScaleSet client adapter for runtime control-plane calls.</summary>
public sealed class ScaleSetRuntimeClientAdapter : IScaleSetRuntimeClient
{
    private readonly ScaleSetClient _client;

    /// <summary>Creates an adapter around the client whose lifetime it owns.</summary>
    public ScaleSetRuntimeClientAdapter(ScaleSetClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public Task<RunnerGroup> GetRunnerGroupByNameAsync(string name, CancellationToken cancellationToken) =>
        _client.GetRunnerGroupByNameAsync(name, cancellationToken);

    /// <inheritdoc />
    public Task<RunnerScaleSet?> FindScaleSetAsync(long runnerGroupId, string name, CancellationToken cancellationToken) =>
        _client.FindScaleSetAsync(runnerGroupId, name, cancellationToken);

    /// <inheritdoc />
    public Task<RunnerScaleSet> CreateScaleSetAsync(RunnerScaleSet scaleSet, CancellationToken cancellationToken) =>
        _client.CreateScaleSetAsync(scaleSet, cancellationToken);

    /// <inheritdoc />
    public Task<RunnerReference?> GetRunnerAsync(long runnerId, CancellationToken cancellationToken) =>
        _client.GetRunnerAsync(runnerId, cancellationToken);

    /// <inheritdoc />
    public Task RemoveRunnerAsync(long runnerId, CancellationToken cancellationToken) =>
        _client.RemoveRunnerAsync(runnerId, cancellationToken);

    /// <inheritdoc />
    public async Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
        long scaleSetId,
        RunnerScaleSetJitRunnerSetting runnerSetting,
        CancellationToken cancellationToken) =>
        await _client.GenerateJitConfigAsync(scaleSetId, runnerSetting, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IMessageSession> CreateMessageSessionAsync(
        long scaleSetId,
        string ownerName,
        CancellationToken cancellationToken) =>
        await _client.CreateMessageSessionAsync(scaleSetId, ownerName, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}

/// <summary>Creates the control-plane client owned by a single scale-set runtime.</summary>
public interface IScaleSetRuntimeClientFactory
{
    /// <summary>Creates a client scoped to the configured GitHub target and credentials.</summary>
    /// <param name="target">GitHub scope the runtime manages.</param>
    /// <param name="credentials">Credentials for that scope.</param>
    /// <param name="options">Control request settings for this client.</param>
    /// <returns>A new client that the runtime will dispose after closing its session.</returns>
    IScaleSetRuntimeClient Create(GitHubTarget target, ScaleSetCredentials credentials, ScaleSetClientOptions options);
}

internal sealed class DefaultScaleSetRuntimeClientFactory(ILoggerFactory loggerFactory, TimeProvider timeProvider) : IScaleSetRuntimeClientFactory
{
    public IScaleSetRuntimeClient Create(GitHubTarget target, ScaleSetCredentials credentials, ScaleSetClientOptions options) =>
        new ScaleSetRuntimeClientAdapter(new ScaleSetClient(
            target, credentials, options, logger: loggerFactory.CreateLogger<ScaleSetClient>(), timeProvider: timeProvider));
}
