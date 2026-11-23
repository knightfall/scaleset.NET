using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using ScaleSet.Client;
using ScaleSet.Hosting.Telemetry;

namespace ScaleSet.Autoscaler.Runtime;

internal sealed partial class ScaleSetSessionRecovery(
    TimeProvider timeProvider,
    ScaleSetRuntimeTelemetry metrics,
    ILogger logger,
    string scaleSetKey) : IMessageSessionClient
{
    private const int MaximumConsecutiveRetries = 3;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private IScaleSetRuntimeSession? _current;
    private CancellationToken _runToken;
    private bool _madeProgress;

    public RunnerScaleSetSession Session => Current.Session;

    private IScaleSetRuntimeSession Current => _current ?? throw new InvalidOperationException("No GitHub session is open.");

    public async Task RunAsync(
        Func<CancellationToken, Task<IScaleSetRuntimeSession>> connect,
        Func<IScaleSetRuntimeSession, CancellationToken, Task> close,
        Func<CancellationToken, Task> listen,
        CancellationToken cancellationToken)
    {
        _runToken = cancellationToken;
        int retries = 0;
        RetryAttempt? retry = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _madeProgress = false;
            Exception failure;
            try
            {
                _current = retry is null
                    ? await connect(cancellationToken).ConfigureAwait(false)
                    : await metrics.MeasureAsync(
                        ScaleSetTelemetryOperation.Recovery,
                        () => ReconnectAsync(retry, connect, close, cancellationToken)).ConfigureAwait(false);
                await listen(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (QueueTransportFailure exception)
            {
                failure = exception.InnerException!;
            }
            catch (Exception exception) when (_current is null && IsRetryable(exception, opening: true))
            {
                failure = exception;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_madeProgress)
            {
                retries = 0;
            }

            if (retries == MaximumConsecutiveRetries)
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            retries++;
            TimeSpan backoff = TimeSpan.FromSeconds(1 << (retries - 1));
            if (failure is ScaleSetRateLimitException { RetryAfter: var retryAfter } && retryAfter > backoff)
            {
                backoff = retryAfter;
            }

            Retrying(logger, scaleSetKey, retries, failure.GetType().Name, backoff);
            retry = new RetryAttempt(failure, backoff);
        }
    }

    private async Task<IScaleSetRuntimeSession> ReconnectAsync(
        RetryAttempt retry,
        Func<CancellationToken, Task<IScaleSetRuntimeSession>> connect,
        Func<IScaleSetRuntimeSession, CancellationToken, Task> close,
        CancellationToken cancellationToken)
    {
        if (_current is not null)
        {
            try
            {
                await close(_current, cancellationToken).ConfigureAwait(false);
                _current = null;
            }
            catch (Exception closeFailure) when (!cancellationToken.IsCancellationRequested)
            {
                // An unclear close outcome keeps the current session owned.
                throw new AggregateException("Could not close the failed GitHub session before reconnecting.", retry.Failure, closeFailure);
            }
        }

        await Task.Delay(retry.Delay, timeProvider, cancellationToken).ConfigureAwait(false);
        return await connect(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunnerScaleSetMessage?> GetMessageAsync(long lastMessageId, int maxCapacity, CancellationToken cancellationToken)
    {
        RunnerScaleSetMessage? message = await InvokeQueueAsync(
            session => session.GetMessageAsync(lastMessageId, maxCapacity, cancellationToken)).ConfigureAwait(false);
        _madeProgress |= message is null;
        return message;
    }

    public async Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken)
    {
        _ = await InvokeQueueAsync(async session =>
        {
            await session.DeleteMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        _madeProgress = true;
    }

    public Task<IReadOnlyList<long>> AcquireJobsAsync(IReadOnlyList<long> runnerRequestIds, CancellationToken cancellationToken) =>
        InvokeQueueAsync(session => session.AcquireJobsAsync(runnerRequestIds, cancellationToken));

    private async Task<T> InvokeQueueAsync<T>(Func<IScaleSetRuntimeSession, Task<T>> operation)
    {
        try
        {
            return await operation(Current).ConfigureAwait(false);
        }
        catch (Exception exception) when (!_runToken.IsCancellationRequested && IsRetryable(exception, opening: false))
        {
            // The listener callback can also fail. Only errors from queue transport trigger a reconnect.
            throw new QueueTransportFailure(exception);
        }
    }

    private static bool IsRetryable(Exception exception, bool opening) => exception switch
    {
        ScaleSetRateLimitException rateLimit => rateLimit.RetryAfter <= MaximumRetryDelay,
        ScaleSetMaxElapsedExceededException => true,
        ScaleSetApiException api => api.IsTransientTransportFailure
            || api.StatusCode is 408 or 429 or 500 or 502 or 503 or 504
            || (opening ? api.StatusCode == 409 : api.StatusCode is 404 or 410),
        HttpRequestException transport => transport.HttpRequestError is HttpRequestError.Unknown
            or HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
            or HttpRequestError.HttpProtocolError or HttpRequestError.ResponseEnded,
        IOException or TimeoutException or OperationCanceledException => true,
        _ => false,
    };

    private sealed class QueueTransportFailure(Exception innerException) : Exception(null, innerException);

    private sealed record RetryAttempt(Exception Failure, TimeSpan Delay);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Recovering the GitHub session for scale set {ScaleSetKey} after {FailureType}. Retry {Attempt} waits {Delay}.")]
    private static partial void Retrying(ILogger logger, string scaleSetKey, int attempt, string failureType, TimeSpan delay);
}
