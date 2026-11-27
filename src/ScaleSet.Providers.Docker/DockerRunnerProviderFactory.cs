using System.Net;
using Microsoft.Extensions.Logging;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;

namespace ScaleSet.Providers.Docker;

/// <summary>Registers Docker runner settings and creates a provider for an individual scale set.</summary>
public sealed class DockerRunnerProviderFactory : IRunnerProviderFactory
{
    private readonly DockerProviderOptions _options;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly TimeProvider? _timeProvider;

    /// <summary>Creates a Docker provider factory with the default provider settings.</summary>
    /// <param name="loggerFactory">Optional factory for the provider logger.</param>
    /// <param name="timeProvider">Optional clock for provider requests.</param>
    public DockerRunnerProviderFactory(ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null)
        : this(new DockerProviderOptions(), loggerFactory, timeProvider)
    {
    }

    /// <summary>Creates a factory that applies the configured Docker provider settings.</summary>
    /// <param name="options">Settings shared by Docker providers from this factory.</param>
    /// <param name="loggerFactory">Optional factory for the provider logger.</param>
    /// <param name="timeProvider">Optional clock for provider requests.</param>
    public DockerRunnerProviderFactory(DockerProviderOptions options, ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = DockerProviderOptionsSnapshot.Capture(options, callerOwnsHttpClient: false);
        _options = options;
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider;
    }

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

        if (profile.DockerBuildMode == DockerBuildMode.HostSocket && !_options.AllowHostSocket)
        {
            errors.Add($"{path}.DockerBuildMode requires Providers:Docker:AllowHostSocket=true.");
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
            options: _options,
            logger: _loggerFactory?.CreateLogger<DockerRunnerProvider>(),
            timeProvider: _timeProvider);
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
