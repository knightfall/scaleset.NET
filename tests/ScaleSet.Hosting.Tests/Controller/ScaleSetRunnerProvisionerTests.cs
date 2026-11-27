using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Client;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Controller;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;

namespace ScaleSet.Hosting.Tests.Controller;

public sealed class ScaleSetRunnerProvisionerTests
{
    private const string SyntheticJit = "synthetic-jit-secret";

    [Fact]
    public async Task ProvisionAsync_PersistsIntentBeforeSideEffects_AndLeavesRunningAsStarting()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, TestContext.Current.CancellationToken);
        List<string> calls = [];
        FakeJitConfigClient client = new(async (_, setting, token) =>
        {
            calls.Add("jit");
            RunnerOperationState? reserved = await journal.FindAsync(identity.OperationId, token);
            Assert.Equal(RunnerLifecycleStage.Reserved, reserved?.Stage);
            Assert.Equal(identity.RunnerName, setting.Name);
            Assert.Equal("_work", setting.WorkFolder);
            return CreateJit(identity, SyntheticJit);
        });
        FakeRunnerProvider provider = new(scope)
        {
            OnCreate = async (request, token) =>
            {
                calls.Add("create");
                RunnerOperationState? creating = await journal.FindAsync(identity.OperationId, token);
                Assert.Equal(RunnerLifecycleStage.Creating, creating?.Stage);
                Assert.Equal(2L, creating?.Revision);
                Assert.Equal(704L, creating?.Identity.GitHubRunnerId);
                Assert.Equal(SyntheticJit, request.EncodedJitConfig);
                return new RunnerResource("container-1", request.Identity, RunnerResourceStatus.Created);
            },
            OnStart = async (resource, token) =>
            {
                calls.Add("start");
                RunnerOperationState? starting = await journal.FindAsync(identity.OperationId, token);
                Assert.Equal(RunnerLifecycleStage.Starting, starting?.Stage);
                Assert.Equal("container-1", starting?.ProviderResourceId);
                return new RunnerResource(resource.ProviderResourceId, resource.Identity, RunnerResourceStatus.Running);
            },
        };
        using ScaleSetRunnerProvisioner provisioner = CreateProvisioner(provider, client, journal);

        RunnerProvisioningResult result = await provisioner.ProvisionAsync(identity, TestContext.Current.CancellationToken);

        RunnerOperationState finalState = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, TestContext.Current.CancellationToken));
        await using var persistedDb = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "runner-state.sqlite3"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await persistedDb.OpenAsync(TestContext.Current.CancellationToken);
        await using var selectPayload = persistedDb.CreateCommand();
        selectPayload.CommandText = "SELECT payload FROM operations";
        string journalJson = Assert.IsType<string>(await selectPayload.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Contains(identity.RunnerName, journalJson, StringComparison.Ordinal);
        Assert.Equal(["jit", "create", "start"], calls);
        Assert.Equal(["discover", "create", "start"], provider.Calls.ToArray());
        Assert.Equal(RunnerLifecycleStage.Starting, result.Stage);
        Assert.False(result.Recovered);
        Assert.Equal(RunnerResourceStatus.Running, result.Resource.Status);
        Assert.Equal(RunnerLifecycleStage.Starting, finalState.Stage);
        Assert.Equal(704L, finalState.Identity.GitHubRunnerId);
        Assert.DoesNotContain(SyntheticJit, journalJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisionAsync_PersistsIssuedIdentityWhenCallerCancelsBeforeCreate()
    {
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        InMemoryRunnerStateJournal journal = new(scope);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        FakeJitConfigClient client = new((_, _, _) =>
        {
            caller.Cancel();
            return Task.FromResult<RunnerScaleSetJitRunnerConfig?>(CreateJit(identity, SyntheticJit));
        });
        FakeRunnerProvider provider = new(scope);
        using ScaleSetRunnerProvisioner provisioner = CreateProvisioner(provider, client, journal);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provisioner.ProvisionAsync(identity, caller.Token));

        var operation = Assert.IsType<RunnerOperationState>(
            await journal.FindAsync(identity.OperationId, TestContext.Current.CancellationToken));
        Assert.Equal(704L, operation.Identity.GitHubRunnerId);
        Assert.Equal(RunnerLifecycleStage.JitIssued, operation.Stage);
        Assert.Equal(["discover"], provider.Calls.ToArray());
    }

    [Fact]
    public async Task ProvisionAsync_MarksFailedWhenAuthenticationPreventsJitDispatch()
    {
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        InMemoryRunnerStateJournal journal = new(scope);
        using var httpClient = new HttpClient(new RegistrationDeniedHandler());
        using var sdk = new ScaleSetClient(GitHubTarget.Parse("https://github.com/example/runner"),
            ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"), httpClient: httpClient);
        var jit = new ScaleSetRunnerJitConfigClient(sdk);
        FakeRunnerProvider provider = new(scope);
        using var provisioner = new ScaleSetRunnerProvisioner(scope.ScaleSetId, "_work", jit, provider, journal);
        var token = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ScaleSetApiException>(() => provisioner.ProvisionAsync(identity, token));

        var operation = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, token));
        Assert.Equal(RunnerLifecycleStage.Failed, operation.Stage);
        Assert.Equal(["discover"], provider.Calls.ToArray());
    }

    private sealed class RegistrationDeniedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.EndsWith("/registration-token", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvisionAsync_DiscoveredMissingWithPartialResourcesRetainsRegistrationUncertainty(bool alreadyUnknown)
    {
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        var token = TestContext.Current.CancellationToken;
        var journal = new InMemoryRunnerStateJournal(scope);
        var discoveredIdentity = new RunnerIdentity(scope, identity.OperationId, identity.RunnerName, 704);
        var provider = new FakeRunnerProvider(scope)
        {
            Discovered = [new RunnerResource("container-missing", discoveredIdentity, RunnerResourceStatus.Missing,
                capacityResources: new ScaleSet.Hosting.Capacity.CapacityResourceQuantities(1m, null))],
        };
        if (alreadyUnknown)
        {
            using var lifecycle = new RunnerLifecycleManager(journal, TimeProvider.System, NullLogger<RunnerLifecycleManager>.Instance);
            await lifecycle.ReserveAsync(identity, token);
            await lifecycle.TransitionAsync(identity, RunnerLifecycleStage.Unknown, cancellationToken: token);
        }

        var client = new FakeJitConfigClient((_, _, _) => throw new InvalidOperationException("Unexpected JIT replay."));
        using ScaleSetRunnerProvisioner provisioner = CreateProvisioner(provider, client, journal);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.ProvisionAsync(identity, token));
        Assert.Contains(alreadyUnknown ? "confirms the operation is absent" : "requires registration reconciliation", failure.Message, StringComparison.Ordinal);
        RunnerOperationState state = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, token));
        Assert.Equal(RunnerLifecycleStage.Unknown, state.Stage);
        Assert.Equal("container-missing", state.ProviderResourceId);
        Assert.Equal(704L, state.Identity.GitHubRunnerId);
        Assert.Null(state.Identity.CapacityResources);
        Assert.Equal(["discover"], provider.Calls.ToArray());
    }

    [Fact]
    public async Task ReconcileCompletedAsync_RequiresTerminalEvidenceAndRemovesWithoutForce()
    {
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        InMemoryRunnerStateJournal journal = new(scope);
        int inspectCount = 0;
        int removeCount = 0;
        bool forceUsed = true;
        FakeRunnerProvider provider = new(scope)
        {
            OnInspect = (resource, _) =>
            {
                int attempt = Interlocked.Increment(ref inspectCount);
                RunnerResourceStatus status = attempt switch
                {
                    1 => RunnerResourceStatus.Running,
                    2 => RunnerResourceStatus.Exited,
                    _ => RunnerResourceStatus.Missing,
                };
                return Task.FromResult<RunnerResource?>(new RunnerResource(
                    resource.ProviderResourceId,
                    resource.Identity,
                    status));
            },
            OnRemove = (_, force, _) =>
            {
                Interlocked.Increment(ref removeCount);
                forceUsed = force;
                return Task.CompletedTask;
            },
        };
        FakeJitConfigClient client = new((_, setting, _) => Task.FromResult<RunnerScaleSetJitRunnerConfig?>(
            new RunnerScaleSetJitRunnerConfig
            {
                Runner = new RunnerReference { Id = 704, Name = setting.Name },
                EncodedJitConfig = SyntheticJit,
            }));
        using ScaleSetRunnerProvisioner provisioner = CreateProvisioner(provider, client, journal);
        using RunnerLifecycleManager lifecycle = new(journal, TimeProvider.System, NullLogger<RunnerLifecycleManager>.Instance);
        RunnerProvisioningResult provisioned = await provisioner.ProvisionAsync(identity, TestContext.Current.CancellationToken);
        _ = await lifecycle.RecordActivityAsync(
            provisioned.Resource.Identity,
            RunnerActivityEvidence.JobAssigned,
            TestContext.Current.CancellationToken);
        _ = await lifecycle.RecordActivityAsync(
            provisioned.Resource.Identity,
            RunnerActivityEvidence.JobCompleted,
            TestContext.Current.CancellationToken);

        Assert.False(await provisioner.ReconcileCompletedAsync(provisioned.Resource.Identity, TestContext.Current.CancellationToken));
        RunnerOperationState stillCompleted = Assert.IsType<RunnerOperationState>(
            await journal.FindAsync(identity.OperationId, TestContext.Current.CancellationToken));
        Assert.Equal(RunnerLifecycleStage.Completed, stillCompleted.Stage);
        Assert.Equal(0, removeCount);

        Assert.True(await provisioner.ReconcileCompletedAsync(provisioned.Resource.Identity, TestContext.Current.CancellationToken));
        RunnerOperationState gone = Assert.IsType<RunnerOperationState>(
            await journal.FindAsync(identity.OperationId, TestContext.Current.CancellationToken));
        Assert.Equal(RunnerLifecycleStage.Gone, gone.Stage);
        Assert.Equal(1, removeCount);
        Assert.False(forceUsed);
    }

    [Fact]
    public async Task ProvisionAsync_ConcurrentSameIdentityCoalescesAndReturnsOneResult()
    {
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        InMemoryRunnerStateJournal journal = new(scope);
        TaskCompletionSource jitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseJit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int jitCalls = 0;
        FakeJitConfigClient client = new((_, _, token) =>
        {
            Interlocked.Increment(ref jitCalls);
            jitEntered.TrySetResult();
            return WaitForJitAsync(identity, token);

            async Task<RunnerScaleSetJitRunnerConfig?> WaitForJitAsync(RunnerIdentity runnerIdentity, CancellationToken cancellationToken)
            {
                await releaseJit.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return CreateJit(runnerIdentity, SyntheticJit);
            }
        });
        FakeRunnerProvider provider = new(scope);
        using ScaleSetRunnerProvisioner provisioner = CreateProvisioner(provider, client, journal);

        Task<RunnerProvisioningResult> first = provisioner.ProvisionAsync(identity, TestContext.Current.CancellationToken);
        await jitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Task<RunnerProvisioningResult> second;
        try
        {
            second = provisioner.ProvisionAsync(identity, TestContext.Current.CancellationToken);
            await Task.Yield();
            Assert.Equal(1, Volatile.Read(ref jitCalls));
        }
        finally
        {
            releaseJit.TrySetResult();
        }

        RunnerProvisioningResult[] results = await Task.WhenAll(first, second);

        Assert.Equal(results[0], results[1]);
        Assert.Equal("container-new", results[0].Resource.ProviderResourceId);
        Assert.Equal(RunnerLifecycleStage.Starting, results[0].Stage);
        Assert.Equal(1, Volatile.Read(ref jitCalls));
        Assert.Equal(1, provider.CreateCount);
        Assert.Equal(1, provider.StartCount);
    }

    [Fact]
    public async Task ProvisionAsync_DifferentOperationIdsCanProvisionConcurrently()
    {
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity firstIdentity = CreateIdentity(scope);
        RunnerIdentity secondIdentity = new(scope, Guid.Parse("33333333-3333-3333-3333-333333333333"), "runner-second");
        InMemoryRunnerStateJournal journal = new(scope);
        TaskCompletionSource firstJitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource bothJitsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseJits = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int jitCalls = 0;
        FakeJitConfigClient client = new(async (_, setting, token) =>
        {
            int call = Interlocked.Increment(ref jitCalls);
            if (call == 1)
            {
                firstJitEntered.TrySetResult();
            }
            else if (call == 2)
            {
                bothJitsEntered.TrySetResult();
            }

            await releaseJits.Task.WaitAsync(token).ConfigureAwait(false);
            RunnerIdentity identity = setting.Name == firstIdentity.RunnerName ? firstIdentity : secondIdentity;
            return CreateJit(identity, $"jit-{setting.Name}");
        });
        FakeRunnerProvider provider = new(scope)
        {
            OnCreate = (request, _) => Task.FromResult(new RunnerResource(
                $"container-{request.Identity.OperationId:N}",
                request.Identity,
                RunnerResourceStatus.Created)),
        };
        using ScaleSetRunnerProvisioner provisioner = CreateProvisioner(provider, client, journal);

        Task<RunnerProvisioningResult> first = provisioner.ProvisionAsync(firstIdentity, TestContext.Current.CancellationToken);
        await firstJitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Task<RunnerProvisioningResult> second;
        try
        {
            second = provisioner.ProvisionAsync(secondIdentity, TestContext.Current.CancellationToken);
            await bothJitsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseJits.TrySetResult();
        }

        RunnerProvisioningResult[] results = await Task.WhenAll(first, second);
        Assert.Equal(2, Volatile.Read(ref jitCalls));
        Assert.Equal(2, provider.CreateCount);
        Assert.Equal(2, provider.StartCount);
        Assert.NotEqual(results[0].Resource.ProviderResourceId, results[1].Resource.ProviderResourceId);
    }

    [Fact]
    public async Task ProvisionAsync_SameOperationIdInDifferentScopesUsesSeparateGatesAndResults()
    {
        RunnerOwnershipScope firstScope = CreateScope();
        RunnerOwnershipScope secondScope = new("controller-a", "linux-arm64", 89);
        RunnerIdentity firstIdentity = CreateIdentity(firstScope);
        RunnerIdentity secondIdentity = CreateIdentity(secondScope);
        InMemoryRunnerStateJournal firstJournal = new(firstScope);
        InMemoryRunnerStateJournal secondJournal = new(secondScope);
        TaskCompletionSource firstJitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource bothJitsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseJits = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int jitCalls = 0;

        FakeJitConfigClient CreateClient(RunnerIdentity expectedIdentity, long runnerId) => new(async (_, setting, token) =>
        {
            int call = Interlocked.Increment(ref jitCalls);
            if (call == 1)
            {
                firstJitEntered.TrySetResult();
            }
            else if (call == 2)
            {
                bothJitsEntered.TrySetResult();
            }

            await releaseJits.Task.WaitAsync(token).ConfigureAwait(false);
            return new RunnerScaleSetJitRunnerConfig
            {
                Runner = new RunnerReference { Id = runnerId, Name = setting.Name },
                EncodedJitConfig = $"jit-{expectedIdentity.Scope.ScaleSetKey}",
            };
        });

        FakeRunnerProvider firstProvider = new(firstScope)
        {
            OnCreate = (request, _) => Task.FromResult(new RunnerResource("container-first", request.Identity, RunnerResourceStatus.Created)),
        };
        FakeRunnerProvider secondProvider = new(secondScope)
        {
            OnCreate = (request, _) => Task.FromResult(new RunnerResource("container-second", request.Identity, RunnerResourceStatus.Created)),
        };
        using ScaleSetRunnerProvisioner firstProvisioner = CreateProvisioner(firstProvider, CreateClient(firstIdentity, 704), firstJournal);
        using ScaleSetRunnerProvisioner secondProvisioner = CreateProvisioner(secondProvider, CreateClient(secondIdentity, 705), secondJournal);

        Task<RunnerProvisioningResult> first = firstProvisioner.ProvisionAsync(firstIdentity, TestContext.Current.CancellationToken);
        await firstJitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Task<RunnerProvisioningResult> second;
        try
        {
            second = secondProvisioner.ProvisionAsync(secondIdentity, TestContext.Current.CancellationToken);
            await bothJitsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseJits.TrySetResult();
        }

        RunnerProvisioningResult[] results = await Task.WhenAll(first, second);
        Assert.Equal(2, Volatile.Read(ref jitCalls));
        Assert.Equal(firstScope, results[0].Resource.Identity.Scope);
        Assert.Equal(secondScope, results[1].Resource.Identity.Scope);
        Assert.Equal("container-first", results[0].Resource.ProviderResourceId);
        Assert.Equal("container-second", results[1].Resource.ProviderResourceId);
    }

    [Fact]
    public async Task ProvisionAsync_CancelledWaiterDoesNotReleaseTheActiveOperationGate()
    {
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        InMemoryRunnerStateJournal journal = new(scope);
        TaskCompletionSource jitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseJit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeJitConfigClient client = new(async (_, _, token) =>
        {
            jitEntered.TrySetResult();
            await releaseJit.Task.WaitAsync(token).ConfigureAwait(false);
            return CreateJit(identity, SyntheticJit);
        });
        FakeRunnerProvider provider = new(scope);
        using ScaleSetRunnerProvisioner provisioner = CreateProvisioner(provider, client, journal);

        Task<RunnerProvisioningResult> owner = provisioner.ProvisionAsync(identity, TestContext.Current.CancellationToken);
        await jitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using CancellationTokenSource waiterCancellation = new();
        Task<RunnerProvisioningResult> waiter = provisioner.ProvisionAsync(identity, waiterCancellation.Token);
        waiterCancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        }
        finally
        {
            releaseJit.TrySetResult();
        }

        RunnerProvisioningResult result = await owner;

        Assert.Equal("container-new", result.Resource.ProviderResourceId);
        Assert.Equal(1, provider.CreateCount);
        Assert.Equal(1, provider.StartCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvisionAsync_StartupUncertaintyAndTerminalEvidenceNeverRepeatTheHandoff(bool ambiguousStart)
    {
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        var journal = new InMemoryRunnerStateJournal(scope);
        var token = TestContext.Current.CancellationToken;
        int jitCalls = 0;
        var client = new FakeJitConfigClient((_, _, _) =>
        {
            jitCalls++;
            return Task.FromResult<RunnerScaleSetJitRunnerConfig?>(CreateJit(identity, SyntheticJit));
        });
        var provider = new FakeRunnerProvider(scope)
        {
            RequiresJitAtStart = true,
            OnCreate = (request, _) =>
            {
                Assert.Null(request.EncodedJitConfig);
                return Task.FromResult(new RunnerResource("container-start", request.Identity, RunnerResourceStatus.Created));
            },
            OnStart = (resource, _) => ambiguousStart
                ? throw new IOException("Synthetic handoff uncertainty.")
                : Task.FromResult(new RunnerResource(resource.ProviderResourceId, resource.Identity, RunnerResourceStatus.Exited, 0)),
            OnInspect = (resource, _) => Task.FromResult<RunnerResource?>(
                new RunnerResource(resource.ProviderResourceId, resource.Identity, RunnerResourceStatus.Exited, 0)),
        };
        using ScaleSetRunnerProvisioner provisioner = CreateProvisioner(provider, client, journal);

        if (ambiguousStart)
        {
            await Assert.ThrowsAsync<IOException>(() => provisioner.ProvisionAsync(identity, token));
            Assert.Equal(RunnerLifecycleStage.Starting, (await journal.FindAsync(identity.OperationId, token))?.Stage);
            Assert.True((await provisioner.ProvisionAsync(identity, token)).Recovered);
        }
        else
        {
            RunnerProvisioningResult result = await provisioner.ProvisionAsync(identity, token);
            Assert.Equal(RunnerLifecycleStage.Unknown, result.Stage);
            Assert.False(result.Recovered);
        }

        var state = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, token));
        Assert.Equal(RunnerLifecycleStage.Unknown, state.Stage);
        Assert.Equal(RunnerResourceStatus.Exited, state.LastObservedProviderStatus);
        Assert.Equal("container-start", state.ProviderResourceId);
        Assert.Equal(1, jitCalls);
        Assert.Equal(SyntheticJit, Assert.Single(provider.StartJitConfigs));
        Assert.Equal(1, provider.StartCount);
        Assert.Equal(1, provider.CreateCount);
    }

    [Fact]
    public async Task ProvisionAsync_ExistingProviderIdInspectsThatResourceAndAdoptsRunningAsUnknown()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        RunnerIdentity linked = await SeedStartingOperationAsync(directory.Path, scope, identity, "container-existing");
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, TestContext.Current.CancellationToken);
        FakeRunnerProvider provider = new(scope)
        {
            OnInspect = (resource, _) =>
            {
                Assert.Equal("container-existing", resource.ProviderResourceId);
                Assert.Equal(linked.OperationId, resource.Identity.OperationId);
                return Task.FromResult<RunnerResource?>(new RunnerResource(
                    "container-existing",
                    linked,
                    RunnerResourceStatus.Running));
            },
        };
        FakeJitConfigClient client = new((_, _, _) => throw new Xunit.Sdk.XunitException("JIT must not be requested during recovery."));
        using ScaleSetRunnerProvisioner provisioner = CreateProvisioner(provider, client, journal);

        RunnerProvisioningResult result = await provisioner.ProvisionAsync(identity, TestContext.Current.CancellationToken);

        RunnerOperationState finalState = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, TestContext.Current.CancellationToken));
        Assert.Equal(["inspect:container-existing"], provider.Calls.ToArray());
        Assert.Equal("container-existing", result.Resource.ProviderResourceId);
        Assert.Equal(linked.OperationId, result.Resource.Identity.OperationId);
        Assert.True(result.Recovered);
        Assert.Equal(0, provider.StartCount);
        Assert.Equal(0, provider.CreateCount);
        Assert.Equal(RunnerLifecycleStage.Unknown, finalState.Stage);
        Assert.Equal(RunnerResourceStatus.Running, finalState.LastObservedProviderStatus);
        Assert.Equal(704L, finalState.Identity.GitHubRunnerId);
    }

    [Fact]
    public async Task ProvisionAsync_ProviderAbsenceDoesNotSettleTheGitHubRegistration()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        const string providerId = "missing-container";
        RunnerIdentity linked = await SeedStartingOperationAsync(directory.Path, scope, identity, providerId);
        var token = TestContext.Current.CancellationToken;
        await using var journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, token);
        FakeRunnerProvider provider = new(scope)
        {
            OnInspect = (_, _) => Task.FromResult<RunnerResource?>(null),
        };
        FakeJitConfigClient client = new((_, _, _) => throw new InvalidOperationException("JIT cannot be replayed."));
        using ScaleSetRunnerProvisioner provisioner = CreateProvisioner(provider, client, journal);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.ProvisionAsync(identity, token));

        var pending = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, token));
        Assert.Equal(RunnerLifecycleStage.Unknown, pending.Stage);
        Assert.Equal(linked.GitHubRunnerId, pending.Identity.GitHubRunnerId);
        Assert.Equal(providerId, pending.ProviderResourceId);
        Assert.Equal(0, provider.CreateCount);
    }

    [Fact]
    public async Task ProvisionAsync_StartingIntentWithCreatedResourceResumesThatStartOnce()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        RunnerIdentity linked = await SeedStartingOperationAsync(directory.Path, scope, identity, "container-created");
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, TestContext.Current.CancellationToken);
        FakeRunnerProvider provider = new(scope)
        {
            OnInspect = (_, _) => Task.FromResult<RunnerResource?>(new RunnerResource(
                "container-created",
                linked,
                RunnerResourceStatus.Created)),
            OnStart = (resource, _) =>
            {
                Assert.Equal("container-created", resource.ProviderResourceId);
                return Task.FromResult(new RunnerResource(
                    resource.ProviderResourceId,
                    resource.Identity,
                    RunnerResourceStatus.Running));
            },
        };
        FakeJitConfigClient client = new((_, _, _) => throw new Xunit.Sdk.XunitException("JIT must not be requested during recovery."));
        using ScaleSetRunnerProvisioner provisioner = CreateProvisioner(provider, client, journal);

        RunnerProvisioningResult result = await provisioner.ProvisionAsync(identity, TestContext.Current.CancellationToken);

        RunnerOperationState finalState = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, TestContext.Current.CancellationToken));
        Assert.Equal(["inspect:container-created", "start"], provider.Calls.ToArray());
        Assert.Equal(1, provider.StartCount);
        Assert.Equal(0, provider.CreateCount);
        Assert.Equal("container-created", result.Resource.ProviderResourceId);
        Assert.Equal(RunnerResourceStatus.Running, result.Resource.Status);
        Assert.Equal(RunnerLifecycleStage.Unknown, result.Stage);
        Assert.True(result.Recovered);
        Assert.Equal(RunnerLifecycleStage.Unknown, finalState.Stage);
        Assert.Equal(RunnerResourceStatus.Running, finalState.LastObservedProviderStatus);
    }

    [Fact]
    public async Task ProvisionAsync_CreatingIntentUsesScopedDiscoveryAndDoesNotCreateAgain()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        RunnerIdentity linked = await SeedCreatingOperationAsync(directory.Path, scope, identity);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, TestContext.Current.CancellationToken);
        FakeRunnerProvider provider = new(scope)
        {
            Discovered = [new RunnerResource("container-recovered", linked, RunnerResourceStatus.Created)],
        };
        FakeJitConfigClient client = new((_, _, _) => throw new Xunit.Sdk.XunitException("JIT must not be requested during recovery."));
        using ScaleSetRunnerProvisioner provisioner = CreateProvisioner(provider, client, journal);

        RunnerProvisioningResult result = await provisioner.ProvisionAsync(identity, TestContext.Current.CancellationToken);

        RunnerOperationState finalState = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, TestContext.Current.CancellationToken));
        Assert.Equal(["discover"], provider.Calls.ToArray());
        Assert.Equal("container-recovered", result.Resource.ProviderResourceId);
        Assert.Equal(RunnerLifecycleStage.Unknown, result.Stage);
        Assert.True(result.Recovered);
        Assert.Equal(RunnerLifecycleStage.Unknown, finalState.Stage);
        Assert.Equal(RunnerResourceStatus.Created, finalState.LastObservedProviderStatus);
    }

    [Fact]
    public async Task ProvisionAsync_UnresolvedCreatingIntentBecomesUnknownInsteadOfBlindCreateRetry()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        _ = await SeedCreatingOperationAsync(directory.Path, scope, identity);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, TestContext.Current.CancellationToken);
        FakeRunnerProvider provider = new(scope);
        FakeJitConfigClient client = new((_, _, _) => throw new Xunit.Sdk.XunitException("JIT must not be requested for an uncertain operation."));
        using ScaleSetRunnerProvisioner provisioner = CreateProvisioner(provider, client, journal);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provisioner.ProvisionAsync(identity, TestContext.Current.CancellationToken));

        RunnerOperationState finalState = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, TestContext.Current.CancellationToken));
        Assert.Contains("outcome is uncertain", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["discover"], provider.Calls.ToArray());
        Assert.Equal(RunnerLifecycleStage.Unknown, finalState.Stage);
        Assert.Equal(0, provider.CreateCount);
        Assert.Equal(0, provider.StartCount);
    }

    private static ScaleSetRunnerProvisioner CreateProvisioner(
        FakeRunnerProvider provider,
        FakeJitConfigClient client,
        IRunnerStateJournal journal) =>
        new(
            journal.Scope.ScaleSetId,
            "_work",
            client,
            provider,
            journal,
            NullLogger<ScaleSetRunnerProvisioner>.Instance);

    private static async Task<RunnerIdentity> SeedCreatingOperationAsync(
        string stateDirectory,
        RunnerOwnershipScope scope,
        RunnerIdentity identity)
    {
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(stateDirectory, scope, TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = new(journal, TimeProvider.System, NullLogger<RunnerLifecycleManager>.Instance);
        RunnerIdentity linked = new(scope, identity.OperationId, identity.RunnerName, 704);
        await manager.ReserveAsync(identity, TestContext.Current.CancellationToken);
        await manager.TransitionAsync(identity, RunnerLifecycleStage.JitIssued, cancellationToken: TestContext.Current.CancellationToken);
        await manager.AssociateGitHubRunnerAsync(linked, TestContext.Current.CancellationToken);
        await manager.TransitionAsync(linked, RunnerLifecycleStage.Creating, cancellationToken: TestContext.Current.CancellationToken);
        return linked;
    }

    private static async Task<RunnerIdentity> SeedStartingOperationAsync(
        string stateDirectory,
        RunnerOwnershipScope scope,
        RunnerIdentity identity,
        string resourceId)
    {
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(stateDirectory, scope, TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = new(journal, TimeProvider.System, NullLogger<RunnerLifecycleManager>.Instance);
        RunnerIdentity linked = new(scope, identity.OperationId, identity.RunnerName, 704);
        await manager.ReserveAsync(identity, TestContext.Current.CancellationToken);
        await manager.TransitionAsync(identity, RunnerLifecycleStage.JitIssued, cancellationToken: TestContext.Current.CancellationToken);
        await manager.AssociateGitHubRunnerAsync(linked, TestContext.Current.CancellationToken);
        await manager.TransitionAsync(linked, RunnerLifecycleStage.Creating, cancellationToken: TestContext.Current.CancellationToken);
        await manager.TransitionAsync(linked, RunnerLifecycleStage.Starting, resourceId, TestContext.Current.CancellationToken);
        return linked;
    }

    private static RunnerScaleSetJitRunnerConfig CreateJit(RunnerIdentity identity, string encoded) => new()
    {
        Runner = new RunnerReference { Id = 704, Name = identity.RunnerName },
        EncodedJitConfig = encoded,
    };

    private static RunnerOwnershipScope CreateScope() => new("controller-a", "linux-x64", 88);

    private static RunnerIdentity CreateIdentity(RunnerOwnershipScope scope) =>
        new(scope, Guid.Parse("22222222-2222-2222-2222-222222222222"), "runner-stable");

    private sealed class InMemoryRunnerStateJournal(RunnerOwnershipScope scope) : IRunnerStateJournal
    {
        private readonly Dictionary<Guid, RunnerOperationState> _operations = [];
        private readonly object _sync = new();

        public RunnerOwnershipScope Scope { get; } = scope;

        public Task<ImmutableArray<RunnerOperationState>> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                return Task.FromResult(_operations.Values.ToImmutableArray());
            }
        }

        public Task<RunnerOperationState?> FindAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _operations.TryGetValue(operationId, out RunnerOperationState? state);
                return Task.FromResult(state);
            }
        }

        public Task WriteAsync(RunnerOperationState state, long expectedRevision, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _operations.TryGetValue(state.Identity.OperationId, out RunnerOperationState? current);
                long? actualRevision = current?.Revision;
                if (actualRevision != (expectedRevision < 0 ? null : expectedRevision)
                    || state.Revision != expectedRevision + 1)
                {
                    throw new InvalidOperationException("The operation revision changed before the write.");
                }

                _operations[state.Identity.OperationId] = state;
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeJitConfigClient(
        Func<long, RunnerScaleSetJitRunnerSetting, CancellationToken, Task<RunnerScaleSetJitRunnerConfig?>> generate) : IRunnerJitConfigClient
    {
        public Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
            long scaleSetId,
            RunnerScaleSetJitRunnerSetting runnerSetting,
            CancellationToken cancellationToken = default) => generate(scaleSetId, runnerSetting, cancellationToken);
    }

    private sealed class FakeRunnerProvider(RunnerOwnershipScope expectedScope) : IRunnerProvider
    {
        private readonly RunnerOwnershipScope _scope = expectedScope;

        public bool RequiresJitAtStart { get; init; }

        public RunnerProviderCapabilities Capabilities => new(
            "fake",
            ImmutableArray.Create(RunnerArchitecture.X64),
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: true)
        {
            RequiresJitConfigurationAtStart = RequiresJitAtStart,
        };

        public ConcurrentQueue<string?> StartJitConfigs { get; } = new();

        public ConcurrentQueue<string> Calls { get; } = new();

        public ImmutableArray<RunnerResource> Discovered { get; init; } = ImmutableArray<RunnerResource>.Empty;

        public Func<RunnerCreateRequest, CancellationToken, Task<RunnerResource>>? OnCreate { get; init; }

        public Func<RunnerResource, CancellationToken, Task<RunnerResource>>? OnStart { get; init; }

        public Func<RunnerResource, CancellationToken, Task<RunnerResource?>>? OnInspect { get; init; }

        public Func<RunnerResource, bool, CancellationToken, Task>? OnRemove { get; init; }

        private int _createCount;

        private int _startCount;

        public int CreateCount => Volatile.Read(ref _createCount);

        public int StartCount => Volatile.Read(ref _startCount);

        public Task<ImmutableArray<RunnerResource>> DiscoverAsync(RunnerOwnershipScope scope, CancellationToken cancellationToken = default)
        {
            Assert.Equal(_scope, scope);
            Calls.Enqueue("discover");
            return Task.FromResult(Discovered);
        }

        public Task<RunnerResource> CreateAsync(RunnerCreateRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _createCount);
            Calls.Enqueue("create");
            return OnCreate?.Invoke(request, cancellationToken)
                ?? Task.FromResult(new RunnerResource("container-new", request.Identity, RunnerResourceStatus.Created));
        }

        public Task<RunnerResource> StartAsync(RunnerResource resource, string? encodedJitConfig, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCount);
            Calls.Enqueue("start");
            StartJitConfigs.Enqueue(encodedJitConfig);
            return OnStart?.Invoke(resource, cancellationToken)
                ?? Task.FromResult(new RunnerResource(resource.ProviderResourceId, resource.Identity, RunnerResourceStatus.Running));
        }

        public Task<RunnerResource?> InspectAsync(RunnerResource resource, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue($"inspect:{resource.ProviderResourceId}");
            return OnInspect?.Invoke(resource, cancellationToken)
                ?? Task.FromResult<RunnerResource?>(null);
        }

        public Task RemoveAsync(RunnerResource resource, bool force, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue($"remove:{resource.ProviderResourceId}:{force}");
            return OnRemove?.Invoke(resource, force, cancellationToken) ?? throw new NotSupportedException();
        }
    }

    private sealed class TemporaryStateDirectory : IDisposable
    {
        public TemporaryStateDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"scaleset-provisioner-{Guid.NewGuid():N}");
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
