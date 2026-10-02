namespace ScaleSet.Listener;

public sealed record ScaleSetListenerOptions
{
    public required long ScaleSetId { get; init; }

    public int MaxRunners { get; init; } = 10;

    public TimeSpan AcknowledgmentTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
