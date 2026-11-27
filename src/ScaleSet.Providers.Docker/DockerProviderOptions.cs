using System.Collections.Immutable;

namespace ScaleSet.Providers.Docker;

/// <summary>Docker Engine transport and runner-process settings.</summary>
public sealed class DockerProviderOptions
{
    /// <summary>Gets the HTTP origin used to address the Docker Engine.</summary>
    /// <remarks>
    /// The default transport connects to <see cref="SocketPath"/> and uses this
    /// URI as its HTTP origin. Supply a caller-owned <see cref="HttpClient"/>
    /// when using a remote HTTP or TLS endpoint.
    /// </remarks>
    public Uri EngineEndpoint { get; init; } = new("http://docker/", UriKind.Absolute);

    /// <summary>Gets the Unix-domain socket path used by the owned transport.</summary>
    public string SocketPath { get; init; } = "/var/run/docker.sock";

    /// <summary>Gets the host-side socket path mounted for explicit HostSocket profiles.</summary>
    public string HostDockerSocketPath { get; init; } = "/var/run/docker.sock";

    /// <summary>Gets whether a missing pinned runner image is pulled from its registry.</summary>
    public bool PullImageIfMissing { get; init; } = true;

    /// <summary>Gets the bounded timeout for Docker Engine control requests.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the bounded timeout for pulling a runner image.</summary>
    public TimeSpan ImagePullTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets the maximum bytes accepted from an image-pull progress stream.</summary>
    public int MaximumPullResponseBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Gets the maximum bytes accepted for one image-pull progress record.</summary>
    public int MaximumPullLineBytes { get; init; } = 64 * 1024;

    /// <summary>Explicitly allows profiles that give jobs access to the host's Docker daemon.</summary>
    public bool AllowHostSocket { get; init; }

    /// <summary>Gets whether unprivileged runners drop capabilities, disallow gaining privileges, and disable swap.</summary>
    public bool HardenUnprivilegedContainers { get; init; } = true;

    /// <summary>Gets the unprivileged runner process limit. Zero uses the daemon's default.</summary>
    public long PidsLimit { get; init; } = 1024;

    /// <summary>Gets the container user used to run the runner process.</summary>
    public string User { get; init; } = "runner";

    /// <summary>Gets the container command used to start the runner process.</summary>
    public IReadOnlyList<string> Command { get; init; } = ImmutableArray.Create("/home/runner/run.sh");

    /// <summary>Gets an optional absolute working directory for custom runner images.</summary>
    /// <remarks>When unset, Docker preserves the working directory from the runner image.</remarks>
    public string? WorkingDirectory { get; init; }
}
