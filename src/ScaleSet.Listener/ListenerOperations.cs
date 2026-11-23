using System.Diagnostics;

namespace ScaleSet.Listener;

internal sealed class ListenerOperations(
    IScaleSetListenerExecutionPolicy policy, TimeProvider timeProvider, CancellationToken ownershipLost) : IListenerOperations
{
    public IListenerOperationBudget StartBudget(TimeSpan timeout, CancellationToken stoppingToken) =>
        new Budget(policy, timeProvider, timeout, ownershipLost, stoppingToken);

    private sealed class Budget : IListenerOperationBudget
    {
        private readonly IScaleSetListenerExecutionPolicy _policy;
        private readonly CancellationToken _ownershipLost;
        private readonly TimeProvider _clock;
        private readonly TimeSpan _timeout;
        private readonly CancellationToken _stoppingToken;
        private readonly long _startedAt;
        private readonly CancellationTokenSource _timeoutSource;
        private readonly CancellationTokenSource _policyCancellation;

        internal Budget(
            IScaleSetListenerExecutionPolicy policy, TimeProvider clock, TimeSpan timeout,
            CancellationToken ownershipLost, CancellationToken stoppingToken)
        {
            _policy = policy;
            _ownershipLost = ownershipLost;
            _clock = clock;
            _timeout = timeout;
            _stoppingToken = stoppingToken;
            _startedAt = clock.GetTimestamp();
            _timeoutSource = new CancellationTokenSource(timeout, clock);
            _policyCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                ownershipLost, stoppingToken, _timeoutSource.Token);
        }

        public async Task<T> RunAsync<T>(
            ScaleSetListenerOperationContext context,
            Func<ScaleSetListenerAuthorization, CancellationToken, Task<T>> operation,
            Func<T, bool>? finishOnStop = null)
        {
            using var activity = ScaleSetListener.ListenerActivitySource.StartActivity("scaleset.listener.operation");
            activity?.SetTag("scaleset.listener.operation", context.Operation.ToString());
            activity?.SetTag("scaleset.id", context.SessionKey.ScaleSetId);
            try
            {
                CheckBudget();
                _stoppingToken.ThrowIfCancellationRequested();
                long requestedAt = _clock.GetTimestamp();
                DateTimeOffset requestedUtc = _clock.GetUtcNow();
                var permit = await _policy.AuthorizeAsync(context, _policyCancellation.Token).ConfigureAwait(false);
                CheckBudget();
                _stoppingToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(permit);
                if (permit.ValidUntil is not { } validUntil)
                {
                    throw new ListenerDeferredException(permit.RetryAt);
                }

                if (permit.OwnershipLost != _ownershipLost ||
                    (context.Operation == ScaleSetListenerOperation.Poll) != permit.MaxCapacity.HasValue)
                {
                    throw new InvalidOperationException("The listener permit does not match the fixed owner or operation.");
                }

                TimeSpan RemainingPermit() => Min(validUntil - _clock.GetUtcNow(),
                    validUntil - requestedUtc - _clock.GetElapsedTime(requestedAt));

                var permitTime = RemainingPermit();
                if (permitTime <= TimeSpan.Zero)
                {
                    throw new TimeoutException("The listener operation permit expired before dispatch.");
                }

                // The phase timer is shared across operations. Only create a shorter permit timer.
                using var permitTimer = permitTime < _timeout - _clock.GetElapsedTime(_startedAt)
                    ? new CancellationTokenSource(permitTime, _clock)
                    : null;
                using var operationCancellation = permitTimer is null ? null :
                    CancellationTokenSource.CreateLinkedTokenSource(_policyCancellation.Token, permitTimer.Token);
                var token = operationCancellation?.Token ?? _policyCancellation.Token;
                token.ThrowIfCancellationRequested();
                if (RemainingPermit() <= TimeSpan.Zero)
                {
                    throw new TimeoutException("The listener operation permit expired before dispatch.");
                }

                T result = await operation(permit, token).ConfigureAwait(false);
                CheckBudget();
                permitTimer?.Token.ThrowIfCancellationRequested();
                if (RemainingPermit() <= TimeSpan.Zero)
                {
                    throw new TimeoutException("The listener operation permit expired before completion.");
                }

                if (_stoppingToken.IsCancellationRequested && finishOnStop?.Invoke(result) != true)
                {
                    _stoppingToken.ThrowIfCancellationRequested();
                }

                activity?.SetTag("scaleset.listener.outcome", "completed");
                return result;
            }
            catch (ListenerDeferredException)
            {
                activity?.SetTag("scaleset.listener.outcome", "deferred");
                throw;
            }
            catch (OperationCanceledException)
            {
                activity?.SetTag("scaleset.listener.outcome", "canceled");
                throw;
            }
            catch
            {
                activity?.SetTag("scaleset.listener.outcome", "failed");
                activity?.SetStatus(ActivityStatusCode.Error);
                throw;
            }
        }

        private void CheckBudget()
        {
            _ownershipLost.ThrowIfCancellationRequested();
            _timeoutSource.Token.ThrowIfCancellationRequested();
            if (_clock.GetElapsedTime(_startedAt) >= _timeout)
            {
                throw new TimeoutException("The listener operation budget expired.");
            }
        }

        private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

        public void Dispose()
        {
            _policyCancellation.Dispose();
            _timeoutSource.Dispose();
        }
    }
}
