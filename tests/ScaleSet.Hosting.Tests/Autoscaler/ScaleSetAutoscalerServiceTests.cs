using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Autoscaler.Runtime;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;

namespace ScaleSet.Hosting.Tests.Autoscaler;

public sealed class ScaleSetAutoscalerServiceTests
{
    [Fact]
    public async Task StartInitializesAllSetInventoriesBeforeAnyListenerRuns_AndStopPreservesRuntimes()
    {
        string[] keys = ["linux-x64", "linux-arm64"];
        ConcurrentQueue<string> timeline = new();
        AutoscalerOptions options = CreateOptions(keys);
        HostCapacityCoordinator coordinator = new(options.HostCapacity, options.ScaleSets);
        FakeRuntimeFactory factory = new(keys, timeline);
        AutoscalerHealthState healthState = new();
        using ScaleSetAutoscalerService service = new(
            options,
            coordinator,
            factory,
            NullLogger<ScaleSetAutoscalerService>.Instance,
            healthState);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await factory.AllRuntimesCreated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        FakeRuntime[] runtimes = factory.Runtimes.ToArray();
        await Task.WhenAll(runtimes.Select(runtime =>
            runtime.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));
        Assert.True(healthState.IsReady);
        Assert.True(healthState.IsLive);

        string[] events = timeline.ToArray();
        int firstRun = Array.FindIndex(events, static item => item.StartsWith("run:", StringComparison.Ordinal));
        Assert.True(firstRun >= 0);
        Assert.Equal(keys.Length, events.Count(static item => item.StartsWith("create:", StringComparison.Ordinal)));
        Assert.Equal(keys.Length, events.Count(static item => item.StartsWith("initialize:", StringComparison.Ordinal)));
        Assert.All(
            events.Select((item, index) => (item, index))
                .Where(static entry => entry.item.StartsWith("initialize:", StringComparison.Ordinal)),
            entry => Assert.True(entry.index < firstRun, $"{entry.item} occurred after a listener started."));

        await service.StopAsync(CancellationToken.None);
        Assert.False(healthState.IsReady);
        Assert.False(healthState.IsLive);

        Assert.All(runtimes, static runtime =>
        {
            Assert.Equal(1, runtime.DrainCount);
            Assert.Equal(1, runtime.StopCount);
            Assert.Equal(1, runtime.DisposeCount);
        });
        Assert.DoesNotContain(events, static item => item.StartsWith("remove:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, static item => item.StartsWith("delete-scale-set:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopAsync_DrainsBeforeCancelingWorkersAndThenStopsRuntimes()
    {
        string[] keys = ["linux-x64"];
        ConcurrentQueue<string> timeline = new();
        AutoscalerOptions options = CreateOptions(keys);
        HostCapacityCoordinator coordinator = new(options.HostCapacity, options.ScaleSets);
        FakeRuntimeFactory factory = new(keys, timeline);
        using ScaleSetAutoscalerService service = new(
            options,
            coordinator,
            factory,
            NullLogger<ScaleSetAutoscalerService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await factory.AllRuntimesCreated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        FakeRuntime runtime = Assert.Single(factory.Runtimes);
        await runtime.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        runtime.PauseDrain = true;

        Task stopping = service.StopAsync(CancellationToken.None);
        await runtime.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(runtime.WorkerToken.IsCancellationRequested);
        Assert.Equal(0, runtime.StopCount);

        runtime.ReleaseDrain();
        await stopping;

        Assert.True(runtime.WorkerToken.IsCancellationRequested);
        Assert.Equal(1, runtime.DrainCount);
        Assert.Equal(1, runtime.StopCount);
        string[] events = timeline.ToArray();
        Assert.True(Array.FindIndex(events, static item => item.StartsWith("drain:", StringComparison.Ordinal))
            < Array.FindIndex(events, static item => item.StartsWith("stop:", StringComparison.Ordinal)));
    }

    private static AutoscalerOptions CreateOptions(IReadOnlyList<string> keys) => new()
    {
        HostCapacity = new HostCapacityOptions { CreationConcurrency = 2 },
        ScaleSets = keys.Select(static key => new ScaleSetOptions
        {
            Key = key,
            Name = key,
            GithubUrl = "https://github.com/example/repo",
            CredentialsRef = "github",
            Scaling = new ScalingOptions { MinRunners = 0, MaxRunners = 10 },
            RunnerProfile = new RunnerProfileOptions
            {
                Image = "example/runner:1",
                Architecture = RunnerArchitecture.X64,
                Cpu = 1,
                Memory = 1024,
            },
        }).ToList(),
    };

    private sealed class FakeRuntimeFactory(IReadOnlyList<string> keys, ConcurrentQueue<string> timeline) : IScaleSetRuntimeFactory
    {
        public List<FakeRuntime> Runtimes { get; } = [];

        public TaskCompletionSource AllRuntimesCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IScaleSetRuntime> CreateAsync(
            ScaleSetOptions scaleSet,
            HostCapacityCoordinator capacityCoordinator,
            CancellationToken cancellationToken)
        {
            Assert.Contains(scaleSet.Key, keys);
            Assert.NotNull(capacityCoordinator);
            FakeRuntime runtime = new(scaleSet.Key!, timeline);
            Runtimes.Add(runtime);
            timeline.Enqueue($"create:{runtime.ScaleSetKey}");
            if (Runtimes.Count == keys.Count)
            {
                AllRuntimesCreated.TrySetResult();
            }

            return Task.FromResult<IScaleSetRuntime>(runtime);
        }
    }

    private sealed class FakeRuntime(string scaleSetKey, ConcurrentQueue<string> timeline) : IScaleSetRuntime
    {
        private int _stopCount;
        private int _disposeCount;
        private int _drainCount;
        private readonly TaskCompletionSource _drainRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ScaleSetKey { get; } = scaleSetKey;

        public TaskCompletionSource RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DrainStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool PauseDrain { get; set; }

        public CancellationToken WorkerToken { get; private set; }

        public int StopCount => Volatile.Read(ref _stopCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int DrainCount => Volatile.Read(ref _drainCount);

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeline.Enqueue($"initialize:{ScaleSetKey}");
            return Task.CompletedTask;
        }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            WorkerToken = cancellationToken;
            timeline.Enqueue($"run:{ScaleSetKey}");
            RunStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _stopCount);
            timeline.Enqueue($"stop:{ScaleSetKey}");
            return Task.CompletedTask;
        }

        public async Task DrainAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _drainCount);
            timeline.Enqueue($"drain:{ScaleSetKey}");
            DrainStarted.TrySetResult();
            if (PauseDrain)
            {
                await _drainRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void ReleaseDrain() => _drainRelease.TrySetResult();

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            timeline.Enqueue($"dispose:{ScaleSetKey}");
            return ValueTask.CompletedTask;
        }
    }
}
