using System.Collections.Immutable;
using System.Text.Json.Serialization;
using ScaleSet.Hosting.Configuration;

namespace ScaleSet.Hosting.Providers;

/// <summary>Identifies the controller and GitHub scale set that own runner resources.</summary>
public sealed record RunnerOwnershipScope
{
    public RunnerOwnershipScope(string controllerId, string scaleSetKey, long scaleSetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scaleSetKey);

        if (scaleSetId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scaleSetId), scaleSetId, "The scale-set ID must be positive.");
        }

        ControllerId = controllerId;
        ScaleSetKey = scaleSetKey;
        ScaleSetId = scaleSetId;
    }

    public string ControllerId { get; }

    public string ScaleSetKey { get; }

    public long ScaleSetId { get; }
}

/// <summary>Stable identity assigned to one runner operation.</summary>
public sealed record RunnerIdentity
{
    public RunnerIdentity(
        RunnerOwnershipScope scope,
        Guid operationId,
        string runnerName,
        long? gitHubRunnerId = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerName);

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("The operation ID must not be empty.", nameof(operationId));
        }

        if (gitHubRunnerId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gitHubRunnerId), gitHubRunnerId, "The GitHub runner ID must be positive when present.");
        }

        Scope = scope;
        OperationId = operationId;
        RunnerName = runnerName;
        GitHubRunnerId = gitHubRunnerId;
    }

    public RunnerOwnershipScope Scope { get; }

    public Guid OperationId { get; }

    public string RunnerName { get; }

    public long? GitHubRunnerId { get; }
}

/// <summary>Provider-observed lifecycle evidence for a runner resource.</summary>
public enum RunnerResourceStatus
{
    Created = 0,
    Running = 1,
    Exited = 2,
    Dead = 3,
    Missing = 4,
    Unknown = 5,
}

/// <summary>A provider resource together with its stable owner and lifecycle evidence.</summary>
public sealed record RunnerResource
{
    public RunnerResource(
        string providerResourceId,
        RunnerIdentity identity,
        RunnerResourceStatus status,
        int? exitCode = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? finishedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerResourceId);
        ArgumentNullException.ThrowIfNull(identity);

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "The provider status is not defined.");
        }

        if (startedAt is not null && finishedAt is not null && startedAt.Value > finishedAt.Value)
        {
            throw new ArgumentException("The finish time must not precede the start time.", nameof(finishedAt));
        }

        ProviderResourceId = providerResourceId;
        Identity = identity;
        Status = status;
        ExitCode = exitCode;
        StartedAt = startedAt;
        FinishedAt = finishedAt;
    }

    public string ProviderResourceId { get; }

    public RunnerIdentity Identity { get; }

    public RunnerResourceStatus Status { get; }

    public int? ExitCode { get; }

    public DateTimeOffset? StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; }
}

/// <summary>Creates one runner resource using short-lived JIT configuration.</summary>
public sealed record RunnerCreateRequest
{
    public RunnerCreateRequest(RunnerIdentity identity, string encodedJitConfig)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedJitConfig);

        Identity = identity;
        EncodedJitConfig = encodedJitConfig;
    }

    public RunnerIdentity Identity { get; }

    [JsonIgnore]
    public string EncodedJitConfig { get; }

    public override string ToString() =>
        $"{nameof(RunnerCreateRequest)} {{ Identity = {Identity}, EncodedJitConfig = <redacted> }}";
}

/// <summary>Describes the provider evidence and runner architectures it supports.</summary>
public sealed record RunnerProviderCapabilities
{
    public RunnerProviderCapabilities(
        string name,
        ImmutableArray<RunnerArchitecture> supportedArchitectures,
        bool supportsOwnershipScopedDiscovery,
        bool providesTerminalStateEvidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (supportedArchitectures.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one runner architecture must be supported.", nameof(supportedArchitectures));
        }

        if (supportedArchitectures.Any(static architecture => !Enum.IsDefined(architecture)))
        {
            throw new ArgumentException("A supported architecture is not defined.", nameof(supportedArchitectures));
        }

        if (supportedArchitectures.Distinct().Count() != supportedArchitectures.Length)
        {
            throw new ArgumentException("Supported architectures must be unique.", nameof(supportedArchitectures));
        }

        Name = name;
        SupportedArchitectures = supportedArchitectures;
        SupportsOwnershipScopedDiscovery = supportsOwnershipScopedDiscovery;
        ProvidesTerminalStateEvidence = providesTerminalStateEvidence;
    }

    public string Name { get; }

    public ImmutableArray<RunnerArchitecture> SupportedArchitectures { get; }

    public bool SupportsOwnershipScopedDiscovery { get; }

    public bool ProvidesTerminalStateEvidence { get; }
}

/// <summary>Manages runner resources for one immutable provider profile.</summary>
public interface IRunnerProvider
{
    RunnerProviderCapabilities Capabilities { get; }

    Task<ImmutableArray<RunnerResource>> DiscoverAsync(
        RunnerOwnershipScope scope,
        CancellationToken cancellationToken = default);

    Task<RunnerResource> CreateAsync(
        RunnerCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<RunnerResource> StartAsync(
        RunnerResource resource,
        CancellationToken cancellationToken = default);

    Task<RunnerResource?> InspectAsync(
        RunnerResource resource,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        RunnerResource resource,
        bool force,
        CancellationToken cancellationToken = default);
}
