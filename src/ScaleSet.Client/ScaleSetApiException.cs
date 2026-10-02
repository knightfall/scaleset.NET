namespace ScaleSet.Client;

/// <summary>An error category returned by the GitHub Actions scale set service.</summary>
public enum ScaleSetDomainError
{
    /// <summary>Runner already exists.</summary>
    AgentExists = 0,

    /// <summary>Runner was not found.</summary>
    AgentNotFound = 1,

    /// <summary>Runner has a job that is still running.</summary>
    JobStillRunning = 2,

    /// <summary>Message queue access token has expired.</summary>
    MessageQueueTokenExpired = 3,
}

/// <summary>An HTTP or domain error returned while using the ScaleSet client.</summary>
public sealed class ScaleSetApiException : Exception
{
    /// <summary>Creates an exception with a default message.</summary>
    public ScaleSetApiException()
        : this("ScaleSet API request failed")
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">Human-readable error message.</param>
    public ScaleSetApiException(string message)
        : this(message, statusCode: null, domainError: null, innerException: null)
    {
    }

    /// <summary>Creates an exception with a message and underlying exception.</summary>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="innerException">Underlying exception.</param>
    public ScaleSetApiException(string message, Exception innerException)
        : this(message, statusCode: null, domainError: null, innerException: innerException)
    {
    }

    /// <summary>Creates an exception with independent HTTP status and domain classifications.</summary>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="statusCode">HTTP status code, when a response was received.</param>
    /// <param name="domainError">Scale set service error classification, when known.</param>
    /// <param name="innerException">Underlying exception, when available.</param>
    public ScaleSetApiException(
        string message,
        int? statusCode,
        ScaleSetDomainError? domainError = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "HTTP status codes must be between 100 and 599.");
        }

        StatusCode = statusCode;
        DomainError = domainError;
    }

    /// <summary>HTTP status code, or <see langword="null"/> when no response was received.</summary>
    public int? StatusCode { get; }

    /// <summary>Recognized domain classification, or <see langword="null"/> when none was identified.</summary>
    public ScaleSetDomainError? DomainError { get; }
}
