using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;

namespace ScaleSet.Hosting.Tests.State;

public sealed class FileRunnerStateJournalTests
{
    [Fact]
    public async Task OpenAsync_RoundTripsScopeAndOperationState()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        DateTimeOffset updatedAt = new(2026, 10, 2, 1, 2, 3, TimeSpan.Zero);

        await using (FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken))
        {
            RunnerOperationState state = new(identity, RunnerLifecycleStage.Reserved, 0, updatedAt);
            await journal.WriteAsync(state, expectedRevision: -1, cancellationToken: TestContext.Current.CancellationToken);
        }

        await using FileRunnerStateJournal reopened = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        ImmutableArray<RunnerOperationState> states = await reopened.ReadAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        RunnerOperationState restored = Assert.Single(states);
        Assert.Equal(identity, restored.Identity);
        Assert.Equal(RunnerLifecycleStage.Reserved, restored.Stage);
        Assert.Equal(0L, restored.Revision);
        Assert.Equal(updatedAt, restored.UpdatedAt);
    }

    [Fact]
    public async Task OpenAsync_HoldsExclusiveDirectoryLeaseUntilDisposed()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        FileRunnerStateJournal first = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken));

        await first.DisposeAsync();
        await using FileRunnerStateJournal second = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(scope, second.Scope);
    }

    [Fact]
    public async Task DisposeAsync_DrainsInFlightWriteBeforeReleasingLeaseAndIsIdempotent()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        SemaphoreSlim gate = GetJournalGate(journal);
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        RunnerOperationState state = new(
            CreateIdentity(scope),
            RunnerLifecycleStage.Reserved,
            0,
            DateTimeOffset.UnixEpoch);
        Task write = journal.WriteAsync(state, expectedRevision: -1, cancellationToken: TestContext.Current.CancellationToken);
        Task firstDispose = journal.DisposeAsync().AsTask();
        Task secondDispose = journal.DisposeAsync().AsTask();

        try
        {
            Assert.Same(firstDispose, secondDispose);
            Assert.False(firstDispose.IsCompleted);
            await Assert.ThrowsAsync<IOException>(() => FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            gate.Release();
        }

        await write;
        await firstDispose;
        await using FileRunnerStateJournal reopened = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(state, Assert.Single(await reopened.ReadAllAsync(cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task OpenAsync_RejectsJournalForDifferentStableScaleSetIdentity()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope originalScope = CreateScope();
        await using (FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, originalScope, cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        RunnerOwnershipScope differentScope = new("controller-a", "production", 18);

        await Assert.ThrowsAsync<InvalidDataException>(() => FileRunnerStateJournal.OpenAsync(directory.Path, differentScope, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenAsync_RejectsMalformedJournalAndPreservesItForInspection()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        string journalPath = Path.Combine(directory.Path, "runner-state.json");
        await File.WriteAllTextAsync(journalPath, "{ malformed", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("{ malformed", await File.ReadAllTextAsync(journalPath, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenAsync_RejectsUnsupportedSchemaWithoutReplacingTheJournal()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        string journalPath = Path.Combine(directory.Path, "runner-state.json");
        string unsupportedDocument = JsonSerializer.Serialize(new
        {
            schemaVersion = 99,
            scope = new { controllerId = scope.ControllerId, scaleSetKey = scope.ScaleSetKey, scaleSetId = scope.ScaleSetId },
            operations = Array.Empty<object>(),
        });
        await File.WriteAllTextAsync(journalPath, unsupportedDocument, cancellationToken: TestContext.Current.CancellationToken);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Unsupported", error.Message, StringComparison.Ordinal);
        Assert.Equal(unsupportedDocument, await File.ReadAllTextAsync(journalPath, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenAsync_RejectsUnknownCredentialBearingJournalFields()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        string journalPath = Path.Combine(directory.Path, "runner-state.json");
        const string forbiddenField = "encoded-jit-secret-for-test";
        string document = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            scope = new { controllerId = scope.ControllerId, scaleSetKey = scope.ScaleSetKey, scaleSetId = scope.ScaleSetId },
            operations = Array.Empty<object>(),
            jitConfig = forbiddenField,
        });
        await File.WriteAllTextAsync(journalPath, document, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(document, await File.ReadAllTextAsync(journalPath, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenAsync_WhenAtomicInitializationCannotReplaceDirectory_LeavesNoTemporaryFile()
    {
        using TemporaryStateDirectory directory = new();
        Directory.CreateDirectory(Path.Combine(directory.Path, "runner-state.json"));

        Exception exception = await Assert.ThrowsAnyAsync<Exception>(() => FileRunnerStateJournal.OpenAsync(directory.Path, CreateScope(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(exception is IOException or UnauthorizedAccessException, $"Unexpected failure type: {exception.GetType().Name}.");

        Assert.True(Directory.Exists(Path.Combine(directory.Path, "runner-state.json")));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_RejectsStaleRevisionAndKeepsCurrentState()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        RunnerOperationState initial = new(identity, RunnerLifecycleStage.Reserved, 0, DateTimeOffset.UnixEpoch);
        await journal.WriteAsync(initial, expectedRevision: -1, cancellationToken: TestContext.Current.CancellationToken);
        RunnerOperationState stale = new(identity, RunnerLifecycleStage.JitIssued, 1, DateTimeOffset.UnixEpoch.AddSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.WriteAsync(stale, expectedRevision: -1, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(initial, await journal.FindAsync(identity.OperationId, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAsync_AllowsOneWayGitHubRunnerIdLinkAndRejectsRebinding()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity unlinked = CreateIdentity(scope);
        RunnerIdentity firstLink = new(scope, unlinked.OperationId, unlinked.RunnerName, 701);
        RunnerIdentity replacement = new(scope, unlinked.OperationId, unlinked.RunnerName, 702);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, cancellationToken: TestContext.Current.CancellationToken);
        await journal.WriteAsync(
            new RunnerOperationState(unlinked, RunnerLifecycleStage.Reserved, 0, DateTimeOffset.UnixEpoch),
            expectedRevision: -1, cancellationToken: TestContext.Current.CancellationToken);
        await journal.WriteAsync(
            new RunnerOperationState(firstLink, RunnerLifecycleStage.JitIssued, 1, DateTimeOffset.UnixEpoch.AddSeconds(1)),
            expectedRevision: 0, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.WriteAsync(
            new RunnerOperationState(replacement, RunnerLifecycleStage.Creating, 2, DateTimeOffset.UnixEpoch.AddSeconds(2)),
            expectedRevision: 1, cancellationToken: TestContext.Current.CancellationToken));

        RunnerOperationState current = Assert.IsType<RunnerOperationState>(await journal.FindAsync(unlinked.OperationId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(701L, current.Identity.GitHubRunnerId);
        Assert.Equal(RunnerLifecycleStage.JitIssued, current.Stage);
    }

    private static RunnerOwnershipScope CreateScope() => new("controller-a", "production", 17);

    private static RunnerIdentity CreateIdentity(RunnerOwnershipScope scope) =>
        new(scope, Guid.NewGuid(), "runner-7");

    private static SemaphoreSlim GetJournalGate(FileRunnerStateJournal journal)
    {
        FieldInfo field = typeof(FileRunnerStateJournal).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The journal operation gate was not found.");
        return Assert.IsType<SemaphoreSlim>(field.GetValue(journal));
    }

    private sealed class TemporaryStateDirectory : IDisposable
    {
        public TemporaryStateDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"scaleset-state-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
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
}
