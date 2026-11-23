using System.Net;
using Microsoft.Extensions.Logging;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;

namespace ScaleSet.Providers.Docker;

/// <summary>Registers Docker runner settings and creates a provider for an individual scale set.</summary>
/// <param name="loggerFactory">Optional factory for the provider logger.</param>
/// <param name="timeProvider">Optional clock for provider requests.</param>
public sealed class DockerRunnerProviderFactory(ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null) : IRunnerProviderFactory
{
    /// <inheritdoc />
    public string Name => "Docker";

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(ScaleSetOptions scaleSet, string configurationPath)
    {
        ArgumentNullException.ThrowIfNull(scaleSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        RunnerProfileOptions profile = scaleSet.RunnerProfile;
        string path = $"{configurationPath}.RunnerProfile";
        List<string> errors = [];
        if (!DockerRunnerProfileSnapshot.IsPinnedImage(profile.Image))
        {
            errors.Add($"{path}.Image must be a pinned image tag or sha256 digest.");
        }

        if (profile.Architecture is null)
        {
            errors.Add($"{path}.Architecture must be X64 or Arm64.");
        }

        if (!Enum.IsDefined(profile.DockerBuildMode))
        {
            errors.Add($"{path}.DockerBuildMode is not supported.");
        }
        else if (profile.DockerBuildMode == DockerBuildMode.DockerInDocker && !profile.Privileged)
        {
            errors.Add($"{path}.Privileged must be true for DockerInDocker.");
        }

        if (profile.Network is not null && string.IsNullOrWhiteSpace(profile.Network))
        {
            errors.Add($"{path}.Network must not be empty when configured.");
        }

        return errors.AsReadOnly();
    }

    /// <inheritdoc />
    public IRunnerProvider Create(ScaleSetOptions scaleSet)
    {
        ArgumentNullException.ThrowIfNull(scaleSet);
        return new DockerRunnerProvider(
            scaleSet.RunnerProfile,
            logger: loggerFactory?.CreateLogger<DockerRunnerProvider>(),
            timeProvider: timeProvider);
    }

    /// <inheritdoc />
    public bool IsTransientFailure(Exception exception) =>
        exception is DockerProviderException
        {
            FailureCode: "request-timeout" or "engine-unavailable" or "engine-response-read-failed",
        }
        || exception is DockerProviderException
        {
            StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
        };
}
