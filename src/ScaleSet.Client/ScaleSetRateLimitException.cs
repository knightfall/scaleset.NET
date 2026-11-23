using System.Diagnostics.CodeAnalysis;

namespace ScaleSet.Client;

/// <summary>Classifies which kind of GitHub rate limit rejected a request.</summary>
public enum ScaleSetRateLimitKind
{
    /// <summary>Spent primary quota (<c>X-RateLimit-Remaining: 0</c> without a secondary-limit message).</summary>
    Primary = 1,

    /// <summary>Secondary or unattributed rate limit (including 429 or secondary-rate 403 responses).</summary>
    Secondary = 2,
}

/// <summary>Exception thrown when GitHub or Actions rejects a request due to primary or secondary rate limiting.</summary>
[SuppressMessage(
    "Roslynator",
    "RCS1194:Implement exception constructors",
    Justification = "Implements the three standard Exception constructors; replaces the base metadata constructor with a specialized constructor.")]
public sealed class ScaleSetRateLimitException : ScaleSetApiException
{
    /// <summary>Creates a rate-limit exception with a default message.</summary>
    public ScaleSetRateLimitException()
        : this("GitHub rate limit exceeded.")
    {
    }

    /// <summary>Creates a rate-limit exception with a message.</summary>
    /// <param name="message">Human-readable error message.</param>
    public ScaleSetRateLimitException(string message)
        : base(message)
    {
        Kind = ScaleSetRateLimitKind.Secondary;
    }

    /// <summary>Creates a rate-limit exception with a message and inner exception.</summary>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="innerException">Underlying exception.</param>
    public ScaleSetRateLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = ScaleSetRateLimitKind.Secondary;
    }

    /// <summary>Creates a rate-limit exception with classification, retry hint, and response metadata.</summary>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="statusCode">HTTP status code, when a response was received.</param>
    /// <param name="kind">Rate-limit classification.</param>
    /// <param name="retryAfter">Server-suggested delay or zero when no usable hint was provided.</param>
    /// <param name="reset">UTC quota reset time, when known.</param>
    /// <param name="retries">Number of rate-limited retries already performed.</param>
    /// <param name="domainError">Scale set service error classification, when known.</param>
    /// <param name="innerException">Underlying exception, when available.</param>
    /// <param name="method">HTTP method associated with the failure.</param>
    /// <param name="safeUri">Redacted request URI.</param>
    /// <param name="activityId">Actions activity correlation identifier.</param>
    /// <param name="githubRequestId">GitHub request correlation identifier.</param>
    /// <param name="bodyPreview">Bounded, redacted preview of the response body.</param>
    /// <param name="response">Optional in-memory snapshot of the response.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate-limit kind is undefined, or the delay or retry count is negative.</exception>
    public ScaleSetRateLimitException(
        string message,
        int? statusCode,
        ScaleSetRateLimitKind kind,
        TimeSpan retryAfter,
        DateTimeOffset? reset,
        int retries,
        ScaleSetDomainError? domainError = null,
        Exception? innerException = null,
        string? method = null,
        string? safeUri = null,
        string? activityId = null,
        string? githubRequestId = null,
        string? bodyPreview = null,
        ScaleSetResponseSnapshot? response = null)
        : base(message, statusCode, domainError, innerException, method, safeUri, activityId, githubRequestId, bodyPreview, response)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Rate-limit kind must be Primary or Secondary.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(retryAfter.Ticks, nameof(retryAfter));
        ArgumentOutOfRangeException.ThrowIfNegative(retries);

        Kind = kind;
        RetryAfter = retryAfter;
        Reset = reset;
        Retries = retries;
    }

    /// <summary>Primary or secondary rate-limit classification.</summary>
    public ScaleSetRateLimitKind Kind { get; }

    /// <summary>
    /// Delay suggested by <c>Retry-After</c> (or <c>X-RateLimit-Reset</c> for primary limits),
    /// or <see cref="TimeSpan.Zero"/> when no usable hint was provided.
    /// </summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>UTC reset timestamp parsed from <c>X-RateLimit-Reset</c>, when valid and positive.</summary>
    public DateTimeOffset? Reset { get; }

    /// <summary>Number of rate-limited retries already performed for this request across both kinds.</summary>
    public int Retries { get; }
}
