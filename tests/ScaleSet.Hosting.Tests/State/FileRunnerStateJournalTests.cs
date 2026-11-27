using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;

namespace ScaleSet.Hosting.Tests.State;

public sealed class FileRunnerStateJournalTests(ITestOutputHelper output)
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
    public async Task OpenAsync_MigratesLegacyTombstonesAndRejectsReusingAnOperationOrName()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        string legacyPath = Path.Combine(directory.Path, "runner-state.json");
        var legacy = new
        {
            schemaVersion = 1,
            scope,
            operations = new[]
            {
                new { identity, stage = "Gone", revision = 4, updatedAt = DateTimeOffset.UnixEpoch },
            },
        };
        var token = TestContext.Current.CancellationToken;
        string original = JsonSerializer.Serialize(legacy, JsonSerializerOptions.Web);
        await File.WriteAllTextAsync(legacyPath, original, token);

        await using (FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, token))
        {
            Assert.Empty(await journal.ReadActiveAsync(token));
            var terminal = Assert.IsType<RunnerOperationState>(await journal.FindAsync(identity.OperationId, token));
            Assert.Equal(RunnerLifecycleStage.Gone, terminal.Stage);
            Assert.Equal(4L, terminal.Revision);
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.WriteAsync(
                new RunnerOperationState(identity, RunnerLifecycleStage.Reserved, 5, DateTimeOffset.UnixEpoch), 4, token));
            var sameName = new RunnerIdentity(scope, Guid.NewGuid(), identity.RunnerName);
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.WriteAsync(
                new RunnerOperationState(sameName, RunnerLifecycleStage.Reserved, 0, DateTimeOffset.UnixEpoch), -1, token));
        }

        using (JsonDocument barrier = JsonDocument.Parse(await File.ReadAllTextAsync(legacyPath, token)))
        {
            Assert.Equal(2, barrier.RootElement.GetProperty("schemaVersion").GetInt32());
        }

        await using (FileRunnerStateJournal reopened = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, token))
        {
            Assert.Equal(identity, Assert.Single(await reopened.ReadAllAsync(token)).Identity);
        }

        File.Delete(Path.Combine(directory.Path, "runner-state.sqlite3"));
        await Assert.ThrowsAsync<InvalidDataException>(() => FileRunnerStateJournal.OpenAsync(directory.Path, scope, token));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OpenAsync_RecoversInterruptedMigrationWithoutOverwritingConflictingLegacyData(
        bool schemaCommitted, bool changeLegacy)
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        RunnerIdentity identity = CreateIdentity(scope);
        CancellationToken token = TestContext.Current.CancellationToken;
        string legacyPath = Path.Combine(directory.Path, "runner-state.json");
        string databasePath = Path.Combine(directory.Path, "runner-state.sqlite3");
        string original = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            scope,
            operations = new[] { new { identity, stage = "Gone", revision = 4, updatedAt = DateTimeOffset.UnixEpoch } },
        }, JsonSerializerOptions.Web);
        await File.WriteAllTextAsync(legacyPath, original, token);

        if (schemaCommitted)
        {
            await using (FileRunnerStateJournal previouslyMigrated = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, token))
            {
                Assert.Equal(4, (await previouslyMigrated.FindAsync(identity.OperationId, token))?.Revision);
            }

            // Restore the precise disk boundary between the committed import and replacing the original.
            await File.WriteAllTextAsync(legacyPath, changeLegacy ? original + " " : original, token);
        }
        else
        {
            await using var initial = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            await initial.OpenAsync(token);
            await using SqliteTransaction interrupted = initial.BeginTransaction();
            await using SqliteCommand partial = initial.CreateCommand();
            partial.Transaction = interrupted;
            partial.CommandText = "CREATE TABLE journal (id INTEGER PRIMARY KEY); PRAGMA user_version = 2;";
            partial.ExecuteNonQuery();
            interrupted.Rollback();
            partial.Transaction = null;
            partial.CommandText = "PRAGMA user_version;";
            Assert.Equal(0L, Assert.IsType<long>(partial.ExecuteScalar()));
        }

        if (changeLegacy)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => FileRunnerStateJournal.OpenAsync(directory.Path, scope, token));
            Assert.Contains("changed since database migration", error.Message, StringComparison.Ordinal);
            Assert.Equal(original + " ", await File.ReadAllTextAsync(legacyPath, token));
            await File.WriteAllTextAsync(legacyPath, original, token);
        }

        await using FileRunnerStateJournal reopened = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, token);
        var operation = Assert.Single(await reopened.ReadAllAsync(token));
        Assert.Equal(identity, operation.Identity);
        Assert.Equal(4, operation.Revision);
        Assert.Equal(RunnerLifecycleStage.Gone, operation.Stage);
        using JsonDocument barrier = JsonDocument.Parse(await File.ReadAllTextAsync(legacyPath, token));
        Assert.Equal(2, barrier.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    public static bool LinuxNativeFileModesAvailable => OperatingSystem.IsLinux();

    [Fact(Skip = "Requires Linux filesystem modes and the shipped native SQLite.", SkipUnless = nameof(LinuxNativeFileModesAvailable))]
    public async Task NativeRollbackJournalInheritsPrivateDatabaseModeDuringCreationAndReopen()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        using TemporaryStateDirectory directory = new();
        const UnixFileMode searchable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        string databasePath = Path.Combine(directory.Path, "runner-state.sqlite3");
        string rollbackPath = databasePath + "-journal";
        CancellationToken token = TestContext.Current.CancellationToken;
        File.SetUnixFileMode(directory.Path, searchable);
        await using (FileStream placeholder = new(databasePath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = ownerOnly,
        }))
        {
            Assert.Equal(ownerOnly, File.GetUnixFileMode(databasePath));
        }

        await AssertModesDuringWriteAsync("CREATE TABLE initial_probe (id INTEGER PRIMARY KEY);");
        await using (FileRunnerStateJournal opened = await FileRunnerStateJournal.OpenAsync(directory.Path, CreateScope(), token))
        {
            Assert.Empty(await opened.ReadAllAsync(token));
        }

        await AssertModesDuringWriteAsync("UPDATE journal SET scope = scope || ' ' WHERE id = 1;");
        await using FileRunnerStateJournal reopened = await FileRunnerStateJournal.OpenAsync(directory.Path, CreateScope(), token);
        Assert.Empty(await reopened.ReadAllAsync(token));

        async Task AssertModesDuringWriteAsync(string sql)
        {
            await using var database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            await database.OpenAsync(token);
            await using SqliteTransaction transaction = database.BeginTransaction();
            await using SqliteCommand command = database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();

            Assert.Equal(searchable, File.GetUnixFileMode(directory.Path));
            Assert.Equal(ownerOnly, File.GetUnixFileMode(databasePath));
            Assert.True(File.Exists(rollbackPath));
            Assert.Equal(ownerOnly, File.GetUnixFileMode(rollbackPath));
            transaction.Rollback();
            Assert.False(File.Exists(rollbackPath));
        }
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
        Assert.False(File.Exists(Path.Combine(directory.Path, "runner-state.sqlite3")));
    }

    [Fact]
    public async Task OpenAsync_ReportsCorruptExistingDatabaseAndPreservesItsOwnershipEvidence()
    {
        using TemporaryStateDirectory directory = new();
        var token = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(directory.Path, "runner-state.sqlite3");
        byte[] invalidDatabase = [32, 88, 48, 92, 63, 24];
        await File.WriteAllBytesAsync(databasePath, invalidDatabase, token);

        await Assert.ThrowsAsync<InvalidDataException>(() => FileRunnerStateJournal.OpenAsync(directory.Path, CreateScope(), token));

        Assert.Equal(invalidDatabase, await File.ReadAllBytesAsync(databasePath, token));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_CancellationUnderNativeContentionRollsBackAndAllowsReopen(bool readerBlocksCommit)
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        CancellationToken token = TestContext.Current.CancellationToken;
        var state = new RunnerOperationState(CreateIdentity(scope), RunnerLifecycleStage.Reserved, 0, DateTimeOffset.UnixEpoch);
        string databasePath = Path.Combine(directory.Path, "runner-state.sqlite3");
        await using (FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, token))
        {
            await using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            await blocker.OpenAsync(token);
            await using SqliteTransaction held = blocker.BeginTransaction(deferred: readerBlocksCommit);
            if (readerBlocksCommit)
            {
                await using SqliteCommand count = blocker.CreateCommand();
                count.Transaction = held;
                count.CommandText = "SELECT COUNT(*) FROM operations;";
                Assert.Equal(0L, Assert.IsType<long>(count.ExecuteScalar()));
            }

            using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task write = Task.Run(() => journal.WriteAsync(state, -1, caller.Token), CancellationToken.None);
            using var entered = CancellationTokenSource.CreateLinkedTokenSource(token);
            entered.CancelAfter(TimeSpan.FromSeconds(2));
            while (GetJournalGate(journal).CurrentCount != 0
                || (readerBlocksCommit && !File.Exists(databasePath + "-journal")))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), entered.Token);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(60), token);
            Assert.False(write.IsCompleted, "The SQLite lock must hold while the operation is canceled.");
            long started = Stopwatch.GetTimestamp();
            await caller.CancelAsync();
            Exception? failure = await Record.ExceptionAsync(() => write);
            TimeSpan latency = Stopwatch.GetElapsedTime(started);
            output.WriteLine($"Native cancellation after {(readerBlocksCommit ? "commit" : "begin")} contention settled in {latency.TotalMilliseconds:F0} ms ({failure?.GetType().Name}).");

            Assert.True(latency < TimeSpan.FromSeconds(2), $"Cancellation exceeded the two-second hosting cleanup budget: {latency}.");
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            held.Rollback();
            Assert.Null(await journal.FindAsync(state.Identity.OperationId, token));
        }

        await using FileRunnerStateJournal reopened = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, token);
        await reopened.WriteAsync(state, -1, token);
        Assert.Equal(state, await reopened.FindAsync(state.Identity.OperationId, token));
    }

    [Fact]
    public async Task WriteAsync_RetriesWithinTheLockBudgetAfterTheFirstSlice()
    {
        using TemporaryStateDirectory directory = new();
        RunnerOwnershipScope scope = CreateScope();
        CancellationToken token = TestContext.Current.CancellationToken;
        var state = new RunnerOperationState(CreateIdentity(scope), RunnerLifecycleStage.Reserved, 0, DateTimeOffset.UnixEpoch);
        await using FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory.Path, scope, token);
        await using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "runner-state.sqlite3"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await blocker.OpenAsync(token);
        await using SqliteTransaction held = blocker.BeginTransaction();
        Task write = journal.WriteAsync(state, -1, token);
        await Task.Delay(TimeSpan.FromMilliseconds(1300), token);
        Assert.False(write.IsCompleted);
        held.Rollback();

        await write.WaitAsync(TimeSpan.FromSeconds(3), token);
        Assert.Equal(state, Assert.Single(await journal.ReadAllAsync(token)));
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
