using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScaleSet.Client;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Controller;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;
using ScaleSet.Hosting.Telemetry;
using ScaleSet.Listener;
using ScaleSet.Providers.Docker;

namespace ScaleSet.Autoscaler.Runtime;

/// <summary>Control-plane calls the autoscaler runtime needs from one GitHub target.</summary>
public interface IScaleSetRuntimeClient : IDisposable
{
    /// <summary>Finds the configured runner group.</summary>
    Task<RunnerGroup> GetRunnerGroupByNameAsync(string name, CancellationToken cancellationToken);

    /// <summary>Finds a named scale set in a runner group.</summary>
    Task<RunnerScaleSet?> FindScaleSetAsync(long runnerGroupId, string name, CancellationToken cancellationToken);

    /// <summary>Creates a scale set during a clean first start.</summary>
    Task<RunnerScaleSet> CreateScaleSetAsync(RunnerScaleSet scaleSet, CancellationToken cancellationToken);

    /// <summary>Gets a short-lived runner configuration for the resolved scale set.</summary>
    Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
        long scaleSetId,
        RunnerScaleSetJitRunnerSetting runnerSetting,
        CancellationToken cancellationToken);

    /// <summary>Opens a message session for the resolved scale-set identity.</summary>
    Task<IScaleSetRuntimeSession> CreateMessageSessionAsync(long scaleSetId, string ownerName, CancellationToken cancellationToken);
}

/// <summary>A queue session that can be closed independently from runner resources.</summary>
public interface IScaleSetRuntimeSession : IMessageSessionClient
{
    /// <summary>Closes the GitHub message session.</summary>
    Task CloseAsync(CancellationToken cancellationToken);
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
    public async Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
        long scaleSetId,
        RunnerScaleSetJitRunnerSetting runnerSetting,
        CancellationToken cancellationToken) =>
        await _client.GenerateJitConfigAsync(scaleSetId, runnerSetting, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IScaleSetRuntimeSession> CreateMessageSessionAsync(
        long scaleSetId,
        string ownerName,
        CancellationToken cancellationToken) =>
        new ScaleSetRuntimeSessionAdapter(await _client.CreateMessageSessionAsync(scaleSetId, ownerName, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private sealed class ScaleSetRuntimeSessionAdapter(MessageSessionClient session) : IScaleSetRuntimeSession
    {
        public RunnerScaleSetSession Session => session.Session;

        public Task<RunnerScaleSetMessage?> GetMessageAsync(long lastMessageId, int maxCapacity, CancellationToken cancellationToken) =>
            session.GetMessageAsync(lastMessageId, maxCapacity, cancellationToken);

        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken) =>
            session.DeleteMessageAsync(messageId, cancellationToken);

        public Task<IReadOnlyList<long>> AcquireJobsAsync(IReadOnlyList<long> runnerRequestIds, CancellationToken cancellationToken) =>
            session.AcquireJobsAsync(runnerRequestIds, cancellationToken);

        public Task CloseAsync(CancellationToken cancellationToken) => session.CloseAsync(cancellationToken);
    }
}

/// <summary>
/// Builds Docker-backed per-scale-set runtimes from the validated environment
/// configuration and a process-wide host capacity coordinator.
/// </summary>
public sealed partial class ScaleSetRuntimeFactory : IScaleSetRuntimeFactory
{
    private readonly AutoscalerOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Func<
        GitHubTarget,
        ScaleSetCredentials,
        ScaleSetClientOptions,
        ILogger<ScaleSetClient>,
        TimeProvider,
        IScaleSetRuntimeClient> _clientFactory;
    private readonly Func<ScaleSetOptions, ILogger<DockerRunnerProvider>, TimeProvider, IRunnerProvider> _providerFactory;
    private readonly ScaleSetTelemetryMetrics? _telemetry;

    /// <summary>Creates a runtime factory; optional factories keep the runtime replaceable in tests.</summary>
    public ScaleSetRuntimeFactory(
        AutoscalerOptions options,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null,
        Func<GitHubTarget, ScaleSetCredentials, ScaleSetClientOptions, ILogger<ScaleSetClient>, TimeProvider, IScaleSetRuntimeClient>? clientFactory = null,
        Func<ScaleSetOptions, ILogger<DockerRunnerProvider>, TimeProvider, IRunnerProvider>? providerFactory = null,
        ScaleSetTelemetryMetrics? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _options = options;
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clientFactory = clientFactory ?? CreateScaleSetClient;
        _providerFactory = providerFactory ?? CreateDockerProvider;
        _telemetry = telemetry;
    }

    /// <inheritdoc />
    public Task<IScaleSetRuntime> CreateAsync(
        ScaleSetOptions scaleSet,
        HostCapacityCoordinator capacityCoordinator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scaleSet);
        ArgumentNullException.ThrowIfNull(capacityCoordinator);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(scaleSet.Key)
            || string.IsNullOrWhiteSpace(scaleSet.CredentialsRef)
            || !_options.Credentials.TryGetValue(scaleSet.CredentialsRef, out CredentialOptions? credential)
            || credential is null)
        {
            throw new AutoscalerConfigurationException("A configured scale set references an unavailable credential.");
        }

        GitHubTarget target = GitHubTarget.Parse(scaleSet.GithubUrl!);
        ScaleSetCredentials credentials = CreateCredentials(credential);
        ScaleSetClientOptions clientOptions = new()
        {
            ControlRequestTimeout = _options.Controller.ControlOperationTimeout,
        };
        IScaleSetRuntimeClient? runtimeClient = null;
        IRunnerProvider? provider = null;
        try
        {
            runtimeClient = _clientFactory(
                target,
                credentials,
                clientOptions,
                _loggerFactory.CreateLogger<ScaleSetClient>(),
                _timeProvider)
                ?? throw new InvalidOperationException("The scale-set client factory returned no client.");
            provider = _providerFactory(
                scaleSet,
                _loggerFactory.CreateLogger<DockerRunnerProvider>(),
                _timeProvider)
                ?? throw new InvalidOperationException("The runner provider factory returned no provider.");
            IScaleSetRuntime runtime = new ScaleSetRuntime(
                _options.Controller,
                scaleSet,
                runtimeClient,
                provider,
                capacityCoordinator,
                _loggerFactory,
                _timeProvider,
                _telemetry);
            return Task.FromResult(runtime);
        }
        catch (Exception exception)
        {
            List<Exception> failures = [exception];
            CaptureDisposalFailure(() => DisposeProvider(provider), failures);
            CaptureDisposalFailure(() => runtimeClient?.Dispose(), failures);
            if (failures.Count > 1)
            {
                throw new AggregateException("Runtime creation and cleanup failed.", failures);
            }

            throw;
        }
    }

    private static ScaleSetRuntimeClientAdapter CreateScaleSetClient(
        GitHubTarget target,
        ScaleSetCredentials credentials,
        ScaleSetClientOptions options,
        ILogger<ScaleSetClient> logger,
        TimeProvider timeProvider) =>
        new ScaleSetRuntimeClientAdapter(new ScaleSetClient(target, credentials, options, logger: logger, timeProvider: timeProvider));

    private static DockerRunnerProvider CreateDockerProvider(
        ScaleSetOptions scaleSet,
        ILogger<DockerRunnerProvider> logger,
        TimeProvider timeProvider) =>
        new DockerRunnerProvider(scaleSet.RunnerProfile, logger: logger, timeProvider: timeProvider);

    private static ScaleSetCredentials CreateCredentials(CredentialOptions credential)
    {
        if (!string.IsNullOrWhiteSpace(credential.Pat))
        {
            return ScaleSetCredentials.FromPersonalAccessToken(credential.Pat);
        }

        if (!long.TryParse(credential.InstallationId, NumberStyles.None, CultureInfo.InvariantCulture, out long installationId)
            || installationId <= 0
            || string.IsNullOrWhiteSpace(credential.ClientId)
            || string.IsNullOrWhiteSpace(credential.PrivateKeyPem))
        {
            throw new AutoscalerConfigurationException("A scale-set GitHub App credential is incomplete.");
        }

        return ScaleSetCredentials.FromGitHubApp(credential.ClientId, installationId, credential.PrivateKeyPem);
    }

    private static void DisposeProvider(IRunnerProvider? provider)
    {
        if (provider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static void CaptureDisposalFailure(Action dispose, List<Exception> failures)
    {
        try
        {
            dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private sealed record ScaleSetIdentityMarker(
        int SchemaVersion,
        string Target,
        string RunnerGroupName,
        string ScaleSetName,
        string ControllerIdentity,
        long? ScaleSetId,
        bool CreationInProgress);

    private sealed partial class ScaleSetRuntime : IScaleSetRuntime
    {
        private const string DefaultRunnerGroup = "Default";
        private readonly ControllerOptions _controllerOptions;
        private readonly ScaleSetOptions _scaleSetOptions;
        private readonly IScaleSetRuntimeClient _client;
        private readonly IRunnerProvider _provider;
        private readonly HostCapacityCoordinator _capacityCoordinator;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<ScaleSetRuntime> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly ScaleSetRuntimeTelemetry _metrics;
        private readonly object _sync = new();
        private readonly CancellationTokenSource _stopSignal = new();
        private ScaleSetBootstrapLease? _bootstrapLease;
        private FileRunnerStateJournal? _journal;
        private RunnerLifecycleManager? _lifecycle;
        private ScaleSetRunnerProvisioner? _provisioner;
        private IScaleSetRuntimeSession? _session;
        private ScaleSetMessageHandler? _handler;
        private TaskCompletionSource? _activeOperation;
        private Task? _drainTask;
        private Task? _stopTask;
        private Task? _disposeTask;
        private long _scaleSetId;
        private bool _initializationStarted;
        private bool _initialized;
        private bool _listenerStarted;
        private bool _stopRequested;

        public ScaleSetRuntime(
            ControllerOptions controllerOptions,
            ScaleSetOptions scaleSetOptions,
            IScaleSetRuntimeClient client,
            IRunnerProvider provider,
            HostCapacityCoordinator capacityCoordinator,
            ILoggerFactory loggerFactory,
            TimeProvider timeProvider,
            ScaleSetTelemetryMetrics? telemetry)
        {
            _controllerOptions = controllerOptions;
            _scaleSetOptions = scaleSetOptions;
            _client = client;
            _provider = provider;
            _capacityCoordinator = capacityCoordinator;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<ScaleSetRuntime>();
            _timeProvider = timeProvider;
            _metrics = new ScaleSetRuntimeTelemetry(telemetry, ScaleSetKey);
        }

        public string ScaleSetKey => _scaleSetOptions.Key!;

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource operation;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                if (_initializationStarted || _stopRequested)
                {
                    throw new InvalidOperationException("The scale-set runtime can initialize only once, before shutdown.");
                }

                _initializationStarted = true;
                operation = _activeOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            try
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopSignal.Token);
                await InitializeCoreAsync(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                CompleteOperation(operation);
            }
        }

        private async Task InitializeCoreAsync(CancellationToken cancellationToken)
        {
            if (!_provider.Capabilities.SupportsOwnershipScopedDiscovery)
            {
                throw new InvalidOperationException("The configured provider must support ownership-scoped discovery.");
            }

            using CancellationTokenSource startup = new(_scaleSetOptions.Lifecycle.StartupTimeout, _timeProvider);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startup.Token);
            CancellationToken token = linked.Token;

            _bootstrapLease = await ScaleSetBootstrapLease.OpenAsync(
                _controllerOptions.StateDirectory!,
                ScaleSetKey,
                token).ConfigureAwait(false);

            string groupName = string.IsNullOrWhiteSpace(_scaleSetOptions.RunnerGroup)
                ? DefaultRunnerGroup
                : _scaleSetOptions.RunnerGroup;
            GitHubTarget target = GitHubTarget.Parse(_scaleSetOptions.GithubUrl!);
            ScaleSetIdentityMarker? marker = await _bootstrapLease.ReadIdentityAsync(token).ConfigureAwait(false);
            if (marker is not null)
            {
                ValidateIdentityMarker(marker, target, groupName, _scaleSetOptions.Name!, _controllerOptions.Identity);
            }

            RunnerGroup group = await _client.GetRunnerGroupByNameAsync(groupName, token).ConfigureAwait(false);
            if (group.Id <= 0)
            {
                throw new InvalidDataException("GitHub returned an invalid runner-group ID.");
            }

            RunnerScaleSet? scaleSet = await _client.FindScaleSetAsync(group.Id, _scaleSetOptions.Name!, token).ConfigureAwait(false);
            if (scaleSet is null)
            {
                if (marker is not null || _bootstrapLease.HasExistingJournal)
                {
                    throw new InvalidOperationException("The previously owned scale set is missing; refusing to create a replacement and change its identity.");
                }

                marker = new ScaleSetIdentityMarker(
                    1,
                    target.SourceUri.AbsoluteUri,
                    groupName,
                    _scaleSetOptions.Name!,
                    _controllerOptions.Identity,
                    null,
                    true);
                await _bootstrapLease.WriteIdentityAsync(marker, token).ConfigureAwait(false);
                scaleSet = await CreateOrResolveScaleSetAsync(group, _bootstrapLease, token).ConfigureAwait(false);
            }

            ValidateResolvedScaleSet(scaleSet, group, _scaleSetOptions.Name!);
            if (marker?.ScaleSetId is long persistedId && persistedId != scaleSet.Id)
            {
                throw new InvalidDataException("The resolved scale-set ID differs from the persisted scale-set identity.");
            }

            await _bootstrapLease.WriteIdentityAsync(
                new ScaleSetIdentityMarker(
                    1,
                    target.SourceUri.AbsoluteUri,
                    groupName,
                    _scaleSetOptions.Name!,
                    _controllerOptions.Identity,
                    scaleSet.Id,
                    false),
                token).ConfigureAwait(false);
            _scaleSetId = scaleSet.Id;
            RunnerOwnershipScope scope = new(_controllerOptions.Identity, ScaleSetKey, _scaleSetId);
            _journal = await FileRunnerStateJournal.OpenAsync(
                _bootstrapLease.StateDirectory,
                scope,
                token).ConfigureAwait(false);
            _lifecycle = new RunnerLifecycleManager(
                _journal,
                _timeProvider,
                _loggerFactory.CreateLogger<RunnerLifecycleManager>());
            _provisioner = new ScaleSetRunnerProvisioner(
                _scaleSetId,
                _scaleSetOptions.RunnerProfile.WorkFolder,
                new RuntimeJitConfigClient(_client),
                _provider,
                _journal,
                _loggerFactory.CreateLogger<ScaleSetRunnerProvisioner>(),
                _timeProvider,
                _metrics.Metrics,
                ScaleSetTelemetryProvider.Docker);

            await _metrics.MeasureAsync(
                ScaleSetTelemetryOperation.Reconcile,
                () => ReconcileInventoryAsync(scope, token)).ConfigureAwait(false);
            _initialized = true;
            RuntimeInitialized(_logger, ScaleSetKey, _scaleSetId);
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource operation;
            lock (_sync)
            {
                if (_stopRequested)
                {
                    return;
                }

                if (!_initialized || _activeOperation is not null)
                {
                    throw new InvalidOperationException("The scale-set runtime must finish initialization before it runs.");
                }

                if (_listenerStarted)
                {
                    throw new InvalidOperationException("The scale-set runtime can start its listener only once.");
                }

                _listenerStarted = true;
                operation = _activeOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            try
            {
                using CancellationTokenSource polling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopSignal.Token);
                await RunListenerAsync(polling.Token).ConfigureAwait(false);
            }
            finally
            {
                CompleteOperation(operation);
            }
        }

        private async Task RunListenerAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IScaleSetRuntimeSession session = await _client.CreateMessageSessionAsync(
                _scaleSetId,
                _controllerOptions.Identity,
                cancellationToken).ConfigureAwait(false);
            if (_metrics.Metrics is not null)
            {
                session = new ScaleSetRuntimeTelemetry.MeteredSession(session, _metrics);
            }

            lock (_sync)
            {
                // Record the session even if shutdown raced with its creation.
                // Stop closes it after the listener operation has returned.
                _session = session;
                if (_stopRequested)
                {
                    return;
                }
            }

            ScaleSetListener listener = new(
                session,
                new ScaleSetListenerOptions
                {
                    ScaleSetId = _scaleSetId,
                    MaxRunners = _scaleSetOptions.Scaling.MaxRunners,
                    AcknowledgmentTimeout = _controllerOptions.ControlOperationTimeout,
                },
                _loggerFactory.CreateLogger<ScaleSetListener>(),
                _timeProvider);
            ScaleSetCapacityContext capacityContext = new(
                _scaleSetOptions,
                _capacityCoordinator,
                _capacityCoordinator.DemandRegistry,
                listener.SetMaxRunners);
            ScaleSetMessageHandler handler = new(
                _journal!.Scope,
                session,
                _provisioner!,
                _lifecycle!,
                _journal,
                capacityContext,
                _loggerFactory.CreateLogger<ScaleSetMessageHandler>());

            lock (_sync)
            {
                if (_stopRequested)
                {
                    return;
                }

                _handler = handler;
            }

            await listener.RunAsync(handler, cancellationToken).ConfigureAwait(false);
        }

        private void CompleteOperation(TaskCompletionSource operation)
        {
            lock (_sync)
            {
                _activeOperation = null;
                operation.SetResult();
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Polling and active callbacks must settle before session closure.
            // Closure gets a separate per-set cleanup budget.
            _ = cancellationToken;
            lock (_sync)
            {
                _stopRequested = true;
                return _stopTask ??= StopCoreAsync(_activeOperation?.Task, _drainTask);
            }
        }

        private async Task StopCoreAsync(Task? activeOperation, Task? activeDrain)
        {
            // The task is installed under the lifetime lock before running any cleanup.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            _handler?.BeginDrain();
            try
            {
                await _stopSignal.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                if (activeOperation is not null)
                {
                    await activeOperation.ConfigureAwait(false);
                }

                if (activeDrain is not null)
                {
                    // The caller of DrainAsync observes its own failure or cancellation.
                    await activeDrain.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                IScaleSetRuntimeSession? session = _session;
                _session = null;
                if (session is not null)
                {
                    using CancellationTokenSource cleanup = new(_scaleSetOptions.Lifecycle.CleanupTimeout, _timeProvider);
                    await session.CloseAsync(cleanup.Token).ConfigureAwait(false);
                    RuntimeSessionClosed(_logger, ScaleSetKey);
                }
            }
        }

        public Task DrainAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _stopRequested = true;
                if (_stopTask is not null || _disposeTask is not null || _handler is null)
                {
                    return Task.CompletedTask;
                }

                _handler.BeginDrain();
                return _drainTask ??= DrainCoreAsync(_handler, cancellationToken);
            }
        }

        private async Task DrainCoreAsync(ScaleSetMessageHandler handler, CancellationToken cancellationToken)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopSignal.Token);
            bool drained = await _metrics.MeasureAsync(
                ScaleSetTelemetryOperation.Drain,
                () => handler.WaitForDrainAsync(_scaleSetOptions.Lifecycle.ShutdownDrainTimeout, _timeProvider, linked.Token),
                static completed => completed ? ScaleSetTelemetryOutcome.Success : ScaleSetTelemetryOutcome.Unknown).ConfigureAwait(false);
            if (!drained)
            {
                RuntimeDrainDeadline(_logger, ScaleSetKey, _scaleSetOptions.Lifecycle.ShutdownDrainTimeout);
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                _stopRequested = true;
                return new ValueTask(_disposeTask ??= DisposeCoreAsync());
            }
        }

        private async Task DisposeCoreAsync()
        {
            List<Exception> failures = [];
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            CaptureDisposalFailure(() => _provisioner?.Dispose(), failures);
            CaptureDisposalFailure(() => _lifecycle?.Dispose(), failures);
            await CaptureAsyncDisposalFailure(_journal, failures).ConfigureAwait(false);
            CaptureDisposalFailure(() => DisposeProvider(_provider), failures);
            CaptureDisposalFailure(_client.Dispose, failures);
            await CaptureAsyncDisposalFailure(_bootstrapLease, failures).ConfigureAwait(false);
            CaptureDisposalFailure(_stopSignal.Dispose, failures);

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Throw(failures[0]);
            }

            if (failures.Count > 1)
            {
                throw new AggregateException("Multiple scale-set runtime resources failed to close.", failures);
            }
        }

        private static async Task CaptureAsyncDisposalFailure(IAsyncDisposable? resource, List<Exception> failures)
        {
            if (resource is null)
            {
                return;
            }

            try
            {
                await resource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        private async Task<RunnerScaleSet> CreateOrResolveScaleSetAsync(
            RunnerGroup group,
            ScaleSetBootstrapLease bootstrapLease,
            CancellationToken cancellationToken)
        {
            RunnerScaleSet request = new()
            {
                Name = _scaleSetOptions.Name,
                RunnerGroupId = group.Id,
                RunnerGroupName = group.Name,
                Labels = CreateScaleSetLabels(_scaleSetOptions),
                RunnerSetting = new RunnerSetting { DisableUpdate = true },
            };

            try
            {
                return await _client.CreateScaleSetAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (ScaleSetApiException exception) when (exception.StatusCode is 400 or 401 or 403 or 404 or 422)
            {
                // The server rejected the request. A future startup can try again
                // after the operator corrects the configuration or credential.
                bootstrapLease.ClearPendingIdentity();
                throw;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Create is a mutation and the response may be lost. Resolve by
                // the stable configured name before deciding the identity is absent.
                RunnerScaleSet? resolved = await _client.FindScaleSetAsync(group.Id, _scaleSetOptions.Name!, cancellationToken).ConfigureAwait(false);
                return resolved ?? throw new InvalidOperationException(
                    "Scale-set creation outcome is uncertain; refusing to create another set.", exception);
            }
        }

        private async Task ReconcileInventoryAsync(RunnerOwnershipScope scope, CancellationToken cancellationToken)
        {
            FileRunnerStateJournal journal = _journal ?? throw new InvalidOperationException("The runner journal is not open.");
            RunnerLifecycleManager lifecycle = _lifecycle ?? throw new InvalidOperationException("The lifecycle manager is not open.");
            ImmutableArray<RunnerResource> discovered = await _provider.DiscoverAsync(scope, cancellationToken).ConfigureAwait(false);
            ImmutableArray<RunnerOperationState> journalStates = await journal.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<Guid, RunnerOperationState> statesById = journalStates.ToDictionary(static state => state.Identity.OperationId);
            HashSet<Guid> reconciled = [];
            int adoptedCount = 0;

            foreach (RunnerResource resource in discovered)
            {
                if (resource.Identity.Scope != scope)
                {
                    throw new InvalidDataException("Provider discovery returned a resource outside the configured ownership scope.");
                }

                reconciled.Add(resource.Identity.OperationId);
                if (resource.Status == RunnerResourceStatus.Missing)
                {
                    if (statesById.ContainsKey(resource.Identity.OperationId))
                    {
                        await lifecycle.ConfirmGoneAsync(resource.Identity, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (statesById.TryGetValue(resource.Identity.OperationId, out RunnerOperationState? completedPrior)
                    && completedPrior.Stage is (RunnerLifecycleStage.Completed or RunnerLifecycleStage.Removing))
                {
                    EnsureResourceMatchesJournal(resource, completedPrior, scope);
                    if (_provisioner is not null
                        && await _provisioner.ReconcileCompletedAsync(completedPrior.Identity, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    AdoptCapacity(
                        completedPrior.Identity,
                        GetObservedCapacityResources(resource, completedPrior.Identity),
                        CapacityOccupancyState.Unknown);
                    adoptedCount++;
                    continue;
                }

                if (statesById.TryGetValue(resource.Identity.OperationId, out RunnerOperationState? prior)
                    && IsStartingCreatedRecovery(prior, resource))
                {
                    EnsureResourceMatchesJournal(resource, prior, scope);
                    // Keep the durable Starting intent: the provisioner can
                    // safely retry StartAsync on this exact Created resource.
                    AdoptCapacity(
                        prior.Identity,
                        GetObservedCapacityResources(resource, prior.Identity),
                        CapacityOccupancyState.Unknown);
                    adoptedCount++;
                    continue;
                }

                RunnerOperationState adopted = await lifecycle.AdoptDiscoveredAsync(resource, cancellationToken).ConfigureAwait(false);
                AdoptCapacity(
                    adopted.Identity,
                    GetObservedCapacityResources(resource, journalIdentity: null),
                    CapacityOccupancyState.Unknown);
                adoptedCount++;
            }

            foreach (RunnerOperationState state in journalStates)
            {
                if (reconciled.Contains(state.Identity.OperationId)
                    || state.Stage is RunnerLifecycleStage.Gone or RunnerLifecycleStage.Failed)
                {
                    continue;
                }

                if (state.ProviderResourceId is not null)
                {
                    RunnerResource probe = new(
                        state.ProviderResourceId,
                        state.Identity,
                        state.LastObservedProviderStatus ?? RunnerResourceStatus.Unknown);
                    RunnerResource? inspected = await _provider.InspectAsync(probe, cancellationToken).ConfigureAwait(false);
                    if (inspected is null || inspected.Status == RunnerResourceStatus.Missing)
                    {
                        await lifecycle.ConfirmGoneAsync(state.Identity, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (!string.Equals(inspected.ProviderResourceId, state.ProviderResourceId, StringComparison.Ordinal)
                        || inspected.Identity.Scope != scope
                        || inspected.Identity.OperationId != state.Identity.OperationId)
                    {
                        throw new InvalidDataException("Provider inspection contradicted the journaled runner identity.");
                    }

                    if (IsStartingCreatedRecovery(state, inspected))
                    {
                        EnsureResourceMatchesJournal(inspected, state, scope);
                        AdoptCapacity(
                            state.Identity,
                            GetObservedCapacityResources(inspected, state.Identity),
                            CapacityOccupancyState.Unknown);
                        adoptedCount++;
                        continue;
                    }

                    RunnerOperationState adopted = await lifecycle.AdoptDiscoveredAsync(inspected, cancellationToken).ConfigureAwait(false);
                    AdoptCapacity(
                        adopted.Identity,
                        GetObservedCapacityResources(inspected, state.Identity),
                        CapacityOccupancyState.Unknown);
                    adoptedCount++;
                    continue;
                }

                RunnerOperationState uncertain = state.Stage == RunnerLifecycleStage.Unknown
                    ? state
                    : await lifecycle.TransitionAsync(
                        state.Identity,
                        RunnerLifecycleStage.Unknown,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                AdoptCapacity(
                    uncertain.Identity,
                    state.Identity.CapacityResources ?? new CapacityResourceQuantities(null, null),
                    CapacityOccupancyState.Unknown);
                adoptedCount++;
            }

            InventoryReconciled(_logger, ScaleSetKey, adoptedCount);
        }

        private void AdoptCapacity(
            RunnerIdentity identity,
            CapacityResourceQuantities resources,
            CapacityOccupancyState state)
        {
            CapacityInventoryAdoptionAttempt attempt = _capacityCoordinator.AdoptInventory(
                new CapacityInventoryItem(identity, resources, state));
            if (attempt.Status is CapacityInventoryAdoptionStatus.IdentityConflict or CapacityInventoryAdoptionStatus.VersionExhausted)
            {
                throw new InvalidDataException($"Capacity inventory could not adopt runner operation {identity.OperationId}.");
            }

            if (attempt.Handle is not null)
            {
                _capacityCoordinator.DemandRegistry.StoreCapacityLease(
                    new CapacityReservationLease(attempt.Handle, attempt.Revision));
            }

            // ResourceConflict deliberately remains Unknown in the shared ledger;
            // its unknown dimensions block further use of that host budget.
        }

        private static CapacityResourceQuantities GetObservedCapacityResources(
            RunnerResource resource,
            RunnerIdentity? journalIdentity) =>
            resource.CapacityResources
                ?? resource.Identity.CapacityResources
                ?? journalIdentity?.CapacityResources
                ?? new CapacityResourceQuantities(null, null);

        private static Label[] CreateScaleSetLabels(ScaleSetOptions options)
        {
            if (options.Labels.Count == 0)
            {
                return [new Label { Name = options.Name!, Type = "System" }];
            }

            return options.Labels
                .Select(static label => new Label { Name = label })
                .ToArray();
        }

        private static void ValidateIdentityMarker(
            ScaleSetIdentityMarker marker,
            GitHubTarget target,
            string groupName,
            string scaleSetName,
            string controllerIdentity)
        {
            if (!string.Equals(marker.Target, target.SourceUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(marker.RunnerGroupName, groupName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(marker.ScaleSetName, scaleSetName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(marker.ControllerIdentity, controllerIdentity, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The configured target or scale-set name conflicts with the persisted scale-set identity.");
            }
        }

        private static bool IsStartingCreatedRecovery(RunnerOperationState state, RunnerResource resource) =>
            state.Stage == RunnerLifecycleStage.Starting
            && state.ProviderResourceId is not null
            && string.Equals(state.ProviderResourceId, resource.ProviderResourceId, StringComparison.Ordinal)
            && resource.Status == RunnerResourceStatus.Created
            && resource.StartedAt is null
            && resource.FinishedAt is null
            && state.LastObservedProviderStatus is null or RunnerResourceStatus.Created or RunnerResourceStatus.Unknown;

        private static void EnsureResourceMatchesJournal(
            RunnerResource resource,
            RunnerOperationState state,
            RunnerOwnershipScope scope)
        {
            bool same = resource.Identity.Scope == scope
                && resource.Identity.OperationId == state.Identity.OperationId
                && string.Equals(resource.Identity.RunnerName, state.Identity.RunnerName, StringComparison.Ordinal)
                && (resource.Identity.GitHubRunnerId is null
                    || state.Identity.GitHubRunnerId is null
                    || resource.Identity.GitHubRunnerId == state.Identity.GitHubRunnerId);
            if (!same)
            {
                throw new InvalidDataException("Provider discovery returned a Created resource that conflicts with the durable Starting operation.");
            }
        }

        private static void ValidateResolvedScaleSet(RunnerScaleSet scaleSet, RunnerGroup group, string configuredName)
        {
            if (scaleSet.Id <= 0
                || scaleSet.RunnerGroupId != group.Id
                || !string.Equals(scaleSet.Name, configuredName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("GitHub returned a scale set with an invalid or conflicting identity.");
            }
        }

        private sealed class RuntimeJitConfigClient(IScaleSetRuntimeClient client) : IRunnerJitConfigClient
        {
            public Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
                long scaleSetId,
                RunnerScaleSetJitRunnerSetting runnerSetting,
                CancellationToken cancellationToken = default) =>
                client.GenerateJitConfigAsync(scaleSetId, runnerSetting, cancellationToken);
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Initialized scale set {ScaleSetKey} with GitHub ID {ScaleSetId}.")]
        private static partial void RuntimeInitialized(ILogger logger, string scaleSetKey, long scaleSetId);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Reconciled {RunnerCount} runner operations for scale set {ScaleSetKey}.")]
        private static partial void InventoryReconciled(ILogger logger, string scaleSetKey, int runnerCount);

        [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Closed the message session for scale set {ScaleSetKey}.")]
        private static partial void RuntimeSessionClosed(ILogger logger, string scaleSetKey);

        [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "The shutdown drain deadline of {DrainTimeout} elapsed for scale set {ScaleSetKey}; unfinished and uncertain runners will be preserved.")]
        private static partial void RuntimeDrainDeadline(ILogger logger, string scaleSetKey, TimeSpan drainTimeout);
    }

    private sealed class ScaleSetBootstrapLease : IAsyncDisposable
    {
        private readonly FileStream _lease;

        private ScaleSetBootstrapLease(string stateDirectory, FileStream lease)
        {
            StateDirectory = stateDirectory;
            _lease = lease;
        }

        public string StateDirectory { get; }

        public bool HasExistingJournal => File.Exists(Path.Combine(StateDirectory, "runner-state.json"));

        private string IdentityPath => Path.Combine(StateDirectory, "scale-set-identity.json");

        public void ClearPendingIdentity() => File.Delete(IdentityPath);

        public static Task<ScaleSetBootstrapLease> OpenAsync(
            string stateRoot,
            string scaleSetKey,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedKey = scaleSetKey.ToUpperInvariant();
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey));
            string setDirectory = Path.Combine(Path.GetFullPath(stateRoot), Convert.ToHexString(digest).ToLowerInvariant());
            Directory.CreateDirectory(setDirectory);
            FileStream lease = new(
                Path.Combine(setDirectory, ".scale-set-bootstrap.lease"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous);
            return Task.FromResult(new ScaleSetBootstrapLease(setDirectory, lease));
        }

        public async Task<ScaleSetIdentityMarker?> ReadIdentityAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(IdentityPath))
            {
                return null;
            }

            try
            {
                await using FileStream stream = new(
                    IdentityPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                ScaleSetIdentityMarker? marker = await JsonSerializer.DeserializeAsync<ScaleSetIdentityMarker>(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (marker is null
                    || marker.SchemaVersion != 1
                    || string.IsNullOrWhiteSpace(marker.Target)
                    || string.IsNullOrWhiteSpace(marker.RunnerGroupName)
                    || string.IsNullOrWhiteSpace(marker.ScaleSetName)
                    || string.IsNullOrWhiteSpace(marker.ControllerIdentity)
                    || (marker.CreationInProgress ? marker.ScaleSetId is not null : marker.ScaleSetId is null or <= 0))
                {
                    throw new InvalidDataException("The persisted scale-set identity marker is invalid.");
                }

                return marker;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The persisted scale-set identity marker could not be read.", exception);
            }
        }

        public async Task WriteIdentityAsync(ScaleSetIdentityMarker marker, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(marker);
            string temporaryPath = IdentityPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, marker, cancellationToken: cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, IdentityPath, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                throw;
            }
        }

        public ValueTask DisposeAsync() => _lease.DisposeAsync();
    }
}
