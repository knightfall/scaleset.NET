using ScaleSet.Hosting.Configuration;

namespace ScaleSet.Hosting.Providers;

/// <summary>Registers a backend that creates a separate runner provider for each scale set.</summary>
public interface IRunnerProviderFactory
{
    /// <summary>Gets the case-insensitive provider name used in <see cref="ScaleSetOptions.Provider"/>.</summary>
    string Name { get; }

    /// <summary>Validates provider settings without opening connections or allocating runner resources.</summary>
    /// <param name="scaleSet">The scale set that selects this provider.</param>
    /// <param name="configurationPath">The scale set's field path for safe validation errors.</param>
    /// <returns>Backend-specific validation errors with secret values omitted.</returns>
    IReadOnlyList<string> Validate(ScaleSetOptions scaleSet, string configurationPath);

    /// <summary>Creates a new provider instance whose lifetime the runtime owns.</summary>
    /// <param name="scaleSet">Validated scale-set options.</param>
    /// <returns>A provider configured for this one scale set.</returns>
    IRunnerProvider Create(ScaleSetOptions scaleSet);

    /// <summary>Classifies backend failures that can be retried by bounded inventory reconciliation.</summary>
    /// <param name="exception">Failure reported by the backend.</param>
    /// <returns>True for a temporary backend failure that leaves ownership unconfirmed.</returns>
    bool IsTransientFailure(Exception exception) => false;
}
