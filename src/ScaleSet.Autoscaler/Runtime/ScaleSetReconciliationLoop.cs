using System.Net;
using Microsoft.Extensions.Logging;
using ScaleSet.Client;
using ScaleSet.Hosting.Telemetry;
using ScaleSet.Listener;
using ScaleSet.Providers.Docker;

namespace ScaleSet.Autoscaler.Runtime;

internal sealed partial class ScaleSetReconciliationLoop(
    TimeProvider timeProvider,
    TimeSpan interval,
    TimeSpan passTimeout,
    ScaleSetRuntimeTelemetry telemetry,
    ILogger logger,
    string scaleSetKey)
{
    public async Task RunAsync(
        IScaleSetMessageHandler handler,
        Func<IScaleSetMessageHandler, CancellationToken, Task> listen,
        Func<CancellationToken, Task> reconcile,
        CancellationToken stopReconciliation,
        CancellationToken cancellationToken)
    {
        using SemaphoreSlim gate = new(1);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenSource reconcileLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, stopReconciliation);
        Task listener = listen(new SerializedHandler(handler, gate), lifetime.Token);
        Task reconciliation = ReconcilePeriodicallyAsync(reconcile, gate, reconcileLifetime.Token);
        Task completed = await Task.WhenAny(listener, reconciliation).ConfigureAwait(false);
        if (completed == reconciliation && reconciliation.IsCanceled && stopReconciliation.IsCancellationRequested)
        {
            // Draining stops periodic inventory work while the listener continues
            // accepting activity and completion messages for work already admitted.
            await listener.ConfigureAwait(false);
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(listener, reconciliation).ConfigureAwait(false);
    }

    private async Task ReconcilePeriodicallyAsync(
        Func<CancellationToken, Task> reconcile,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(interval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            using CancellationTokenSource timeout = new(passTimeout, timeProvider);
            using CancellationTokenSource pass = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await telemetry.MeasureAsync(ScaleSetTelemetryOperation.Reconcile, async () =>
                {
                    await gate.WaitAsync(pass.Token).ConfigureAwait(false);
                    try
                    {
                        await reconcile(pass.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                && (IsTemporaryFailure(exception) || (exception is OperationCanceledException && timeout.IsCancellationRequested)))
            {
                // Keep the last protected occupancy and wait for the next tick.
                // Data and ownership contradictions propagate to supervision.
                PassFailed(logger, scaleSetKey, exception.GetType().Name);
            }
        }
    }

    private static bool IsTemporaryFailure(Exception exception) =>
        exception is HttpRequestException or TimeoutException or IOException
        || exception is ScaleSetApiException { IsTransientTransportFailure: true }
        || exception is ScaleSetApiException { StatusCode: 408 or 429 or 500 or 502 or 503 or 504 }
        || exception is DockerProviderException
        {
            FailureCode: "request-timeout" or "engine-unavailable" or "engine-response-read-failed",
        }
        || exception is DockerProviderException
        {
            StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
        };

    private sealed class SerializedHandler(IScaleSetMessageHandler handler, SemaphoreSlim gate) : IScaleSetMessageHandler
    {
        public async Task HandleAsync(RunnerScaleSetMessage? message, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Inventory reconciliation for scale set {ScaleSetKey} encountered {FailureType} and will retry on the next tick.")]
    private static partial void PassFailed(ILogger logger, string scaleSetKey, string failureType);
}
