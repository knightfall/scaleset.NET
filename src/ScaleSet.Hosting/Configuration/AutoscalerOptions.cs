using ScaleSet.Hosting.Scaling;

namespace ScaleSet.Hosting.Configuration;

/// <summary>Configuration for one autoscaler process.</summary>
public sealed class AutoscalerOptions
{
    /// <summary>Gets or sets process-wide controller settings.</summary>
    public ControllerOptions Controller { get; set; } = new();

    /// <summary>Gets or sets shared capacity limits for this controller.</summary>
    public HostCapacityOptions HostCapacity { get; set; } = new();

    /// <summary>Gets or sets the scale sets owned by this controller.</summary>
    public List<ScaleSetOptions> ScaleSets { get; set; } = [];

    /// <summary>
    /// Gets or sets credentials by stable, case-insensitive configuration key.
    /// </summary>
    public Dictionary<string, CredentialOptions> Credentials { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Process-wide controller settings.</summary>
public sealed class ControllerOptions
{
    /// <summary>Gets or sets the stable identity used to mark owned resources.</summary>
    public string Identity { get; set; } = "scaleset-controller";

    /// <summary>Gets or sets the directory used for durable controller state.</summary>
    public string? StateDirectory { get; set; }

    /// <summary>Gets or sets the interval between steady-state inventory reconciliations.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the budget for control operations such as acknowledgments.</summary>
    public TimeSpan ControlOperationTimeout { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Shared runner and resource limits for one controller host.</summary>
public sealed class HostCapacityOptions
{
    /// <summary>Gets or sets the optional aggregate runner-count limit.</summary>
    public int? MaxTotalRunners { get; set; }

    /// <summary>Gets or sets the optional aggregate CPU budget in virtual CPU cores.</summary>
    public decimal? CpuBudget { get; set; }

    /// <summary>Gets or sets the optional aggregate memory budget in mebibytes.</summary>
    public long? MemoryBudget { get; set; }

    /// <summary>Gets or sets the maximum number of concurrent runner creations.</summary>
    public int CreationConcurrency { get; set; } = 2;
}

/// <summary>Configuration for one GitHub-hosted runner scale set.</summary>
public sealed class ScaleSetOptions
{
    /// <summary>Gets or sets the stable local key for this scale set.</summary>
    public string? Key { get; set; }

    /// <summary>
    /// Gets or sets the GitHub.com or GitHub Enterprise Cloud URL for a repository, organization, or enterprise.
    /// </summary>
    public string? GithubUrl { get; set; }

    /// <summary>Gets or sets the GitHub scale-set name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the optional runner-group name.</summary>
    public string? RunnerGroup { get; set; }

    /// <summary>Gets or sets labels assigned to runners in this scale set.</summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>Gets or sets the key of the credential used by this scale set.</summary>
    public string? CredentialsRef { get; set; }

    /// <summary>Gets or sets the provider used to create runners.</summary>
    public string Provider { get; set; } = "Docker";

    /// <summary>Gets or sets the scaling policy and per-set limits.</summary>
    public ScalingOptions Scaling { get; set; } = new();

    /// <summary>Gets or sets startup, drain, and cleanup timeouts.</summary>
    public LifecycleOptions Lifecycle { get; set; } = new();

    /// <summary>Gets or sets the Docker runner resource profile.</summary>
    public RunnerProfileOptions RunnerProfile { get; set; } = new();
}

/// <summary>Authentication material for one named GitHub credential.</summary>
public sealed class CredentialOptions
{
    /// <summary>Gets or sets the GitHub App client ID.</summary>
    public string? ClientId { get; set; }

    /// <summary>Gets or sets the GitHub App installation ID.</summary>
    public string? InstallationId { get; set; }

    /// <summary>Gets or sets the GitHub App private key in PEM form.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>Gets or sets a personal access token.</summary>
    public string? Pat { get; set; }
}

/// <summary>Scaling policy and limits for one scale set.</summary>
public sealed class ScalingOptions
{
    /// <summary>Gets or sets the scaling policy.</summary>
    public ScalingPolicy Policy { get; set; } = ScalingPolicy.SpareCapacity;

    /// <summary>Gets or sets the minimum total runner count.</summary>
    public int MinRunners { get; set; }

    /// <summary>Gets or sets the maximum total runner count.</summary>
    public int MaxRunners { get; set; } = 10;

    /// <summary>Gets or sets the idle retirement setting. Null and zero disable automatic retirement.</summary>
    /// <remarks>Positive timeouts require assignment-safe per-runner idle evidence, which the autoscaler cannot establish.</remarks>
    public TimeSpan? IdleTimeout { get; set; }
}

/// <summary>Lifecycle time budgets for one scale set.</summary>
public sealed class LifecycleOptions
{
    /// <summary>Gets or sets the maximum time allowed for a runner to become ready.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets or sets how long shutdown waits for active jobs to finish.</summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the bounded cleanup-operation budget.</summary>
    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Resources and execution settings for a Docker runner.</summary>
public sealed class RunnerProfileOptions
{
    /// <summary>Gets or sets the pinned container image reference.</summary>
    public string? Image { get; set; }

    /// <summary>Gets or sets the runner architecture.</summary>
    public RunnerArchitecture? Architecture { get; set; }

    /// <summary>Gets or sets the requested CPU cores.</summary>
    public decimal Cpu { get; set; }

    /// <summary>Gets or sets the requested memory in mebibytes.</summary>
    public long Memory { get; set; }

    /// <summary>Gets or sets the work directory inside the runner container.</summary>
    public string WorkFolder { get; set; } = "_work";

    /// <summary>Gets or sets the optional Docker network name.</summary>
    public string? Network { get; set; }

    /// <summary>Gets or sets the requested Docker build mode.</summary>
    public DockerBuildMode DockerBuildMode { get; set; }

    /// <summary>Gets or sets whether the runner container is privileged.</summary>
    public bool Privileged { get; set; }
}

/// <summary>CPU architecture supported by the phase-one runner images.</summary>
public enum RunnerArchitecture
{
    /// <summary>64-bit x86.</summary>
    X64 = 0,

    /// <summary>64-bit ARM.</summary>
    Arm64 = 1,
}

/// <summary>Docker access mode requested by a runner profile.</summary>
public enum DockerBuildMode
{
    /// <summary>Docker access is disabled.</summary>
    Disabled = 0,

    /// <summary>The runner uses the host Docker socket.</summary>
    HostSocket = 1,

    /// <summary>The runner starts its own Docker daemon.</summary>
    DockerInDocker = 2,
}
