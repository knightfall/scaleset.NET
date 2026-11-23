using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Autoscaler.Runtime;
using ScaleSet.Client;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.Scaling;
using ScaleSet.Hosting.State;
using ScaleSet.Hosting.Telemetry;

namespace ScaleSet.Hosting.Tests.Autoscaler;

public sealed class ScaleSetRuntimeFactoryTests
{
    private const string CredentialSentinel = "synthetic-PAT-secret-6419";

    [Fact]
    public async Task InitializeAsync_CleanBootstrapCreatesOnceAndPersistsFinalNonSecretIdentity()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        string setStateDirectory = GetSetStateDirectory(directory.Path, scaleSet.Key!);
        FakeScaleSetRuntimeClient client = new()
        {
            OnCreate = async (request, _) =>
            {
                Assert.True(request.RunnerSetting.DisableUpdate);
                string pendingJson = await File.ReadAllTextAsync(Path.Combine(setStateDirectory, "scale-set-identity.json"), TestContext.Current.CancellationToken);
                using JsonDocument pending = JsonDocument.Parse(pendingJson);
                Assert.True(pending.RootElement.GetProperty("CreationInProgress").GetBoolean());
                Assert.True(pending.RootElement.GetProperty("ScaleSetId").ValueKind == JsonValueKind.Null);
                Assert.Contains("org/repo", pending.RootElement.GetProperty("Target").GetString(), StringComparison.OrdinalIgnoreCase);
                return new RunnerScaleSet
                {
                    Id = 987,
                    Name = request.Name,
                    RunnerGroupId = request.RunnerGroupId,
                    RunnerGroupName = request.RunnerGroupName,
                    Labels = request.Labels,
                };
            },
        };
        FakeRunnerProvider provider = new();
        ScaleSetRuntimeFactory factory = CreateFactory(options, client, provider);

        IScaleSetRuntime runtime = await factory.CreateAsync(
            scaleSet,
            new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets),
            TestContext.Current.CancellationToken);
        try
        {
            await runtime.InitializeAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await runtime.DisposeAsync();
        }

        string identityJson = await File.ReadAllTextAsync(Path.Combine(setStateDirectory, "scale-set-identity.json"), TestContext.Current.CancellationToken);
        using JsonDocument identity = JsonDocument.Parse(identityJson);
        Assert.Equal(1, client.CreateCount);
        Assert.Equal(0, client.SessionCreateCount);
        Assert.Equal(987L, identity.RootElement.GetProperty("ScaleSetId").GetInt64());
        Assert.False(identity.RootElement.GetProperty("CreationInProgress").GetBoolean());
        Assert.Equal("https://github.com/org/repo", identity.RootElement.GetProperty("Target").GetString());
        Assert.Equal("Default", identity.RootElement.GetProperty("RunnerGroupName").GetString());
        Assert.Equal(scaleSet.Name, identity.RootElement.GetProperty("ScaleSetName").GetString());
        Assert.DoesNotContain(CredentialSentinel, identityJson, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(setStateDirectory, "runner-state.json")));
    }

    [Fact]
    public async Task InitializeAsync_RejectedFirstCreateSurfacesApiErrorAndCanRetryOnRestart()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ScaleSetApiException rejection = new("Scale-set creation denied", statusCode: 403);
        FakeScaleSetRuntimeClient deniedClient = new() { OnCreate = (_, _) => throw rejection };
        ScaleSetRuntimeFactory firstFactory = CreateFactory(options, deniedClient, new FakeRunnerProvider());
        await using (IScaleSetRuntime runtime = await firstFactory.CreateAsync(
            scaleSet, new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets), cancellationToken))
        {
            Assert.Same(rejection, await Assert.ThrowsAsync<ScaleSetApiException>(() => runtime.InitializeAsync(cancellationToken)));
        }

        string statePath = GetSetStateDirectory(directory.Path, scaleSet.Key!);
        Assert.False(File.Exists(Path.Combine(statePath, "scale-set-identity.json")));

        FakeScaleSetRuntimeClient permittedClient = new();
        ScaleSetRuntimeFactory restartFactory = CreateFactory(options, permittedClient, new FakeRunnerProvider());
        await using IScaleSetRuntime restarted = await restartFactory.CreateAsync(
            scaleSet, new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets), cancellationToken);
        await restarted.InitializeAsync(cancellationToken);

        Assert.Equal(1, deniedClient.CreateCount);
        Assert.Equal(1, permittedClient.CreateCount);
        Assert.True(File.Exists(Path.Combine(statePath, "runner-state.json")));
    }

    [Fact]
    public async Task InitializeAsync_PendingIdentityWithoutRemoteScaleSetFailsClosedWithoutSecondCreate()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        string setStateDirectory = GetSetStateDirectory(directory.Path, scaleSet.Key!);
        FakeScaleSetRuntimeClient firstClient = new()
        {
            OnCreate = async (_, _) =>
            {
                string pendingJson = await File.ReadAllTextAsync(Path.Combine(setStateDirectory, "scale-set-identity.json"), TestContext.Current.CancellationToken);
                using JsonDocument pending = JsonDocument.Parse(pendingJson);
                Assert.True(pending.RootElement.GetProperty("CreationInProgress").GetBoolean());
                throw new HttpRequestException("synthetic lost create response");
            },
        };
        FakeRunnerProvider firstProvider = new();
        ScaleSetRuntimeFactory firstFactory = CreateFactory(options, firstClient, firstProvider);
        IScaleSetRuntime firstRuntime = await firstFactory.CreateAsync(
            scaleSet,
            new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets),
            TestContext.Current.CancellationToken);

        try
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => firstRuntime.InitializeAsync(TestContext.Current.CancellationToken));
            Assert.IsType<HttpRequestException>(failure.InnerException);
        }
        finally
        {
            await firstRuntime.DisposeAsync();
        }

        string pendingIdentityJson = await File.ReadAllTextAsync(
            Path.Combine(setStateDirectory, "scale-set-identity.json"),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(CredentialSentinel, pendingIdentityJson, StringComparison.Ordinal);
        FakeScaleSetRuntimeClient restartClient = new();
        FakeRunnerProvider restartProvider = new();
        ScaleSetRuntimeFactory restartFactory = CreateFactory(options, restartClient, restartProvider);
        IScaleSetRuntime restartRuntime = await restartFactory.CreateAsync(
            scaleSet,
            new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets),
            TestContext.Current.CancellationToken);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => restartRuntime.InitializeAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await restartRuntime.DisposeAsync();
        }

        Assert.Equal(1, firstClient.CreateCount);
        Assert.Equal(0, restartClient.CreateCount);
        Assert.Equal(0, restartClient.SessionCreateCount);
    }

    [Fact]
    public async Task InitializeAsync_AccountsDiscoveredRunnerUsingObservedResourcesAfterProfileChange()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        RunnerOwnershipScope scope = new(options.Controller.Identity, scaleSet.Key!, 987);
        RunnerIdentity identity = new(
            scope,
            Guid.NewGuid(),
            "runner-from-old-profile",
            915,
            new CapacityResourceQuantities(1m, 1024));
        FakeRunnerProvider provider = new()
        {
            Discovered =
            [
                new RunnerResource(
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    identity,
                    RunnerResourceStatus.Running,
                    capacityResources: new CapacityResourceQuantities(2m, 2048)),
            ],
        };
        FakeScaleSetRuntimeClient client = new();
        ScaleSetRuntimeFactory factory = CreateFactory(options, client, provider);
        HostCapacityCoordinator coordinator = new(options.HostCapacity, options.ScaleSets);
        IScaleSetRuntime runtime = await factory.CreateAsync(scaleSet, coordinator, TestContext.Current.CancellationToken);

        try
        {
            await runtime.InitializeAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await runtime.DisposeAsync();
        }

        CapacityUsageSnapshot usage = coordinator.CaptureSnapshot().ScaleSets[scaleSet.Key!].Usage;
        Assert.Equal(1, usage.RunnerCount);
        Assert.Equal(2m, usage.CpuCoresUsed);
        Assert.Equal(2048, usage.MemoryMiBUsed);
        Assert.Equal(1, usage.StateCounts[CapacityOccupancyState.Unknown]);

        string setStateDirectory = GetSetStateDirectory(directory.Path, scaleSet.Key!);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(
            setStateDirectory,
            scope,
            TestContext.Current.CancellationToken);
        RunnerOperationState persisted = Assert.Single(await journal.ReadAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new CapacityResourceQuantities(1m, 1024), persisted.Identity.CapacityResources);
    }

    [Fact]
    public async Task DisposeAsync_StopsActivePollingBeforeClosureAndDisposesAllResourcesAfterFailures()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource polling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource pollStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InvalidOperationException closeFailure = new("synthetic session close failure");
        InvalidOperationException providerFailure = new("synthetic provider disposal failure");
        FakeRuntimeSession session = new()
        {
            Poll = async cancellationToken =>
            {
                polling.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return null;
                }
                finally
                {
                    pollStopped.SetResult();
                }
            },
            Close = _ =>
            {
                Assert.True(pollStopped.Task.IsCompletedSuccessfully);
                throw closeFailure;
            },
        };
        FakeScaleSetRuntimeClient client = new() { RuntimeSession = session };
        FakeRunnerProvider provider = new()
        {
            OnDispose = () =>
            {
                Assert.Equal(1, session.CloseCount);
                throw providerFailure;
            },
        };
        IScaleSetRuntime runtime = await CreateFactory(options, client, provider).CreateAsync(
            scaleSet, new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets), token);
        await runtime.InitializeAsync(token);
        Task listener = runtime.RunAsync(token);
        await polling.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        Task disposal = runtime.DisposeAsync().AsTask();
        Assert.Same(disposal, runtime.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener);
        AggregateException failures = await Assert.ThrowsAsync<AggregateException>(() => disposal);
        Assert.Equal([closeFailure, providerFailure], failures.InnerExceptions);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, provider.DisposeCount);
        Assert.Equal(1, session.CloseCount);

        RunnerOwnershipScope scope = new(options.Controller.Identity, scaleSet.Key!, 987);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(
            GetSetStateDirectory(directory.Path, scaleSet.Key!), scope, token);
        Assert.Empty(await journal.ReadAllAsync(token));
    }

    [Fact]
    public async Task DisposeAsync_CancelsPartialInitializationBeforeDisposingItsDependencies()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource discovering = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource discoveryStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRunnerProvider provider = new()
        {
            OnDiscover = async cancellationToken =>
            {
                discovering.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return [];
                }
                finally
                {
                    discoveryStopped.SetResult();
                }
            },
            OnDispose = () => Assert.True(discoveryStopped.Task.IsCompletedSuccessfully),
        };
        FakeScaleSetRuntimeClient client = new();
        IScaleSetRuntime runtime = await CreateFactory(options, client, provider).CreateAsync(
            scaleSet, new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets), token);
        Task initializer = runtime.InitializeAsync(token);
        await discovering.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        await runtime.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initializer);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, provider.DisposeCount);
        Assert.Equal(0, client.SessionCreateCount);

        string stateDirectory = GetSetStateDirectory(directory.Path, scaleSet.Key!);
        await using FileStream bootstrap = File.Open(Path.Combine(stateDirectory, ".scale-set-bootstrap.lease"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(
            stateDirectory, new RunnerOwnershipScope(options.Controller.Identity, scaleSet.Key!, 987), token);
        Assert.Empty(await journal.ReadAllAsync(token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_RecordsRealOperationsAndStopsReportingReleasedRunnerCapacity(bool failFinalAcknowledgment)
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        CancellationToken token = TestContext.Current.CancellationToken;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        HostCapacityCoordinator capacity = new(options.HostCapacity, options.ScaleSets);
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = services.GetRequiredService<IMeterFactory>();
        using var metrics = new ScaleSetTelemetryMetrics(meterFactory, [scaleSet.Key!], capacity, ScaleSetTelemetryProvider.Docker);
        ConcurrentQueue<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> measurements = new();
        using MeterListener meterListener = new()
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meterFactory))
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, tags.ToArray())));
        meterListener.Start();

        Channel<RunnerScaleSetMessage?> messages = Channel.CreateUnbounded<RunnerScaleSetMessage?>();
        Channel<long> acknowledgmentAttempts = Channel.CreateUnbounded<long>();
        TaskCompletionSource waitingForAnotherMessage = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IOException acknowledgmentFailure = new("synthetic acknowledgment failure");
        int polls = 0;
        FakeRuntimeSession session = new()
        {
            Poll = cancellationToken =>
            {
                if (++polls == 4)
                {
                    waitingForAnotherMessage.SetResult();
                }

                return messages.Reader.ReadAsync(cancellationToken).AsTask();
            },
            Acknowledge = async (messageId, cancellationToken) =>
            {
                await acknowledgmentAttempts.Writer.WriteAsync(messageId, cancellationToken).ConfigureAwait(false);
                if (messageId == 11 && failFinalAcknowledgment)
                {
                    throw acknowledgmentFailure;
                }
            },
            Close = _ => Task.CompletedTask,
        };
        FakeScaleSetRuntimeClient client = new()
        {
            RuntimeSession = session,
            OnGenerateJit = setting => new RunnerScaleSetJitRunnerConfig
            {
                Runner = new RunnerReference { Id = 915, Name = setting.Name },
                EncodedJitConfig = "synthetic-jit",
            },
        };
        FakeRunnerProvider provider = new() { AllowProvisioning = true };
        await using IScaleSetRuntime runtime = await CreateFactory(options, client, provider, metrics).CreateAsync(scaleSet, capacity, token);
        await runtime.InitializeAsync(token);
        Task run = runtime.RunAsync(deadline.Token);
        await messages.Writer.WriteAsync(null, token);
        await messages.Writer.WriteAsync(new RunnerScaleSetMessage
        {
            MessageId = 10,
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = 800 }],
        }, token);
        Assert.Equal(10, await acknowledgmentAttempts.Reader.ReadAsync(deadline.Token));

        meterListener.RecordObservableInstruments();
        var active = Assert.Single(measurements, item => item.Name == "scaleset.runners");
        Assert.Equal(1, active.Value);
        Assert.Equal(new Dictionary<string, object?> { ["scale_set"] = scaleSet.Key, ["provider"] = "docker", ["state"] = "starting" }, active.Tags.ToDictionary());
        RunnerResource runner = Assert.IsType<RunnerResource>(provider.Current);
        provider.Current = new RunnerResource(runner.ProviderResourceId, runner.Identity, RunnerResourceStatus.Exited, exitCode: 0);
        await messages.Writer.WriteAsync(new RunnerScaleSetMessage
        {
            MessageId = 11,
            Statistics = new RunnerScaleSetStatistic(),
            JobCompletedMessages =
            [
                new JobCompletedMessage { RunnerRequestId = 800, RunnerId = 915, RunnerName = runner.Identity.RunnerName },
            ],
        }, token);
        Assert.Equal(11, await acknowledgmentAttempts.Reader.ReadAsync(deadline.Token));
        if (failFinalAcknowledgment)
        {
            Assert.Same(acknowledgmentFailure, await Assert.ThrowsAsync<IOException>(() => run));
        }
        else
        {
            await waitingForAnotherMessage.Task.WaitAsync(deadline.Token);
        }

        await runtime.DrainAsync(token);
        await runtime.StopAsync(token);
        if (!failFinalAcknowledgment)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }

        Assert.Null(provider.Current);
        meterListener.RecordObservableInstruments();
        Assert.Single(measurements, item => item.Name == "scaleset.runners");
        var operations = measurements.Where(item => item.Name == "scaleset.operations")
            .Select(item =>
            {
                Assert.Equal(1, item.Value);
                var tags = item.Tags.ToDictionary();
                Assert.Equal(4, tags.Count);
                Assert.Equal(scaleSet.Key, tags["scale_set"]);
                Assert.Equal("docker", tags["provider"]);
                return (Operation: Assert.IsType<string>(tags["operation"]), Outcome: Assert.IsType<string>(tags["outcome"]));
            }).ToArray();
        Assert.Equal(1, operations.Count(item => item == ("reconcile", "success")));
        Assert.Equal(1, operations.Count(item => item == ("job_acquire", "success")));
        Assert.Equal(1, operations.Count(item => item == ("provision", "success")));
        Assert.Equal(failFinalAcknowledgment ? 1 : 2, operations.Count(item => item == ("acknowledge", "success")));
        Assert.Equal(failFinalAcknowledgment ? 1 : 0, operations.Count(item => item == ("acknowledge", "failure")));
        Assert.Equal(2, operations.Count(item => item == ("queue_poll", "success")));
        Assert.Equal(1, operations.Count(item => item == ("queue_poll", "empty")));
        Assert.Equal(failFinalAcknowledgment ? 0 : 1, operations.Count(item => item == ("queue_poll", "cancelled")));
        Assert.Equal(1, operations.Count(item => item == ("drain", "success")));
    }

    private static ScaleSetRuntimeFactory CreateFactory(
        AutoscalerOptions options,
        FakeScaleSetRuntimeClient client,
        FakeRunnerProvider provider,
        ScaleSetTelemetryMetrics? telemetry = null) =>
        new(
            options,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            (_, _, _, _, _) => client,
            (_, _, _) => provider,
            telemetry);

    private static (AutoscalerOptions Options, ScaleSetOptions ScaleSet) CreateOptions(string stateDirectory)
    {
        ScaleSetOptions scaleSet = new()
        {
            Key = "linux-x64",
            GithubUrl = "https://github.com/org/repo",
            Name = "ci-linux",
            CredentialsRef = "github",
            Labels = [],
            Scaling = new ScalingOptions { Policy = ScalingPolicy.SpareCapacity, MinRunners = 0, MaxRunners = 10 },
            Lifecycle = new LifecycleOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(30),
                ShutdownDrainTimeout = TimeSpan.FromSeconds(2),
                CleanupTimeout = TimeSpan.FromSeconds(2),
            },
            RunnerProfile = new RunnerProfileOptions
            {
                Image = "example/runner:1",
                Architecture = RunnerArchitecture.X64,
                Cpu = 1,
                Memory = 1024,
                WorkFolder = "_work",
            },
        };
        AutoscalerOptions options = new()
        {
            Controller = new ControllerOptions
            {
                Identity = "controller-test",
                StateDirectory = stateDirectory,
                ControlOperationTimeout = TimeSpan.FromSeconds(2),
            },
            HostCapacity = new HostCapacityOptions { CreationConcurrency = 2 },
            ScaleSets = [scaleSet],
            Credentials = new Dictionary<string, CredentialOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["github"] = new CredentialOptions { Pat = CredentialSentinel },
            },
        };
        return (options, scaleSet);
    }

    private static string GetSetStateDirectory(string stateRoot, string scaleSetKey)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(scaleSetKey.ToUpperInvariant()));
        return Path.Combine(Path.GetFullPath(stateRoot), Convert.ToHexString(digest).ToLowerInvariant());
    }

    private sealed class FakeScaleSetRuntimeClient : IScaleSetRuntimeClient
    {
        private int _createCount;
        private int _sessionCreateCount;

        public Func<RunnerScaleSet, CancellationToken, Task<RunnerScaleSet>>? OnCreate { get; init; }
        public Func<RunnerScaleSetJitRunnerSetting, RunnerScaleSetJitRunnerConfig>? OnGenerateJit { get; init; }

        public FakeRuntimeSession? RuntimeSession { get; init; }

        public int DisposeCount { get; private set; }

        public int CreateCount => Volatile.Read(ref _createCount);

        public int SessionCreateCount => Volatile.Read(ref _sessionCreateCount);

        public Task<RunnerGroup> GetRunnerGroupByNameAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("Default", name);
            return Task.FromResult(new RunnerGroup { Id = 44, Name = name });
        }

        public Task<RunnerScaleSet?> FindScaleSetAsync(long runnerGroupId, string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(44L, runnerGroupId);
            Assert.Equal("ci-linux", name);
            return Task.FromResult<RunnerScaleSet?>(null);
        }

        public Task<RunnerScaleSet> CreateScaleSetAsync(RunnerScaleSet scaleSet, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _createCount);
            return OnCreate?.Invoke(scaleSet, cancellationToken)
                ?? Task.FromResult(new RunnerScaleSet
                {
                    Id = 987,
                    Name = scaleSet.Name,
                    RunnerGroupId = scaleSet.RunnerGroupId,
                    RunnerGroupName = scaleSet.RunnerGroupName,
                    Labels = scaleSet.Labels,
                });
        }

        public Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
            long scaleSetId,
            RunnerScaleSetJitRunnerSetting runnerSetting,
            CancellationToken cancellationToken) =>
            Task.FromResult<RunnerScaleSetJitRunnerConfig?>(OnGenerateJit?.Invoke(runnerSetting)
                ?? throw new InvalidOperationException("This test did not configure JIT generation."));

        public Task<IScaleSetRuntimeSession> CreateMessageSessionAsync(long scaleSetId, string ownerName, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sessionCreateCount);
            return Task.FromResult<IScaleSetRuntimeSession>(RuntimeSession
                ?? throw new InvalidOperationException("This test did not configure a session."));
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeRuntimeSession : IScaleSetRuntimeSession
    {
        public required Func<CancellationToken, Task<RunnerScaleSetMessage?>> Poll { get; init; }
        public required Func<CancellationToken, Task> Close { get; init; }
        public Func<long, CancellationToken, Task>? Acknowledge { get; init; }
        public int CloseCount { get; private set; }

        public RunnerScaleSetSession Session { get; } = new()
        {
            SessionId = Guid.NewGuid(),
            Statistics = new RunnerScaleSetStatistic(),
        };

        public Task<RunnerScaleSetMessage?> GetMessageAsync(long lastMessageId, int maxCapacity, CancellationToken cancellationToken) =>
            Poll(cancellationToken);

        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken) =>
            Acknowledge?.Invoke(messageId, cancellationToken) ?? Task.CompletedTask;

        public Task<IReadOnlyList<long>> AcquireJobsAsync(IReadOnlyList<long> runnerRequestIds, CancellationToken cancellationToken) =>
            Task.FromResult(runnerRequestIds);

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            return Close(cancellationToken);
        }
    }

    private sealed class FakeRunnerProvider : IRunnerProvider, IDisposable
    {
        public RunnerProviderCapabilities Capabilities { get; } = new(
            "fake",
            ImmutableArray.Create(RunnerArchitecture.X64),
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: true);

        public ImmutableArray<RunnerResource> Discovered { get; init; } = ImmutableArray<RunnerResource>.Empty;
        public Func<CancellationToken, Task<ImmutableArray<RunnerResource>>>? OnDiscover { get; init; }
        public Action? OnDispose { get; init; }
        public bool AllowProvisioning { get; init; }
        public RunnerResource? Current { get; set; }
        public int DisposeCount { get; private set; }

        public Task<ImmutableArray<RunnerResource>> DiscoverAsync(RunnerOwnershipScope scope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return OnDiscover?.Invoke(cancellationToken) ?? Task.FromResult(Discovered);
        }

        public Task<RunnerResource> CreateAsync(RunnerCreateRequest request, CancellationToken cancellationToken = default)
        {
            Assert.True(AllowProvisioning);
            Assert.Null(Current);
            Current = new RunnerResource("fake-container", request.Identity, RunnerResourceStatus.Created);
            return Task.FromResult(Current);
        }

        public Task<RunnerResource> StartAsync(RunnerResource resource, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Current, resource);
            Current = new RunnerResource(resource.ProviderResourceId, resource.Identity, RunnerResourceStatus.Running);
            return Task.FromResult(Current);
        }

        public Task<RunnerResource?> InspectAsync(RunnerResource resource, CancellationToken cancellationToken = default) =>
            Task.FromResult(Current?.ProviderResourceId == resource.ProviderResourceId ? Current : null);

        public Task RemoveAsync(RunnerResource resource, bool force, CancellationToken cancellationToken = default)
        {
            Assert.False(force);
            Assert.Equal(Current, resource);
            Current = null;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            DisposeCount++;
            OnDispose?.Invoke();
        }
    }

    private sealed class TemporaryStateDirectory : IDisposable
    {
        public TemporaryStateDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"scaleset-runtime-factory-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
