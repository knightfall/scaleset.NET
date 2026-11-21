using System.Globalization;
using Microsoft.Extensions.Configuration;
using ScaleSet.Client;
using ScaleSet.Hosting.Scaling;

namespace ScaleSet.Hosting.Configuration;

/// <summary>Binds and validates the product configuration supplied by a host.</summary>
/// <remarks>
/// The supplied configuration is the product-specific root after its source has
/// applied the <c>SCALESET__</c> environment-variable prefix. This method does not
/// add configuration providers; hosts can therefore keep JSON and command-line
/// sources out of the product settings chain.
/// </remarks>
public static class AutoscalerConfiguration
{
    /// <summary>Binds options from the product configuration root and validates the complete snapshot.</summary>
    /// <param name="configuration">Configuration rooted at Controller, HostCapacity, ScaleSets, and Credentials.</param>
    /// <returns>A validated options snapshot.</returns>
    /// <exception cref="AutoscalerConfigurationException">A value cannot be bound or validation fails.</exception>
    public static AutoscalerOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        List<string> errors = [];
        AutoscalerOptions options = new()
        {
            Controller = BindSection<ControllerOptions>(configuration.GetSection(nameof(AutoscalerOptions.Controller)), nameof(AutoscalerOptions.Controller), errors),
            HostCapacity = BindSection<HostCapacityOptions>(configuration.GetSection(nameof(AutoscalerOptions.HostCapacity)), nameof(AutoscalerOptions.HostCapacity), errors),
            ScaleSets = BindScaleSets(configuration.GetSection(nameof(AutoscalerOptions.ScaleSets)), errors),
            Credentials = BindCredentials(configuration.GetSection(nameof(AutoscalerOptions.Credentials)), errors),
        };

        errors.AddRange(Validate(options));
        if (errors.Count > 0)
        {
            throw new AutoscalerConfigurationException(errors);
        }

        return options;
    }

    /// <summary>Validates an options snapshot and returns safe, field-keyed error messages.</summary>
    /// <param name="options">The options snapshot to validate.</param>
    /// <returns>Validation errors; an empty list means the snapshot is valid.</returns>
    public static IReadOnlyList<string> Validate(AutoscalerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> errors = [];
        ValidateController(options.Controller, errors);
        ValidateHostCapacity(options.HostCapacity, errors);

        Dictionary<string, CredentialOptions> credentialLookup = BuildCredentialLookup(options.Credentials, errors);
        ValidateCredentials(credentialLookup, errors);

        List<ScaleSetIdentity> ownershipIdentities = [];
        HashSet<string> scaleSetKeys = new(StringComparer.OrdinalIgnoreCase);
        List<RunnerDemand> minimumDemand = [];

        if (options.ScaleSets is null || options.ScaleSets.Count == 0)
        {
            errors.Add("ScaleSets must contain at least one scale set.");
        }
        else
        {
            for (int index = 0; index < options.ScaleSets.Count; index++)
            {
                ScaleSetOptions? scaleSet = options.ScaleSets[index];
                string path = $"ScaleSets[{index}]";
                if (scaleSet is null)
                {
                    errors.Add($"{path} must be configured.");
                    continue;
                }

                ValidateScaleSet(scaleSet, path, options.HostCapacity, credentialLookup, scaleSetKeys, ownershipIdentities, minimumDemand, errors);
            }
        }

        ValidateMinimumCapacity(options.HostCapacity, minimumDemand, errors);
        return errors.AsReadOnly();
    }

    private static T BindSection<T>(IConfigurationSection section, string path, List<string> errors)
        where T : class, new()
    {
        T result = new();
        try
        {
            section.Bind(result);
        }
        catch (Exception exception) when (IsBindingFailure(exception))
        {
            // Binder exception messages can contain configuration values. Only expose the key path.
            errors.Add($"{path} contains a value with an invalid format.");
        }

        return result;
    }

    private static bool IsBindingFailure(Exception exception) =>
        exception is ArgumentException or FormatException or InvalidOperationException or OverflowException;

    private static List<ScaleSetOptions> BindScaleSets(IConfigurationSection section, List<string> errors)
    {
        List<(int Index, IConfigurationSection Section)> indexedSections = [];
        foreach (IConfigurationSection child in section.GetChildren())
        {
            if (!int.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0)
            {
                errors.Add("ScaleSets contains a child with an invalid numeric index.");
                continue;
            }

            indexedSections.Add((index, child));
        }

        indexedSections.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        List<ScaleSetOptions> scaleSets = new(indexedSections.Count);
        for (int position = 0; position < indexedSections.Count; position++)
        {
            (int index, IConfigurationSection child) = indexedSections[position];
            if (index != position)
            {
                errors.Add("ScaleSets indices must be unique, contiguous, and start at zero.");
            }

            scaleSets.Add(BindSection<ScaleSetOptions>(child, $"ScaleSets[{index}]", errors));
        }

        return scaleSets;
    }

    private static Dictionary<string, CredentialOptions> BindCredentials(IConfigurationSection section, List<string> errors)
    {
        Dictionary<string, CredentialOptions> credentials = new(StringComparer.OrdinalIgnoreCase);
        IConfigurationSection[] children = section.GetChildren().OrderBy(static child => child.Key, StringComparer.Ordinal).ToArray();

        for (int index = 0; index < children.Length; index++)
        {
            IConfigurationSection child = children[index];
            CredentialOptions credential = BindSection<CredentialOptions>(child, $"Credentials[{child.Key}]", errors);
            if (!credentials.TryAdd(child.Key, credential))
            {
                errors.Add("Credentials contains keys that differ only by case.");
            }
        }

        return credentials;
    }

    private static void ValidateController(ControllerOptions? controller, List<string> errors)
    {
        if (controller is null)
        {
            errors.Add("Controller must be configured.");
            return;
        }

        if (string.IsNullOrWhiteSpace(controller.Identity))
        {
            errors.Add("Controller.Identity is required.");
        }

        if (string.IsNullOrWhiteSpace(controller.StateDirectory))
        {
            errors.Add("Controller.StateDirectory is required.");
        }

        if (controller.ReconcileInterval <= TimeSpan.Zero)
        {
            errors.Add("Controller.ReconcileInterval must be greater than zero.");
        }

        if (controller.ControlOperationTimeout <= TimeSpan.Zero)
        {
            errors.Add("Controller.ControlOperationTimeout must be greater than zero.");
        }
    }

    private static void ValidateHostCapacity(HostCapacityOptions? capacity, List<string> errors)
    {
        if (capacity is null)
        {
            errors.Add("HostCapacity must be configured.");
            return;
        }

        if (capacity.MaxTotalRunners < 0)
        {
            errors.Add("HostCapacity.MaxTotalRunners must be zero or greater.");
        }

        if (capacity.CpuBudget is <= 0)
        {
            errors.Add("HostCapacity.CpuBudget must be greater than zero when configured.");
        }

        if (capacity.MemoryBudget is <= 0)
        {
            errors.Add("HostCapacity.MemoryBudget must be greater than zero when configured.");
        }

        if (capacity.CreationConcurrency <= 0)
        {
            errors.Add("HostCapacity.CreationConcurrency must be greater than zero.");
        }
    }

    private static Dictionary<string, CredentialOptions> BuildCredentialLookup(
        Dictionary<string, CredentialOptions>? credentials,
        List<string> errors)
    {
        Dictionary<string, CredentialOptions> result = new(StringComparer.OrdinalIgnoreCase);
        if (credentials is null)
        {
            errors.Add("Credentials must be configured.");
            return result;
        }

        foreach (KeyValuePair<string, CredentialOptions> entry in credentials)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                errors.Add("Credentials contains an empty key.");
                continue;
            }

            if (!result.TryAdd(entry.Key, entry.Value))
            {
                errors.Add("Credentials contains keys that differ only by case.");
            }
        }

        return result;
    }

    private static void ValidateCredentials(Dictionary<string, CredentialOptions> credentials, List<string> errors)
    {
        foreach (KeyValuePair<string, CredentialOptions> entry in credentials.OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            string path = $"Credentials[{entry.Key}]";
            CredentialOptions? credential = entry.Value;
            if (credential is null)
            {
                errors.Add($"{path} must be configured.");
                continue;
            }

            ValidateCredential(credential, path, errors);
        }
    }

    private static void ValidateCredential(CredentialOptions credential, string path, List<string> errors)
    {
        bool hasPatValue = credential.Pat is not null;
        bool hasAppValues = credential.ClientId is not null
            || credential.InstallationId is not null
            || credential.PrivateKeyPem is not null;

        if (hasPatValue && hasAppValues)
        {
            errors.Add($"{path} must use either PAT or GitHub App credentials, not both.");
            return;
        }

        if (hasPatValue)
        {
            if (string.IsNullOrWhiteSpace(credential.Pat))
            {
                errors.Add($"{path}.Pat is required when PAT authentication is selected.");
            }

            return;
        }

        if (!hasAppValues)
        {
            errors.Add($"{path} must define PAT or a complete GitHub App credential.");
            return;
        }

        if (string.IsNullOrWhiteSpace(credential.ClientId))
        {
            errors.Add($"{path}.ClientId is required for GitHub App authentication.");
        }

        if (!long.TryParse(credential.InstallationId, NumberStyles.None, CultureInfo.InvariantCulture, out long installationId)
            || installationId <= 0)
        {
            errors.Add($"{path}.InstallationId must be a positive integer for GitHub App authentication.");
        }

        if (string.IsNullOrWhiteSpace(credential.PrivateKeyPem))
        {
            errors.Add($"{path}.PrivateKeyPem is required for GitHub App authentication.");
        }
    }

    private static void ValidateScaleSet(
        ScaleSetOptions scaleSet,
        string path,
        HostCapacityOptions? capacity,
        Dictionary<string, CredentialOptions> credentials,
        HashSet<string> scaleSetKeys,
        List<ScaleSetIdentity> ownershipIdentities,
        List<RunnerDemand> minimumDemand,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(scaleSet.Key))
        {
            errors.Add($"{path}.Key is required.");
        }
        else if (!scaleSetKeys.Add(scaleSet.Key))
        {
            errors.Add($"{path}.Key duplicates another scale-set key.");
        }

        if (string.IsNullOrWhiteSpace(scaleSet.Name))
        {
            errors.Add($"{path}.Name is required.");
        }

        if (!string.Equals(scaleSet.Provider, "Docker", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{path}.Provider must be Docker in phase one.");
        }

        if (string.IsNullOrWhiteSpace(scaleSet.CredentialsRef)
            || !credentials.ContainsKey(scaleSet.CredentialsRef))
        {
            errors.Add($"{path}.CredentialsRef must reference a configured credential key.");
        }

        if (!TryParseGitHubScope(scaleSet.GithubUrl, out GitHubScope scope))
        {
            errors.Add($"{path}.GithubUrl must be an HTTPS GitHub.com or GitHub Enterprise Cloud (*.ghe.com) organization, repository, or enterprise URL.");
        }
        else if (!string.IsNullOrWhiteSpace(scaleSet.Name))
        {
            string runnerGroup = string.IsNullOrWhiteSpace(scaleSet.RunnerGroup)
                ? "Default"
                : scaleSet.RunnerGroup.Trim();
            ScaleSetIdentity identity = new(scope, runnerGroup, scaleSet.Name.Trim());
            if (ownershipIdentities.Any(existingIdentity => HasSameOwnershipIdentity(identity, existingIdentity)))
            {
                errors.Add($"{path}.Name duplicates a scale-set name for the same GitHub target and runner group.");
            }
            else
            {
                ownershipIdentities.Add(identity);
            }
        }

        ValidateLabels(scaleSet.Labels, path, errors);
        ValidateScaling(scaleSet.Scaling, path, errors);
        ValidateLifecycle(scaleSet.Lifecycle, path, errors);

        RunnerProfileOptions? runnerProfile = scaleSet.RunnerProfile;
        if (runnerProfile is null)
        {
            errors.Add($"{path}.RunnerProfile must be configured.");
            return;
        }

        ValidateRunnerProfile(runnerProfile, path, errors);
        if (scaleSet.Scaling is not null
            && runnerProfile.Cpu > 0
            && runnerProfile.Memory > 0
            && scaleSet.Scaling.MinRunners >= 0)
        {
            minimumDemand.Add(new RunnerDemand(scaleSet.Scaling.MinRunners, runnerProfile.Cpu, runnerProfile.Memory));
        }

        if (capacity is not null)
        {
            ValidateProfileWithinBudget(runnerProfile, capacity, path, errors);
        }
    }

    private static void ValidateLabels(List<string>? labels, string path, List<string> errors)
    {
        if (labels is null)
        {
            errors.Add($"{path}.Labels must not be null.");
            return;
        }

        HashSet<string> labelsSeen = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < labels.Count; index++)
        {
            string? label = labels[index];
            if (string.IsNullOrWhiteSpace(label))
            {
                errors.Add($"{path}.Labels[{index}] must not be empty.");
            }
            else if (!labelsSeen.Add(label))
            {
                errors.Add($"{path}.Labels[{index}] duplicates another label.");
            }
        }
    }

    private static void ValidateScaling(ScalingOptions? scaling, string path, List<string> errors)
    {
        if (scaling is null)
        {
            errors.Add($"{path}.Scaling must be configured.");
            return;
        }

        if (!Enum.IsDefined(scaling.Policy))
        {
            errors.Add($"{path}.Scaling.Policy is not supported.");
        }

        if (scaling.MinRunners < 0)
        {
            errors.Add($"{path}.Scaling.MinRunners must be zero or greater.");
        }

        if (scaling.MaxRunners < 0)
        {
            errors.Add($"{path}.Scaling.MaxRunners must be zero or greater.");
        }

        if (scaling.MinRunners >= 0 && scaling.MaxRunners >= 0 && scaling.MinRunners > scaling.MaxRunners)
        {
            errors.Add($"{path}.Scaling.MinRunners must not exceed MaxRunners.");
        }

        if (scaling.IdleTimeout < TimeSpan.Zero)
        {
            errors.Add($"{path}.Scaling.IdleTimeout must be zero or greater when configured.");
        }
    }

    private static void ValidateLifecycle(LifecycleOptions? lifecycle, string path, List<string> errors)
    {
        if (lifecycle is null)
        {
            errors.Add($"{path}.Lifecycle must be configured.");
            return;
        }

        if (lifecycle.StartupTimeout <= TimeSpan.Zero)
        {
            errors.Add($"{path}.Lifecycle.StartupTimeout must be greater than zero.");
        }

        if (lifecycle.ShutdownDrainTimeout < TimeSpan.Zero)
        {
            errors.Add($"{path}.Lifecycle.ShutdownDrainTimeout must be zero or greater.");
        }

        if (lifecycle.CleanupTimeout <= TimeSpan.Zero)
        {
            errors.Add($"{path}.Lifecycle.CleanupTimeout must be greater than zero.");
        }
    }

    private static void ValidateRunnerProfile(RunnerProfileOptions profile, string path, List<string> errors)
    {
        string profilePath = $"{path}.RunnerProfile";
        if (!IsPinnedImage(profile.Image))
        {
            errors.Add($"{profilePath}.Image must be a pinned image tag or sha256 digest.");
        }

        if (profile.Architecture is null || !Enum.IsDefined(profile.Architecture.Value))
        {
            errors.Add($"{profilePath}.Architecture must be X64 or Arm64.");
        }

        if (profile.Cpu <= 0)
        {
            errors.Add($"{profilePath}.Cpu must be greater than zero.");
        }

        if (profile.Memory <= 0)
        {
            errors.Add($"{profilePath}.Memory must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(profile.WorkFolder))
        {
            errors.Add($"{profilePath}.WorkFolder is required.");
        }

        if (!Enum.IsDefined(profile.DockerBuildMode))
        {
            errors.Add($"{profilePath}.DockerBuildMode is not supported.");
        }
        else if (profile.DockerBuildMode == DockerBuildMode.DockerInDocker && !profile.Privileged)
        {
            errors.Add($"{profilePath}.Privileged must be true for DockerInDocker.");
        }

        if (profile.Network is not null && string.IsNullOrWhiteSpace(profile.Network))
        {
            errors.Add($"{profilePath}.Network must not be empty when configured.");
        }
    }

    private static void ValidateProfileWithinBudget(
        RunnerProfileOptions profile,
        HostCapacityOptions capacity,
        string path,
        List<string> errors)
    {
        if (profile.Cpu > 0 && capacity.CpuBudget is > 0 && profile.Cpu > capacity.CpuBudget.Value)
        {
            errors.Add($"{path}.RunnerProfile.Cpu exceeds HostCapacity.CpuBudget.");
        }

        if (profile.Memory > 0 && capacity.MemoryBudget is > 0 && profile.Memory > capacity.MemoryBudget.Value)
        {
            errors.Add($"{path}.RunnerProfile.Memory exceeds HostCapacity.MemoryBudget.");
        }
    }

    private static void ValidateMinimumCapacity(
        HostCapacityOptions? capacity,
        List<RunnerDemand> demands,
        List<string> errors)
    {
        if (capacity is null)
        {
            return;
        }

        try
        {
            long minimumRunners = 0;
            decimal minimumCpu = 0;
            long minimumMemory = 0;
            foreach (RunnerDemand demand in demands)
            {
                minimumRunners = checked(minimumRunners + demand.MinimumRunners);
                minimumCpu = checked(minimumCpu + (demand.Cpu * demand.MinimumRunners));
                minimumMemory = checked(minimumMemory + (demand.Memory * demand.MinimumRunners));
            }

            if (capacity.MaxTotalRunners is >= 0 && minimumRunners > capacity.MaxTotalRunners.Value)
            {
                errors.Add("HostCapacity.MaxTotalRunners is below the sum of configured scale-set minimums.");
            }

            if (capacity.CpuBudget is > 0 && minimumCpu > capacity.CpuBudget.Value)
            {
                errors.Add("HostCapacity.CpuBudget is below the CPU demand of configured scale-set minimums.");
            }

            if (capacity.MemoryBudget is > 0 && minimumMemory > capacity.MemoryBudget.Value)
            {
                errors.Add("HostCapacity.MemoryBudget is below the memory demand of configured scale-set minimums.");
            }
        }
        catch (OverflowException)
        {
            errors.Add("HostCapacity minimum runner demand exceeds the supported numeric range.");
        }
    }

    private static bool TryParseGitHubScope(string? value, out GitHubScope scope)
    {
        scope = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        GitHubTarget target;
        try
        {
            target = GitHubTarget.Parse(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (target.Owner.Any(char.IsWhiteSpace)
            || target.Repository?.Any(char.IsWhiteSpace) == true
            || target.Enterprise?.Any(char.IsWhiteSpace) == true)
        {
            return false;
        }

        string normalizedHost = string.Equals(target.SourceUri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase)
            ? "GITHUB.COM"
            : target.SourceUri.Host.ToUpperInvariant();
        scope = new GitHubScope(
            normalizedHost,
            target.Scope,
            target.Owner.ToUpperInvariant(),
            target.Repository?.ToUpperInvariant());
        return true;
    }

    private static bool HasSameOwnershipIdentity(ScaleSetIdentity left, ScaleSetIdentity right) =>
        string.Equals(left.Target.Host, right.Target.Host, StringComparison.OrdinalIgnoreCase)
        && left.Target.Scope == right.Target.Scope
        && string.Equals(left.Target.Owner, right.Target.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Target.Repository, right.Target.Repository, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.RunnerGroup, right.RunnerGroup, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

    private static bool IsPinnedImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.Any(char.IsWhiteSpace))
        {
            return false;
        }

        int digestSeparator = image.LastIndexOf("@sha256:", StringComparison.OrdinalIgnoreCase);
        if (digestSeparator >= 0)
        {
            string digest = image[(digestSeparator + "@sha256:".Length)..];
            return digestSeparator > 0 && digest.Length == 64 && digest.All(Uri.IsHexDigit);
        }

        int lastSlash = image.LastIndexOf('/');
        int tagSeparator = image.LastIndexOf(':');
        if (tagSeparator <= lastSlash + 1 || tagSeparator == image.Length - 1)
        {
            return false;
        }

        string tag = image[(tagSeparator + 1)..];
        return !string.Equals(tag, "latest", StringComparison.OrdinalIgnoreCase)
            && (char.IsLetterOrDigit(tag[0]) || tag[0] == '_')
            && tag.All(static character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private readonly record struct GitHubScope(string Host, GitHubTargetScope Scope, string Owner, string? Repository);

    private readonly record struct ScaleSetIdentity(GitHubScope Target, string RunnerGroup, string Name);

    private readonly record struct RunnerDemand(int MinimumRunners, decimal Cpu, long Memory);
}
