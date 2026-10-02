using Microsoft.Extensions.Configuration;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Scaling;

namespace ScaleSet.Hosting.Tests.Configuration;

public sealed class AutoscalerConfigurationTests
{
    private const string SecretSentinel = "do-not-print-this-secret";

    [Fact]
    public void OptionDefaults_MatchTheAcceptedControllerAndLifecycleStartingValues()
    {
        ControllerOptions controller = new();
        HostCapacityOptions capacity = new();
        ScalingOptions scaling = new();
        LifecycleOptions lifecycle = new();

        Assert.Equal(TimeSpan.FromSeconds(30), controller.ReconcileInterval);
        Assert.Equal(TimeSpan.FromMinutes(1), controller.ControlOperationTimeout);
        Assert.Equal(2, capacity.CreationConcurrency);
        Assert.Equal(ScalingPolicy.SpareCapacity, scaling.Policy);
        Assert.Equal(0, scaling.MinRunners);
        Assert.Equal(10, scaling.MaxRunners);
        Assert.Equal(TimeSpan.FromMinutes(15), lifecycle.StartupTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), lifecycle.ShutdownDrainTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1), lifecycle.CleanupTimeout);
    }

    [Fact]
    public void Bind_UsesClrPropertyNamesAndBindsNestedListsAndCredentialsCaseInsensitively()
    {
        IConfiguration configuration = BuildConfiguration(ValidConfigurationValues(
            "controller:statedirectory", "/state",
            "Controller:ReconcileInterval", "00:00:45",
            "HostCapacity:CreationConcurrency", "3",
            "ScaleSets:0:Key", "build-linux",
            "ScaleSets:0:GithubUrl", "https://github.com/Example-Org/",
            "ScaleSets:0:Name", "linux-runners",
            "ScaleSets:0:CredentialsRef", "github-app",
            "ScaleSets:0:Scaling:Policy", "TotalFloor",
            "ScaleSets:0:Scaling:MinRunners", "1",
            "ScaleSets:0:Scaling:MaxRunners", "4",
            "ScaleSets:0:Lifecycle:StartupTimeout", "00:12:00",
            "ScaleSets:0:RunnerProfile:Image", "ghcr.io/example/runner:2.4.1",
            "ScaleSets:0:RunnerProfile:Architecture", "Arm64",
            "ScaleSets:0:RunnerProfile:Cpu", "2.5",
            "ScaleSets:0:RunnerProfile:Memory", "4096",
            "Credentials:GitHub-App:Pat", SecretSentinel));

        AutoscalerOptions options = AutoscalerConfiguration.Bind(configuration);

        Assert.Equal("/state", options.Controller.StateDirectory);
        Assert.Equal(TimeSpan.FromSeconds(45), options.Controller.ReconcileInterval);
        Assert.Equal(3, options.HostCapacity.CreationConcurrency);
        Assert.Single(options.ScaleSets);
        Assert.Equal(ScalingPolicy.TotalFloor, options.ScaleSets[0].Scaling.Policy);
        Assert.Equal(1, options.ScaleSets[0].Scaling.MinRunners);
        Assert.Equal(4, options.ScaleSets[0].Scaling.MaxRunners);
        Assert.Equal(TimeSpan.FromMinutes(12), options.ScaleSets[0].Lifecycle.StartupTimeout);
        Assert.Equal(RunnerArchitecture.Arm64, options.ScaleSets[0].RunnerProfile.Architecture);
        Assert.Equal(2.5m, options.ScaleSets[0].RunnerProfile.Cpu);
        Assert.True(options.Credentials.ContainsKey("GITHUB-APP"));
        Assert.Equal(SecretSentinel, options.Credentials["github-app"].Pat);
    }

    [Fact]
    public void Bind_RejectsMalformedScalarWithoutEchoingItsValue()
    {
        const string invalidValue = "private-value-should-not-appear";
        IConfiguration configuration = BuildConfiguration(ValidConfigurationValues(
            "HostCapacity:MaxTotalRunners", invalidValue));

        AutoscalerConfigurationException exception = Assert.Throws<AutoscalerConfigurationException>(
            () => AutoscalerConfiguration.Bind(configuration));

        Assert.Contains(exception.Errors, error => error.StartsWith("HostCapacity", StringComparison.Ordinal));
        Assert.DoesNotContain(invalidValue, exception.Message);
    }

    [Fact]
    public void Bind_RequiresContiguousNumericScaleSetIndices()
    {
        Dictionary<string, string?> values = ValidConfigurationValues();
        foreach (string key in values.Keys.Where(static key => key.StartsWith("ScaleSets:0:", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            values.Remove(key);
        }

        values["ScaleSets:1:Key"] = "build-linux";
        values["ScaleSets:1:GithubUrl"] = "https://github.com/example-org";
        values["ScaleSets:1:Name"] = "linux-runners";
        values["ScaleSets:1:CredentialsRef"] = "build";
        values["ScaleSets:1:RunnerProfile:Image"] = "ghcr.io/example/runner:2.4.1";
        values["ScaleSets:1:RunnerProfile:Architecture"] = "X64";
        values["ScaleSets:1:RunnerProfile:Cpu"] = "2";
        values["ScaleSets:1:RunnerProfile:Memory"] = "2048";
        IConfiguration configuration = BuildConfiguration(values);

        AutoscalerConfigurationException exception = Assert.Throws<AutoscalerConfigurationException>(
            () => AutoscalerConfiguration.Bind(configuration));

        Assert.Contains(exception.Errors, error => error.Contains("ScaleSets indices", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsCaseInsensitiveCredentialReferencesAndAZeroMaximumPause()
    {
        AutoscalerOptions options = ValidOptions();
        options.ScaleSets[0].CredentialsRef = "BUILD-CREDENTIAL";
        options.ScaleSets[0].Scaling.MinRunners = 0;
        options.ScaleSets[0].Scaling.MaxRunners = 0;

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Empty(errors);
        Assert.True(options.Credentials.ContainsKey("build-credential"));
    }

    [Fact]
    public void Validate_RejectsUnknownAndNonGithubComTargets()
    {
        string[] urls =
        [
            "https://ghe.example.com/acme",
            "http://github.com/acme",
            "https://github.com/acme/repo/actions",
            "https://user:password@github.com/acme",
            "https://github.com/acme?tab=repositories",
        ];

        foreach (string url in urls)
        {
            AutoscalerOptions options = ValidOptions();
            options.ScaleSets[0].GithubUrl = url;

            IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

            Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].GithubUrl", StringComparison.Ordinal));
            Assert.DoesNotContain(url, string.Join(";", errors));
        }
    }

    [Fact]
    public void Validate_RejectsDuplicateTargetGroupAndNameCaseInsensitively()
    {
        AutoscalerOptions options = ValidOptions();
        options.ScaleSets[0].GithubUrl = "https://github.com/acme/";
        options.ScaleSets[0].RunnerGroup = null;
        options.ScaleSets[0].Name = "linux-runners";
        ScaleSetOptions secondScaleSet = ValidScaleSet("other", "https://github.com/ACME", "LINUX-RUNNERS", "build-credential");
        secondScaleSet.RunnerGroup = "Default";
        options.ScaleSets.Add(secondScaleSet);

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("ScaleSets[1].Name", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AllowsMultipleScaleSetNamesForOneOrganization()
    {
        AutoscalerOptions options = ValidOptions();
        options.ScaleSets[0].GithubUrl = "https://github.com/acme";
        options.ScaleSets[0].Name = "linux-runners";
        options.ScaleSets.Add(ValidScaleSet("other", "https://github.com/acme/", "windows-runners", "build-credential"));

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_AllowsTheSameNameInDifferentRunnerGroups()
    {
        AutoscalerOptions options = ValidOptions();
        options.ScaleSets[0].GithubUrl = "https://github.com/acme";
        options.ScaleSets[0].Name = "build-runners";
        options.ScaleSets[0].RunnerGroup = "Linux";
        ScaleSetOptions secondScaleSet = ValidScaleSet("other", "https://github.com/ACME/", "BUILD-RUNNERS", "build-credential");
        secondScaleSet.RunnerGroup = "Windows";
        options.ScaleSets.Add(secondScaleSet);

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_AllowsOrganizationAndRepositoryTargetsWithDifferentIdentities()
    {
        AutoscalerOptions options = ValidOptions();
        options.ScaleSets[0].GithubUrl = "https://github.com/acme";
        options.ScaleSets[0].Name = "build-runners";
        options.ScaleSets.Add(ValidScaleSet("repo", "https://github.com/acme/repo", "build-runners", "build-credential"));

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RejectsDuplicateScaleSetKeysCaseInsensitively()
    {
        AutoscalerOptions options = ValidOptions();
        options.ScaleSets.Add(ValidScaleSet("BUILDS", "https://github.com/other-org", "other", "build-credential"));

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("ScaleSets[1].Key", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsEmptyAndDuplicateLabels()
    {
        AutoscalerOptions options = ValidOptions();
        options.ScaleSets[0].Labels = ["linux", "", "LINUX"];

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].Labels[1]", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].Labels[2]", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsMissingCredentialReference()
    {
        AutoscalerOptions options = ValidOptions();
        options.ScaleSets[0].CredentialsRef = "missing";

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].CredentialsRef", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsCredentialKeysThatDifferOnlyByCase()
    {
        AutoscalerOptions options = ValidOptions();
        options.Credentials = new Dictionary<string, CredentialOptions>(StringComparer.Ordinal)
        {
            ["build-credential"] = new() { Pat = SecretSentinel },
            ["BUILD-CREDENTIAL"] = new() { Pat = "another-secret" },
        };

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("Credentials contains keys", StringComparison.Ordinal));
        Assert.DoesNotContain(SecretSentinel, string.Join(";", errors));
        Assert.DoesNotContain("another-secret", string.Join(";", errors));
    }

    [Fact]
    public void Validate_RejectsMixedAppAndPatWithoutEchoingEitherSecret()
    {
        AutoscalerOptions options = ValidOptions();
        options.Credentials["build-credential"] = new CredentialOptions
        {
            Pat = SecretSentinel,
            ClientId = "client-id",
            InstallationId = "123",
            PrivateKeyPem = "private-key-sentinel",
        };

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);
        string messages = string.Join(";", errors);

        Assert.Contains(errors, error => error.Contains("either PAT or GitHub App", StringComparison.Ordinal));
        Assert.DoesNotContain(SecretSentinel, messages);
        Assert.DoesNotContain("private-key-sentinel", messages);
    }

    [Fact]
    public void Validate_RequiresCompleteGitHubAppCredentialsAndPositiveInstallationId()
    {
        AutoscalerOptions options = ValidOptions();
        options.Credentials["build-credential"] = new CredentialOptions
        {
            ClientId = "client-id",
            InstallationId = "0",
        };

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("Credentials[build-credential].InstallationId", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("Credentials[build-credential].PrivateKeyPem", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsCompleteGitHubAppCredentials()
    {
        AutoscalerOptions options = ValidOptions();
        options.Credentials["build-credential"] = new CredentialOptions
        {
            ClientId = "client-id",
            InstallationId = "123456789",
            PrivateKeyPem = "synthetic-private-key",
        };

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RequiresOneCredentialMode()
    {
        AutoscalerOptions options = ValidOptions();
        options.Credentials["build-credential"] = new CredentialOptions();

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("Credentials[build-credential] must define PAT", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsNonDockerProviderUnpinnedImagesAndInvalidResources()
    {
        AutoscalerOptions options = ValidOptions();
        options.ScaleSets[0].Provider = "Ecs";
        options.ScaleSets[0].RunnerProfile.Image = "ghcr.io/example/runner:latest";
        options.ScaleSets[0].RunnerProfile.Cpu = 0;
        options.ScaleSets[0].RunnerProfile.Memory = 0;
        options.ScaleSets[0].RunnerProfile.Architecture = null;

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].Provider", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].RunnerProfile.Image", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].RunnerProfile.Cpu", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].RunnerProfile.Memory", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].RunnerProfile.Architecture", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RequiresExplicitPrivilegeForDockerInDocker()
    {
        AutoscalerOptions options = ValidOptions();
        options.ScaleSets[0].RunnerProfile.DockerBuildMode = DockerBuildMode.DockerInDocker;

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].RunnerProfile.Privileged", StringComparison.Ordinal));

        options.ScaleSets[0].RunnerProfile.Privileged = true;
        Assert.Empty(AutoscalerConfiguration.Validate(options));
    }

    [Fact]
    public void Validate_RejectsInvalidCountRangesAndHostCapacityBudgets()
    {
        AutoscalerOptions options = ValidOptions();
        options.ScaleSets[0].Scaling.MinRunners = 2;
        options.ScaleSets[0].Scaling.MaxRunners = 1;
        options.ScaleSets[0].RunnerProfile.Cpu = 2;
        options.ScaleSets[0].RunnerProfile.Memory = 4096;
        options.HostCapacity.MaxTotalRunners = 1;
        options.HostCapacity.CpuBudget = 1;
        options.HostCapacity.MemoryBudget = 2048;
        options.HostCapacity.CreationConcurrency = 0;

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].Scaling.MinRunners", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("HostCapacity.CreationConcurrency", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("below the sum of configured scale-set minimums", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("CPU demand of configured scale-set minimums", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("memory demand of configured scale-set minimums", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].RunnerProfile.Cpu exceeds", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].RunnerProfile.Memory exceeds", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsNegativeScaleSetLimitsAndUnknownPolicy()
    {
        AutoscalerOptions options = ValidOptions();
        options.ScaleSets[0].Scaling.MinRunners = -1;
        options.ScaleSets[0].Scaling.MaxRunners = -2;
        options.ScaleSets[0].Scaling.Policy = (ScalingPolicy)99;

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].Scaling.Policy", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].Scaling.MinRunners", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].Scaling.MaxRunners", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsNegativeHostMaximumAndNonPositiveConfiguredBudgets()
    {
        AutoscalerOptions options = ValidOptions();
        options.HostCapacity.MaxTotalRunners = -1;
        options.HostCapacity.CpuBudget = 0;
        options.HostCapacity.MemoryBudget = -1;

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("HostCapacity.MaxTotalRunners", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("HostCapacity.CpuBudget", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("HostCapacity.MemoryBudget", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsDigestPinnedDockerImage()
    {
        AutoscalerOptions options = ValidOptions();
        options.ScaleSets[0].RunnerProfile.Image = $"ghcr.io/example/runner@sha256:{new string('a', 64)}";

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RejectsInvalidTimersAndNegativeIdleGrace()
    {
        AutoscalerOptions options = ValidOptions();
        options.Controller.ReconcileInterval = TimeSpan.Zero;
        options.Controller.ControlOperationTimeout = TimeSpan.FromTicks(-1);
        options.ScaleSets[0].Scaling.IdleTimeout = TimeSpan.FromTicks(-1);
        options.ScaleSets[0].Lifecycle.StartupTimeout = TimeSpan.Zero;
        options.ScaleSets[0].Lifecycle.ShutdownDrainTimeout = TimeSpan.FromTicks(-1);
        options.ScaleSets[0].Lifecycle.CleanupTimeout = TimeSpan.Zero;

        IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options);

        Assert.Contains(errors, error => error.StartsWith("Controller.ReconcileInterval", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("Controller.ControlOperationTimeout", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].Scaling.IdleTimeout", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].Lifecycle.StartupTimeout", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].Lifecycle.ShutdownDrainTimeout", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("ScaleSets[0].Lifecycle.CleanupTimeout", StringComparison.Ordinal));
    }

    private static AutoscalerOptions ValidOptions()
    {
        return new AutoscalerOptions
        {
            Controller = new ControllerOptions { StateDirectory = "/state" },
            ScaleSets = [ValidScaleSet("builds", "https://github.com/example-org/repo", "linux-runners", "build-credential")],
            Credentials = new Dictionary<string, CredentialOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["build-credential"] = new() { Pat = SecretSentinel },
            },
        };
    }

    private static ScaleSetOptions ValidScaleSet(string key, string githubUrl, string name, string credentialsRef)
    {
        return new ScaleSetOptions
        {
            Key = key,
            GithubUrl = githubUrl,
            Name = name,
            CredentialsRef = credentialsRef,
            RunnerProfile = new RunnerProfileOptions
            {
                Image = "ghcr.io/example/runner:2.4.1",
                Architecture = RunnerArchitecture.X64,
                Cpu = 2,
                Memory = 4096,
            },
        };
    }

    private static Dictionary<string, string?> ValidConfigurationValues(params string[] overrides)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Controller:StateDirectory"] = "/state",
            ["ScaleSets:0:Key"] = "builds",
            ["ScaleSets:0:GithubUrl"] = "https://github.com/example-org/repo",
            ["ScaleSets:0:Name"] = "linux-runners",
            ["ScaleSets:0:CredentialsRef"] = "build",
            ["ScaleSets:0:RunnerProfile:Image"] = "ghcr.io/example/runner:2.4.1",
            ["ScaleSets:0:RunnerProfile:Architecture"] = "X64",
            ["ScaleSets:0:RunnerProfile:Cpu"] = "2",
            ["ScaleSets:0:RunnerProfile:Memory"] = "4096",
            ["Credentials:Build:Pat"] = SecretSentinel,
        };

        for (int index = 0; index < overrides.Length; index += 2)
        {
            values[overrides[index]] = overrides[index + 1];
        }

        return values;
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
