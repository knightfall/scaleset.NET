namespace ScaleSet.Autoscaler.Runtime;

/// <summary>Tracks whether startup ownership and provider reconciliation have completed.</summary>
public sealed class AutoscalerHealthState
{
    private int _ready;
    private int _failed;
    private int _stopping;

    /// <summary>Gets whether the worker has not faulted or entered shutdown.</summary>
    public bool IsLive => Volatile.Read(ref _failed) == 0 && Volatile.Read(ref _stopping) == 0;

    /// <summary>Gets whether all configured scale sets completed initial reconciliation.</summary>
    public bool IsReady => Volatile.Read(ref _ready) == 1;

    /// <summary>Marks the process ready after every runtime has initialized successfully.</summary>
    public void MarkReady() => Interlocked.Exchange(ref _ready, 1);

    /// <summary>Marks the process not ready during startup, failure, or shutdown.</summary>
    public void MarkNotReady() => Interlocked.Exchange(ref _ready, 0);

    /// <summary>Marks the process failed so liveness checks can report the worker fault.</summary>
    public void MarkFailed()
    {
        MarkNotReady();
        Interlocked.Exchange(ref _failed, 1);
    }

    /// <summary>Marks normal host shutdown so liveness and readiness stop succeeding.</summary>
    public void MarkStopping()
    {
        MarkNotReady();
        Interlocked.Exchange(ref _stopping, 1);
    }
}
