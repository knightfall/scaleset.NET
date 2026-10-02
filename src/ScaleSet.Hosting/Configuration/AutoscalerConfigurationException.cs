namespace ScaleSet.Hosting.Configuration;

/// <summary>Reports safe configuration-key errors without including configured values.</summary>
public sealed class AutoscalerConfigurationException : Exception
{
    /// <summary>Initializes an exception with the default configuration error message.</summary>
    public AutoscalerConfigurationException()
        : base("Autoscaler configuration is invalid.")
    {
        Errors = Array.Empty<string>();
    }

    /// <summary>Initializes an exception with a specified error message.</summary>
    /// <param name="message">The message that describes the error.</param>
    public AutoscalerConfigurationException(string? message)
        : base(message)
    {
        Errors = Array.Empty<string>();
    }

    /// <summary>Initializes an exception with a specified message and inner exception.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused the current exception.</param>
    public AutoscalerConfigurationException(string? message, Exception? innerException)
        : base(message, innerException)
    {
        Errors = Array.Empty<string>();
    }

    internal AutoscalerConfigurationException(IEnumerable<string> errors)
        : this(errors.ToArray())
    {
    }

    private AutoscalerConfigurationException(string[] errors)
        : base($"Autoscaler configuration is invalid: {string.Join("; ", errors)}")
    {
        Errors = Array.AsReadOnly(errors);
    }

    /// <summary>Gets the field-keyed validation errors.</summary>
    public IReadOnlyList<string> Errors { get; }
}
