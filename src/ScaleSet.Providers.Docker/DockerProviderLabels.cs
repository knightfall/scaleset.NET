namespace ScaleSet.Providers.Docker;

/// <summary>Stable Docker labels used to identify scale-set runner containers.</summary>
public static class DockerProviderLabels
{
    /// <summary>Identifies containers created by this product.</summary>
    public const string Product = "io.scaleset.product";

    /// <summary>Identifies the owning controller.</summary>
    public const string ControllerId = "io.scaleset.controller-id";

    /// <summary>Identifies the configured scale-set key.</summary>
    public const string ScaleSetKey = "io.scaleset.scale-set-key";

    /// <summary>Identifies the GitHub scale-set numeric ID.</summary>
    public const string ScaleSetId = "io.scaleset.scale-set-id";

    /// <summary>Identifies the stable provisioning operation.</summary>
    public const string OperationId = "io.scaleset.operation-id";

    /// <summary>Identifies the runner name supplied to GitHub.</summary>
    public const string RunnerName = "io.scaleset.runner-name";

    /// <summary>Identifies the GitHub runner ID when it is known before container creation.</summary>
    public const string GitHubRunnerId = "io.scaleset.github-runner-id";
}
