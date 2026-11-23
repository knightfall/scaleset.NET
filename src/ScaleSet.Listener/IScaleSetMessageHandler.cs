using ScaleSet.Client;

namespace ScaleSet.Listener;

/// <summary>Processes initial statistics, real batches and empty long polls.</summary>
/// <remarks>Successful handling permits acknowledgment. Partial work must tolerate replay.</remarks>
public interface IScaleSetMessageHandler
{
    /// <summary>Processes the next listener callback.</summary>
    /// <param name="message">Initial statistics or a real batch, or <see langword="null"/> after an empty poll.</param>
    /// <param name="cancellationToken">Token used to cancel callback processing.</param>
    Task HandleAsync(RunnerScaleSetMessage? message, CancellationToken cancellationToken);
}
