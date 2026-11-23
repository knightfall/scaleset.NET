using System.Net;
using System.Text.Json;
using ScaleSet.Client;
using Xunit;

namespace ScaleSet.Listener.Tests;

public sealed class ListenerStepEngineTests
{
    [Fact]
    public async Task InitializesBeforePollingThenDistinguishesEmptyZeroAndLargeIds()
    {
        var h = new EngineFixture();
        const long large = (long)int.MaxValue + 42;
        h.Session.Messages.Enqueue(null);
        h.Session.Messages.Enqueue(new() { MessageId = 0 });
        h.Session.Messages.Enqueue(new() { MessageId = large });
        h.Session.Messages.Enqueue(new() { MessageId = 0, Statistics = new() { TotalAssignedJobs = 9 } });
        h.Session.Messages.Enqueue(null);
        var engine = h.Create();

        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await Step(engine)).Status);
        Assert.Equal(["read", "write:1", "handle:InitialStatistics:", "write:2"], h.Calls);
        Assert.Null(h.Store.Saved!.LastAcknowledgedMessageId);
        Assert.Null(h.Deliveries[0].Message);
        Assert.Equal(4, h.Deliveries[0].InitialStatistics!.TotalAssignedJobs);

        Assert.Equal(ScaleSetListenerStepStatus.EmptyPoll, (await Step(engine)).Status);
        Assert.Null(h.Deliveries[1].Message);
        Assert.Null(h.Deliveries[1].InitialStatistics);
        Assert.Equal(2, h.Store.Writes.Count);

        Assert.Equal(new(ScaleSetListenerStepStatus.MessageAcknowledged, 0), await Step(engine));
        Assert.Equal(0, h.Store.Saved!.LastAcknowledgedMessageId);
        Assert.Equal(new(ScaleSetListenerStepStatus.MessageAcknowledged, large), await Step(engine));
        Assert.Equal(new(ScaleSetListenerStepStatus.MessageAcknowledged, 0), await Step(engine));
        Assert.Equal(9, h.Deliveries[4].Message!.Statistics!.TotalAssignedJobs);
        Assert.Equal(ScaleSetListenerStepStatus.EmptyPoll, (await Step(engine)).Status);
        Assert.Equal([0, 0, 0, large, large], h.Session.Polls.Select(poll => poll.Cursor));
        Assert.Equal([0, large, 0], h.Session.Deletes);
        Assert.Equal(14, h.Store.Writes.Count);
        Assert.Null(h.Store.Saved!.Pending);
        Assert.Equal(large, h.Store.Saved.LastAcknowledgedMessageId);
    }

    [Fact]
    public async Task RestoredReceiptPreservesObservationAndDoesNotRepeatLedgeredAcquisition()
    {
        var h = new EngineFixture();
        h.Initialize();
        var labels = new List<string> { "linux" };
        var jobs = new List<JobAvailableMessage>
        {
            new() { RunnerRequestId = 17, RequestLabels = labels, AcquireJobUrl = "https://example.invalid/?sig=secret" },
        };
        h.Session.Messages.Enqueue(new() { MessageId = 71, JobAvailableMessages = jobs });
        h.LedgerHandler();
        h.Store.NextWrite = next =>
        {
            if (next.Pending?.Phase == ScaleSetListenerPendingPhase.Processed)
            {
                throw new IOException("processing commit interrupted");
            }

            return h.Store.Commit(next);
        };
        var observedAt = h.Clock.UtcNow;
        await Assert.ThrowsAsync<IOException>(() => Step(h.Create()));
        Assert.Equal(3, h.Store.Saved!.Revision);
        Assert.Equal(ScaleSetListenerPendingPhase.Received, h.Store.Saved.Pending!.Phase);
        Assert.Empty(h.Session.Deletes);
        Assert.Equal(17, Assert.Single(h.Session.Acquisitions));
        Assert.False(Assert.Single(h.Deliveries).IsReplay);

        labels[0] = "mutated";
        jobs.Clear();
        string serialized = JsonSerializer.Serialize(h.Store.Saved);
        Assert.DoesNotContain("sig=secret", serialized, StringComparison.Ordinal);
        h.Store.Saved = JsonSerializer.Deserialize<ScaleSetListenerCheckpoint>(serialized);
        h.Clock.UtcNow = observedAt.AddHours(1);
        h.Store.NextWrite = null;

        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await Step(h.Create())).Status);
        Assert.Single(h.Session.Polls);
        Assert.Equal(71, Assert.Single(h.Session.Deletes));
        Assert.Equal(17, Assert.Single(h.Session.Acquisitions));
        Assert.Equal(2, h.Deliveries.Count);
        Assert.True(h.Deliveries[1].IsReplay);
        Assert.Equal(observedAt, h.Deliveries[1].ObservedAt);
        var available = Assert.Single(h.Deliveries[1].Message!.JobAvailableMessages);
        Assert.Equal("linux", Assert.Single(available.RequestLabels!));
        Assert.Empty(available.AcquireJobUrl);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)available.RequestLabels!)[0] = "changed");
        Assert.Equal([3, 4, 4, 5, 6], h.Store.Writes.Select(write => write.Revision));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptWriteFailureBlocksEffectsAndUsesExactReadback(bool committedBeforeFailure)
    {
        var h = new EngineFixture();
        h.Initialize();
        h.Session.Messages.Enqueue(new() { MessageId = 27 });
        var engine = h.Create();
        h.Store.NextWrite = next =>
        {
            if (committedBeforeFailure)
            {
                _ = h.Store.Commit(next);
            }

            throw new IOException("receipt response lost");
        };

        await Assert.ThrowsAsync<IOException>(() => Step(engine));
        Assert.Empty(h.Deliveries);
        Assert.Empty(h.Session.Deletes);
        Assert.Equal(["read", "poll:0", "write:3"], h.Calls);

        var attempted = Assert.Single(h.Store.Writes);
        h.Store.NextWrite = null;
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await Step(engine)).Status);
        Assert.Single(h.Session.Polls);
        Assert.True(Assert.Single(h.Deliveries).IsReplay);
        Assert.Equal(27, Assert.Single(h.Session.Deletes));
        Assert.Equal(committedBeforeFailure ? 4 : 5, h.Store.Writes.Count);
        if (!committedBeforeFailure)
        {
            Assert.Same(attempted, h.Store.Writes[1]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedIntentWriteBlocksDeleteAndReconstructionUsesDurablePhase(bool committedBeforeFailure)
    {
        var h = new EngineFixture();
        h.Seed(ScaleSetListenerPendingPhase.Processed, messageId: 88);
        h.Store.NextWrite = next =>
        {
            if (committedBeforeFailure)
            {
                _ = h.Store.Commit(next);
            }

            throw new IOException("intent response lost");
        };

        await Assert.ThrowsAsync<IOException>(() => Step(h.Create()));
        Assert.Equal(["read", "write:5"], h.Calls);
        Assert.Empty(h.Session.Deletes);
        h.Store.NextWrite = null;

        var result = await Step(h.Create());
        Assert.Equal(committedBeforeFailure ? ScaleSetListenerStepStatus.RecoveryRequired :
            ScaleSetListenerStepStatus.MessageAcknowledged, result.Status);
        Assert.Equal(committedBeforeFailure ? 0 : 1, h.Session.Deletes.Count);
        Assert.Equal(committedBeforeFailure ? 1 : 3, h.Store.Writes.Count);
        Assert.Empty(h.Deliveries);
        Assert.Empty(h.Session.Polls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteSuccessAndStorageFailureCanOnlyRetrySettlement(bool committedBeforeFailure)
    {
        var h = new EngineFixture();
        h.Seed(ScaleSetListenerPendingPhase.Processed, messageId: 51);
        var engine = h.Create();
        h.Store.NextWrite = next =>
        {
            if (next.Pending is not null)
            {
                return h.Store.Commit(next);
            }

            if (committedBeforeFailure)
            {
                _ = h.Store.Commit(next);
            }

            throw new IOException("settlement response lost");
        };

        await Assert.ThrowsAsync<IOException>(() => Step(engine));
        Assert.Equal(["read", "write:5", "delete:51", "write:6"], h.Calls);
        h.Store.NextWrite = null;

        if (!committedBeforeFailure)
        {
            var restored = await Step(h.Create());
            Assert.Equal(ScaleSetListenerStepStatus.RecoveryRequired, restored.Status);
            Assert.Null(h.Store.Saved!.LastAcknowledgedMessageId);
            Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, h.Store.Saved.Pending!.Phase);
        }

        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await Step(engine)).Status);
        Assert.Equal(51, Assert.Single(h.Session.Deletes));
        Assert.Equal(committedBeforeFailure ? 2 : 3, h.Store.Writes.Count);
        Assert.Empty(h.Session.Polls);
        Assert.Empty(h.Deliveries);
        Assert.Equal(51, h.Store.Saved!.LastAcknowledgedMessageId);
        Assert.Null(h.Store.Saved.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainDeletePropagatesOriginalErrorAndBlocksAllQueueWork(bool notFound)
    {
        var h = new EngineFixture();
        h.Seed(ScaleSetListenerPendingPhase.Processed, messageId: 38);
        var engine = h.Create();
        Exception failure = notFound
            ? new HttpRequestException("not found", null, HttpStatusCode.NotFound)
            : new IOException("response lost");
        h.Session.OnDelete = (_, _) => Task.FromException(failure);

        Assert.Same(failure, await Record.ExceptionAsync(() => Step(engine)));
        Assert.Equal(["read", "write:5", "delete:38"], h.Calls);
        Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, h.Store.Saved!.Pending!.Phase);
        Assert.Equal(new(ScaleSetListenerStepStatus.RecoveryRequired, 38, ScaleSetListenerPendingPhase.Acknowledging),
            await Step(engine));
        Assert.Equal(ScaleSetListenerStepStatus.RecoveryRequired, (await Step(h.Create())).Status);
        Assert.Equal(38, Assert.Single(h.Session.Deletes));
        Assert.Empty(h.Session.Polls);
        Assert.Empty(h.Deliveries);
        Assert.Single(h.Store.Writes);
        Assert.Null(h.Store.Saved.LastAcknowledgedMessageId);
    }

    [Fact]
    public async Task ConditionalConflictRetiresPumpAndDoesNotOverwriteOrProcess()
    {
        var h = new EngineFixture();
        h.Initialize();
        h.Session.Messages.Enqueue(new() { MessageId = 4 });
        h.Store.NextWrite = _ => ScaleSetListenerCheckpointWriteResult.Conflict;
        var engine = h.Create();

        Assert.Equal(ScaleSetListenerStepStatus.CheckpointConflict, (await Step(engine)).Status);
        Assert.Equal(ScaleSetListenerStepStatus.CheckpointConflict, (await Step(engine)).Status);
        Assert.Equal(["read", "poll:0", "write:3"], h.Calls);
        Assert.Null(h.Store.Saved!.Pending);
        Assert.Empty(h.Deliveries);
        Assert.Empty(h.Session.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadbackRejectsAnotherTransitionOrAlteredPayload(bool sameTransition)
    {
        var h = new EngineFixture();
        h.Initialize();
        h.Session.Messages.Enqueue(new() { MessageId = 4 });
        var engine = h.Create();
        h.Store.NextWrite = _ => throw new IOException("write response lost");

        await Assert.ThrowsAsync<IOException>(() => Step(engine));
        var intended = Assert.Single(h.Store.Writes);
        var different = new ScaleSetListenerDelivery(h.Key, ScaleSetListenerDeliveryKind.Message, h.Clock.UtcNow,
            new RunnerScaleSetMessage { MessageId = 5 });
        h.Store.Saved = intended with
        {
            TransitionId = sameTransition ? intended.TransitionId : Guid.NewGuid(),
            Pending = new(different, ScaleSetListenerPendingPhase.Received),
        };

        Assert.Equal(ScaleSetListenerStepStatus.CheckpointConflict, (await Step(engine)).Status);
        Assert.Equal(["read", "poll:0", "write:3", "read"], h.Calls);
        Assert.Empty(h.Deliveries);
        Assert.Empty(h.Session.Deletes);
    }

    [Fact]
    public async Task InitialDeferralRetainsOriginalStatisticsAndNeverPollsPastIt()
    {
        var h = new EngineFixture();
        h.Handler = (_, _) => Task.FromResult(ScaleSetListenerDeliveryDisposition.Deferred);
        Assert.Equal(ScaleSetListenerStepStatus.Deferred, (await Step(h.Create())).Status);
        Assert.Equal(["read", "write:1", "handle:InitialStatistics:"], h.Calls);
        Assert.False(h.Store.Saved!.InitialStatisticsCompleted);

        h.Session.Session = h.Session.Session with { Statistics = new() { TotalAssignedJobs = 100 } };
        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(1);
        h.Handler = null;
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await Step(h.Create())).Status);
        Assert.True(h.Deliveries[1].IsReplay);
        Assert.Equal(h.Deliveries[0].ObservedAt, h.Deliveries[1].ObservedAt);
        Assert.Equal(4, h.Deliveries[1].InitialStatistics!.TotalAssignedJobs);
        Assert.Empty(h.Session.Polls);
        Assert.Empty(h.Session.Deletes);
        Assert.Equal(2, h.Store.Writes.Count);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("scope")]
    [InlineData("set")]
    [InlineData("session")]
    [InlineData("revision")]
    [InlineData("transition")]
    [InlineData("cursor")]
    [InlineData("phase")]
    [InlineData("uninitialized")]
    public async Task InvalidCheckpointStopsAfterRead(string invalidField)
    {
        var h = new EngineFixture();
        h.Seed(ScaleSetListenerPendingPhase.Processed, messageId: 8);
        var saved = h.Store.Saved!;
        h.Store.Saved = invalidField switch
        {
            "schema" => saved with { SchemaVersion = 2 },
            "scope" => saved with { SessionKey = h.Key with { Scope = "other" } },
            "set" => saved with { SessionKey = h.Key with { ScaleSetId = 2 } },
            "session" => saved with { SessionKey = h.Key with { SessionId = Guid.NewGuid() } },
            "revision" => saved with { Revision = 1 },
            "transition" => saved with { TransitionId = Guid.Empty },
            "cursor" => saved with { LastAcknowledgedMessageId = -1 },
            "phase" => saved with { Pending = saved.Pending! with { Phase = (ScaleSetListenerPendingPhase)99 } },
            _ => saved with { InitialStatisticsCompleted = false },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => Step(h.Create()));
        Assert.Equal(["read"], h.Calls);
        Assert.Empty(h.Session.Polls);
        Assert.Empty(h.Session.Deletes);
        Assert.Empty(h.Deliveries);
    }

    [Fact]
    public async Task OversizedQueuePayloadStopsBeforePersistenceAndPreventsAnotherPoll()
    {
        var h = new EngineFixture();
        h.Initialize();
        h.Session.Messages.Enqueue(new()
        {
            MessageId = 64,
            JobAvailableMessages = [new() { JobDisplayName = new('a', 1048577) }],
        });

        var engine = h.Create();
        await Assert.ThrowsAsync<InvalidDataException>(() => Step(engine));
        await Assert.ThrowsAsync<InvalidDataException>(() => Step(engine));
        Assert.Equal(["read", "poll:0"], h.Calls);
        Assert.Empty(h.Session.Deletes);
        Assert.Empty(h.Deliveries);
    }

    [Fact(Timeout = 10000)]
    public async Task ConcurrentAndReentrantStepsDoNotDisturbTheActiveStep()
    {
        var h = new EngineFixture();
        h.Initialize();
        h.Session.Messages.Enqueue(new() { MessageId = 63 });
        var engine = h.Create();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Handler = async (_, _) =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Step(engine));
            start.SetResult();
            await release.Task;
            return ScaleSetListenerDeliveryDisposition.Completed;
        };

        var active = Step(engine);
        await start.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Step(engine));
        Assert.Equal(["read", "poll:0", "write:3", "handle:Message:63"], h.Calls);
        release.SetResult();
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await active).Status);
        Assert.Single(h.Session.Polls);
        Assert.Equal(63, Assert.Single(h.Session.Deletes));
    }

    private static Task<ScaleSetListenerStepResult> Step(ListenerStepEngine engine) =>
        engine.StepAsync(TestContext.Current.CancellationToken);
}

internal sealed class EngineFixture
{
    internal EngineFixture()
    {
        Session = new(Calls);
        Key = new("github.com/org/repo", 1, Session.Session.SessionId);
        Store = new(Calls);
        Options = new(Key, CancellationToken.None);
    }

    internal List<string> Calls { get; } = [];
    internal List<ScaleSetListenerDelivery> Deliveries { get; } = [];
    internal MutableClock Clock { get; } = new();
    internal ScriptedCheckpointStore Store { get; }
    internal ScriptedQueue Session { get; }
    internal ScaleSetListenerSessionKey Key { get; }
    internal ScaleSetListenerPumpOptions Options { get; set; }
    internal Func<ScaleSetListenerDelivery, CancellationToken, Task<ScaleSetListenerDeliveryDisposition>>? Handler { get; set; }

    internal ListenerStepEngine Create() => new(Session, Options, Store, new FixtureHandler(this),
        new ImmediateOperations(new FixturePolicy(Clock), Options.OwnershipLost), Clock);

    internal void Initialize() => Store.Saved = new(Key, 1, 2, Guid.NewGuid(), true, null, null);

    internal void Seed(ScaleSetListenerPendingPhase phase, long messageId)
    {
        var delivery = new ScaleSetListenerDelivery(Key, ScaleSetListenerDeliveryKind.Message, Clock.UtcNow,
            new RunnerScaleSetMessage { MessageId = messageId });
        Store.Saved = new(Key, 1, 3 + (int)phase, Guid.NewGuid(), true, null, new(delivery, phase));
    }

    internal void LedgerHandler()
    {
        var ledger = new HashSet<long>();
        Handler = async (delivery, token) =>
        {
            foreach (var job in delivery.Message!.JobAvailableMessages)
            {
                if (ledger.Add(job.RunnerRequestId))
                {
                    await Session.AcquireJobsAsync([job.RunnerRequestId], token);
                }
            }

            return ScaleSetListenerDeliveryDisposition.Completed;
        };
    }

    internal sealed class FixtureHandler(EngineFixture fixture) : IScaleSetListenerDeliveryHandler
    {
        public Task<ScaleSetListenerDeliveryDisposition> HandleAsync(ScaleSetListenerDelivery delivery, CancellationToken cancellationToken)
        {
            fixture.Calls.Add($"handle:{delivery.Kind}:{delivery.Message?.MessageId}");
            fixture.Deliveries.Add(delivery);
            return fixture.Handler?.Invoke(delivery, cancellationToken) ?? Task.FromResult(ScaleSetListenerDeliveryDisposition.Completed);
        }
    }

    internal sealed class MutableClock : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    internal sealed class FixturePolicy(MutableClock clock) : IScaleSetListenerExecutionPolicy
    {
        public Task<ScaleSetListenerAuthorization> AuthorizeAsync(
            ScaleSetListenerOperationContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ScaleSetListenerAuthorization.Allow(clock.UtcNow.AddMinutes(1),
                context.Operation == ScaleSetListenerOperation.Poll ? 3 : null, CancellationToken.None));
    }

    private sealed class ImmediateOperations(IScaleSetListenerExecutionPolicy policy, CancellationToken owner) : IListenerOperations
    {
        public IListenerOperationBudget StartBudget(TimeSpan timeout, CancellationToken stoppingToken) =>
            new Budget(policy, owner, stoppingToken);

        private sealed class Budget(IScaleSetListenerExecutionPolicy policy, CancellationToken owner, CancellationToken stop)
            : IListenerOperationBudget
        {
            public async Task<T> RunAsync<T>(
                ScaleSetListenerOperationContext context,
                Func<ScaleSetListenerAuthorization, CancellationToken, Task<T>> operation,
                Func<T, bool>? finishOnStop = null)
            {
                owner.ThrowIfCancellationRequested();
                var permit = await policy.AuthorizeAsync(context, stop);
                return await operation(permit, stop);
            }

            public void Dispose()
            {
            }
        }
    }

    internal sealed class ScriptedCheckpointStore(List<string> calls) : IScaleSetListenerCheckpointStore
    {
        internal ScaleSetListenerCheckpoint? Saved { get; set; }
        internal List<ScaleSetListenerCheckpoint> Writes { get; } = [];
        internal Func<ScaleSetListenerCheckpoint, ScaleSetListenerCheckpointWriteResult>? NextWrite { get; set; }

        public Task<ScaleSetListenerCheckpoint?> ReadAsync(ScaleSetListenerSessionKey key, CancellationToken cancellationToken)
        {
            calls.Add("read");
            return Task.FromResult(Saved);
        }

        public Task<ScaleSetListenerCheckpointWriteResult> WriteAsync(
            ScaleSetListenerSessionKey key, long? expectedRevision,
            ScaleSetListenerCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            calls.Add($"write:{checkpoint.Revision}");
            Writes.Add(checkpoint);
            return Task.FromResult(Saved?.Revision != expectedRevision
                ? ScaleSetListenerCheckpointWriteResult.Conflict
                : NextWrite?.Invoke(checkpoint) ?? Commit(checkpoint));
        }

        internal ScaleSetListenerCheckpointWriteResult Commit(ScaleSetListenerCheckpoint checkpoint)
        {
            Saved = checkpoint;
            return ScaleSetListenerCheckpointWriteResult.Committed;
        }
    }

    internal sealed class ScriptedQueue(List<string> calls) : IMessageSession
    {
        public RunnerScaleSetSession Session { get; set; } = new()
        {
            SessionId = Guid.NewGuid(),
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 4 },
        };

        internal Queue<RunnerScaleSetMessage?> Messages { get; } = new();
        internal List<(long Cursor, int Capacity)> Polls { get; } = [];
        internal List<long> Deletes { get; } = [];
        internal List<long> Acquisitions { get; } = [];
        internal Func<long, CancellationToken, Task>? OnDelete { get; set; }
        internal Func<CancellationToken, Task<RunnerScaleSetMessage?>>? OnPoll { get; set; }

        public Task<RunnerScaleSetMessage?> GetMessageAsync(long lastMessageId, int maxCapacity, CancellationToken cancellationToken)
        {
            Polls.Add((lastMessageId, maxCapacity));
            calls.Add($"poll:{lastMessageId}");
            return OnPoll?.Invoke(cancellationToken) ?? Task.FromResult(Messages.Dequeue());
        }

        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken)
        {
            Deletes.Add(messageId);
            calls.Add($"delete:{messageId}");
            return OnDelete?.Invoke(messageId, cancellationToken) ?? Task.CompletedTask;
        }

        public Task<IReadOnlyList<long>> AcquireJobsAsync(IReadOnlyList<long> runnerRequestIds, CancellationToken cancellationToken)
        {
            Acquisitions.AddRange(runnerRequestIds);
            return Task.FromResult(runnerRequestIds);
        }

        public Task CloseAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The pump must leave session closure to its caller.");
    }
}
