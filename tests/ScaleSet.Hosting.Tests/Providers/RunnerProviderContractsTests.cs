using System.Collections.Immutable;
using System.Text.Json;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;

namespace ScaleSet.Hosting.Tests.Providers;

public sealed class RunnerProviderContractsTests
{
    [Fact]
    public void OwnershipScope_RequiresStableNonEmptyIdentityAndPositiveScaleSetId()
    {
        Assert.Throws<ArgumentException>(() => new RunnerOwnershipScope(" ", "production", 17));
        Assert.Throws<ArgumentException>(() => new RunnerOwnershipScope("controller-a", " ", 17));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunnerOwnershipScope("controller-a", "production", 0));
    }

    [Fact]
    public void RunnerIdentity_RequiresOperationAndNameAndAcceptsOptionalGitHubId()
    {
        RunnerOwnershipScope scope = CreateScope();

        Assert.Throws<ArgumentException>(() => new RunnerIdentity(scope, Guid.Empty, "runner-a"));
        Assert.Throws<ArgumentException>(() => new RunnerIdentity(scope, Guid.NewGuid(), " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunnerIdentity(scope, Guid.NewGuid(), "runner-a", 0));
        Assert.Null(new RunnerIdentity(scope, Guid.NewGuid(), "runner-a").GitHubRunnerId);
    }

    [Fact]
    public void RunnerIdentity_RejectsMissingScope()
    {
        Assert.Throws<ArgumentNullException>(() => new RunnerIdentity(null!, Guid.NewGuid(), "runner-a"));
    }

    [Fact]
    public void RunnerResource_RejectsUndefinedStatusAndInvertedTimes()
    {
        RunnerIdentity identity = CreateIdentity();

        Assert.Throws<ArgumentOutOfRangeException>(() => new RunnerResource("resource-a", identity, (RunnerResourceStatus)99));
        Assert.Throws<ArgumentException>(() => new RunnerResource(" ", identity, RunnerResourceStatus.Created));
        Assert.Throws<ArgumentNullException>(() => new RunnerResource("resource-a", null!, RunnerResourceStatus.Created));
        Assert.Throws<ArgumentException>(() => new RunnerResource(
            "resource-a",
            identity,
            RunnerResourceStatus.Exited,
            startedAt: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            finishedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void ProviderCapabilities_RequireUniqueSupportedArchitectures()
    {
        Assert.Throws<ArgumentException>(() => new RunnerProviderCapabilities(
            "docker",
            ImmutableArray<RunnerArchitecture>.Empty,
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: true));
        Assert.Throws<ArgumentException>(() => new RunnerProviderCapabilities(
            "docker",
            [RunnerArchitecture.X64, RunnerArchitecture.X64],
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: true));
        Assert.Throws<ArgumentException>(() => new RunnerProviderCapabilities(
            "docker",
            [(RunnerArchitecture)99],
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: true));

        RunnerProviderCapabilities capabilities = new(
            "docker",
            [RunnerArchitecture.X64, RunnerArchitecture.Arm64],
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: false);

        Assert.Collection(
            capabilities.SupportedArchitectures,
            architecture => Assert.Equal(RunnerArchitecture.X64, architecture),
            architecture => Assert.Equal(RunnerArchitecture.Arm64, architecture));
        Assert.True(capabilities.SupportsOwnershipScopedDiscovery);
        Assert.False(capabilities.ProvidesTerminalStateEvidence);
    }

    [Fact]
    public void RunnerCreateRequest_RedactsJitConfigurationFromDiagnosticsAndJson()
    {
        const string secret = "encoded-jit-secret-for-test";
        RunnerCreateRequest request = new(CreateIdentity(), secret);

        Assert.DoesNotContain(secret, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(request), StringComparison.Ordinal);
        Assert.Contains("<redacted>", request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunnerCreateRequest_RejectsMissingJitConfiguration()
    {
        Assert.Throws<ArgumentException>(() => new RunnerCreateRequest(CreateIdentity(), " "));
    }

    private static RunnerOwnershipScope CreateScope() => new("controller-a", "production", 17);

    private static RunnerIdentity CreateIdentity() =>
        new(CreateScope(), Guid.NewGuid(), "runner-7");
}
