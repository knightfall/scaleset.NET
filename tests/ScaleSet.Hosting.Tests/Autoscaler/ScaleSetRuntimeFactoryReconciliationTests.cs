using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Autoscaler.Runtime;
using ScaleSet.Client;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;
using ScaleSet.Hosting.Telemetry;

namespace ScaleSet.Hosting.Tests.Autoscaler;

public sealed partial class ScaleSetRuntimeFactoryTests
{
    [Fact]
    public async Task ReconcileAsync_RetriesDiscoveryAndKeepsCapacityUntilOwnedRunnerIsGoneWithCurrentLease()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        options.Controller.ReconcileInterval = TimeSpan.FromSeconds(3);
        CancellationToken token = TestContext.Current.CancellationToken;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        RunnerOwnershipScope scope = new(options.Controller.Identity, scaleSet.Key!, 987);
        RunnerIdentity identity = new(scope, Guid.NewGuid(), "owned-recovered-runner", 915, new CapacityResourceQuantities(1m, 1024));
        RunnerResource running = new("existing-container", identity, RunnerResourceStatus.Running);
        HostCapacityCoordinator capacity = new(options.HostCapacity, options.ScaleSets);
        PeriodicReconciliationClock clock = new();
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = services.GetRequiredService<IMeterFactory>();
        using var metrics = new ScaleSetTelemetryMetrics(meterFactory, [scaleSet.Key!]);
        Channel<string> outcomes = Channel.CreateUnbounded<string>();
        using MeterListener telemetry = ObserveReconciliationOutcomes(meterFactory, outcomes.Writer);

        RunnerResource? snapshot = running;
        int discoveries = 0;
        FakeRunnerProvider provider = new()
        {
            Current = running,
            OnDiscover = _ => ++discoveries == 2
                ? throw new IOException("synthetic discovery failure")
                : Task.FromResult(snapshot is { } resource ? ImmutableArray.Create(resource) : ImmutableArray<RunnerResource>.Empty),
        };
        int removalAttempts = 0;
        FakeRuntimeSession session = CreateQuietSession();
        FakeScaleSetRuntimeClient client = new()
        {
            RuntimeSession = session,
            OnGetRunner = (id, _) =>
            {
                Assert.Equal(915, id);
                return Task.FromResult<RunnerReference?>(new RunnerReference
                {
                    Id = id,
                    Name = identity.RunnerName,
                    RunnerScaleSetId = scope.ScaleSetId,
                });
            },
            OnRemoveRunner = (id, _) =>
            {
                Assert.Equal(915, id);
                if (++removalAttempts == 1)
                {
                    throw new ScaleSetApiException("synthetic runner busy", statusCode: 409, domainError: ScaleSetDomainError.JobStillRunning);
                }

                CapacityReservationLease lease = Assert.IsType<CapacityReservationLease>(capacity.DemandRegistry.GetCapacityLease(identity.OperationId));
                CapacityStateUpdateAttempt concurrentUpdate = capacity.TryUpdateState(lease.Handle, lease.Revision, CapacityOccupancyState.Busy);
                Assert.Equal(CapacityStateUpdateStatus.Updated, concurrentUpdate.Status);
                Assert.True(capacity.DemandRegistry.TryUpdateCapacityLease(lease, concurrentUpdate.CurrentRevision));
                snapshot = null;
                return Task.CompletedTask;
            },
        };
        await using IScaleSetRuntime runtime = await CreateFactory(options, client, provider, metrics, clock).CreateAsync(scaleSet, capacity, token);
        await runtime.InitializeAsync(token);
        Assert.Equal("success", await outcomes.Reader.ReadAsync(deadline.Token));
        Task listener = runtime.RunAsync(deadline.Token);
        PeriodicReconciliationClock.ManualTimer timer = await clock.Timer.WaitAsync(deadline.Token);
        Assert.Equal(options.Controller.ReconcileInterval, timer.DueTime);
        Assert.Equal(options.Controller.ReconcileInterval, timer.Period);
        Assert.Equal(1, discoveries);

        timer.Fire();
        Assert.Equal("failure", await outcomes.Reader.ReadAsync(deadline.Token));
        Assert.Equal(1, capacity.CaptureSnapshot().Usage.RunnerCount);
        Assert.Equal(0, removalAttempts);

        timer.Fire();
        Assert.Equal("success", await outcomes.Reader.ReadAsync(deadline.Token));
        Assert.Same(running, provider.Current);
        Assert.Equal(0, removalAttempts);

        RunnerResource exited = new(running.ProviderResourceId, identity, RunnerResourceStatus.Exited, exitCode: 0);
        snapshot = exited;
        provider.Current = exited;
        timer.Fire();
        Assert.Equal("success", await outcomes.Reader.ReadAsync(deadline.Token));
        Assert.Same(exited, provider.Current);
        Assert.Equal(1, removalAttempts);
        Assert.Equal(1, capacity.CaptureSnapshot().Usage.RunnerCount);

        timer.Fire();
        Assert.Equal("success", await outcomes.Reader.ReadAsync(deadline.Token));
        Assert.Null(provider.Current);
        Assert.Equal(2, removalAttempts);
        Assert.Equal(1, capacity.CaptureSnapshot().Usage.RunnerCount);

        timer.Fire();
        Assert.Equal("success", await outcomes.Reader.ReadAsync(deadline.Token));
        Assert.Equal(0, capacity.CaptureSnapshot().Usage.RunnerCount);
        Assert.Null(capacity.DemandRegistry.GetCapacityLease(identity.OperationId));
        await runtime.DrainAsync(token);
        await timer.Disposed.WaitAsync(deadline.Token);
        Assert.False(listener.IsCompleted);
        Assert.Equal(0, session.CloseCount);
        await runtime.StopAsync(token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener);
        Assert.Equal(1, session.CloseCount);
    }

    [Fact]
    public async Task InitializeAsync_KeepsMissingProviderCapacityWhileTheRegistrationIsPresent()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        CancellationToken token = TestContext.Current.CancellationToken;
        RunnerOwnershipScope scope = new(options.Controller.Identity, scaleSet.Key!, 987);
        RunnerIdentity identity = new(scope, Guid.NewGuid(), "protected-busy-runner", 915, new CapacityResourceQuantities(1m, 1024));
        string stateDirectory = GetSetStateDirectory(directory.Path, scope.ScaleSetKey);
        await using (FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(stateDirectory, scope, token))
        {
            using RunnerLifecycleManager lifecycle = new(journal, TimeProvider.System, NullLogger<RunnerLifecycleManager>.Instance);
            RunnerResource resource = new("externally-removed", identity, RunnerResourceStatus.Running);
            _ = await lifecycle.AdoptDiscoveredAsync(resource, token);
            _ = await lifecycle.RecordActivityAsync(identity, RunnerActivityEvidence.JobAssigned, token);
        }

        RunnerReference? registration = new() { Id = 915, Name = identity.RunnerName, RunnerScaleSetId = 987 };
        for (int restart = 0; restart <= 1; restart++)
        {
            HostCapacityCoordinator capacity = new(options.HostCapacity, options.ScaleSets);
            FakeScaleSetRuntimeClient client = new()
            {
                ExistingScaleSet = new RunnerScaleSet { Id = 987, RunnerGroupId = 44, Name = scaleSet.Name },
                OnGetRunner = (id, _) =>
                {
                    Assert.Equal(915, id);
                    return Task.FromResult(registration);
                },
            };
            await using (IScaleSetRuntime runtime = await CreateFactory(options, client, new FakeRunnerProvider())
                .CreateAsync(scaleSet, capacity, token))
            {
                await runtime.InitializeAsync(token);
                Assert.Equal(restart == 0 ? 1 : 0, capacity.CaptureSnapshot().Usage.RunnerCount);
            }

            await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(stateDirectory, scope, token);
            RunnerOperationState operation = Assert.Single(await journal.ReadAllAsync(token));
            Assert.Equal(restart == 0 ? RunnerLifecycleStage.Busy : RunnerLifecycleStage.Gone, operation.Stage);
            registration = null;
        }
    }

    [Fact]
    public async Task ReconcileAsync_RestartsOnlyTheKnownCreatedResourceWithDurableStartIntent()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        CancellationToken token = TestContext.Current.CancellationToken;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        RunnerOwnershipScope scope = new(options.Controller.Identity, scaleSet.Key!, 987);
        RunnerIdentity initial = new(scope, Guid.NewGuid(), "recovered-created-runner", capacityResources: new CapacityResourceQuantities(1m, 1024));
        RunnerIdentity registered = new(scope, initial.OperationId, initial.RunnerName, 915, initial.CapacityResources);
        RunnerResource created = new("durable-created-container", registered, RunnerResourceStatus.Created);
        string stateDirectory = GetSetStateDirectory(directory.Path, scope.ScaleSetKey);
        await using (FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(stateDirectory, scope, token))
        {
            using RunnerLifecycleManager lifecycle = new(journal, TimeProvider.System, NullLogger<RunnerLifecycleManager>.Instance);
            _ = await lifecycle.ReserveAsync(initial, token);
            _ = await lifecycle.TransitionAsync(initial, RunnerLifecycleStage.JitIssued, cancellationToken: token);
            _ = await lifecycle.AssociateGitHubRunnerAsync(registered, token);
            _ = await lifecycle.TransitionAsync(registered, RunnerLifecycleStage.Creating, cancellationToken: token);
            _ = await lifecycle.TransitionAsync(registered, RunnerLifecycleStage.Starting, created.ProviderResourceId, token);
        }

        PeriodicReconciliationClock clock = new();
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = services.GetRequiredService<IMeterFactory>();
        using var metrics = new ScaleSetTelemetryMetrics(meterFactory, [scaleSet.Key!]);
        Channel<string> outcomes = Channel.CreateUnbounded<string>();
        using MeterListener telemetry = ObserveReconciliationOutcomes(meterFactory, outcomes.Writer);
        HostCapacityCoordinator capacity = new(options.HostCapacity, options.ScaleSets);
        FakeRunnerProvider provider = new() { Current = created };
        FakeScaleSetRuntimeClient client = new()
        {
            ExistingScaleSet = new RunnerScaleSet { Id = 987, RunnerGroupId = 44, Name = scaleSet.Name },
            RuntimeSession = CreateQuietSession(),
            OnGetRunner = (id, _) => Task.FromResult<RunnerReference?>(new RunnerReference
            {
                Id = id,
                Name = registered.RunnerName,
                RunnerScaleSetId = scope.ScaleSetId,
            }),
        };
        await using IScaleSetRuntime runtime = await CreateFactory(options, client, provider, metrics, clock).CreateAsync(scaleSet, capacity, token);
        await runtime.InitializeAsync(token);
        Assert.Equal("success", await outcomes.Reader.ReadAsync(deadline.Token));
        Assert.Same(created, provider.Current);
        Task listener = runtime.RunAsync(deadline.Token);
        PeriodicReconciliationClock.ManualTimer timer = await clock.Timer.WaitAsync(deadline.Token);

        timer.Fire();
        Assert.Equal("success", await outcomes.Reader.ReadAsync(deadline.Token));
        RunnerResource running = Assert.IsType<RunnerResource>(provider.Current);
        Assert.Equal(RunnerResourceStatus.Running, running.Status);
        Assert.Equal(created.ProviderResourceId, running.ProviderResourceId);
        Assert.Equal(registered, running.Identity);
        Assert.Equal(1, capacity.CaptureSnapshot().Usage.RunnerCount);
        await runtime.StopAsync(token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener);
    }

    [Fact]
    public async Task ReconcileAsync_ExhaustsItsPassBudgetWithoutOverlappingAnActiveMessageCallback()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        scaleSet.Lifecycle.CleanupTimeout = TimeSpan.FromSeconds(7);
        CancellationToken token = TestContext.Current.CancellationToken;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        PeriodicReconciliationClock clock = new(scaleSet.Lifecycle.CleanupTimeout);
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = services.GetRequiredService<IMeterFactory>();
        using var metrics = new ScaleSetTelemetryMetrics(meterFactory, [scaleSet.Key!]);
        Channel<string> outcomes = Channel.CreateUnbounded<string>();
        using MeterListener telemetry = ObserveReconciliationOutcomes(meterFactory, outcomes.Writer);
        TaskCompletionSource admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int polls = 0;
        int discoveries = 0;
        FakeRuntimeSession session = new()
        {
            Poll = async cancellationToken =>
            {
                if (++polls == 1)
                {
                    return new RunnerScaleSetMessage
                    {
                        MessageId = 71,
                        Statistics = new RunnerScaleSetStatistic(),
                        JobAvailableMessages = [new JobAvailableMessage { RunnerRequestId = 72 }],
                    };
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return null;
            },
            Acquire = async (offered, cancellationToken) =>
            {
                admitted.SetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return offered;
            },
            Acknowledge = (_, _) =>
            {
                acknowledged.SetResult();
                return Task.CompletedTask;
            },
            Close = _ => Task.CompletedTask,
        };
        FakeRunnerProvider provider = new()
        {
            OnDiscover = _ =>
            {
                Interlocked.Increment(ref discoveries);
                return Task.FromResult(ImmutableArray<RunnerResource>.Empty);
            },
        };
        FakeScaleSetRuntimeClient client = new() { RuntimeSession = session };
        await using IScaleSetRuntime runtime = await CreateFactory(options, client, provider, metrics, clock)
            .CreateAsync(scaleSet, new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets), token);
        await runtime.InitializeAsync(token);
        Assert.Equal("success", await outcomes.Reader.ReadAsync(deadline.Token));
        Task listener = runtime.RunAsync(deadline.Token);
        await admitted.Task.WaitAsync(deadline.Token);
        PeriodicReconciliationClock.ManualTimer timer = await clock.Timer.WaitAsync(deadline.Token);

        timer.Fire();
        PeriodicReconciliationClock.ManualTimer budget = await clock.Budget.WaitAsync(deadline.Token);
        budget.Fire();
        Assert.Equal("cancelled", await outcomes.Reader.ReadAsync(deadline.Token));
        Assert.Equal(1, Volatile.Read(ref discoveries));
        Assert.False(acknowledged.Task.IsCompleted);

        release.SetResult();
        await acknowledged.Task.WaitAsync(deadline.Token);
        timer.Fire();
        Assert.Equal("success", await outcomes.Reader.ReadAsync(deadline.Token));
        Assert.Equal(2, Volatile.Read(ref discoveries));
        await runtime.StopAsync(token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener);
    }

    private static FakeRuntimeSession CreateQuietSession() => new()
    {
        Poll = async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return null;
        },
        Close = _ => Task.CompletedTask,
    };

    private static MeterListener ObserveReconciliationOutcomes(IMeterFactory factory, ChannelWriter<string> outcomes)
    {
        MeterListener meter = new()
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Name == "scaleset.operations")
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            },
        };
        meter.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var dimensions = tags.ToArray().ToDictionary();
            if (Equals(dimensions["operation"], "reconcile"))
            {
                Assert.True(outcomes.TryWrite(Assert.IsType<string>(dimensions["outcome"])));
            }
        });
        meter.Start();
        return meter;
    }

    private sealed class PeriodicReconciliationClock(TimeSpan? capturedBudget = null) : TimeProvider
    {
        private readonly TaskCompletionSource<ManualTimer> _timer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ManualTimer> _budget = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ManualTimer> Timer => _timer.Task;

        public Task<ManualTimer> Budget => _budget.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period == Timeout.InfiniteTimeSpan)
            {
                if (dueTime == capturedBudget && !_budget.Task.IsCompleted)
                {
                    ManualTimer budget = new(callback, state, dueTime, period);
                    _budget.SetResult(budget);
                    return budget;
                }

                return System.CreateTimer(callback, state, dueTime, period);
            }

            ManualTimer timer = new(callback, state, dueTime, period);
            _timer.SetResult(timer);
            return timer;
        }

        public sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
        {
            private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TimeSpan DueTime => dueTime;

            public TimeSpan Period => period;

            public Task Disposed => _disposed.Task;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose() => _disposed.TrySetResult();

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
