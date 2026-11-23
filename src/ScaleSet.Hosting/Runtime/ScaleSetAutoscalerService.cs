using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;

namespace ScaleSet.Hosting.Runtime;

/// <summary>Starts all configured scale-set runtimes after shared capacity is rebuilt.</summary>
public sealed partial class ScaleSetAutoscalerService : BackgroundService
{
    private readonly AutoscalerOptions _options;
    private readonly HostCapacityCoordinator _capacityCoordinator;
    private readonly IScaleSetRuntimeFactory _runtimeFactory;
    private readonly ILogger<ScaleSetAutoscalerService> _logger;
    private readonly AutoscalerHealthState _healthState;
    private readonly List<IScaleSetRuntime> _runtimes = [];
    private readonly object _runtimeSync = new();
    private int _cleanupStarted;
    private bool _shutdownRequested;

    /// <summary>Creates the process-wide autoscaler service.</summary>
    public ScaleSetAutoscalerService(
        AutoscalerOptions options,
        HostCapacityCoordinator capacityCoordinator,
        IScaleSetRuntimeFactory runtimeFactory,
        ILogger<ScaleSetAutoscalerService> logger,
        AutoscalerHealthState? healthState = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capacityCoordinator);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _capacityCoordinator = capacityCoordinator;
        _runtimeFactory = runtimeFactory;
        _logger = logger;
        _healthState = healthState ?? new AutoscalerHealthState();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _healthState.MarkNotReady();
        try
        {
            foreach (ScaleSetOptions scaleSet in _options.ScaleSets)
            {
                lock (_runtimeSync)
                {
                    if (_shutdownRequested)
                    {
                        return;
                    }
                }

                IScaleSetRuntime runtime = await _runtimeFactory.CreateAsync(
                    scaleSet,
                    _capacityCoordinator,
                    stoppingToken).ConfigureAwait(false);
                if (runtime is null)
                {
                    throw new InvalidOperationException("The runtime factory returned no scale-set runtime.");
                }

                if (!string.Equals(runtime.ScaleSetKey, scaleSet.Key, StringComparison.OrdinalIgnoreCase))
                {
                    await runtime.DisposeAsync().ConfigureAwait(false);
                    throw new InvalidOperationException("The runtime factory returned a runtime for a different scale-set key.");
                }

                bool retainRuntime;
                lock (_runtimeSync)
                {
                    retainRuntime = !_shutdownRequested;
                    if (retainRuntime)
                    {
                        _runtimes.Add(runtime);
                    }
                }

                if (!retainRuntime)
                {
                    await runtime.DisposeAsync().ConfigureAwait(false);
                    return;
                }
            }

            foreach (IScaleSetRuntime runtime in SnapshotRuntimes())
            {
                lock (_runtimeSync)
                {
                    if (_shutdownRequested)
                    {
                        return;
                    }
                }

                await runtime.InitializeAsync(stoppingToken).ConfigureAwait(false);
            }

            _healthState.MarkReady();

            lock (_runtimeSync)
            {
                if (_shutdownRequested)
                {
                    return;
                }
            }

            // Every provider inventory is adopted before any listener begins to
            // advertise capacity or acquire a job. Runtime implementations open
            // their sessions here and preserve the runner resources on stop.
            using CancellationTokenSource workersCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Task[] workers;
            lock (_runtimeSync)
            {
                if (_shutdownRequested)
                {
                    return;
                }

                workers = _runtimes
                    .Select(runtime => RunWorkerAsync(runtime, workersCancellation))
                    .ToArray();
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown reaches the cleanup path below.
        }
        catch (Exception exception)
        {
            _healthState.MarkFailed();
            RuntimeFailed(_logger, exception);
            throw;
        }
        finally
        {
            _healthState.MarkNotReady();
            await StopAndDisposeRuntimesAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _healthState.MarkStopping();
        IScaleSetRuntime[] runtimes;
        lock (_runtimeSync)
        {
            _shutdownRequested = true;
            runtimes = _runtimes.ToArray();
        }

        await Task.WhenAll(runtimes.Select(runtime => DrainRuntimeAsync(runtime, cancellationToken))).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await StopAndDisposeRuntimesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StopAndDisposeRuntimesAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
        {
            return;
        }

        foreach (IScaleSetRuntime runtime in SnapshotRuntimes().Reverse())
        {
            try
            {
                await runtime.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RuntimeStopFailed(_logger, runtime.ScaleSetKey, exception);
            }

            try
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RuntimeDisposeFailed(_logger, runtime.ScaleSetKey, exception);
            }
        }
    }

    private async Task DrainRuntimeAsync(IScaleSetRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.DrainAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RuntimeDrainFailed(_logger, runtime.ScaleSetKey, exception);
        }
    }

    private IScaleSetRuntime[] SnapshotRuntimes()
    {
        lock (_runtimeSync)
        {
            return _runtimes.ToArray();
        }
    }

    private static async Task RunWorkerAsync(IScaleSetRuntime runtime, CancellationTokenSource workersCancellation)
    {
        try
        {
            await runtime.RunAsync(workersCancellation.Token).ConfigureAwait(false);
            if (!workersCancellation.IsCancellationRequested)
            {
                throw new InvalidOperationException($"The listener for scale set '{runtime.ScaleSetKey}' stopped unexpectedly.");
            }
        }
        catch (OperationCanceledException) when (workersCancellation.IsCancellationRequested)
        {
            // One listener may observe host cancellation before its peers.
        }
        catch
        {
            workersCancellation.Cancel();
            throw;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Critical, Message = "Autoscaler runtime failed; stopping all scale-set workers.")]
    private static partial void RuntimeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to close the message session for scale set {ScaleSetKey}.")]
    private static partial void RuntimeStopFailed(ILogger logger, string scaleSetKey, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Failed to dispose runtime resources for scale set {ScaleSetKey}.")]
    private static partial void RuntimeDisposeFailed(ILogger logger, string scaleSetKey, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Failed to drain jobs for scale set {ScaleSetKey}; runner resources are preserved.")]
    private static partial void RuntimeDrainFailed(ILogger logger, string scaleSetKey, Exception exception);
}
