using System.Diagnostics;
using ScaleSet.Client;
using Xunit;

namespace ScaleSet.Listener.Tests;

public sealed class ListenerPumpAuthorizationTests
{
    [Fact]
    public async Task PollDeferralSharesCooldownAcrossPumpsWithoutChangingEitherCheckpoint()
    {
        var first = new EngineFixture();
        var second = new EngineFixture();
        first.Initialize();
        second.Initialize();
        var clock = new ListenerTestClock();
        var until = clock.GetUtcNow().AddSeconds(30);
        var policy = new OperationPolicy(clock)
        {
            Decide = (context, _) => context.Operation == ScaleSetListenerOperation.Poll && clock.GetUtcNow() < until
                ? ScaleSetListenerAuthorization.Defer(until)
                : null,
        };
        var firstPump = Create(first, clock, policy);
        var secondPump = Create(second, clock, policy);

        Assert.Equal(new(ScaleSetListenerStepStatus.Deferred, RetryAt: until), await Step(firstPump));
        Assert.Equal(new(ScaleSetListenerStepStatus.Deferred, RetryAt: until), await Step(secondPump));
        Assert.Equal(["read"], first.Calls);
        Assert.Equal(["read"], second.Calls);
        Assert.Equal(2, first.Store.Saved!.Revision);
        Assert.Equal(2, second.Store.Saved!.Revision);

        clock.Advance(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        first.Session.Messages.Enqueue(null);
        Assert.Equal(ScaleSetListenerStepStatus.EmptyPoll, (await Step(firstPump)).Status);
        Assert.Single(first.Session.Polls);
        Assert.Empty(second.Session.Polls);
        Assert.Empty(second.Session.Deletes);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Theory]
    [InlineData(ScaleSetListenerOperation.HandleDelivery, ScaleSetListenerPendingPhase.Received, 0, 1)]
    [InlineData(ScaleSetListenerOperation.PersistAcknowledging, ScaleSetListenerPendingPhase.Processed, 0, 2)]
    [InlineData(ScaleSetListenerOperation.Acknowledge, ScaleSetListenerPendingPhase.Acknowledging, 0, 3)]
    [InlineData(ScaleSetListenerOperation.PersistSettled, ScaleSetListenerPendingPhase.Acknowledging, 1, 3)]
    public async Task DeferredOperationsPreserveDurablePhaseAndResumeOnlyAllowedEffects(
        ScaleSetListenerOperation deferred,
        ScaleSetListenerPendingPhase phase,
        int sentBeforeDeferral,
        int writesBeforeDeferral)
    {
        var h = new EngineFixture();
        h.Initialize();
        h.Session.Messages.Enqueue(new() { MessageId = 40 });
        var clock = new ListenerTestClock();
        bool withholding = true;
        var policy = new OperationPolicy(clock)
        {
            Decide = (context, _) => withholding && context.Operation == deferred
                ? ScaleSetListenerAuthorization.Defer()
                : null,
        };
        var pump = Create(h, clock, policy);

        var decision = await Step(pump);
        Assert.Equal(new(ScaleSetListenerStepStatus.Deferred, 40, phase), decision);
        Assert.Equal(2 + writesBeforeDeferral, h.Store.Saved!.Revision);
        Assert.Equal(writesBeforeDeferral, h.Store.Writes.Count);
        Assert.Equal(sentBeforeDeferral, h.Session.Deletes.Count);
        Assert.Single(h.Session.Polls);

        withholding = false;
        if (phase == ScaleSetListenerPendingPhase.Acknowledging)
        {
            Assert.Equal(ScaleSetListenerStepStatus.RecoveryRequired, (await Step(Create(h, clock, policy))).Status);
            Assert.Equal(sentBeforeDeferral, h.Session.Deletes.Count);
        }

        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await Step(pump)).Status);
        Assert.Equal(40, Assert.Single(h.Session.Deletes));
        Assert.Single(h.Session.Polls);
        Assert.Single(h.Deliveries);
        Assert.Equal(4, h.Store.Writes.Count);
        Assert.Null(h.Store.Saved.Pending);
    }

    [Fact(Timeout = 10000)]
    public async Task OwnershipLossDuringPollCancelsTokenAndPreventsReceiptHandlingAndAck()
    {
        using var owner = new CancellationTokenSource();
        var h = new EngineFixture();
        h.Initialize();
        h.Options = h.Options with { OwnershipLost = owner.Token };
        var clock = new ListenerTestClock();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Session.OnPoll = async token =>
        {
            entered.SetResult(token);
            await release.Task;
            return new RunnerScaleSetMessage { MessageId = 70 };
        };
        var policy = new OperationPolicy(clock) { Owner = owner.Token };
        var pump = Create(h, clock, policy);
        var active = Step(pump);
        var operationToken = await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        owner.Cancel();
        Assert.True(operationToken.IsCancellationRequested);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Step(pump));
        Assert.Equal(["read", "poll:0"], h.Calls);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);

        policy.Owner = TestContext.Current.CancellationToken;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Step(pump));
        Assert.Equal(["read", "poll:0"], h.Calls);
        Assert.Empty(h.Session.Acquisitions);
        Assert.Empty(h.Session.Deletes);
        Assert.Empty(h.Store.Writes);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task FixedOwnerMustMatchAndIsCheckedBeforeAckIntentMutation()
    {
        using var owner = new CancellationTokenSource();
        using var nextOwner = new CancellationTokenSource();
        var h = new EngineFixture();
        h.Seed(ScaleSetListenerPendingPhase.Processed, 12);
        h.Options = h.Options with { OwnershipLost = owner.Token };
        var clock = new ListenerTestClock();
        var policy = new OperationPolicy(clock) { Owner = owner.Token };
        policy.Decide = (context, _) =>
        {
            if (context.Operation == ScaleSetListenerOperation.PersistAcknowledging)
            {
                return ScaleSetListenerAuthorization.Allow(clock.GetUtcNow().AddMinutes(1),
                    ownershipLost: nextOwner.Token);
            }

            return null;
        };
        var pump = Create(h, clock, policy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Step(pump));
        Assert.Equal(["read"], h.Calls);

        policy.Decide = (context, token) =>
        {
            if (context.Operation == ScaleSetListenerOperation.PersistAcknowledging)
            {
                owner.Cancel();
                Assert.True(token.IsCancellationRequested);
            }

            return null;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Step(pump));
        Assert.Equal(["read", "read"], h.Calls);
        Assert.Equal(ScaleSetListenerPendingPhase.Processed, h.Store.Saved!.Pending!.Phase);
        Assert.Empty(h.Session.Deletes);
    }

    [Fact]
    public async Task OwnershipLossAfterHandlerCompletionStopsAcknowledgement()
    {
        using var owner = new CancellationTokenSource();
        var h = new EngineFixture();
        h.Initialize();
        h.Session.Messages.Enqueue(new() { MessageId = 22 });
        h.Options = h.Options with { OwnershipLost = owner.Token };
        var clock = new ListenerTestClock();
        var policy = new OperationPolicy(clock)
        {
            Owner = owner.Token,
            Decide = (context, token) =>
            {
                if (context.Operation == ScaleSetListenerOperation.PersistAcknowledging)
                {
                    owner.Cancel();
                    Assert.True(token.IsCancellationRequested);
                }

                return null;
            },
        };
        var pump = Create(h, clock, policy);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Step(pump));
        Assert.Equal(["read", "poll:0", "write:3", "handle:Message:22", "write:4"], h.Calls);
        Assert.Equal(ScaleSetListenerPendingPhase.Processed, h.Store.Saved!.Pending!.Phase);
        Assert.Empty(h.Session.Deletes);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact(Timeout = 10000)]
    public async Task AuthorizationItselfConsumesTheOperationBudgetAndCannotOutliveTheStep()
    {
        var h = new EngineFixture();
        h.Options = h.Options with { CheckpointTimeout = TimeSpan.FromSeconds(1) };
        var clock = new ListenerTestClock();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new BlockingPolicy(async token =>
        {
            entered.SetResult(token);
            await release.Task;
            return ScaleSetListenerAuthorization.Allow(clock.GetUtcNow().AddMinutes(1), ownershipLost: CancellationToken.None);
        });
        var pump = Create(h, clock, policy);
        var active = Step(pump);
        var authorizationToken = await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1), TimeSpan.Zero);

        Assert.True(authorizationToken.IsCancellationRequested);
        Assert.False(active.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Step(pump));
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.Empty(h.Calls);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Theory]
    [InlineData(ScaleSetListenerDeliveryDisposition.Completed, true)]
    [InlineData(ScaleSetListenerDeliveryDisposition.Deferred, false)]
    public async Task GracefulStopAllowsOnlyCompletedHandlerToFinish(
        ScaleSetListenerDeliveryDisposition disposition, bool acknowledges)
    {
        using var stopping = new CancellationTokenSource();
        var h = new EngineFixture();
        h.Initialize();
        h.Session.Messages.Enqueue(new() { MessageId = 40 });
        var clock = new ListenerTestClock();
        h.Handler = (_, token) =>
        {
            stopping.Cancel();
            Assert.True(token.IsCancellationRequested);
            return Task.FromResult(disposition);
        };
        h.Session.OnDelete = (_, token) =>
        {
            Assert.False(token.IsCancellationRequested);
            return Task.CompletedTask;
        };

        var pump = Create(h, clock, new OperationPolicy(clock));
        if (acknowledges)
        {
            Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await pump.StepAsync(stopping.Token)).Status);
            Assert.Equal(["read", "poll:0", "write:3", "handle:Message:40", "write:4", "write:5", "delete:40", "write:6"], h.Calls);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.StepAsync(stopping.Token));
            Assert.Equal(["read", "poll:0", "write:3", "handle:Message:40"], h.Calls);
            Assert.Equal(ScaleSetListenerPendingPhase.Received, h.Store.Saved!.Pending!.Phase);
        }
    }

    [Fact(Timeout = 10000)]
    public async Task TimedOutHandlerRemainsSerializedUntilItActuallyReturns()
    {
        var h = new EngineFixture();
        h.Seed(ScaleSetListenerPendingPhase.Received, 3);
        h.Options = h.Options with { ProcessingTimeout = TimeSpan.FromSeconds(2) };
        var clock = new ListenerTestClock();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Handler = async (_, token) =>
        {
            entered.SetResult(token);
            await release.Task;
            return ScaleSetListenerDeliveryDisposition.Completed;
        };
        var pump = Create(h, clock, new OperationPolicy(clock));
        var active = Step(pump);
        var handlerToken = await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2), TimeSpan.FromHours(-1));

        Assert.True(handlerToken.IsCancellationRequested);
        Assert.False(active.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Step(pump));
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.Equal(["read", "handle:Message:3"], h.Calls);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task BackwardsWallClockDuringPolicyCannotExtendPermit()
    {
        var h = new EngineFixture();
        h.Initialize();
        var clock = new ListenerTestClock();
        var policy = new OperationPolicy(clock)
        {
            Decide = (context, _) =>
            {
                if (context.Operation != ScaleSetListenerOperation.Poll)
                {
                    return null;
                }

                var deadline = clock.GetUtcNow().AddSeconds(1);
                clock.Advance(TimeSpan.FromSeconds(2), TimeSpan.FromHours(-1));
                return ScaleSetListenerAuthorization.Allow(deadline, 2, CancellationToken.None);
            },
        };
        var pump = Create(h, clock, policy);

        await Assert.ThrowsAsync<TimeoutException>(() => Step(pump));
        Assert.Equal(["read"], h.Calls);
        Assert.Empty(h.Session.Polls);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task AcknowledgementSubstepsShareOneBudgetAndLateSuccessOnlyRetriesSettlement()
    {
        var h = new EngineFixture();
        h.Seed(ScaleSetListenerPendingPhase.Processed, 19);
        h.Options = h.Options with { AcknowledgmentTimeout = TimeSpan.FromSeconds(5) };
        var clock = new ListenerTestClock();
        var policy = new OperationPolicy(clock)
        {
            Decide = (context, _) =>
            {
                if (context.Operation != ScaleSetListenerOperation.PersistAcknowledging)
                {
                    return null;
                }

                var validUntil = clock.GetUtcNow().AddMinutes(1);
                clock.Advance(TimeSpan.FromSeconds(3), TimeSpan.FromHours(-1));
                return ScaleSetListenerAuthorization.Allow(validUntil, ownershipLost: CancellationToken.None);
            },
        };
        h.Session.OnDelete = (_, token) =>
        {
            Assert.False(token.IsCancellationRequested);
            clock.Advance(TimeSpan.FromSeconds(2), TimeSpan.FromHours(-1));
            Assert.True(token.IsCancellationRequested);
            return Task.CompletedTask;
        };

        var pump = Create(h, clock, policy);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Step(pump));
        Assert.Equal(["read", "write:5", "delete:19"], h.Calls);
        Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, h.Store.Saved!.Pending!.Phase);

        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await Step(pump)).Status);
        Assert.Equal(["read", "write:5", "delete:19", "read", "write:6"], h.Calls);
        Assert.Equal(19, h.Store.Saved.LastAcknowledgedMessageId);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact(Timeout = 10000)]
    public async Task FreshAndResumedHandlingRecomputeAdmissionAfterLongPoll()
    {
        var h = new EngineFixture();
        h.Initialize();
        var clock = new ListenerTestClock();
        int capacity = 3;
        var pollEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Session.OnPoll = async _ =>
        {
            pollEntered.SetResult();
            await release.Task;
            return new RunnerScaleSetMessage
            {
                MessageId = 33,
                JobAvailableMessages = [new() { RunnerRequestId = 23 }, new() { RunnerRequestId = 24 }],
            };
        };
        var policy = new OperationPolicy(clock) { Capacity = () => capacity };
        h.Handler = async (delivery, token) =>
        {
            Assert.Equal(ScaleSetListenerPendingPhase.Received, h.Store.Saved!.Pending!.Phase);
            if (capacity == 0)
            {
                return ScaleSetListenerDeliveryDisposition.Deferred;
            }

            await h.Session.AcquireJobsAsync(delivery.Message!.JobAvailableMessages.Take(capacity)
                .Select(job => job.RunnerRequestId).ToArray(), token);
            return ScaleSetListenerDeliveryDisposition.Completed;
        };

        var active = Step(Create(h, clock, policy));
        await pollEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        capacity = 0;
        release.SetResult();
        Assert.Equal(ScaleSetListenerStepStatus.Deferred, (await active).Status);
        Assert.Equal((0L, 3), Assert.Single(h.Session.Polls));
        Assert.Empty(h.Session.Acquisitions);
        Assert.Empty(h.Session.Deletes);

        capacity = 1;
        Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await Step(Create(h, clock, policy))).Status);
        Assert.Equal(23, Assert.Single(h.Session.Acquisitions));
        Assert.Single(h.Session.Polls);
        Assert.Equal(33, Assert.Single(h.Session.Deletes));
        Assert.True(h.Deliveries[1].IsReplay);
        Assert.Equal(2, policy.Operations.Count(context => context.Operation == ScaleSetListenerOperation.HandleDelivery));
    }

    [Fact]
    public async Task SessionChangeDuringPollCannotStoreReceiptAndReplacementStartsWithNewKey()
    {
        var old = new EngineFixture();
        old.Initialize();
        var clock = new ListenerTestClock();
        old.Session.OnPoll = _ =>
        {
            old.Session.Session = old.Session.Session with { SessionId = Guid.NewGuid() };
            return Task.FromResult<RunnerScaleSetMessage?>(new() { MessageId = 9 });
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Step(Create(old, clock, new OperationPolicy(clock))));
        Assert.Equal(["read", "poll:0"], old.Calls);
        Assert.Empty(old.Store.Writes);
        Assert.Empty(old.Session.Deletes);

        var replacement = new EngineFixture();
        var fresh = Create(replacement, clock, new OperationPolicy(clock));
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await Step(fresh)).Status);
        Assert.Equal(replacement.Key, replacement.Store.Saved!.SessionKey);
        Assert.Null(replacement.Store.Saved.LastAcknowledgedMessageId);
        Assert.Equal(old.Key, old.Store.Saved!.SessionKey);
        Assert.Empty(replacement.Session.Polls);
        Assert.Empty(replacement.Session.Deletes);
    }

    [Fact]
    public async Task TracingUsesListenerSourceAndOnlyBoundedOperationMetadata()
    {
        var h = new EngineFixture();
        h.Session.Session = h.Session.Session with
        {
            MessageQueueUrl = "https://secret-queue-fixture.actions.githubusercontent.com/secret",
            MessageQueueAccessToken = "sensitive-queue-token",
        };
        var clock = new ListenerTestClock();
        var captured = new List<Activity>();
        using var parent = new Activity("listener-test").Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ScaleSetListener.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName.StartsWith("scaleset.listener.", StringComparison.Ordinal) &&
                    parent.TraceId == activity.TraceId)
                {
                    lock (captured)
                    {
                        captured.Add(activity);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        Assert.Equal(ScaleSetListenerStepStatus.Initialized,
            (await Step(Create(h, clock, new OperationPolicy(clock)))).Status);
        var activities = captured.Where(activity => activity.TraceId == parent.TraceId).ToArray();
        Assert.Equal(5, activities.Length);
        var step = Assert.Single(activities, activity => activity.OperationName == "scaleset.listener.step");
        Assert.Equal("Initialized", step.GetTagItem("scaleset.listener.outcome"));
        string[] allowedTags = ["scaleset.listener.operation", "scaleset.listener.outcome", "scaleset.id"];
        Assert.All(activities.SelectMany(activity => activity.TagObjects), tag => Assert.Contains(tag.Key, allowedTags));
    }

    private sealed class BlockingPolicy(Func<CancellationToken, Task<ScaleSetListenerAuthorization>> authorize)
        : IScaleSetListenerExecutionPolicy
    {
        public Task<ScaleSetListenerAuthorization> AuthorizeAsync(
            ScaleSetListenerOperationContext context, CancellationToken cancellationToken) => authorize(cancellationToken);
    }

    private static ScaleSetListenerPump Create(EngineFixture h, TimeProvider clock, IScaleSetListenerExecutionPolicy policy) =>
        new(h.Session, h.Options, h.Store, new EngineFixture.FixtureHandler(h), policy, clock);

    private static Task<ScaleSetListenerStepResult> Step(ScaleSetListenerPump pump) =>
        pump.StepAsync(TestContext.Current.CancellationToken);
}

internal sealed class OperationPolicy(TimeProvider clock) : IScaleSetListenerExecutionPolicy
{
    internal CancellationToken Owner { get; set; }
    internal List<ScaleSetListenerOperationContext> Operations { get; } = [];
    internal Func<int> Capacity { get; set; } = static () => 3;
    internal Func<ScaleSetListenerOperationContext, CancellationToken, ScaleSetListenerAuthorization?>? Decide { get; set; }

    public Task<ScaleSetListenerAuthorization> AuthorizeAsync(
        ScaleSetListenerOperationContext context, CancellationToken cancellationToken)
    {
        Operations.Add(context);
        return Task.FromResult(Decide?.Invoke(context, cancellationToken) ?? ScaleSetListenerAuthorization.Allow(
            clock.GetUtcNow().AddMinutes(1), context.Operation == ScaleSetListenerOperation.Poll ? Capacity() : null, Owner));
    }
}

internal sealed class ListenerTestClock : TimeProvider
{
    private readonly List<TestTimer> _timers = [];
    private DateTimeOffset _utcNow = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    internal int ActiveTimers => _timers.Count;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TestTimer(this, callback, state);
        _ = timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    internal void Advance(TimeSpan elapsed, TimeSpan utcChange)
    {
        _timestamp += elapsed.Ticks;
        _utcNow += utcChange;
        foreach (var timer in _timers.ToArray())
        {
            timer.FireIfDue(_timestamp);
        }
    }

    private sealed class TestTimer(ListenerTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long _due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._timestamp + dueTime.Ticks;
            return true;
        }

        internal void FireIfDue(long timestamp)
        {
            if (timestamp >= _due)
            {
                _due = long.MaxValue;
                callback(state);
            }
        }

        public void Dispose() => clock._timers.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
