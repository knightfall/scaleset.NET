using ScaleSet.Hosting.State;

namespace ScaleSet.Hosting.Controller;

public sealed partial class ScaleSetMessageHandler
{
    private readonly object _drainSync = new();
    private readonly HashSet<long> _pendingAcquiredRequests = [];
    private bool _drainStarted;
    private int _activeAdmissions;
    private int _activeCallbacks;

    /// <summary>Stops admitting new jobs while allowing lifecycle messages to continue.</summary>
    public void BeginDrain()
    {
        lock (_drainSync)
        {
            _drainStarted = true;
        }

        _capacity?.SetListenerMaximum(0);
    }

    /// <summary>Gets whether shutdown has closed admission for this handler.</summary>
    public bool IsDraining
    {
        get
        {
            lock (_drainSync)
            {
                return _drainStarted;
            }
        }
    }

    /// <summary>
    /// Waits for admitted provisioning, acquired requests, and known busy jobs
    /// to settle. Returns false when the configured drain deadline expires.
    /// </summary>
    public async Task<bool> WaitForDrainAsync(
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The drain timeout must be zero or greater and finite.");
        }
        ArgumentNullException.ThrowIfNull(timeProvider);
        BeginDrain();

        using CancellationTokenSource deadline = new(timeout, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<RunnerOperationState> operations = await _journal
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false);
            bool hasBusyOperation = operations.Any(static operation => operation.Stage == RunnerLifecycleStage.Busy);
            bool hasPendingRequests;
            bool hasActiveAdmissions;
            bool hasActiveCallbacks;
            lock (_drainSync)
            {
                hasPendingRequests = _pendingAcquiredRequests.Count > 0;
                hasActiveAdmissions = _activeAdmissions > 0;
                hasActiveCallbacks = _activeCallbacks > 0;
            }

            if (!hasBusyOperation && !hasPendingRequests && !hasActiveAdmissions && !hasActiveCallbacks)
            {
                return true;
            }

            if (deadline.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private bool TryEnterAdmission(out IDisposable? lease)
    {
        lock (_drainSync)
        {
            if (_drainStarted)
            {
                lease = null;
                return false;
            }

            _activeAdmissions++;
            lease = new AdmissionLease(this);
            return true;
        }
    }

    private CallbackLease EnterCallback()
    {
        lock (_drainSync)
        {
            _activeCallbacks++;
        }

        return new CallbackLease(this);
    }

    private void RecordAcquiredRequests(IEnumerable<long> requestIds)
    {
        ArgumentNullException.ThrowIfNull(requestIds);
        lock (_drainSync)
        {
            foreach (long requestId in requestIds)
            {
                if (requestId > 0)
                {
                    _pendingAcquiredRequests.Add(requestId);
                }
            }
        }
    }

    private void RecordJobStarted(long requestId)
    {
        lock (_drainSync)
        {
            _pendingAcquiredRequests.Remove(requestId);
        }
    }

    private void RecordJobCompleted(long requestId)
    {
        lock (_drainSync)
        {
            _pendingAcquiredRequests.Remove(requestId);
        }
    }

    private void ExitAdmission()
    {
        lock (_drainSync)
        {
            if (_activeAdmissions <= 0)
            {
                throw new InvalidOperationException("A message-handler admission lease was released more than once.");
            }

            _activeAdmissions--;
        }
    }

    private void ExitCallback()
    {
        lock (_drainSync)
        {
            if (_activeCallbacks <= 0)
            {
                throw new InvalidOperationException("A message-handler callback lease was released more than once.");
            }

            _activeCallbacks--;
        }
    }

    private sealed class AdmissionLease(ScaleSetMessageHandler owner) : IDisposable
    {
        private ScaleSetMessageHandler? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitAdmission();
    }

    private sealed class CallbackLease(ScaleSetMessageHandler owner) : IDisposable
    {
        private ScaleSetMessageHandler? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitCallback();
    }
}
