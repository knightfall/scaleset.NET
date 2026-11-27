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

namespace ScaleSet.Hosting.Runtime;

/// <summary>
/// Builds per-scale-set runtimes from the validated host
/// configuration and a process-wide host capacity coordinator.
/// </summary>
public sealed partial class ScaleSetRuntimeFactory : IScaleSetRuntimeFactory
{
    private readonly AutoscalerOptions _options;
    private readonly Dictionary<string, CredentialOptions> _credentials;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IScaleSetRuntimeClientFactory _clientFactory;
    private readonly Dictionary<string, IRunnerProviderFactory> _providers;
    private readonly ScaleSetTelemetryMetrics? _telemetry;

    /// <summary>Creates a runtime factory using the registered provider backends and control-plane client factory.</summary>
    public ScaleSetRuntimeFactory(
        AutoscalerOptions options,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        IScaleSetRuntimeClientFactory clientFactory,
        IEnumerable<IRunnerProviderFactory> providerFactories,
        ScaleSetTelemetryMetrics? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(providerFactories);
        _options = options;
        _credentials = new Dictionary<string, CredentialOptions>(options.Credentials, StringComparer.OrdinalIgnoreCase);
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider;
        _clientFactory = clientFactory;
        _providers = providerFactories.ToDictionary(static factory => factory.Name, StringComparer.OrdinalIgnoreCase);
        _telemetry = telemetry;
    }

    /// <inheritdoc />
    public async Task<IScaleSetRuntime> CreateAsync(
        ScaleSetOptions scaleSet,
        HostCapacityCoordinator capacityCoordinator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scaleSet);
        ArgumentNullException.ThrowIfNull(capacityCoordinator);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(scaleSet.Key)
            || string.IsNullOrWhiteSpace(scaleSet.CredentialsRef)
            || !_credentials.TryGetValue(scaleSet.CredentialsRef, out CredentialOptions? credential)
            || credential is null)
        {
            throw new AutoscalerConfigurationException("A configured scale set references an unavailable credential.");
        }

        if (!_providers.TryGetValue(scaleSet.Provider, out IRunnerProviderFactory? providerFactory))
        {
            throw new AutoscalerConfigurationException("A configured scale set references an unavailable runner provider.");
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
            runtimeClient = _clientFactory.Create(target, credentials, clientOptions)
                ?? throw new InvalidOperationException("The scale-set client factory returned no client.");
            provider = providerFactory.Create(scaleSet)
                ?? throw new InvalidOperationException("The runner provider factory returned no provider.");
            return new ScaleSetRuntime(
                _options.Controller,
                scaleSet,
                runtimeClient,
                provider,
                providerFactory,
                capacityCoordinator,
                _loggerFactory,
                _timeProvider,
                _telemetry);
        }
        catch (Exception exception)
        {
            List<Exception> failures = [exception];
            await CaptureProviderDisposalFailureAsync(provider, failures).ConfigureAwait(false);
            CaptureDisposalFailure(() => runtimeClient?.Dispose(), failures);
            if (failures.Count > 1)
            {
                throw new AggregateException("Runtime creation and cleanup failed.", failures);
            }

            throw;
        }
    }

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

    private static async Task CaptureProviderDisposalFailureAsync(IRunnerProvider? provider, List<Exception> failures)
    {
        if (provider is IAsyncDisposable asyncDisposable)
        {
            await CaptureAsyncDisposalFailure(asyncDisposable, failures).ConfigureAwait(false);
        }
        else if (provider is IDisposable disposable)
        {
            CaptureDisposalFailure(disposable.Dispose, failures);
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
        private readonly IRunnerProviderFactory _providerFactory;
        private readonly HostCapacityCoordinator _capacityCoordinator;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<ScaleSetRuntime> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly ScaleSetRuntimeTelemetry _metrics;
        private readonly object _sync = new();
        private readonly CancellationTokenSource _stopSignal = new();
        private readonly CancellationTokenSource _stopReconciliation = new();
        private ScaleSetBootstrapLease? _bootstrapLease;
        private FileRunnerStateJournal? _journal;
        private RunnerLifecycleManager? _lifecycle;
        private ScaleSetRunnerProvisioner? _provisioner;
        private IMessageSession? _session;
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
            IRunnerProviderFactory providerFactory,
            HostCapacityCoordinator capacityCoordinator,
            ILoggerFactory loggerFactory,
            TimeProvider timeProvider,
            ScaleSetTelemetryMetrics? telemetry)
        {
            _controllerOptions = controllerOptions;
            _scaleSetOptions = scaleSetOptions;
            _client = client;
            _provider = provider;
            _providerFactory = providerFactory;
            _capacityCoordinator = capacityCoordinator;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<ScaleSetRuntime>();
            _timeProvider = timeProvider;
            _metrics = new ScaleSetRuntimeTelemetry(telemetry, ScaleSetKey, providerFactory.Name.ToUpperInvariant() switch
            {
                "DOCKER" => ScaleSetTelemetryProvider.Docker,
                "ECS" => ScaleSetTelemetryProvider.Ecs,
                "EC2" => ScaleSetTelemetryProvider.Ec2,
                _ => ScaleSetTelemetryProvider.Other,
            });
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
                _metrics.Provider,
                _lifecycle);

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
            ScaleSetSessionRecovery queue = new(_timeProvider, _metrics, _logger, ScaleSetKey);
            ScaleSetListener listener = new(
                queue,
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
                queue,
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

            ScaleSetReconciliationLoop reconciliation = new(
                _timeProvider, _controllerOptions.ReconcileInterval, _scaleSetOptions.Lifecycle.CleanupTimeout,
                _metrics, _logger, ScaleSetKey, _providerFactory.IsTransientFailure);
            await reconciliation.RunAsync(
                handler,
                (serialized, token) => queue.RunAsync(
                    OpenSessionAsync,
                    CloseSessionAsync,
                    listenerToken => listener.RunAsync(serialized, listenerToken),
                    token),
                ReconcileSteadyStateAsync,
                _stopReconciliation.Token,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<IMessageSession> OpenSessionAsync(CancellationToken cancellationToken)
        {
            IMessageSession session = await _client.CreateMessageSessionAsync(
                _scaleSetId,
                _controllerOptions.Identity,
                cancellationToken).ConfigureAwait(false);
            if (_metrics.Metrics is not null)
            {
                session = new ScaleSetRuntimeTelemetry.MeteredSession(session, _metrics);
            }

            // Stop waits for this operation before closing the session, even
            // when cancellation races with the response to an open request.
            _session = session;
            return session;
        }

        private async Task CloseSessionAsync(IMessageSession session, CancellationToken cancellationToken)
        {
            using CancellationTokenSource cleanup = new(_scaleSetOptions.Lifecycle.CleanupTimeout, _timeProvider);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cleanup.Token);
            try
            {
                await session.CloseAsync(linked.Token).ConfigureAwait(false);
            }
            catch (ScaleSetApiException exception) when (exception.StatusCode is 404 or 410
                && string.Equals(exception.Method, HttpMethod.Delete.Method, StringComparison.Ordinal))
            {
                // The DELETE names the exact owned session. GitHub may have
                // expired it, or applied a prior close whose response was lost.
            }

            _session = null;
            RuntimeSessionClosed(_logger, ScaleSetKey);
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

                await SettleAdmissionAndCloseSessionAsync().ConfigureAwait(false);
            }
        }

        private async Task SettleAdmissionAndCloseSessionAsync()
        {
            Exception? settlementFailure = null;
            try
            {
                if (_handler is { } handler)
                {
                    await handler.SettleDrainAdmissionAsync(_timeProvider).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                settlementFailure = exception;
            }

            try
            {
                if (_session is { } session)
                {
                    await CloseSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception closeFailure) when (settlementFailure is not null)
            {
                throw new AggregateException("Scale-set admission settlement and session closure failed.", settlementFailure, closeFailure);
            }

            if (settlementFailure is not null)
            {
                ExceptionDispatchInfo.Throw(settlementFailure);
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
            bool drained;
            try
            {
                drained = await _metrics.MeasureAsync(
                    ScaleSetTelemetryOperation.Drain,
                    () => WaitForOwnedRunnerDrainAsync(handler, linked.Token),
                    static completed => completed ? ScaleSetTelemetryOutcome.Success : ScaleSetTelemetryOutcome.Unknown).ConfigureAwait(false);
            }
            finally
            {
                await _stopReconciliation.CancelAsync().ConfigureAwait(false);
            }

            if (!drained)
            {
                RuntimeDrainDeadline(_logger, ScaleSetKey, _scaleSetOptions.Lifecycle.ShutdownDrainTimeout);
            }
        }

        private async Task<bool> WaitForOwnedRunnerDrainAsync(ScaleSetMessageHandler handler, CancellationToken cancellationToken)
        {
            TimeSpan timeout = _scaleSetOptions.Lifecycle.ShutdownDrainTimeout;
            bool wait = timeout > TimeSpan.Zero;
            using var deadline = new CancellationTokenSource(wait ? timeout : Timeout.InfiniteTimeSpan, _timeProvider);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                while (true)
                {
                    int retryIntervalMs = 250;
                    if (await handler.TryReadDrainedOperationsAsync(bounded.Token).ConfigureAwait(false) is { } snapshot)
                    {
                        var protectedOperations = snapshot.Where(RequiresDrainEvidence).ToArray();
                        if (protectedOperations.Length == 0)
                        {
                            return true;
                        }

                        if (wait && _client is IScaleSetRunnerIdleLookup lookup
                            && protectedOperations.All(static operation => operation.Stage == RunnerLifecycleStage.Starting))
                        {
                            if (await VerifyWarmRunnersAsync(handler, lookup, protectedOperations, bounded.Token).ConfigureAwait(false))
                            {
                                return true;
                            }

                            retryIntervalMs = 5000;
                        }
                    }

                    if (!wait)
                    {
                        return false;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(retryIntervalMs), _timeProvider, bounded.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        private static async Task<bool> VerifyWarmRunnersAsync(
            ScaleSetMessageHandler handler,
            IScaleSetRunnerIdleLookup lookup,
            IReadOnlyList<RunnerOperationState> protectedOperations,
            CancellationToken cancellationToken)
        {
            foreach (RunnerOperationState operation in protectedOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await lookup.IsRunnerIdleAsync(operation.Identity, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }

            // A delivery can change the durable revision while an idle lookup is in flight.
            return await handler.TryReadDrainedOperationsAsync(cancellationToken).ConfigureAwait(false) is { } current
                && current.Where(RequiresDrainEvidence).All(protectedOperations.Contains);
        }

        private static bool RequiresDrainEvidence(RunnerOperationState operation) => operation.ProviderResourceId is not null
            && operation.Stage is RunnerLifecycleStage.Starting or RunnerLifecycleStage.Busy or RunnerLifecycleStage.Unknown
                or RunnerLifecycleStage.Completed or RunnerLifecycleStage.Removing;

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
            await CaptureProviderDisposalFailureAsync(_provider, failures).ConfigureAwait(false);
            CaptureDisposalFailure(_client.Dispose, failures);
            await CaptureAsyncDisposalFailure(_bootstrapLease, failures).ConfigureAwait(false);
            CaptureDisposalFailure(_stopReconciliation.Dispose, failures);
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
            Dictionary<Guid, RunnerResource> discovered = await ReadOwnedInventoryAsync(scope, cancellationToken).ConfigureAwait(false);
            ImmutableArray<RunnerOperationState> journalStates = await journal.ReadActiveAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<Guid, RunnerOperationState> statesById = journalStates.ToDictionary(static state => state.Identity.OperationId);
            int adoptedCount = 0;

            foreach (RunnerResource resource in discovered.Values)
            {
                RunnerOperationState? previous = statesById.GetValueOrDefault(resource.Identity.OperationId)
                    ?? await journal.FindAsync(resource.Identity.OperationId, cancellationToken).ConfigureAwait(false);
                if (previous is not null)
                {
                    EnsureResourceMatchesJournal(resource, previous, scope);
                }

                if (statesById.TryGetValue(resource.Identity.OperationId, out RunnerOperationState? completedPrior)
                    && completedPrior.Stage is (RunnerLifecycleStage.Completed or RunnerLifecycleStage.Removing))
                {
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
                    GetObservedCapacityResources(resource, adopted.Identity),
                    CapacityOccupancyState.Unknown);
                adoptedCount++;
            }

            foreach (RunnerOperationState state in journalStates)
            {
                if (discovered.ContainsKey(state.Identity.OperationId)
                    || state.Stage is RunnerLifecycleStage.Gone or RunnerLifecycleStage.Failed)
                {
                    continue;
                }

                if (state.ProviderResourceId is not null)
                {
                    RunnerResource? inspected = await InspectOwnedResourceAsync(state, state.ProviderResourceId, cancellationToken).ConfigureAwait(false);
                    if (inspected is null)
                    {
                        if (state.Stage == RunnerLifecycleStage.Completed
                            || (await ReadRegistrationAsync(state.Identity, cancellationToken).ConfigureAwait(false)).State == RegistrationState.Absent)
                        {
                            await lifecycle.ConfirmGoneAsync(state.Identity, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        AdoptCapacity(
                            state.Identity,
                            state.Identity.CapacityResources ?? new CapacityResourceQuantities(null, null),
                            CapacityOccupancyState.Unknown);
                        adoptedCount++;
                        continue;
                    }

                    if (IsStartingCreatedRecovery(state, inspected))
                    {
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

                if (_client is IScaleSetRunnerNameLookup
                    && await ReconcileInactiveRunnerAsync(state, resource: null, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                RunnerOperationState uncertain = await lifecycle.TransitionAsync(
                    state.Identity, RunnerLifecycleStage.Unknown, cancellationToken: cancellationToken).ConfigureAwait(false);
                AdoptCapacity(
                    uncertain.Identity,
                    uncertain.Identity.CapacityResources ?? new CapacityResourceQuantities(null, null),
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

        private bool IsStartingCreatedRecovery(RunnerOperationState state, RunnerResource resource) =>
            !_provider.Capabilities.RequiresJitConfigurationAtStart && state.Stage == RunnerLifecycleStage.Starting
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
                && (state.ProviderResourceId is null || state.ProviderResourceId == resource.ProviderResourceId)
                && (resource.Identity.GitHubRunnerId is null
                    || state.Identity.GitHubRunnerId is null
                    || resource.Identity.GitHubRunnerId == state.Identity.GitHubRunnerId);
            if (!same)
            {
                throw new InvalidDataException("Provider inventory conflicts with a durable runner operation.");
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

        private sealed class RuntimeJitConfigClient(IScaleSetRuntimeClient client) : ITrackedRunnerJitConfigClient
        {
            public Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
                long scaleSetId,
                RunnerScaleSetJitRunnerSetting runnerSetting,
                CancellationToken cancellationToken = default) =>
                client.GenerateJitConfigAsync(scaleSetId, runnerSetting, cancellationToken);

            public Task<RunnerScaleSetJitRunnerConfig?> GenerateTrackedJitConfigAsync(
                long scaleSetId, RunnerScaleSetJitRunnerSetting setting, Action onDispatch, CancellationToken cancellationToken)
            {
                if (client is ScaleSetRuntimeClientAdapter adapter)
                {
                    return adapter.GenerateJitConfigAsync(scaleSetId, setting, onDispatch, cancellationToken);
                }

                onDispatch();
                return client.GenerateJitConfigAsync(scaleSetId, setting, cancellationToken);
            }
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

        public bool HasExistingJournal => FileRunnerStateJournal.Exists(StateDirectory);

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
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(setDirectory);
            }
            else
            {
                Directory.CreateDirectory(setDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
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
