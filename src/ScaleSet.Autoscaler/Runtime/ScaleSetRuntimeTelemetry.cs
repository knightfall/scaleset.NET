using System.Diagnostics;
using ScaleSet.Client;
using ScaleSet.Hosting.Telemetry;

namespace ScaleSet.Autoscaler.Runtime;

internal sealed class ScaleSetRuntimeTelemetry(ScaleSetTelemetryMetrics? metrics, string scaleSetKey)
{
    public ScaleSetTelemetryMetrics? Metrics => metrics;

    public Task MeasureAsync(ScaleSetTelemetryOperation operation, Func<Task> execute) =>
        MeasureAsync(operation, async () =>
        {
            await execute().ConfigureAwait(false);
            return true;
        });

    public async Task<T> MeasureAsync<T>(
        ScaleSetTelemetryOperation operation,
        Func<Task<T>> execute,
        Func<T, ScaleSetTelemetryOutcome>? getOutcome = null)
    {
        if (metrics is null)
        {
            return await execute().ConfigureAwait(false);
        }

        long start = Stopwatch.GetTimestamp();
        ScaleSetTelemetryOutcome outcome = ScaleSetTelemetryOutcome.Failure;
        try
        {
            T result = await execute().ConfigureAwait(false);
            outcome = getOutcome?.Invoke(result) ?? ScaleSetTelemetryOutcome.Success;
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = ScaleSetTelemetryOutcome.Cancelled;
            throw;
        }
        finally
        {
            metrics.RecordOperation(operation, outcome, Stopwatch.GetElapsedTime(start), scaleSetKey, ScaleSetTelemetryProvider.Docker);
        }
    }

    internal sealed class MeteredSession(IScaleSetRuntimeSession session, ScaleSetRuntimeTelemetry telemetry) : IScaleSetRuntimeSession
    {
        public RunnerScaleSetSession Session => session.Session;

        public Task<RunnerScaleSetMessage?> GetMessageAsync(long lastMessageId, int maxCapacity, CancellationToken cancellationToken) =>
            telemetry.MeasureAsync(
                ScaleSetTelemetryOperation.QueuePoll,
                () => session.GetMessageAsync(lastMessageId, maxCapacity, cancellationToken),
                static message => message is null ? ScaleSetTelemetryOutcome.Empty : ScaleSetTelemetryOutcome.Success);

        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken) =>
            telemetry.MeasureAsync(ScaleSetTelemetryOperation.Acknowledge, () => session.DeleteMessageAsync(messageId, cancellationToken));

        public Task<IReadOnlyList<long>> AcquireJobsAsync(IReadOnlyList<long> runnerRequestIds, CancellationToken cancellationToken) =>
            telemetry.MeasureAsync(
                ScaleSetTelemetryOperation.JobAcquire,
                () => session.AcquireJobsAsync(runnerRequestIds, cancellationToken),
                static acquired => acquired?.Count is null or 0 ? ScaleSetTelemetryOutcome.Empty : ScaleSetTelemetryOutcome.Success);

        public Task CloseAsync(CancellationToken cancellationToken) => session.CloseAsync(cancellationToken);
    }
}
