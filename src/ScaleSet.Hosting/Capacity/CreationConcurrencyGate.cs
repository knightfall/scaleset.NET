using System.Diagnostics;
using System.Threading;

namespace ScaleSet.Hosting.Capacity;

/// <summary>Bounds concurrent runner creation across every scale set on one host.</summary>
public sealed class CreationConcurrencyGate
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private int _activeLeases;

    /// <summary>Creates a shared gate with a positive lease limit.</summary>
    public CreationConcurrencyGate(int maximumLeases)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLeases);
        MaximumLeases = maximumLeases;
    }

    /// <summary>Gets the configured maximum number of concurrent leases.</summary>
    public int MaximumLeases { get; }

    /// <summary>Gets the number of currently held leases.</summary>
    public int ActiveLeases
    {
        get
        {
            lock (_sync)
            {
                return _activeLeases;
            }
        }
    }

    /// <summary>Waits for a creation slot and returns a lease that releases it exactly once.</summary>
    public async ValueTask<CreationConcurrencyLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = CapacityTelemetry.StartActivity("creation_lease.acquire");
        if (cancellationToken.IsCancellationRequested)
        {
            CapacityTelemetry.RecordCreationLeaseOperation("cancelled");
            activity?.SetTag("outcome", "cancelled");
            cancellationToken.ThrowIfCancellationRequested();
        }

        Waiter waiter;
        lock (_sync)
        {
            if (_activeLeases < MaximumLeases && _waiters.Count == 0)
            {
                _activeLeases++;
                CapacityTelemetry.RecordCreationLeaseOperation("acquired");
                activity?.SetTag("outcome", "acquired");
                return new CreationConcurrencyLease(this);
            }

            waiter = new Waiter();
            waiter.Node = _waiters.AddLast(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            CancellationTokenRegistration registration = cancellationToken.Register(
                static state =>
                {
                    CancellationRegistrationState callback = (CancellationRegistrationState)state!;
                    callback.Gate.CancelWaiter(callback.Waiter, callback.CancellationToken);
                },
                new CancellationRegistrationState(this, waiter, cancellationToken));

            bool disposeRegistration;
            lock (_sync)
            {
                waiter.Registration = registration;
                disposeRegistration = waiter.State != WaiterState.Waiting;
            }

            if (disposeRegistration)
            {
                registration.Dispose();
            }
        }

        activity?.SetTag("outcome", "waiting");
        try
        {
            CreationConcurrencyLease lease = await waiter.Completion.Task.ConfigureAwait(false);
            activity?.SetTag("outcome", "acquired");
            return lease;
        }
        catch (OperationCanceledException)
        {
            activity?.SetTag("outcome", "cancelled");
            throw;
        }
    }

    internal void Release()
    {
        Waiter? granted = null;
        CancellationTokenRegistration grantedRegistration = default;
        lock (_sync)
        {
            if (_activeLeases <= 0)
            {
                throw new InvalidOperationException("A creation concurrency lease was released more than once.");
            }

            while (_waiters.First is LinkedListNode<Waiter> node)
            {
                _waiters.RemoveFirst();
                Waiter candidate = node.Value;
                candidate.Node = null;
                if (candidate.State != WaiterState.Waiting)
                {
                    continue;
                }

                candidate.State = WaiterState.Granted;
                granted = candidate;
                grantedRegistration = candidate.Registration;
                break;
            }

            if (granted is null)
            {
                _activeLeases--;
            }
        }

        CapacityTelemetry.RecordCreationLeaseOperation("released");
        if (granted is not null)
        {
            grantedRegistration.Dispose();
            CapacityTelemetry.RecordCreationLeaseOperation("acquired");
            granted.Completion.TrySetResult(new CreationConcurrencyLease(this));
        }
    }

    private void CancelWaiter(Waiter waiter, CancellationToken cancellationToken)
    {
        bool cancelled = false;
        lock (_sync)
        {
            if (waiter.State == WaiterState.Waiting)
            {
                if (waiter.Node is not null)
                {
                    _waiters.Remove(waiter.Node);
                    waiter.Node = null;
                }

                waiter.State = WaiterState.Cancelled;
                cancelled = true;
            }
        }

        if (cancelled)
        {
            CapacityTelemetry.RecordCreationLeaseOperation("cancelled");
            waiter.Completion.TrySetCanceled(cancellationToken);
        }
    }

    private enum WaiterState
    {
        Waiting = 0,
        Granted = 1,
        Cancelled = 2,
    }

    private sealed class Waiter
    {
        public TaskCompletionSource<CreationConcurrencyLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter>? Node { get; set; }

        public CancellationTokenRegistration Registration { get; set; }

        public WaiterState State { get; set; }
    }

    private sealed record CancellationRegistrationState(
        CreationConcurrencyGate Gate,
        Waiter Waiter,
        CancellationToken CancellationToken);
}

/// <summary>An idempotently disposable slot in the host-wide creation gate.</summary>
public sealed class CreationConcurrencyLease : IDisposable, IAsyncDisposable
{
    private CreationConcurrencyGate? _owner;

    internal CreationConcurrencyLease(CreationConcurrencyGate owner) => _owner = owner;

    /// <summary>Releases the creation slot if this lease is still active.</summary>
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();

    /// <summary>Releases the creation slot if this lease is still active.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
