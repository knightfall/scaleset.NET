using ScaleSet.Client;

namespace ScaleSet.Listener;

/// <summary>Processes initial statistics, real batches and empty long polls.</summary>
/// <remarks>Successful handling permits acknowledgment. Partial work must tolerate replay.</remarks>
public interface IScaleSetMessageHandler
{
    Task HandleAsync(RunnerScaleSetMessage? message, CancellationToken cancellationToken);
}
