using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;

namespace ScaleSet.Hosting.Tests.State;

public sealed class RunnerLifecycleManagerTests
{
    [Fact]
    public async Task ReserveAndTransition_PersistIntentWithInjectedTimeAndNoCredentialFields()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        ManualTimeProvider timeProvider = new(new DateTimeOffset(2026, 10, 2, 1, 0, 0, TimeSpan.Zero));
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, timeProvider);

        RunnerOperationState reserved = await manager.ReserveAsync(identity, cancellationToken: TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(3));
        RunnerOperationState jitIntent = await manager.TransitionAsync(identity, RunnerLifecycleStage.JitIssued, cancellationToken: TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        RunnerIdentity linkedIdentity = new(scope, identity.OperationId, identity.RunnerName, 701);
        RunnerOperationState associated = await manager.AssociateGitHubRunnerAsync(linkedIdentity, cancellationToken: TestContext.Current.CancellationToken);
        RunnerOperationState createIntent = await manager.TransitionAsync(linkedIdentity, RunnerLifecycleStage.Creating, cancellationToken: TestContext.Current.CancellationToken);
        await using var persistedDb = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "runner-state.sqlite3"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await persistedDb.OpenAsync(TestContext.Current.CancellationToken);
        await using var selectPayload = persistedDb.CreateCommand();
        selectPayload.CommandText = "SELECT payload FROM operations";
        string persistedJson = Assert.IsType<string>(await selectPayload.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Contains(identity.RunnerName, persistedJson, StringComparison.Ordinal);

        Assert.Equal(RunnerLifecycleStage.Reserved, reserved.Stage);
        Assert.Equal(0L, reserved.Revision);
        Assert.Equal(RunnerLifecycleStage.JitIssued, jitIntent.Stage);
        Assert.Equal(1L, jitIntent.Revision);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 1, 0, 3, TimeSpan.Zero), jitIntent.UpdatedAt);
        Assert.Equal(2L, associated.Revision);
        Assert.Equal(701L, associated.Identity.GitHubRunnerId);
        Assert.Equal(RunnerLifecycleStage.Creating, createIntent.Stage);
        Assert.Equal(3L, createIntent.Revision);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 1, 0, 8, TimeSpan.Zero), createIntent.UpdatedAt);
        Assert.DoesNotContain("jitConfig", persistedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", persistedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pat", persistedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("appCredential", persistedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", persistedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransitionAsync_RejectsSkippedIntentAndDoesNotAdvanceTheJournal()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await manager.ReserveAsync(identity, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.TransitionAsync(identity, RunnerLifecycleStage.Starting, "container-1", cancellationToken: TestContext.Current.CancellationToken));

        RunnerOperationState current = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(RunnerLifecycleStage.Reserved, current.Stage);
        Assert.Equal(0L, current.Revision);
    }

    [Fact]
    public async Task AssociateGitHubRunnerAsync_LinksIdAfterJitIssueInItsOwnReplaySafeRevision()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity unlinkedIdentity = CreateIdentity(scope);
        RunnerIdentity linkedIdentity = new(scope, unlinkedIdentity.OperationId, unlinkedIdentity.RunnerName, 701);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await manager.ReserveAsync(unlinkedIdentity, cancellationToken: TestContext.Current.CancellationToken);
        await manager.TransitionAsync(unlinkedIdentity, RunnerLifecycleStage.JitIssued, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.TransitionAsync(linkedIdentity, RunnerLifecycleStage.Creating, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.TransitionAsync(unlinkedIdentity, RunnerLifecycleStage.Creating, cancellationToken: TestContext.Current.CancellationToken));

        RunnerOperationState associated = await manager.AssociateGitHubRunnerAsync(linkedIdentity, cancellationToken: TestContext.Current.CancellationToken);
        RunnerOperationState replayedAssociation = await manager.AssociateGitHubRunnerAsync(linkedIdentity, cancellationToken: TestContext.Current.CancellationToken);
        RunnerOperationState creating = await manager.TransitionAsync(linkedIdentity, RunnerLifecycleStage.Creating, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunnerLifecycleStage.JitIssued, associated.Stage);
        Assert.Equal(2L, associated.Revision);
        Assert.Equal(701L, associated.Identity.GitHubRunnerId);
        Assert.Equal(associated, replayedAssociation);
        Assert.Equal(RunnerLifecycleStage.Creating, creating.Stage);
        Assert.Equal(3L, creating.Revision);
        Assert.Equal(701L, creating.Identity.GitHubRunnerId);
    }

    [Fact]
    public async Task AssociateGitHubRunnerAsync_RejectsRebindingAnAlreadyLinkedId()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity unlinkedIdentity = CreateIdentity(scope);
        RunnerIdentity firstLink = new(scope, unlinkedIdentity.OperationId, unlinkedIdentity.RunnerName, 701);
        RunnerIdentity replacement = new(scope, unlinkedIdentity.OperationId, unlinkedIdentity.RunnerName, 702);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await manager.ReserveAsync(unlinkedIdentity, cancellationToken: TestContext.Current.CancellationToken);
        await manager.TransitionAsync(unlinkedIdentity, RunnerLifecycleStage.JitIssued, cancellationToken: TestContext.Current.CancellationToken);
        await manager.AssociateGitHubRunnerAsync(firstLink, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AssociateGitHubRunnerAsync(replacement, cancellationToken: TestContext.Current.CancellationToken));

        RunnerOperationState current = Assert.IsType<RunnerOperationState>(await journal.FindAsync(unlinkedIdentity.OperationId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(701L, current.Identity.GitHubRunnerId);
    }

    [Fact]
    public async Task TransitionAsync_IgnoresDuplicateAndOutOfOrderLifecycleMessages()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await manager.ReserveAsync(identity, cancellationToken: TestContext.Current.CancellationToken);
        identity = await StartRunnerAsync(manager, identity);
        await manager.RecordActivityAsync(identity, RunnerActivityEvidence.ConfirmedIdle, cancellationToken: TestContext.Current.CancellationToken);
        RunnerOperationState busy = await manager.RecordActivityAsync(identity, RunnerActivityEvidence.JobAssigned, cancellationToken: TestContext.Current.CancellationToken);

        RunnerOperationState duplicateBusy = await manager.RecordActivityAsync(identity, RunnerActivityEvidence.JobAssigned, cancellationToken: TestContext.Current.CancellationToken);
        RunnerOperationState delayedIdle = await manager.RecordActivityAsync(identity, RunnerActivityEvidence.ConfirmedIdle, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(busy, duplicateBusy);
        Assert.Equal(busy, delayedIdle);
        Assert.Equal(6L, delayedIdle.Revision);
    }

    [Fact]
    public async Task TransitionAsync_AllowsRemovalOfConfirmedIdleRunner()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await manager.ReserveAsync(identity, cancellationToken: TestContext.Current.CancellationToken);
        identity = await StartRunnerAsync(manager, identity);
        await manager.RecordActivityAsync(identity, RunnerActivityEvidence.ConfirmedIdle, cancellationToken: TestContext.Current.CancellationToken);

        RunnerOperationState removing = await manager.TransitionAsync(identity, RunnerLifecycleStage.Removing, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunnerLifecycleStage.Removing, removing.Stage);
        Assert.Equal("container-1", removing.ProviderResourceId);
    }

    [Fact]
    public async Task TransitionAsync_RejectsRemovalOfBusyRunner()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await manager.ReserveAsync(identity, cancellationToken: TestContext.Current.CancellationToken);
        identity = await StartRunnerAsync(manager, identity);
        await manager.RecordActivityAsync(identity, RunnerActivityEvidence.JobAssigned, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.TransitionAsync(identity, RunnerLifecycleStage.Removing, cancellationToken: TestContext.Current.CancellationToken));

        RunnerOperationState current = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(RunnerLifecycleStage.Busy, current.Stage);
    }

    [Fact]
    public async Task CompletedRemovalAndGone_TombstoneWinsOverReplayedMessages()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await manager.ReserveAsync(identity, cancellationToken: TestContext.Current.CancellationToken);
        identity = await StartRunnerAsync(manager, identity);
        await manager.RecordActivityAsync(identity, RunnerActivityEvidence.JobAssigned, cancellationToken: TestContext.Current.CancellationToken);
        await manager.RecordActivityAsync(identity, RunnerActivityEvidence.JobCompleted, cancellationToken: TestContext.Current.CancellationToken);
        await manager.TransitionAsync(identity, RunnerLifecycleStage.Removing, cancellationToken: TestContext.Current.CancellationToken);

        RunnerOperationState gone = await manager.ConfirmGoneAsync(identity, cancellationToken: TestContext.Current.CancellationToken);
        RunnerOperationState delayedCompletion = await manager.RecordActivityAsync(identity, RunnerActivityEvidence.JobCompleted, cancellationToken: TestContext.Current.CancellationToken);
        RunnerOperationState replayedReservation = await manager.ReserveAsync(identity, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunnerLifecycleStage.Gone, gone.Stage);
        Assert.Equal(gone, delayedCompletion);
        Assert.Equal(gone, replayedReservation);
        Assert.Equal(gone.Revision, delayedCompletion.Revision);
    }

    [Theory]
    [InlineData(RunnerLifecycleStage.Idle)]
    [InlineData(RunnerLifecycleStage.Busy)]
    public async Task AdoptDiscoveredAsync_RunningSurvivorBecomesUnknownRegardlessOfPersistedActivity(
        RunnerLifecycleStage priorStage)
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await manager.ReserveAsync(identity, cancellationToken: TestContext.Current.CancellationToken);
        identity = await StartRunnerAsync(manager, identity);
        RunnerActivityEvidence evidence = priorStage switch
        {
            RunnerLifecycleStage.Idle => RunnerActivityEvidence.ConfirmedIdle,
            RunnerLifecycleStage.Busy => RunnerActivityEvidence.JobAssigned,
            _ => throw new ArgumentOutOfRangeException(nameof(priorStage)),
        };
        await manager.RecordActivityAsync(identity, evidence, cancellationToken: TestContext.Current.CancellationToken);
        RunnerResource discovered = new("container-1", identity, RunnerResourceStatus.Running);

        RunnerOperationState recovered = await manager.AdoptDiscoveredAsync(discovered, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunnerLifecycleStage.Unknown, recovered.Stage);
        Assert.Equal(RunnerResourceStatus.Running, recovered.LastObservedProviderStatus);
        Assert.Equal("container-1", recovered.ProviderResourceId);
        Assert.Equal(6L, recovered.Revision);
    }

    [Fact]
    public async Task AdoptDiscoveredAsync_RecordsUnjournaledResourcesAsUnknownAndReplayIsIdempotent()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        RunnerResource discovered = new("container-2", identity, RunnerResourceStatus.Exited, exitCode: 0);

        RunnerOperationState adopted = await manager.AdoptDiscoveredAsync(discovered, cancellationToken: TestContext.Current.CancellationToken);
        RunnerOperationState replayed = await manager.AdoptDiscoveredAsync(discovered, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunnerLifecycleStage.Unknown, adopted.Stage);
        Assert.Equal(RunnerResourceStatus.Exited, adopted.LastObservedProviderStatus);
        Assert.Equal(0L, adopted.Revision);
        Assert.Equal(adopted, replayed);
    }

    [Fact]
    public async Task AdoptDiscoveredAsync_RejectsProviderResourceIdRebinding()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await manager.AdoptDiscoveredAsync(new RunnerResource("container-original", identity, RunnerResourceStatus.Unknown), cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.AdoptDiscoveredAsync(
            new RunnerResource("container-replacement", identity, RunnerResourceStatus.Unknown), cancellationToken: TestContext.Current.CancellationToken));

        RunnerOperationState current = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("container-original", current.ProviderResourceId);
    }

    [Fact]
    public async Task AdoptDiscoveredAsync_RunningResourceRequiresActivityEvidenceBeforeKnownState()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await manager.AdoptDiscoveredAsync(new RunnerResource("container-4", identity, RunnerResourceStatus.Running), cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.TransitionAsync(identity, RunnerLifecycleStage.Removing, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.TransitionAsync(identity, RunnerLifecycleStage.Idle, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.TransitionAsync(identity, RunnerLifecycleStage.Busy, cancellationToken: TestContext.Current.CancellationToken));

        RunnerOperationState current = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(RunnerLifecycleStage.Unknown, current.Stage);

        RunnerOperationState resolved = await manager.RecordActivityAsync(identity, RunnerActivityEvidence.JobAssigned, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RunnerLifecycleStage.Busy, resolved.Stage);
    }

    [Theory]
    [InlineData(RunnerResourceStatus.Exited)]
    [InlineData(RunnerResourceStatus.Dead)]
    public async Task BeginRemovalAsync_RequiresTerminalCapabilityForExitedSurvivor(RunnerResourceStatus terminalStatus)
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        RunnerResource exited = new("container-5", identity, terminalStatus, exitCode: 0);
        RunnerProviderCapabilities withoutOwnershipEvidence = new(
            "docker",
            [RunnerArchitecture.X64],
            supportsOwnershipScopedDiscovery: false,
            providesTerminalStateEvidence: true);
        RunnerProviderCapabilities withoutTerminalEvidence = new(
            "docker",
            [RunnerArchitecture.X64],
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.BeginRemovalAsync(exited, withoutOwnershipEvidence, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await journal.FindAsync(identity.OperationId, cancellationToken: TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.BeginRemovalAsync(exited, withoutTerminalEvidence, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await journal.FindAsync(identity.OperationId, cancellationToken: TestContext.Current.CancellationToken));

        RunnerProviderCapabilities withTerminalEvidence = new(
            "docker",
            [RunnerArchitecture.X64],
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: true);
        RunnerOperationState removing = await manager.BeginRemovalAsync(exited, withTerminalEvidence, cancellationToken: TestContext.Current.CancellationToken);
        RunnerOperationState gone = await manager.ConfirmGoneAsync(identity, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunnerLifecycleStage.Removing, removing.Stage);
        Assert.Equal(terminalStatus, removing.LastObservedProviderStatus);
        Assert.Equal(RunnerLifecycleStage.Gone, gone.Stage);
        Assert.Equal(RunnerResourceStatus.Missing, gone.LastObservedProviderStatus);
    }

    [Theory]
    [InlineData(RunnerResourceStatus.Running)]
    [InlineData(RunnerResourceStatus.Unknown)]
    public async Task BeginRemovalAsync_ProtectsRunningAndUncertainResources(RunnerResourceStatus status)
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        RunnerResource resource = new("container-active", identity, status);
        RunnerProviderCapabilities capabilities = new(
            "docker",
            [RunnerArchitecture.X64],
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.BeginRemovalAsync(resource, capabilities, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(await journal.FindAsync(identity.OperationId, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BeginRemovalAsync_CompensatesCreatedNeverStartedResource()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        RunnerResource created = new("container-6", identity, RunnerResourceStatus.Created);
        RunnerProviderCapabilities capabilities = new(
            "docker",
            [RunnerArchitecture.Arm64],
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: false);

        RunnerOperationState removing = await manager.BeginRemovalAsync(created, capabilities, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunnerLifecycleStage.Removing, removing.Stage);
        Assert.Equal(RunnerResourceStatus.Created, removing.LastObservedProviderStatus);
        Assert.Equal("container-6", removing.ProviderResourceId);
    }

    [Fact]
    public async Task BeginRemovalAsync_RejectsCreatedResourceWithStartTimestamp()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        RunnerResource createdWithStartTime = new(
            "container-8",
            identity,
            RunnerResourceStatus.Created,
            startedAt: DateTimeOffset.UnixEpoch);
        RunnerProviderCapabilities capabilities = new(
            "docker",
            [RunnerArchitecture.X64],
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.BeginRemovalAsync(createdWithStartTime, capabilities, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(await journal.FindAsync(identity.OperationId, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BeginRemovalAsync_RejectsCreatedEvidenceThatContradictsAPreviouslyRunningResource()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        RunnerProviderCapabilities capabilities = new(
            "docker",
            [RunnerArchitecture.X64],
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: true);
        await manager.AdoptDiscoveredAsync(new RunnerResource("container-7", identity, RunnerResourceStatus.Running), cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.BeginRemovalAsync(
            new RunnerResource("container-7", identity, RunnerResourceStatus.Created),
            capabilities, cancellationToken: TestContext.Current.CancellationToken));

        RunnerOperationState current = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(RunnerLifecycleStage.Unknown, current.Stage);
    }

    [Fact]
    public async Task BeginRemovalAsync_MissingProviderResourceWritesGoneTombstone()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        using RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await manager.AdoptDiscoveredAsync(new RunnerResource("container-9", identity, RunnerResourceStatus.Running), cancellationToken: TestContext.Current.CancellationToken);

        RunnerOperationState gone = await manager.BeginRemovalAsync(
            new RunnerResource("container-9", identity, RunnerResourceStatus.Missing),
            new RunnerProviderCapabilities(
                "docker",
                [RunnerArchitecture.X64],
                supportsOwnershipScopedDiscovery: true,
                providesTerminalStateEvidence: false), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RunnerLifecycleStage.Gone, gone.Stage);
        Assert.Equal(RunnerResourceStatus.Missing, gone.LastObservedProviderStatus);
    }

    [Fact]
    public async Task AdoptDiscoveredAsync_RejectsResourceThatContradictsGoneTombstone()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await manager.ReserveAsync(identity, cancellationToken: TestContext.Current.CancellationToken);
        await manager.TransitionAsync(identity, RunnerLifecycleStage.Failed, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.AdoptDiscoveredAsync(
            new RunnerResource("container-3", identity, RunnerResourceStatus.Running), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedJournalWrite_DoesNotAdvanceIntentAndRetryCanPersistIt()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        FileRunnerStateJournal fileJournal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        await using FailingWriteJournal journal = new(fileJournal);
        RunnerLifecycleManager manager = CreateManager(journal, new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await manager.ReserveAsync(identity, cancellationToken: TestContext.Current.CancellationToken);
        journal.FailNextWrite();

        await Assert.ThrowsAsync<IOException>(() => manager.TransitionAsync(identity, RunnerLifecycleStage.JitIssued, cancellationToken: TestContext.Current.CancellationToken));

        RunnerOperationState afterFailure = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(RunnerLifecycleStage.Reserved, afterFailure.Stage);

        RunnerOperationState retry = await manager.TransitionAsync(identity, RunnerLifecycleStage.JitIssued, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RunnerLifecycleStage.JitIssued, retry.Stage);
        Assert.Equal(1L, retry.Revision);
    }

    private static RunnerLifecycleManager CreateManager(IRunnerStateJournal journal, TimeProvider timeProvider) =>
        new(journal, timeProvider, NullLogger<RunnerLifecycleManager>.Instance);

    private static RunnerOwnershipScope CreateScope() => new("controller-a", "production", 17);

    private static RunnerIdentity CreateIdentity(RunnerOwnershipScope scope) =>
        new(scope, Guid.NewGuid(), "runner-7");

    private static async Task<RunnerIdentity> StartRunnerAsync(
        RunnerLifecycleManager manager,
        RunnerIdentity unlinkedIdentity)
    {
        await manager.TransitionAsync(unlinkedIdentity, RunnerLifecycleStage.JitIssued, cancellationToken: TestContext.Current.CancellationToken);
        RunnerIdentity linkedIdentity = new(
            unlinkedIdentity.Scope,
            unlinkedIdentity.OperationId,
            unlinkedIdentity.RunnerName,
            gitHubRunnerId: 701);
        await manager.AssociateGitHubRunnerAsync(linkedIdentity, cancellationToken: TestContext.Current.CancellationToken);
        await manager.TransitionAsync(linkedIdentity, RunnerLifecycleStage.Creating, cancellationToken: TestContext.Current.CancellationToken);
        await manager.TransitionAsync(linkedIdentity, RunnerLifecycleStage.Starting, "container-1", cancellationToken: TestContext.Current.CancellationToken);
        return linkedIdentity;
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _current = initialTime;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan delta) => _current += delta;
    }

    private sealed class TemporaryStateDirectory : IDisposable
    {
        public TemporaryStateDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"scaleset-state-{Guid.NewGuid():N}");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FailingWriteJournal(IRunnerStateJournal inner) : IRunnerStateJournal
    {
        private int _failNextWrite;

        public RunnerOwnershipScope Scope => inner.Scope;

        public Task<ImmutableArray<RunnerOperationState>> ReadAllAsync(
            CancellationToken cancellationToken = default) => inner.ReadAllAsync(cancellationToken);

        public Task<RunnerOperationState?> FindAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(operationId, cancellationToken);

        public Task WriteAsync(
            RunnerOperationState state,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _failNextWrite, 0) == 1)
            {
                throw new IOException("Injected journal write failure.");
            }

            return inner.WriteAsync(state, expectedRevision, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public void FailNextWrite() => Interlocked.Exchange(ref _failNextWrite, 1);
    }
}
