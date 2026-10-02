using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Client;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Controller;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;
using ScaleSet.Listener;

namespace ScaleSet.Hosting.Tests.Controller;

public sealed class ScaleSetMessageHandlerTests
{
    [Fact]
    public async Task HandleAsync_AcquiresBeforeProvisioningAndCreatesOnlyConfirmedSubset()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        await using Fixture fixture = await Fixture.CreateAsync(directory.Path, scope);
        fixture.Session.Acquire = (offered, _) =>
        {
            fixture.Timeline.Enqueue("acquire");
            Assert.Equal([101L, 102L, 103L], offered);
            return Task.FromResult<IReadOnlyList<long>>([102, 999, 102]);
        };

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 4 },
            JobAvailableMessages =
            [
                new JobAvailableMessage { RunnerRequestId = 101 },
                new JobAvailableMessage { RunnerRequestId = 102 },
                new JobAvailableMessage { RunnerRequestId = 103 },
            ],
        }, TestContext.Current.CancellationToken);

        RunnerIdentity expected = RunnerRequestIdentityFactory.Create(scope, 102);
        RunnerCreateRequest created = Assert.Single(fixture.Provider.CreateRequests);
        Assert.Equal(expected.OperationId, created.Identity.OperationId);
        Assert.Equal(expected.RunnerName, created.Identity.RunnerName);
        Assert.Equal(1, fixture.Jit.CallCount);
        Assert.Equal(1, fixture.Provider.StartCount);
        Assert.Equal(4, fixture.Handler.LatestAssignedJobs);
        Assert.Equal("acquire", fixture.Timeline.First());
        Assert.True(Array.IndexOf(fixture.Timeline.ToArray(), "acquire") < Array.IndexOf(fixture.Timeline.ToArray(), "jit"));
    }

    [Fact]
    public async Task HandleAsync_RedeliveredAcquiredRequestUsesSameDurableOperation()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        await using Fixture fixture = await Fixture.CreateAsync(directory.Path, scope);
        fixture.Session.Acquire = (offered, _) => Task.FromResult<IReadOnlyList<long>>(offered.ToArray());
        RunnerScaleSetMessage message = new()
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = 777 }],
        };

        await fixture.Handler.HandleAsync(message, TestContext.Current.CancellationToken);
        RunnerIdentity expected = RunnerRequestIdentityFactory.Create(scope, 777);
        RunnerOperationState first = Assert.IsType<RunnerOperationState>(
            await fixture.Journal.FindAsync(expected.OperationId, TestContext.Current.CancellationToken));
        await fixture.Handler.HandleAsync(message, TestContext.Current.CancellationToken);
        RunnerOperationState replayed = Assert.IsType<RunnerOperationState>(
            await fixture.Journal.FindAsync(expected.OperationId, TestContext.Current.CancellationToken));

        Assert.Equal(2, fixture.Session.AcquireCount);
        Assert.Equal(1, fixture.Jit.CallCount);
        Assert.Equal(1, fixture.Provider.CreateCount);
        Assert.Equal(1, fixture.Provider.StartCount);
        Assert.Equal(expected.OperationId, first.Identity.OperationId);
        Assert.Equal(first.Identity.OperationId, replayed.Identity.OperationId);
        Assert.Equal(expected.RunnerName, replayed.Identity.RunnerName);
        Assert.Equal(RunnerLifecycleStage.Unknown, replayed.Stage);
    }

    [Fact]
    public async Task HandleAsync_ActivityUsesRequestAndRunnerCorrelationAndIgnoresConflicts()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        await using Fixture fixture = await Fixture.CreateAsync(directory.Path, scope);
        fixture.Session.Acquire = (offered, _) => Task.FromResult<IReadOnlyList<long>>(offered.ToArray());
        const long requestId = 501;
        RunnerIdentity expected = RunnerRequestIdentityFactory.Create(scope, requestId);
        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
            JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = requestId }],
        }, TestContext.Current.CancellationToken);
        RunnerOperationState provisioned = Assert.IsType<RunnerOperationState>(
            await fixture.Journal.FindAsync(expected.OperationId, TestContext.Current.CancellationToken));
        long runnerId = Assert.IsType<long>(provisioned.Identity.GitHubRunnerId);

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobAssignedMessages = [new JobAssignedMessage { RunnerRequestId = requestId }],
        }, TestContext.Current.CancellationToken);
        RunnerOperationState assigned = Assert.IsType<RunnerOperationState>(
            await fixture.Journal.FindAsync(expected.OperationId, TestContext.Current.CancellationToken));
        Assert.Equal(RunnerLifecycleStage.Busy, assigned.Stage);

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobStartedMessages =
            [
                new JobStartedMessage { RunnerRequestId = requestId, RunnerId = runnerId + 1, RunnerName = expected.RunnerName },
            ],
        }, TestContext.Current.CancellationToken);
        RunnerOperationState afterConflict = Assert.IsType<RunnerOperationState>(
            await fixture.Journal.FindAsync(expected.OperationId, TestContext.Current.CancellationToken));
        Assert.Equal(RunnerLifecycleStage.Busy, afterConflict.Stage);

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobStartedMessages =
            [
                new JobStartedMessage { RunnerRequestId = requestId, RunnerId = runnerId, RunnerName = expected.RunnerName },
            ],
        }, TestContext.Current.CancellationToken);
        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobCompletedMessages =
            [
                new JobCompletedMessage { RunnerRequestId = requestId, RunnerId = runnerId, RunnerName = expected.RunnerName },
            ],
        }, TestContext.Current.CancellationToken);
        RunnerOperationState completed = Assert.IsType<RunnerOperationState>(
            await fixture.Journal.FindAsync(expected.OperationId, TestContext.Current.CancellationToken));

        await fixture.Handler.HandleAsync(new RunnerScaleSetMessage
        {
            JobAssignedMessages = [new JobAssignedMessage { RunnerRequestId = 9001 }],
        }, TestContext.Current.CancellationToken);

        Assert.Equal(RunnerLifecycleStage.Completed, completed.Stage);
        Assert.Null(await fixture.Journal.FindAsync(
            RunnerRequestIdentityFactory.Create(scope, 9001).OperationId,
            TestContext.Current.CancellationToken));
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
        ScaleSetMessageHandler handler = new(scope, session, provisioner, lifecycle, journal);
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
        RunnerOperationState uncertain = Assert.IsType<RunnerOperationState>(await journal.FindAsync(
            RunnerRequestIdentityFactory.Create(scope, 708).OperationId,
            TestContext.Current.CancellationToken));
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

        public static async Task<Fixture> CreateAsync(string stateDirectory, RunnerOwnershipScope scope)
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
            ScaleSetMessageHandler handler = new(scope, session, provisioner, lifecycle, journal);
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

        public List<RunnerCreateRequest> CreateRequests { get; } = [];

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
            CreateRequests.Add(request);
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

        public Task RemoveAsync(RunnerResource resource, bool force, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
