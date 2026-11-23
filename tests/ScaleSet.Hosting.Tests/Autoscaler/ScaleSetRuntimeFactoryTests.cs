using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScaleSet.Client;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.Runtime;
using ScaleSet.Hosting.Scaling;
using ScaleSet.Hosting.State;
using ScaleSet.Hosting.Telemetry;

namespace ScaleSet.Hosting.Tests.Autoscaler;

public sealed partial class ScaleSetRuntimeFactoryTests
{
    private const string CredentialSentinel = "synthetic-PAT-secret-6419";

    [Fact]
    public async Task AddScaleSetHosting_RejectsAnUnregisteredProviderBeforeStartingAnyRuntime()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions first) = CreateOptions(directory.Path);
        options.ScaleSets.Add(new ScaleSetOptions
        {
            Key = "second",
            Name = "another-set",
            GithubUrl = first.GithubUrl,
            CredentialsRef = first.CredentialsRef,
            RunnerProfile = first.RunnerProfile,
            Provider = "Unregistered",
        });
        FakeScaleSetRuntimeClient client = new();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IScaleSetRuntimeClientFactory>(new TestRuntimeClientFactory(client));
        builder.Services.AddSingleton<IRunnerProviderFactory>(new TestRunnerProviderFactory(new FakeRunnerProvider()));
        builder.Services.AddScaleSetHosting(options);
        using IHost host = builder.Build();

        AutoscalerConfigurationException error = await Assert.ThrowsAsync<AutoscalerConfigurationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ScaleSets[1].Provider", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.CreateCount);
        Assert.Equal(0, client.SessionCreateCount);
        Assert.False(host.Services.GetRequiredService<AutoscalerHealthState>().IsReady);
    }

    [Theory]
    [InlineData(1, 14)]
    [InlineData(20, 20)]
    public void AddScaleSetHosting_PreservesEnoughHostShutdownTime(int configuredSeconds, int expectedSeconds)
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        scaleSet.Lifecycle.ShutdownDrainTimeout = TimeSpan.FromSeconds(5);
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(configuredSeconds));
        builder.Services.AddSingleton<IRunnerProviderFactory>(new TestRunnerProviderFactory(new FakeRunnerProvider()));
        builder.Services.AddScaleSetHosting(options);
        using IHost host = builder.Build();

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

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
        InvalidOperationException acknowledgmentFailure = new("synthetic permanent acknowledgment failure");
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
            Assert.Same(acknowledgmentFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => run));
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

    [Fact]
    public async Task RunAsync_RecoversFailedAcknowledgmentWithoutDuplicateProvisioningOrStoppingPeer()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateRecoveryOptions(directory.Path);
        ScaleSetOptions peer = new()
        {
            Key = "peer",
            GithubUrl = "https://github.com/org/peer",
            Name = scaleSet.Name,
            CredentialsRef = scaleSet.CredentialsRef,
            RunnerProfile = scaleSet.RunnerProfile,
            Scaling = scaleSet.Scaling,
            Lifecycle = scaleSet.Lifecycle,
        };
        options.ScaleSets.Add(peer);
        BackoffClock clock = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        TaskCompletionSource<long> acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunnerScaleSetMessage message = new()
        {
            MessageId = 51,
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = 800 }],
        };
        FakeRuntimeSession first = new()
        {
            Poll = _ => Task.FromResult<RunnerScaleSetMessage?>(message),
            Acknowledge = (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "synthetic lost acknowledgment"),
            Close = _ => Task.CompletedTask,
        };
        int replacementPolls = 0;
        FakeRuntimeSession replacement = new()
        {
            Poll = async cancellationToken =>
            {
                if (++replacementPolls == 1)
                {
                    return message;
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return null;
            },
            Acknowledge = (messageId, _) =>
            {
                acknowledged.SetResult(messageId);
                return Task.CompletedTask;
            },
            Close = _ => Task.CompletedTask,
        };
        int jitRequests = 0;
        FakeScaleSetRuntimeClient client = new()
        {
            OnOpen = (attempt, _) =>
            {
                if (attempt == 1)
                {
                    return Task.FromResult<IMessageSession>(first);
                }

                Assert.Equal(2, attempt);
                Assert.Equal(1, first.CloseCount);
                return Task.FromResult<IMessageSession>(replacement);
            },
            OnGenerateJit = setting =>
            {
                Interlocked.Increment(ref jitRequests);
                return new RunnerScaleSetJitRunnerConfig
                {
                    Runner = new RunnerReference { Id = 915, Name = setting.Name },
                    EncodedJitConfig = "synthetic-jit",
                };
            },
        };
        FakeRunnerProvider provider = new() { AllowProvisioning = true };
        HostCapacityCoordinator capacity = new(options.HostCapacity, options.ScaleSets);
        await using IScaleSetRuntime runtime = await CreateFactory(options, client, provider, timeProvider: clock)
            .CreateAsync(scaleSet, capacity, token);
        Channel<RunnerScaleSetMessage?> peerMessages = Channel.CreateUnbounded<RunnerScaleSetMessage?>();
        TaskCompletionSource<long> peerAcknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRuntimeSession peerSession = new()
        {
            Poll = cancellationToken => peerMessages.Reader.ReadAsync(cancellationToken).AsTask(),
            Acknowledge = (messageId, _) =>
            {
                peerAcknowledged.SetResult(messageId);
                return Task.CompletedTask;
            },
            Close = _ => Task.CompletedTask,
        };
        FakeScaleSetRuntimeClient peerClient = new() { RuntimeSession = peerSession };
        await using IScaleSetRuntime peerRuntime = await CreateFactory(options, peerClient, new FakeRunnerProvider())
            .CreateAsync(peer, capacity, token);
        await runtime.InitializeAsync(token);
        await peerRuntime.InitializeAsync(token);
        Task peerRun = peerRuntime.RunAsync(deadline.Token);
        Task run = runtime.RunAsync(deadline.Token);

        Action reconnect = await clock.WaitForDelayAsync(TimeSpan.FromSeconds(1), deadline.Token);
        await peerMessages.Writer.WriteAsync(new RunnerScaleSetMessage
        {
            MessageId = 52,
            Statistics = new RunnerScaleSetStatistic(),
        }, token);
        Assert.Equal(52, await peerAcknowledged.Task.WaitAsync(deadline.Token));
        Assert.Equal(1, client.SessionCreateCount);
        Assert.Equal(0, peerSession.CloseCount);

        reconnect();
        Assert.Equal(51, await acknowledged.Task.WaitAsync(deadline.Token));
        await runtime.StopAsync(token);
        await peerRuntime.StopAsync(token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peerRun);

        Assert.Equal(2, client.SessionCreateCount);
        Assert.Equal(1, first.CloseCount);
        Assert.Equal(1, replacement.CloseCount);
        Assert.Equal(1, Volatile.Read(ref jitRequests));
        Assert.Equal(1, capacity.CaptureSnapshot().Usage.RunnerCount);
    }

    [Theory]
    [InlineData(409, false, 4)]
    [InlineData(409, true, 1)]
    [InlineData(403, false, 1)]
    public async Task RunAsync_BoundsSessionConflictsAndStopsOnCancellationOrPermanentFailure(
        int status,
        bool cancelDuringBackoff,
        int expectedSessionAttempts)
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateRecoveryOptions(directory.Path);
        BackoffClock clock = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        ScaleSetApiException failure = new("synthetic session-open failure", statusCode: status);
        FakeScaleSetRuntimeClient client = new() { OnOpen = (_, _) => throw failure };
        await using IScaleSetRuntime runtime = await CreateFactory(options, client, new FakeRunnerProvider(), timeProvider: clock)
            .CreateAsync(scaleSet, new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets), token);
        await runtime.InitializeAsync(token);
        Task run = runtime.RunAsync(deadline.Token);

        if (cancelDuringBackoff)
        {
            await clock.CancelDuringNextDelayAsync(TimeSpan.FromSeconds(1), deadline);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        else
        {
            for (int retry = 0; retry < expectedSessionAttempts - 1; retry++)
            {
                await clock.ElapseNextDelayAsync(TimeSpan.FromSeconds(1 << retry), deadline.Token);
            }

            Assert.Same(failure, await Assert.ThrowsAsync<ScaleSetApiException>(() => run.WaitAsync(deadline.Token)));
        }

        Assert.Equal(expectedSessionAttempts, client.SessionCreateCount);
    }

    [Fact]
    public async Task RunAsync_UnconfirmedClosePreventsOpeningAReplacementSession()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        CancellationToken token = TestContext.Current.CancellationToken;
        HttpRequestException pollFailure = new(HttpRequestError.ConnectionError, "synthetic queue failure");
        ScaleSetApiException closeFailure = new("synthetic close failure", statusCode: 503);
        int closeAttempts = 0;
        FakeRuntimeSession session = new()
        {
            Poll = _ => throw pollFailure,
            Close = _ => ++closeAttempts == 1 ? throw closeFailure : Task.CompletedTask,
        };
        FakeScaleSetRuntimeClient client = new() { RuntimeSession = session };
        await using IScaleSetRuntime runtime = await CreateFactory(options, client, new FakeRunnerProvider())
            .CreateAsync(scaleSet, new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets), token);
        await runtime.InitializeAsync(token);

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => runtime.RunAsync(token));
        Assert.Equal([pollFailure, closeFailure], failure.InnerExceptions);
        Assert.Equal(1, client.SessionCreateCount);
        await runtime.StopAsync(token);
        Assert.Equal(2, session.CloseCount);
    }

    [Theory]
    [InlineData("connection", true)]
    [InlineData("timeout", true)]
    [InlineData("tls", false)]
    public async Task RunAsync_UsesTheProductionClientTransportClassification(string failure, bool reconnects)
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateRecoveryOptions(directory.Path);
        CancellationToken token = TestContext.Current.CancellationToken;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        BackoffClock clock = new();
        using SessionTransport transport = new(failure, options.Controller.Identity);
        using HttpClient httpClient = new(transport, disposeHandler: false);
        using ScaleSetRuntimeClientAdapter adapter = new(new ScaleSetClient(
            GitHubTarget.Parse(scaleSet.GithubUrl!),
            ScaleSetCredentials.FromPersonalAccessToken(CredentialSentinel),
            new ScaleSetClientOptions { Retry = ScaleSetRetryOptions.Disabled },
            httpClient,
            timeProvider: clock));
        FakeScaleSetRuntimeClient client = new()
        {
            OnOpen = (_, cancellationToken) => adapter.CreateMessageSessionAsync(987, options.Controller.Identity, cancellationToken),
        };
        await using IScaleSetRuntime runtime = await CreateFactory(options, client, new FakeRunnerProvider(), timeProvider: clock)
            .CreateAsync(scaleSet, new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets), token);
        await runtime.InitializeAsync(token);
        Task run = runtime.RunAsync(deadline.Token);

        if (reconnects)
        {
            await clock.ElapseNextDelayAsync(TimeSpan.FromSeconds(1), deadline.Token);
            await transport.ReplacementPolled.WaitAsync(deadline.Token);
            Assert.Equal([transport.FirstSessionId], transport.Closed);
            Assert.Equal(2, client.SessionCreateCount);
        }
        else
        {
            ScaleSetApiException error = await Assert.ThrowsAsync<ScaleSetApiException>(() => run.WaitAsync(deadline.Token));
            Assert.Null(error.StatusCode);
            Assert.False(error.IsTransientTransportFailure);
            Assert.DoesNotContain("synthetic transport detail", error.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, client.SessionCreateCount);
        }

        await runtime.StopAsync(token);
        if (reconnects)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal([transport.FirstSessionId, transport.ReplacementSessionId], transport.Closed);
        }
        else
        {
            Assert.Equal([transport.FirstSessionId], transport.Closed);
        }
    }

    private static ScaleSetRuntimeFactory CreateFactory(
        AutoscalerOptions options,
        FakeScaleSetRuntimeClient client,
        FakeRunnerProvider provider,
        ScaleSetTelemetryMetrics? telemetry = null,
        TimeProvider? timeProvider = null) =>
        new(
            options,
            NullLoggerFactory.Instance,
            timeProvider ?? TimeProvider.System,
            new TestRuntimeClientFactory(client),
            [new TestRunnerProviderFactory(provider)],
            telemetry);

    private sealed class TestRuntimeClientFactory(IScaleSetRuntimeClient client) : IScaleSetRuntimeClientFactory
    {
        public IScaleSetRuntimeClient Create(GitHubTarget target, ScaleSetCredentials credentials, ScaleSetClientOptions options) => client;
    }

    private sealed class TestRunnerProviderFactory(IRunnerProvider provider) : IRunnerProviderFactory
    {
        public string Name => "Docker";

        public IReadOnlyList<string> Validate(ScaleSetOptions scaleSet, string configurationPath) => [];

        public IRunnerProvider Create(ScaleSetOptions scaleSet) => provider;
    }

    private static (AutoscalerOptions Options, ScaleSetOptions ScaleSet) CreateRecoveryOptions(string stateDirectory)
    {
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(stateDirectory);
        TimeSpan controlBudget = TimeSpan.FromMinutes(1);
        options.Controller.ControlOperationTimeout = controlBudget;
        scaleSet.Lifecycle.CleanupTimeout = controlBudget;
        scaleSet.Lifecycle.ShutdownDrainTimeout = controlBudget;
        return (options, scaleSet);
    }

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
        public Func<int, CancellationToken, Task<IMessageSession>>? OnOpen { get; init; }
        public Func<long, CancellationToken, Task<RunnerReference?>>? OnGetRunner { get; init; }
        public Func<long, CancellationToken, Task>? OnRemoveRunner { get; init; }
        public RunnerScaleSet? ExistingScaleSet { get; init; }

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
            return Task.FromResult(ExistingScaleSet);
        }

        public Task<RunnerReference?> GetRunnerAsync(long runnerId, CancellationToken cancellationToken) =>
            OnGetRunner?.Invoke(runnerId, cancellationToken) ?? Task.FromResult<RunnerReference?>(null);

        public Task RemoveRunnerAsync(long runnerId, CancellationToken cancellationToken) =>
            OnRemoveRunner?.Invoke(runnerId, cancellationToken) ?? throw new InvalidOperationException("This test did not configure runner removal.");

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

        public Task<IMessageSession> CreateMessageSessionAsync(long scaleSetId, string ownerName, CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref _sessionCreateCount);
            return OnOpen?.Invoke(attempt, cancellationToken) ?? Task.FromResult<IMessageSession>(RuntimeSession
                ?? throw new InvalidOperationException("This test did not configure a session."));
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeRuntimeSession : IMessageSession
    {
        public required Func<CancellationToken, Task<RunnerScaleSetMessage?>> Poll { get; init; }
        public required Func<CancellationToken, Task> Close { get; init; }
        public Func<IReadOnlyList<long>, CancellationToken, Task<IReadOnlyList<long>>>? Acquire { get; init; }
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
            Acquire?.Invoke(runnerRequestIds, cancellationToken) ?? Task.FromResult(runnerRequestIds);

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
            return OnDiscover?.Invoke(cancellationToken)
                ?? Task.FromResult(Current is { } current ? Discovered.Add(current) : Discovered);
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

    private sealed class BackoffClock : TimeProvider
    {
        private readonly Channel<(TimeSpan Due, BackoffTimer Timer)> _delays = Channel.CreateUnbounded<(TimeSpan, BackoffTimer)>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // This fixture uses system timers for control and cleanup budgets.
            // Only the 1, 2, and 4 second recovery delays use manual timers.
            if (period != Timeout.InfiniteTimeSpan || dueTime.TotalSeconds is not (1 or 2 or 4))
            {
                return System.CreateTimer(callback, state, dueTime, period);
            }

            BackoffTimer timer = new(callback, state);
            Assert.True(_delays.Writer.TryWrite((dueTime, timer)));
            return timer;
        }

        public async Task<Action> WaitForDelayAsync(TimeSpan expected, CancellationToken cancellationToken)
        {
            (TimeSpan due, BackoffTimer timer) = await _delays.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Assert.Equal(expected, due);
            return timer.Fire;
        }

        public async Task ElapseNextDelayAsync(TimeSpan expected, CancellationToken cancellationToken)
        {
            Action elapse = await WaitForDelayAsync(expected, cancellationToken).ConfigureAwait(false);
            elapse();
        }

        public async Task CancelDuringNextDelayAsync(TimeSpan expected, CancellationTokenSource cancellation)
        {
            _ = await WaitForDelayAsync(expected, cancellation.Token).ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        private sealed class BackoffTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class SessionTransport(string failure, string owner) : HttpMessageHandler
    {
        private const string DiscoveryJwt = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJleHAiOjQxMDI0NDQ4MDB9.";
        private readonly TaskCompletionSource _replacementPolled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<Guid> _closed = new();
        private int _opened;

        public Guid FirstSessionId { get; } = Guid.NewGuid();

        public Guid ReplacementSessionId { get; } = Guid.NewGuid();

        public Task ReplacementPolled => _replacementPolled.Task;

        public IReadOnlyList<Guid> Closed => _closed.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = Assert.IsType<Uri>(request.RequestUri, exactMatch: false).AbsolutePath;
            if (path.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.Created, """{"token":"synthetic-registration","expires_at":"2099-01-01T00:00:00Z"}""");
            }

            if (path == "/actions/runner-registration")
            {
                return Json(HttpStatusCode.OK, $$"""{"url":"https://actions.example.test/tenant/123/","token":"{{DiscoveryJwt}}"}""");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/sessions", StringComparison.Ordinal))
            {
                bool first = Interlocked.Increment(ref _opened) == 1;
                if (!first)
                {
                    Assert.Equal([FirstSessionId], Closed);
                }

                return Json(HttpStatusCode.OK, ScaleSetJson.Serialize(new RunnerScaleSetSession
                {
                    SessionId = first ? FirstSessionId : ReplacementSessionId,
                    OwnerName = owner,
                    MessageQueueUrl = first ? "https://queue.example.test/first" : "https://queue.example.test/replacement",
                    MessageQueueAccessToken = "synthetic-queue-token",
                    Statistics = new RunnerScaleSetStatistic(),
                }));
            }

            if (request.Method == HttpMethod.Delete && path.Contains("/sessions/", StringComparison.Ordinal))
            {
                _closed.Enqueue(Guid.Parse(path[(path.LastIndexOf('/') + 1)..]));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path == "/first")
            {
                throw failure switch
                {
                    "connection" => new HttpRequestException(HttpRequestError.ConnectionError, "synthetic transport detail"),
                    "timeout" => new OperationCanceledException("synthetic transport detail"),
                    "tls" => new HttpRequestException(HttpRequestError.SecureConnectionError, "synthetic transport detail"),
                    _ => throw new InvalidOperationException("Unexpected test failure classification."),
                };
            }

            Assert.Equal("/replacement", path);
            _replacementPolled.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
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
