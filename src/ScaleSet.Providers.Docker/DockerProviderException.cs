using System.Net;

namespace ScaleSet.Providers.Docker;

/// <summary>A safe, bounded Docker provider failure without response or request content.</summary>
public sealed class DockerProviderException : Exception
{
    public DockerProviderException()
        : this("other", "provider-failure")
    {
    }

    public DockerProviderException(string? message)
        : base(message)
    {
        Operation = "other";
        FailureCode = "provider-failure";
    }

    public DockerProviderException(string? message, Exception? innerException)
        : base(message, innerException)
    {
        Operation = "other";
        FailureCode = "provider-failure";
    }

    internal DockerProviderException(
        string operation,
        string failureCode,
        HttpStatusCode? statusCode = null,
        bool outcomeMayBeUnknown = false)
        : base(CreateMessage(operation, failureCode, statusCode, outcomeMayBeUnknown))
    {
        Operation = operation;
        FailureCode = failureCode;
        StatusCode = statusCode;
        OutcomeMayBeUnknown = outcomeMayBeUnknown;
    }

    /// <summary>Gets the finite provider operation name.</summary>
    public string Operation { get; }

    /// <summary>Gets the finite, secret-free failure category.</summary>
    public string FailureCode { get; }

    /// <summary>Gets the HTTP status returned by Docker, when one was received.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Gets whether a request may have taken effect before the failure.</summary>
    public bool OutcomeMayBeUnknown { get; }

    private static string CreateMessage(
        string operation,
        string failureCode,
        HttpStatusCode? statusCode,
        bool outcomeMayBeUnknown)
    {
        string status = statusCode is null ? string.Empty : $" (HTTP {(int)statusCode.Value})";
        string outcome = outcomeMayBeUnknown ? " The provider outcome may be unknown." : string.Empty;
        return $"Docker provider operation '{operation}' failed ({failureCode}){status}.{outcome}";
    }
}
