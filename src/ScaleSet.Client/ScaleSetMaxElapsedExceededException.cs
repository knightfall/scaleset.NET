using System.Diagnostics.CodeAnalysis;

namespace ScaleSet.Client;

/// <summary>
/// Exception thrown when a request exceeds its configured <see cref="ScaleSetRetryOptions.MaxElapsed"/>
/// budget during an attempt or while reading the final response body.
/// </summary>
[SuppressMessage(
    "Roslynator",
    "RCS1194:Implement exception constructors",
    Justification = "Implements the three standard Exception constructors; replaces the base metadata constructor with a specialized constructor.")]
public sealed class ScaleSetMaxElapsedExceededException : ScaleSetApiException
{
    /// <summary>Creates a max-elapsed exception with a default message.</summary>
    public ScaleSetMaxElapsedExceededException()
        : this("ScaleSet request exceeded its configured MaxElapsed retry budget.")
    {
    }

    /// <summary>Creates a max-elapsed exception with a message.</summary>
    /// <param name="message">Human-readable error message.</param>
    public ScaleSetMaxElapsedExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a max-elapsed exception with a message and underlying exception.</summary>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="innerException">Underlying exception.</param>
    public ScaleSetMaxElapsedExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates a max-elapsed exception with budget and response metadata.</summary>
    public ScaleSetMaxElapsedExceededException(
        string message,
        TimeSpan maxElapsed,
        int? statusCode = null,
        string? method = null,
        string? safeUri = null,
        string? activityId = null,
        string? githubRequestId = null,
        string? bodyPreview = null,
        ScaleSetResponseSnapshot? response = null,
        Exception? innerException = null)
        : base(message, statusCode, domainError: null, innerException, method, safeUri, activityId, githubRequestId, bodyPreview, response)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxElapsed.Ticks, nameof(maxElapsed));
        MaxElapsed = maxElapsed;
    }

    /// <summary>The configured <see cref="ScaleSetRetryOptions.MaxElapsed"/> budget that was exceeded.</summary>
    public TimeSpan MaxElapsed { get; }
}
