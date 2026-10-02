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
        using ScaleSetAutoscalerService service = new(
            options,
            coordinator,
            factory,
            NullLogger<ScaleSetAutoscalerService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await factory.AllRuntimesCreated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        FakeRuntime[] runtimes = factory.Runtimes.ToArray();
        await Task.WhenAll(runtimes.Select(runtime =>
            runtime.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));

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

        Assert.All(runtimes, static runtime =>
        {
            Assert.Equal(1, runtime.StopCount);
            Assert.Equal(1, runtime.DisposeCount);
        });
        Assert.DoesNotContain(events, static item => item.StartsWith("remove:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, static item => item.StartsWith("delete-scale-set:", StringComparison.Ordinal));
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

        public string ScaleSetKey { get; } = scaleSetKey;

        public TaskCompletionSource RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount => Volatile.Read(ref _stopCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeline.Enqueue($"initialize:{ScaleSetKey}");
            return Task.CompletedTask;
        }

        public Task RunAsync(CancellationToken cancellationToken)
        {
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

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            timeline.Enqueue($"dispose:{ScaleSetKey}");
            return ValueTask.CompletedTask;
        }
    }
}
