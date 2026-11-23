using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Client;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Controller;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.Scaling;
using ScaleSet.Hosting.State;
using ScaleSet.Listener;

namespace ScaleSet.Hosting.Tests.Controller;

public sealed class ScaleSetMessageHandlerTests
{
    [Theory]
    [InlineData(ScalingPolicy.SpareCapacity, 1, 2, 3)]
    [InlineData(ScalingPolicy.TotalFloor, 2, 1, 2)]
    [InlineData(ScalingPolicy.TotalFloor, 2, 4, 4)]
    public async Task HandleAsync_CapacityPathProvisionsFromAssignedStatistics(
        ScalingPolicy policy,
        int minimumRunners,
        int assignedJobs,
        int expectedRunnerCount)
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        ScaleSetOptions options = CreateScaleSetOptions(scope, policy, minimumRunners, maximumRunners: 5);
        HostCapacityCoordinator coordinator = new(new HostCapacityOptions
        {
            MaxTotalRunners = 20,
            CreationConcurrency = 2,
        }, [options]);
        int advertisedMaximum = -1;
        ScaleSetCapacityContext capacity = new(options, coordinator, coordinator.DemandRegistry, maximum => advertisedMaximum = maximum);
        await using Fixture fixture = await Fixture.CreateAsync(directory.Path, scope, capacity);

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = assignedJobs },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(expectedRunnerCount, fixture.Provider.CreateCount);
        Assert.Equal(expectedRunnerCount, fixture.Jit.CallCount);
        Assert.Equal(expectedRunnerCount, coordinator.CaptureSnapshot().ScaleSets[scope.ScaleSetKey].Usage.StateCounts[CapacityOccupancyState.Starting]);
        Assert.Equal(5, advertisedMaximum);
    }

    [Fact]
    public async Task HandleAsync_CapacityPathUsesJobMessagesForAdmissionButNotScaleDemand()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        ScaleSetOptions options = CreateScaleSetOptions(scope, ScalingPolicy.SpareCapacity, minimumRunners: 0, maximumRunners: 5);
        HostCapacityCoordinator coordinator = new(new HostCapacityOptions
        {
            MaxTotalRunners = 5,
            CreationConcurrency = 2,
        }, [options]);
        int advertisedMaximum = -1;
        ScaleSetCapacityContext capacity = new(options, coordinator, coordinator.DemandRegistry, maximum => advertisedMaximum = maximum);
        await using Fixture fixture = await Fixture.CreateAsync(directory.Path, scope, capacity);
        fixture.Session.Acquire = (offered, _) => Task.FromResult<IReadOnlyList<long>>(offered.ToArray());

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 0 },
            JobAvailableMessages =
            [
                new JobAvailableMessage { RunnerRequestId = 701 },
                new JobAvailableMessage { RunnerRequestId = 702 },
                new JobAvailableMessage { RunnerRequestId = 703 },
                new JobAvailableMessage { RunnerRequestId = 704 },
            ],
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Session.AcquireCount);
        Assert.Equal(0, fixture.Provider.CreateCount);
        Assert.Equal(4, coordinator.DemandRegistry.GetAdmissionPermits(scope.ScaleSetKey).Count);
        Assert.Equal(4, coordinator.CaptureSnapshot().ScaleSets[scope.ScaleSetKey].Usage.StateCounts[CapacityOccupancyState.Reserved]);
        Assert.Equal(5, advertisedMaximum);

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Provider.CreateCount);
        Assert.Equal(3, coordinator.DemandRegistry.GetAdmissionPermits(scope.ScaleSetKey).Count);
        RunnerOperationState operation = Assert.Single(await fixture.Journal.ReadAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new CapacityResourceQuantities(1m, 512), operation.Identity.CapacityResources);
        long runnerId = Assert.IsType<long>(operation.Identity.GitHubRunnerId);
        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobStartedMessages =
            [new JobStartedMessage { RunnerRequestId = 701, RunnerId = runnerId, RunnerName = operation.Identity.RunnerName }],
        }, TestContext.Current.CancellationToken);
        RunnerOperationState busy = Assert.IsType<RunnerOperationState>(
            await fixture.Journal.FindAsync(operation.Identity.OperationId, TestContext.Current.CancellationToken));
        Assert.Equal(RunnerLifecycleStage.Busy, busy.Stage);

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobCompletedMessages =
            [new JobCompletedMessage { RunnerRequestId = 701, RunnerId = runnerId + 1, RunnerName = operation.Identity.RunnerName }],
        }, TestContext.Current.CancellationToken);
        RunnerOperationState afterConflictingActivity = Assert.IsType<RunnerOperationState>(
            await fixture.Journal.FindAsync(operation.Identity.OperationId, TestContext.Current.CancellationToken));
        Assert.Equal(RunnerLifecycleStage.Busy, afterConflictingActivity.Stage);
    }

    [Fact]
    public async Task HandleAsync_CapacityPathRequiresFreshStatisticsAndHonorsHostLimit()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        ScaleSetOptions options = CreateScaleSetOptions(scope, ScalingPolicy.SpareCapacity, minimumRunners: 0, maximumRunners: 5);
        HostCapacityCoordinator coordinator = new(new HostCapacityOptions
        {
            MaxTotalRunners = 0,
            CreationConcurrency = 1,
        }, [options]);
        int advertisedMaximum = -1;
        ScaleSetCapacityContext capacity = new(options, coordinator, coordinator.DemandRegistry, maximum => advertisedMaximum = maximum);
        await using Fixture fixture = await Fixture.CreateAsync(directory.Path, scope, capacity);
        fixture.Session.Acquire = (offered, _) => Task.FromResult<IReadOnlyList<long>>(offered.ToArray());

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = 801 }],
        }, TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.Session.AcquireCount);

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = 802 }],
        }, TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.Session.AcquireCount);
        Assert.Equal(0, fixture.Provider.CreateCount);
        Assert.Equal(0, advertisedMaximum);
    }

    [Fact]
    public async Task Constructor_RejectsMissingCapacityContext()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        await using Fixture fixture = await Fixture.CreateAsync(directory.Path, scope);

        Assert.Throws<ArgumentNullException>("capacity", () => new ScaleSetMessageHandler(
            scope, fixture.Session, fixture.Provisioner, fixture.Lifecycle, fixture.Journal, capacity: null!));
    }

    [Fact]
    public async Task HandleAsync_AcquiresBeforeProvisioningAndDoesNotProvisionTwiceOnReplay()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        await using Fixture fixture = await Fixture.CreateAsync(directory.Path, scope);
        fixture.Session.Acquire = (offered, _) =>
        {
            IReadOnlyList<long> expected = fixture.Session.AcquireCount == 1 ? [101L, 102L, 103L] : [101L, 103L];
            Assert.Equal(expected, offered);
            return Task.FromResult<IReadOnlyList<long>>([102, 999, 102]);
        };
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RunnerScaleSetMessage message = new()
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
            JobAvailableMessages =
            [
                new JobAvailableMessage { RunnerRequestId = 101 },
                new JobAvailableMessage { RunnerRequestId = 102 },
                new JobAvailableMessage { RunnerRequestId = 103 },
            ],
        };

        await fixture.Handler.HandleAsync(message, cancellationToken);
        RunnerOperationState first = Assert.Single(await fixture.Journal.ReadAllAsync(cancellationToken));
        await fixture.Handler.HandleAsync(message, cancellationToken);
        RunnerOperationState replayed = Assert.Single(await fixture.Journal.ReadAllAsync(cancellationToken));

        Assert.Equal(first, replayed);
        Assert.Equal(2, fixture.Session.AcquireCount);
        Assert.Equal(1, fixture.Jit.CallCount);
        Assert.Equal(1, fixture.Provider.CreateCount);
        Assert.Equal(1, fixture.Provider.StartCount);
        Assert.Equal("acquire", fixture.Timeline.First());
        Assert.True(Array.IndexOf(fixture.Timeline.ToArray(), "acquire") < Array.IndexOf(fixture.Timeline.ToArray(), "jit"));

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobCompletedMessages =
            [
                new JobCompletedMessage
                {
                    RunnerRequestId = 102,
                    RunnerId = Assert.IsType<long>(first.Identity.GitHubRunnerId),
                    RunnerName = first.Identity.RunnerName,
                },
            ],
        }, cancellationToken);
        Assert.True(await fixture.Handler.WaitForDrainAsync(TimeSpan.Zero, TimeProvider.System, cancellationToken));
    }

    [Fact]
    public async Task HandleAsync_SwappedAssignmentsAndReplayedEventsOnlyCleanUpTheMatchingRunner()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        ScaleSetCapacityContext capacity = CreateCapacityContext(scope);
        await using Fixture fixture = await Fixture.CreateAsync(directory.Path, scope, capacity);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const long firstRequest = 501;
        const long secondRequest = 502;
        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = firstRequest }],
        }, cancellationToken);
        RunnerOperationState first = Assert.Single(await fixture.Journal.ReadAllAsync(cancellationToken));
        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 2 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = secondRequest }],
        }, cancellationToken);
        RunnerOperationState second = Assert.Single(await fixture.Journal.ReadAllAsync(cancellationToken),
            operation => operation.Identity.OperationId != first.Identity.OperationId);
        long firstRunnerId = Assert.IsType<long>(first.Identity.GitHubRunnerId);
        long secondRunnerId = Assert.IsType<long>(second.Identity.GitHubRunnerId);

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobStartedMessages =
            [
                new JobStartedMessage { RunnerRequestId = firstRequest, RunnerId = secondRunnerId, RunnerName = second.Identity.RunnerName },
            ],
            JobCompletedMessages =
            [
                new JobCompletedMessage { RunnerRequestId = secondRequest, RunnerId = firstRunnerId, RunnerName = second.Identity.RunnerName },
            ],
        }, cancellationToken);
        Assert.Equal(RunnerLifecycleStage.Starting,
            (await fixture.Journal.FindAsync(first.Identity.OperationId, cancellationToken))?.Stage);
        Assert.Equal(RunnerLifecycleStage.Busy,
            (await fixture.Journal.FindAsync(second.Identity.OperationId, cancellationToken))?.Stage);

        // Deliver the second request's completion before its delayed started event.
        RunnerScaleSetMessage completed = new()
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
            JobCompletedMessages =
            [
                new JobCompletedMessage { RunnerRequestId = secondRequest, RunnerId = firstRunnerId, RunnerName = first.Identity.RunnerName },
            ],
        };
        await fixture.Handler.HandleAsync(completed, cancellationToken);
        Assert.Equal(RunnerLifecycleStage.Completed,
            (await fixture.Journal.FindAsync(first.Identity.OperationId, cancellationToken))?.Stage);
        Assert.Empty(fixture.Provider.RemovedOperations);
        Assert.Equal(2, capacity.Coordinator.CaptureSnapshot().Usage.RunnerCount);

        fixture.Provider.MarkExited(first.Identity.OperationId);
        await fixture.Handler.HandleAsync(completed, cancellationToken);
        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobStartedMessages =
            [
                new JobStartedMessage { RunnerRequestId = secondRequest, RunnerId = firstRunnerId, RunnerName = first.Identity.RunnerName },
            ],
            JobCompletedMessages = completed.JobCompletedMessages,
        }, cancellationToken);

        Assert.Equal([first.Identity.OperationId], fixture.Provider.RemovedOperations);
        Assert.Equal(RunnerLifecycleStage.Gone,
            (await fixture.Journal.FindAsync(first.Identity.OperationId, cancellationToken))?.Stage);
        Assert.Equal(RunnerLifecycleStage.Busy,
            (await fixture.Journal.FindAsync(second.Identity.OperationId, cancellationToken))?.Stage);
        Assert.Equal(1, capacity.Coordinator.CaptureSnapshot().Usage.RunnerCount);
        Assert.Equal(1, capacity.Coordinator.CaptureSnapshot().Usage.StateCounts[CapacityOccupancyState.Busy]);
        Assert.Equal(2, fixture.Provider.CreateCount);
    }

    [Fact]
    public async Task HandleAsync_NullStatisticsRetainsLatestAssignedCount()
    {
        using TemporaryStateDirectory directory = new();
        await using Fixture fixture = await Fixture.CreateAsync(directory.Path, CreateScope());

        await fixture.Handler.HandleAsync(
            new RunnerScaleSetMessage { Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 3 } },
            TestContext.Current.CancellationToken);
        await fixture.Handler.HandleAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(3, fixture.Handler.LatestAssignedJobs);

        await fixture.Handler.HandleAsync(
            new RunnerScaleSetMessage { Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 5 } },
            TestContext.Current.CancellationToken);
        Assert.Equal(5, fixture.Handler.LatestAssignedJobs);
    }

    [Fact]
    public async Task BeginDrain_SkipsNewAcquisitionButStillProcessesCompletionActivity()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        await using Fixture fixture = await Fixture.CreateAsync(directory.Path, scope);
        fixture.Session.Acquire = (offered, _) => Task.FromResult<IReadOnlyList<long>>(offered.ToArray());
        const long requestId = 510;
        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = requestId }],
        }, TestContext.Current.CancellationToken);
        RunnerOperationState provisioned = Assert.Single(
            await fixture.Journal.ReadAllAsync(TestContext.Current.CancellationToken));
        long runnerId = Assert.IsType<long>(provisioned.Identity.GitHubRunnerId);
        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobStartedMessages =
            [new JobStartedMessage { RunnerRequestId = requestId, RunnerId = runnerId, RunnerName = provisioned.Identity.RunnerName }],
        }, TestContext.Current.CancellationToken);

        fixture.Handler.BeginDrain();
        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 0 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = 511 }],
            JobCompletedMessages =
            [new JobCompletedMessage { RunnerRequestId = requestId, RunnerId = runnerId, RunnerName = provisioned.Identity.RunnerName }],
        }, TestContext.Current.CancellationToken);

        RunnerOperationState completed = Assert.IsType<RunnerOperationState>(
            await fixture.Journal.FindAsync(provisioned.Identity.OperationId, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Session.AcquireCount);
        Assert.Equal(1, fixture.Provider.CreateCount);
        Assert.Equal(RunnerLifecycleStage.Completed, completed.Stage);
        Assert.True(await fixture.Handler.WaitForDrainAsync(TimeSpan.Zero, TimeProvider.System, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitForDrainAsync_WaitsForAdmittedProvisioningAndBusyJobThenCompletes()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        await using Fixture fixture = await Fixture.CreateAsync(directory.Path, scope);
        TaskCompletionSource acquireStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IReadOnlyList<long>> releaseAcquire = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.Acquire = async (_, cancellationToken) =>
        {
            acquireStarted.TrySetResult();
            return await releaseAcquire.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        };
        const long requestId = 512;
        Task handling = fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = requestId }],
        }, TestContext.Current.CancellationToken);
        await acquireStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        fixture.Handler.BeginDrain();

        Assert.False(await fixture.Handler.WaitForDrainAsync(
            TimeSpan.Zero,
            TimeProvider.System,
            TestContext.Current.CancellationToken));
        releaseAcquire.SetResult([requestId]);
        await handling;

        RunnerOperationState provisioned = Assert.Single(
            await fixture.Journal.ReadAllAsync(TestContext.Current.CancellationToken));
        long runnerId = Assert.IsType<long>(provisioned.Identity.GitHubRunnerId);
        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobStartedMessages =
            [new JobStartedMessage { RunnerRequestId = requestId, RunnerId = runnerId, RunnerName = provisioned.Identity.RunnerName }],
        }, TestContext.Current.CancellationToken);
        Assert.False(await fixture.Handler.WaitForDrainAsync(
            TimeSpan.Zero,
            TimeProvider.System,
            TestContext.Current.CancellationToken));

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobCompletedMessages =
            [new JobCompletedMessage { RunnerRequestId = requestId, RunnerId = runnerId, RunnerName = provisioned.Identity.RunnerName }],
        }, TestContext.Current.CancellationToken);

        Assert.True(await fixture.Handler.WaitForDrainAsync(
            TimeSpan.Zero,
            TimeProvider.System,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Provider.CreateCount);
    }

    [Fact]
    public async Task Listener_DoesNotAcknowledgeWhenAcquisitionProvisioningHandlerFails()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerScaleSetMessage available = new()
        {
            MessageId = 78,
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = 708 }],
        };
        FakeMessageSession session = new(available)
        {
            Acquire = (offered, _) => Task.FromResult<IReadOnlyList<long>>(offered.ToArray()),
        };
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, TestContext.Current.CancellationToken);
        FakeRunnerProvider provider = new(scope, []);
        FakeJitConfigClient jit = new((_, _, _) => throw new InvalidOperationException("synthetic JIT failure"));
        using ScaleSetRunnerProvisioner provisioner = new(scope.ScaleSetId, "_work", jit, provider, journal);
        using RunnerLifecycleManager lifecycle = new(journal, TimeProvider.System, NullLogger<RunnerLifecycleManager>.Instance);
        ScaleSetMessageHandler handler = new(scope, session, provisioner, lifecycle, journal, CreateCapacityContext(scope));
        ScaleSetListener listener = new(session, new ScaleSetListenerOptions
        {
            ScaleSetId = scope.ScaleSetId,
            MaxRunners = 2,
            AcknowledgmentTimeout = TimeSpan.FromSeconds(1),
        });

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.RunAsync(handler, TestContext.Current.CancellationToken));

        Assert.Equal("synthetic JIT failure", exception.Message);
        Assert.Equal(0, session.DeleteCount);
        RunnerOperationState uncertain = Assert.Single(await journal.ReadAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(RunnerLifecycleStage.Unknown, uncertain.Stage);
    }

    [Fact]
    public void RunnerRequestIdentityFactory_IsStableScopedAndProducesSafeRunnerName()
    {
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity first = RunnerRequestIdentityFactory.Create(scope, 1234567890123);
        RunnerIdentity replay = RunnerRequestIdentityFactory.Create(scope, 1234567890123);
        RunnerIdentity otherRequest = RunnerRequestIdentityFactory.Create(scope, 1234567890124);
        RunnerIdentity otherScope = RunnerRequestIdentityFactory.Create(new RunnerOwnershipScope("controller-a", "linux-arm64", 89), 1234567890123);

        Assert.Equal(first.OperationId, replay.OperationId);
        Assert.Equal(first.RunnerName, replay.RunnerName);
        Assert.NotEqual(first.OperationId, otherRequest.OperationId);
        Assert.NotEqual(first.OperationId, otherScope.OperationId);
        Assert.StartsWith("ss-", first.RunnerName, StringComparison.Ordinal);
        Assert.True(first.RunnerName.Length <= 40);
        Assert.All(first.RunnerName, static character => Assert.True(char.IsAsciiLetterOrDigit(character) || character == '-'));
        Assert.Throws<ArgumentOutOfRangeException>(() => RunnerRequestIdentityFactory.Create(scope, 0));
    }

    private static RunnerOwnershipScope CreateScope() => new("controller-a", "linux-x64", 88);

    private static ScaleSetCapacityContext CreateCapacityContext(RunnerOwnershipScope scope)
    {
        ScaleSetOptions options = CreateScaleSetOptions(scope, ScalingPolicy.SpareCapacity, minimumRunners: 0, maximumRunners: 8);
        HostCapacityCoordinator coordinator = new(new HostCapacityOptions
        {
            MaxTotalRunners = 8,
            CreationConcurrency = 2,
        }, [options]);
        return new ScaleSetCapacityContext(options, coordinator, coordinator.DemandRegistry, static _ => { });
    }

    private static ScaleSetOptions CreateScaleSetOptions(
        RunnerOwnershipScope scope,
        ScalingPolicy policy,
        int minimumRunners,
        int maximumRunners) => new()
    {
        Key = scope.ScaleSetKey,
        Scaling = new ScalingOptions
        {
            Policy = policy,
            MinRunners = minimumRunners,
            MaxRunners = maximumRunners,
        },
        RunnerProfile = new RunnerProfileOptions
        {
            Image = "example/runner:1",
            Architecture = RunnerArchitecture.X64,
            Cpu = 1,
            Memory = 512,
        },
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            FileRunnerStateJournal journal,
            FakeMessageSession session,
            FakeRunnerProvider provider,
            FakeJitConfigClient jit,
            RunnerLifecycleManager lifecycle,
            ScaleSetRunnerProvisioner provisioner,
            ScaleSetMessageHandler handler,
            ConcurrentQueue<string> timeline)
        {
            Journal = journal;
            Session = session;
            Provider = provider;
            Jit = jit;
            Lifecycle = lifecycle;
            Provisioner = provisioner;
            Handler = handler;
            Timeline = timeline;
        }

        public FileRunnerStateJournal Journal { get; }

        public FakeMessageSession Session { get; }

        public FakeRunnerProvider Provider { get; }

        public FakeJitConfigClient Jit { get; }

        public RunnerLifecycleManager Lifecycle { get; }

        public ScaleSetRunnerProvisioner Provisioner { get; }

        public ScaleSetMessageHandler Handler { get; }

        public ConcurrentQueue<string> Timeline { get; }

        public static async Task<Fixture> CreateAsync(
            string stateDirectory,
            RunnerOwnershipScope scope,
            ScaleSetCapacityContext? capacity = null)
        {
            FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(stateDirectory, scope, TestContext.Current.CancellationToken);
            ConcurrentQueue<string> timeline = new();
            FakeMessageSession session = new() { Timeline = timeline };
            FakeRunnerProvider provider = new(scope, timeline);
            FakeJitConfigClient jit = new((_, setting, _) =>
            {
                timeline.Enqueue("jit");
                return Task.FromResult<RunnerScaleSetJitRunnerConfig?>(new RunnerScaleSetJitRunnerConfig
                {
                    Runner = new RunnerReference { Id = 12000 + Interlocked.Increment(ref _nextRunnerId), Name = setting.Name },
                    EncodedJitConfig = $"synthetic-jit-{setting.Name}",
                });
            });
            ScaleSetRunnerProvisioner provisioner = new(scope.ScaleSetId, "_work", jit, provider, journal);
            RunnerLifecycleManager lifecycle = new(journal, TimeProvider.System, NullLogger<RunnerLifecycleManager>.Instance);
            ScaleSetMessageHandler handler = new(scope, session, provisioner, lifecycle, journal, capacity ?? CreateCapacityContext(scope));
            return new Fixture(journal, session, provider, jit, lifecycle, provisioner, handler, timeline);
        }

        public async ValueTask DisposeAsync()
        {
            Lifecycle.Dispose();
            Provisioner.Dispose();
            await Journal.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static int _nextRunnerId;

    private sealed class FakeJitConfigClient(
        Func<long, RunnerScaleSetJitRunnerSetting, CancellationToken, Task<RunnerScaleSetJitRunnerConfig?>> generate) : IRunnerJitConfigClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
            long scaleSetId,
            RunnerScaleSetJitRunnerSetting runnerSetting,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return generate(scaleSetId, runnerSetting, cancellationToken);
        }
    }

    private sealed class FakeMessageSession(RunnerScaleSetMessage? queuedMessage = null) : IMessageSessionClient
    {
        private int _acquireCount;
        private int _deleteCount;
        private int _messageRead;

        public RunnerScaleSetSession Session { get; } = new()
        {
            SessionId = Guid.NewGuid(),
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 0 },
        };

        public Func<IReadOnlyList<long>, CancellationToken, Task<IReadOnlyList<long>>> Acquire { get; set; } =
            (offered, _) => Task.FromResult<IReadOnlyList<long>>(offered.ToArray());

        public ConcurrentQueue<string>? Timeline { get; init; }

        public int AcquireCount => Volatile.Read(ref _acquireCount);

        public int DeleteCount => Volatile.Read(ref _deleteCount);

        public Task<RunnerScaleSetMessage?> GetMessageAsync(long lastMessageId, int maxCapacity, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Interlocked.Exchange(ref _messageRead, 1) == 0 ? queuedMessage : null);
        }

        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _deleteCount);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<long>> AcquireJobsAsync(IReadOnlyList<long> runnerRequestIds, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _acquireCount);
            Timeline?.Enqueue("acquire");
            return Acquire(runnerRequestIds, cancellationToken);
        }
    }

    private sealed class FakeRunnerProvider(RunnerOwnershipScope expectedScope, ConcurrentQueue<string>? timeline = null) : IRunnerProvider
    {
        private readonly RunnerOwnershipScope _scope = expectedScope;
        private readonly ConcurrentDictionary<Guid, RunnerResource> _resources = new();
        private int _createCount;
        private int _startCount;

        public RunnerProviderCapabilities Capabilities { get; } = new(
            "fake",
            ImmutableArray.Create(RunnerArchitecture.X64),
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: true);

        public List<Guid> RemovedOperations { get; } = [];

        public int CreateCount => Volatile.Read(ref _createCount);

        public int StartCount => Volatile.Read(ref _startCount);

        public Task<ImmutableArray<RunnerResource>> DiscoverAsync(RunnerOwnershipScope scope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeline?.Enqueue("discover");
            Assert.Equal(_scope, scope);
            return Task.FromResult(_resources.Values
                .Where(resource => resource.Identity.Scope == _scope)
                .ToImmutableArray());
        }

        public Task<RunnerResource> CreateAsync(RunnerCreateRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _createCount);
            timeline?.Enqueue("create");
            RunnerResource resource = new(
                $"container-{request.Identity.OperationId:N}",
                request.Identity,
                RunnerResourceStatus.Created);
            _resources[request.Identity.OperationId] = resource;
            return Task.FromResult(resource);
        }

        public Task<RunnerResource> StartAsync(RunnerResource resource, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _startCount);
            timeline?.Enqueue("start");
            RunnerResource started = new(resource.ProviderResourceId, resource.Identity, RunnerResourceStatus.Running);
            _resources[resource.Identity.OperationId] = started;
            return Task.FromResult(started);
        }

        public Task<RunnerResource?> InspectAsync(RunnerResource resource, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeline?.Enqueue("inspect");
            _resources.TryGetValue(resource.Identity.OperationId, out RunnerResource? found);
            return Task.FromResult(found is not null && found.ProviderResourceId == resource.ProviderResourceId ? found : null);
        }

        public void MarkExited(Guid operationId)
        {
            RunnerResource running = _resources[operationId];
            _resources[operationId] = new RunnerResource(
                running.ProviderResourceId, running.Identity, RunnerResourceStatus.Exited, exitCode: 0);
        }

        public Task RemoveAsync(RunnerResource resource, bool force, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(force);
            Assert.True(_resources.TryRemove(resource.Identity.OperationId, out RunnerResource? removed));
            Assert.Equal(RunnerResourceStatus.Exited, removed.Status);
            RemovedOperations.Add(resource.Identity.OperationId);
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryStateDirectory : IDisposable
    {
        public TemporaryStateDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"scaleset-message-handler-{Guid.NewGuid():N}");
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
