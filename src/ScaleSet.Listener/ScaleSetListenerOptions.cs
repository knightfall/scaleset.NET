namespace ScaleSet.Listener;

/// <summary>Queue and acknowledgment settings for one scale-set listener.</summary>
public sealed record ScaleSetListenerOptions
{
    /// <summary>Gets the positive GitHub ID of the scale set owning the session.</summary>
    public required long ScaleSetId { get; init; }

    /// <summary>Gets the initial maximum runner capacity advertised to the queue.</summary>
    public int MaxRunners { get; init; } = 10;

    /// <summary>Gets the positive, finite time budget for each acknowledgment request.</summary>
    public TimeSpan AcknowledgmentTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
