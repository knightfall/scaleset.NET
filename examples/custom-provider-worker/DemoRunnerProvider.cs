using System.Collections.Immutable;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;

namespace ScaleSet.CustomProvider.Example;

internal sealed class DemoRunnerProviderFactory(DemoRunnerBackend backend) : IRunnerProviderFactory
{
    public string Name => "Demo";

    public IReadOnlyList<string> Validate(ScaleSetOptions scaleSet, string configurationPath) => [];

    public IRunnerProvider Create(ScaleSetOptions scaleSet) => new DemoRunnerProvider(backend);
}

/// <summary>Shares runner resources across the two hosts to emulate an external backend.</summary>
internal sealed class DemoRunnerBackend
{
    private RunnerResource? _current;
    private int _runnersCreated;
    private int _providersDisposed;

    public RunnerResource? Current => Volatile.Read(ref _current);

    public int RunnersCreated => Volatile.Read(ref _runnersCreated);

    public int ProvidersDisposed => Volatile.Read(ref _providersDisposed);

    public RunnerResource Create(RunnerIdentity identity)
    {
        RunnerResource resource = new(identity.OperationId.ToString("N"), identity, RunnerResourceStatus.Created);
        if (Interlocked.CompareExchange(ref _current, resource, null) is not null)
        {
            throw new InvalidOperationException("The demo backend already has a runner.");
        }

        Interlocked.Increment(ref _runnersCreated);
        return resource;
    }

    public RunnerResource Start(RunnerResource resource)
    {
        RunnerResource running = new(resource.ProviderResourceId, resource.Identity, RunnerResourceStatus.Running);
        Transition(resource, running);
        return running;
    }

    public void CompleteRunner()
    {
        RunnerResource runner = Current ?? throw new InvalidOperationException("No runner to complete.");
        Transition(runner, new RunnerResource(runner.ProviderResourceId, runner.Identity, RunnerResourceStatus.Exited, exitCode: 0));
    }

    public void Remove(RunnerResource resource)
    {
        if (resource.Status != RunnerResourceStatus.Exited)
        {
            throw new InvalidOperationException("Only a completed demo runner may be removed.");
        }

        Transition(resource, replacement: null);
    }

    public void RecordProviderDisposal() => Interlocked.Increment(ref _providersDisposed);

    private void Transition(RunnerResource resource, RunnerResource? replacement)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _current, replacement, resource), resource))
        {
            throw new InvalidOperationException("The backend runner changed before the operation.");
        }
    }
}

internal sealed class DemoRunnerProvider(DemoRunnerBackend backend) : IRunnerProvider, IAsyncDisposable
{
    public RunnerProviderCapabilities Capabilities { get; } = new(
        "Demo", [RunnerArchitecture.X64], supportsOwnershipScopedDiscovery: true, providesTerminalStateEvidence: true);

    public Task<ImmutableArray<RunnerResource>> DiscoverAsync(RunnerOwnershipScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ImmutableArray<RunnerResource>>(backend.Current is { } resource && resource.Identity.Scope == scope
            ? [resource]
            : []);
    }

    public Task<RunnerResource> CreateAsync(RunnerCreateRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(backend.Create(request.Identity));
    }

    public Task<RunnerResource> StartAsync(RunnerResource resource, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(backend.Start(resource));
    }

    public Task<RunnerResource?> InspectAsync(RunnerResource resource, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(backend.Current is { } current
            && current.ProviderResourceId == resource.ProviderResourceId
            && current.Identity == resource.Identity
            ? current
            : null);
    }

    public Task RemoveAsync(RunnerResource resource, bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (force)
        {
            throw new InvalidOperationException("The demo backend refuses forced removal.");
        }

        backend.Remove(resource);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        backend.RecordProviderDisposal();
        return ValueTask.CompletedTask;
    }
}
