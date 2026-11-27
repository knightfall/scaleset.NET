using Microsoft.Extensions.Logging;
using ScaleSet.Client;
using ScaleSet.Hosting.Providers;

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

/// <summary>Optional target-scoped lookup used to reconcile JIT requests with an unknown outcome.</summary>
public interface IScaleSetRunnerNameLookup
{
    /// <summary>Looks up the exact runner name. Null represents an authoritative empty result.</summary>
    Task<RunnerReference?> FindRunnerByNameAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Optional point-in-time evidence for leaving an exact warm runner available during controller shutdown.</summary>
public interface IScaleSetRunnerIdleLookup
{
    /// <summary>Checks the exact runner's scale-set membership and idle status.</summary>
    Task<bool> IsRunnerIdleAsync(RunnerIdentity identity, CancellationToken cancellationToken);
}

/// <summary>Creates a production ScaleSet client adapter for runtime control-plane calls.</summary>
public sealed class ScaleSetRuntimeClientAdapter : IScaleSetRuntimeClient, IScaleSetRunnerNameLookup, IScaleSetRunnerIdleLookup
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
    public Task<RunnerReference?> FindRunnerByNameAsync(string name, CancellationToken cancellationToken) =>
        _client.FindRunnerByNameAsync(name, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> IsRunnerIdleAsync(RunnerIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.GitHubRunnerId is not long runnerId)
        {
            return false;
        }

        try
        {
            return await _client.IsRunnerIdleAsync(identity.Scope.ScaleSetId, runnerId, identity.RunnerName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ScaleSetApiException)
        {
            // Authorization, throttling and missing data leave the runner protected.
            return false;
        }
    }

    /// <inheritdoc />
    public Task RemoveRunnerAsync(long runnerId, CancellationToken cancellationToken) =>
        _client.RemoveRunnerAsync(runnerId, cancellationToken);

    /// <inheritdoc />
    public async Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
        long scaleSetId,
        RunnerScaleSetJitRunnerSetting runnerSetting,
        CancellationToken cancellationToken) =>
        await GenerateJitConfigAsync(scaleSetId, runnerSetting, onDispatch: null, cancellationToken).ConfigureAwait(false);

    internal async Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
        long scaleSetId, RunnerScaleSetJitRunnerSetting setting, Action? onDispatch, CancellationToken cancellationToken)
    {
        using var retry = ScaleSetRequestRetry.SuppressMutationReplay();
        return await _client.GenerateJitConfigAsync(scaleSetId, setting, onDispatch, cancellationToken).ConfigureAwait(false);
    }

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
