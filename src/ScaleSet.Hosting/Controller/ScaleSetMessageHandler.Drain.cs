using System.Runtime.ExceptionServices;
using ScaleSet.Client;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.State;

namespace ScaleSet.Hosting.Controller;

public sealed partial class ScaleSetMessageHandler
{
    private static readonly TimeSpan AdmissionSettlementTimeout = TimeSpan.FromSeconds(1);
    private readonly object _drainSync = new();
    private readonly HashSet<long> _pendingAcquiredRequests = [];
    private readonly HashSet<long> _assignedAcquiredRequests = [];
    private readonly Dictionary<(Guid OperationId, long Generation), DeferredPermitRelease> _deferredPermitReleases = [];
    private readonly CancellationTokenSource _deferredSettlementStop = new();
    private Task? _deferredSettlementTask;
    private bool _admissionTrimPending;
    private bool _drainStarted;
    private int _activeAdmissions;
    private int _activeCallbacks;

    /// <summary>Stops admitting new jobs while allowing lifecycle messages to continue.</summary>
    /// <remarks>Unused admission permits are settled during the drain wait.</remarks>
    public void BeginDrain()
    {
        lock (_drainSync)
        {
            _drainStarted = true;
        }

        Interlocked.Exchange(ref _lastAdvertisedMaximum, 0);
        _capacity.SetListenerMaximum(0);
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
        if (timeout == TimeSpan.Zero)
        {
            return await TryCompleteDrainAsync(cancellationToken).ConfigureAwait(false);
        }

        using CancellationTokenSource deadline = new(timeout, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            while (!await TryCompleteDrainAsync(linked.Token).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, linked.Token).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> TryCompleteDrainAsync(CancellationToken cancellationToken)
    {
        // Keep journal evidence stable against callbacks without holding the host-wide gate during I/O.
        if (!_callbackGate.Wait(0, cancellationToken))
        {
            return false;
        }

        try
        {
            SemaphoreSlim gate = _capacity.DemandRegistry.PlanningGate;
            if (!gate.Wait(0, cancellationToken))
            {
                return false;
            }

            try
            {
                lock (_drainSync)
                {
                    _admissionTrimPending = true;
                    SettleAdmissionUnderGate();
                    if (!IsDrainQuiescent())
                    {
                        return false;
                    }
                }
            }
            finally
            {
                gate.Release();
            }

            var operations = await _journal.ReadActiveAsync(cancellationToken).ConfigureAwait(false);
            lock (_drainSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return IsDrainQuiescent() && !operations.Any(static operation => operation.Stage == RunnerLifecycleStage.Busy);
            }
        }
        finally
        {
            _callbackGate.Release();
        }
    }

    private bool IsDrainQuiescent() => _activeCallbacks == 0 && _activeAdmissions == 0
        && _pendingAcquiredRequests.Count == 0 && !_admissionTrimPending && _deferredPermitReleases.Count == 0;

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

    private long[] GetUnacquiredOffers(IReadOnlyList<JobAvailableMessage> messages)
    {
        lock (_drainSync)
        {
            return messages
                .Select(static message => message.RunnerRequestId)
                .Where(requestId => requestId > 0 && !_pendingAcquiredRequests.Contains(requestId))
                .Distinct()
                .ToArray();
        }
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

    internal async Task SettleDrainAdmissionAsync(TimeProvider timeProvider)
    {
        // The runtime has stopped all producers. Join any previously scheduled settlement before disposal.
        Task? deferredSettlement;
        bool needsSettlement;
        lock (_drainSync)
        {
            deferredSettlement = _deferredSettlementTask;
            needsSettlement = _admissionTrimPending || _deferredPermitReleases.Count != 0
                || _capacity.DemandRegistry.GetAdmissionPermits(_scope.ScaleSetKey).Count != 0;
        }

        using CancellationTokenSource deadline = new(AdmissionSettlementTimeout, timeProvider);
        SemaphoreSlim gate = _capacity.DemandRegistry.PlanningGate;
        try
        {
            if (!needsSettlement)
            {
                return;
            }

            await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                lock (_drainSync)
                {
                    _admissionTrimPending = true;
                    SettleAdmissionUnderGate();
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out settling scale-set admission permits during stop.", exception);
        }
        finally
        {
            await _deferredSettlementStop.CancelAsync().ConfigureAwait(false);
            if (deferredSettlement is not null)
            {
                await deferredSettlement.ConfigureAwait(false);
            }
            _deferredSettlementStop.Dispose();
        }
    }

    private void DeferPermitRelease(CapacityReservationLease lease, bool retainTombstone)
    {
        lock (_drainSync)
        {
            var key = (lease.Handle.Identity.OperationId, lease.Handle.Generation);
            if (_deferredPermitReleases.TryGetValue(key, out var previous))
            {
                retainTombstone |= previous.RetainTombstone;
            }

            _deferredPermitReleases[key] = new DeferredPermitRelease(lease, retainTombstone);
            StartDeferredSettlementUnderLock();
        }
    }

    private void StartDeferredSettlementUnderLock() => _deferredSettlementTask ??= RunDeferredSettlementAsync();

    private async Task RunDeferredSettlementAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        SemaphoreSlim gate = _capacity.DemandRegistry.PlanningGate;
        try
        {
            await gate.WaitAsync(_deferredSettlementStop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_deferredSettlementStop.IsCancellationRequested)
        {
            return;
        }

        try
        {
            lock (_drainSync)
            {
                try
                {
                    SettleAdmissionUnderGate();
                }
                catch (Exception exception)
                {
                    LogAdmissionTrimFailed(_logger, _scope.ScaleSetKey, exception.GetType().Name);
                }
                finally
                {
                    _deferredSettlementTask = null;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void SettleAdmissionUnderGate()
    {
        // Callers hold the planning gate and the drain lock. Failed releases stay tracked for the next attempt.
        Exception? releaseFailure = null;
        foreach (var (key, release) in _deferredPermitReleases.ToArray())
        {
            try
            {
                ReleaseKnownUnusedPermit(release.Lease, release.RetainTombstone);
                _deferredPermitReleases.Remove(key);
            }
            catch (Exception exception)
            {
                LogCapacityStateUpdateFailed(_logger, key.OperationId, _scope.ScaleSetKey, exception.GetType().Name);
                releaseFailure ??= exception;
            }
        }

        SettleAdmissionTrimUnderGate();

        if (releaseFailure is not null)
        {
            ExceptionDispatchInfo.Throw(releaseFailure);
        }
    }

    private void SettleAdmissionTrimUnderGate()
    {
        if (_admissionTrimPending && _activeAdmissions == 0)
        {
            TrimUnneededAdmissionPermitsUnderGate();
            _admissionTrimPending = false;
        }
    }

    private void RecordAssignments(IReadOnlyList<JobAssignedMessage> assignments)
    {
        lock (_drainSync)
        {
            foreach (JobAssignedMessage assignment in assignments)
            {
                if (_pendingAcquiredRequests.Contains(assignment.RunnerRequestId))
                {
                    _assignedAcquiredRequests.Add(assignment.RunnerRequestId);
                }
            }
        }
    }

    private void RecordJobStarted(long requestId) => RemoveAcquiredRequest(requestId);

    private void RecordJobCompleted(long requestId) => RemoveAcquiredRequest(requestId);

    private void RemoveAcquiredRequest(long requestId)
    {
        lock (_drainSync)
        {
            _pendingAcquiredRequests.Remove(requestId);
            _assignedAcquiredRequests.Remove(requestId);
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

    private sealed record DeferredPermitRelease(CapacityReservationLease Lease, bool RetainTombstone);
}
