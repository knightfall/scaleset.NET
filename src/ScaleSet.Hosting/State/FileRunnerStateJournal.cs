using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScaleSet.Hosting.Providers;

namespace ScaleSet.Hosting.State;

/// <summary>
/// Indexed, transactional runner operation journal with one exclusive owner per state directory.
/// </summary>
public sealed class FileRunnerStateJournal : IRunnerStateJournal
{
    private const int LegacySchemaVersion = 1;
    private const int DatabaseSchemaVersion = 2;
    private const int SqliteLockSliceSeconds = 1;
    private const int SqliteLockBudgetSeconds = 5;
    private static readonly TimeSpan SqliteLockRetryDelay = TimeSpan.FromMilliseconds(25);
    private const long MaximumLegacyJournalBytes = 256L * 1024 * 1024;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const string LegacyFileName = "runner-state.json";
    private const string DatabaseFileName = "runner-state.sqlite3";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly SqliteConnection _database;
    private readonly FileStream _lease;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycleLock = new();
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _disposeTask;
    /// <summary>Includes operations admitted but still waiting for the journal gate.</summary>
    private int _activeOperations;
    private bool _disposeStarted;

    private FileRunnerStateJournal(RunnerOwnershipScope scope, SqliteConnection database, FileStream lease)
    {
        Scope = scope;
        _database = database;
        _database.DefaultTimeout = SqliteLockSliceSeconds;
        _lease = lease;
    }

    internal static bool Exists(string directory) =>
        File.Exists(Path.Combine(directory, LegacyFileName)) || File.Exists(Path.Combine(directory, DatabaseFileName));

    public RunnerOwnershipScope Scope { get; }

    /// <summary>
    /// Opens a journal for one ownership scope and takes the exclusive lease for
    /// its directory. Use a stable per-scale-set directory below the mounted state root.
    /// </summary>
    /// <param name="stateDirectory">Directory for the journal and its exclusive writer lease.</param>
    /// <param name="scope">Controller and scale-set identity recorded by the journal.</param>
    /// <param name="cancellationToken">Token used to cancel opening or loading the journal.</param>
    public static async Task<FileRunnerStateJournal> OpenAsync(
        string stateDirectory,
        RunnerOwnershipScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        string fullPath = Path.GetFullPath(stateDirectory);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(fullPath);
        }
        else
        {
            Directory.CreateDirectory(fullPath, PrivateFileMode | UnixFileMode.UserExecute);
        }

        string leasePath = Path.Combine(fullPath, ".runner-state.lease");
        FileStream lease = OpenPrivateFile(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            bufferSize: 1, FileOptions.Asynchronous);

        SqliteConnection? database = null;
        string databasePath = Path.Combine(fullPath, DatabaseFileName);
        bool createdHere = false;
        try
        {
            string legacyPath = Path.Combine(fullPath, LegacyFileName);
            if (Directory.Exists(legacyPath))
            {
                throw new IOException("The legacy journal path is a directory.");
            }

            if (!File.Exists(databasePath))
            {
                await using FileStream initial = OpenPrivateFile(
                    databasePath, FileMode.CreateNew, FileAccess.Write, bufferSize: 1, FileOptions.None);
                createdHere = true;
            }

            database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = SqliteLockBudgetSeconds,
            }.ToString());
            database.Open();
            await InitializeDatabaseAsync(database, legacyPath, scope, cancellationToken).ConfigureAwait(false);
            return new FileRunnerStateJournal(scope, database, lease);
        }
        catch (Exception exception)
        {
            try
            {
                database?.Dispose();
                if (createdHere && new FileInfo(databasePath) is { Exists: true, Length: 0 })
                {
                    // A failed open may leave the placeholder. Keep any initialized or preexisting file.
                    File.Delete(databasePath);
                }
            }
            finally
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            if (exception is JsonException or SqliteException { SqliteErrorCode: 1 or 11 or 26 })
            {
                throw new InvalidDataException("The runner state database could not be read.", exception);
            }

            throw;
        }
    }

    public Task<ImmutableArray<RunnerOperationState>> ReadAllAsync(CancellationToken cancellationToken = default) =>
        ReadOperationsAsync(activeOnly: false, cancellationToken);

    public Task<ImmutableArray<RunnerOperationState>> ReadActiveAsync(CancellationToken cancellationToken = default) =>
        ReadOperationsAsync(activeOnly: true, cancellationToken);

    private async Task<ImmutableArray<RunnerOperationState>> ReadOperationsAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImmutableArray<RunnerOperationState> results = default;
            await RetryOnLockAsync(() => results = ReadOperations(activeOnly, cancellationToken), cancellationToken).ConfigureAwait(false);
            return results;
        }
        finally
        {
            ExitOperation();
        }
    }

    private ImmutableArray<RunnerOperationState> ReadOperations(bool activeOnly, CancellationToken cancellationToken)
    {
        using var select = _database.CreateCommand();
        select.CommandText = activeOnly
            ? "SELECT payload FROM operations WHERE active = 1 ORDER BY rowid"
            : "SELECT payload FROM operations ORDER BY rowid";
        using var reader = select.ExecuteReader();
        var results = ImmutableArray.CreateBuilder<RunnerOperationState>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(DecodeOperation(reader.GetString(0)));
        }

        return results.ToImmutable();
    }

    public async Task<RunnerOperationState?> FindAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("The operation ID must not be empty.", nameof(operationId));
        }

        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RunnerOperationState? result = null;
            await RetryOnLockAsync(() => result = FindOperation(operationId), cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            ExitOperation();
        }
    }

    public async Task WriteAsync(
        RunnerOperationState state,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Identity.Scope != Scope)
            {
                throw new InvalidOperationException("The operation belongs to a different ownership scope.");
            }

            if (expectedRevision < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedRevision), expectedRevision, "The expected revision must be -1 or greater.");
            }

            if (state.Revision != expectedRevision + 1)
            {
                throw new InvalidOperationException("The operation revision must be exactly one greater than the expected revision.");
            }

            await RetryOnLockAsync(() => WriteCheckedOperation(state, expectedRevision, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    private void WriteCheckedOperation(RunnerOperationState state, long expectedRevision, CancellationToken cancellationToken)
    {
        using SqliteTransaction transaction = _database.BeginTransaction();
        RunnerOperationState? current = FindOperation(state.Identity.OperationId, transaction);
        long? currentRevision = current?.Revision;
        if (currentRevision != (expectedRevision < 0 ? null : expectedRevision))
        {
            throw new InvalidOperationException("The operation state changed before this revision could be saved.");
        }

        if (current is not null && IRunnerStateJournal.IsTerminal(current))
        {
            throw new InvalidOperationException("A terminal runner operation cannot be rewritten.");
        }

        if (current is not null && !SameStableIdentity(current.Identity, state.Identity))
        {
            throw new InvalidOperationException("An operation ID cannot be reused for a different runner identity.");
        }

        if (current?.Identity.GitHubRunnerId is not null
            && current.Identity.GitHubRunnerId != state.Identity.GitHubRunnerId)
        {
            throw new InvalidOperationException("A linked GitHub runner ID cannot be removed or rebound.");
        }

        if (current?.ProviderResourceId is not null && current.ProviderResourceId != state.ProviderResourceId)
        {
            throw new InvalidOperationException("A linked provider resource ID cannot be removed or rebound.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            WriteOperation(_database, transaction, state);
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == 2067)
        {
            throw new InvalidOperationException("A runner name cannot be reused while its operation record is retained.", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static async Task RetryOnLockAsync(Action operation, CancellationToken cancellationToken)
    {
        // Microsoft.Data.Sqlite retries native lock failures synchronously. Keep each slice short and
        // keep the journal gate held across attempts so the connection and transaction have one owner.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                operation();
                return;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed + SqliteLockRetryDelay >= TimeSpan.FromSeconds(SqliteLockBudgetSeconds - SqliteLockSliceSeconds))
                {
                    throw;
                }

                await Task.Delay(SqliteLockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask is null)
            {
                // Close admission before waiting for every operation that already started.
                _disposeStarted = true;
                if (_activeOperations == 0)
                {
                    _operationsDrained.TrySetResult();
                }

                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task EnterOperationAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            // Count before waiting so disposal also drains queued readers/writers.
            _activeOperations++;
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            CompleteOperation();
            throw;
        }
    }

    private void ExitOperation()
    {
        _gate.Release();
        CompleteOperation();
    }

    private void CompleteOperation()
    {
        lock (_lifecycleLock)
        {
            _activeOperations--;
            if (_activeOperations == 0 && _disposeStarted)
            {
                _operationsDrained.TrySetResult();
            }
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _operationsDrained.Task.ConfigureAwait(false);
        try
        {
            _database.Dispose();
        }
        finally
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
            _gate.Dispose();
        }
    }

    private static async Task InitializeDatabaseAsync(
        SqliteConnection database, string legacyPath, RunnerOwnershipScope expectedScope, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand();
        command.CommandText = "PRAGMA synchronous = FULL;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA user_version;";
        long version = (long)command.ExecuteScalar()!;
        string? legacyHash = File.Exists(legacyPath) ? await HashLegacyAsync(legacyPath, cancellationToken).ConfigureAwait(false) : null;
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new JournalDocument(DatabaseSchemaVersion, expectedScope, []), SerializerOptions);
        string markerHash = Convert.ToHexString(SHA256.HashData(marker));
        if (version == 0)
        {
            if (legacyHash == markerHash)
            {
                throw new InvalidDataException("The migrated runner state database is missing or uninitialized.");
            }

            ImmutableArray<RunnerOperationState> legacy = legacyHash is null
                ? [] : await ReadDocumentAsync(legacyPath, expectedScope, cancellationToken).ConfigureAwait(false);
            await using var transaction = database.BeginTransaction();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE journal (id INTEGER PRIMARY KEY CHECK (id = 1), scope TEXT NOT NULL, legacy_hash TEXT);
                CREATE TABLE operations (
                    operation_id TEXT NOT NULL UNIQUE, runner_name TEXT NOT NULL UNIQUE,
                    active INTEGER NOT NULL CHECK (active IN (0, 1)), payload TEXT NOT NULL);
                CREATE INDEX operations_active ON operations(active) WHERE active = 1;
                PRAGMA user_version = 2;
                INSERT INTO journal (id, scope, legacy_hash) VALUES (1, $scope, $legacy);
                """;
            command.Parameters.AddWithValue("$scope", JsonSerializer.Serialize(expectedScope, SerializerOptions));
            command.Parameters.AddWithValue("$legacy", (object?)legacyHash ?? DBNull.Value);
            command.ExecuteNonQuery();
            foreach (RunnerOperationState operation in legacy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteOperation(database, transaction, operation);
            }

            transaction.Commit();
        }
        else
        {
            if (version != DatabaseSchemaVersion)
            {
                throw new InvalidDataException($"Unsupported runner state database version {version}.");
            }

            command.CommandText = "SELECT scope, legacy_hash FROM journal WHERE id = 1";
            await using var reader = command.ExecuteReader();
            if (!reader.Read() || JsonSerializer.Deserialize<RunnerOwnershipScope>(reader.GetString(0), SerializerOptions) != expectedScope)
            {
                throw new InvalidDataException("The runner state journal belongs to a different ownership scope.");
            }

            if (legacyHash is not null && legacyHash != markerHash
                && (reader.IsDBNull(1) || legacyHash != reader.GetString(1)))
            {
                throw new InvalidDataException("The legacy runner state journal changed since database migration.");
            }
        }

        if (legacyHash != markerHash)
        {
            // Install the downgrade barrier before admitting any v2 writes. After an interrupted
            // import, the original snapshot hash above proves that installing it is still safe.
            string temporary = legacyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (FileStream stream = OpenPrivateFile(
                    temporary, FileMode.CreateNew, FileAccess.Write, bufferSize: 4096, FileOptions.WriteThrough))
                {
                    stream.Write(marker);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, legacyPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    private RunnerOperationState? FindOperation(Guid operationId, SqliteTransaction? transaction = null)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM operations WHERE operation_id = $id";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        return command.ExecuteScalar() is string payload ? DecodeOperation(payload) : null;
    }

    private RunnerOperationState DecodeOperation(string payload)
    {
        try
        {
            RunnerOperationState? state = JsonSerializer.Deserialize<RunnerOperationState>(payload, SerializerOptions);
            if (state?.Identity?.Scope != Scope)
            {
                throw new InvalidDataException("The runner state database contains an invalid operation.");
            }

            return state;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException("The runner state database contains an invalid operation.", exception);
        }
    }

    private static void WriteOperation(SqliteConnection database, SqliteTransaction transaction, RunnerOperationState state)
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operations (operation_id, runner_name, active, payload) VALUES ($id, $name, $active, $payload)
            ON CONFLICT(operation_id) DO UPDATE SET active = excluded.active, payload = excluded.payload;
            """;
        command.Parameters.AddWithValue("$id", state.Identity.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$name", state.Identity.RunnerName);
        command.Parameters.AddWithValue("$active", !IRunnerStateJournal.IsTerminal(state));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(state, SerializerOptions));
        command.ExecuteNonQuery();
    }

    private static async Task<string> HashLegacyAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumLegacyJournalBytes)
        {
            throw new InvalidDataException("The legacy runner state journal exceeds the 256 MiB migration limit.");
        }

        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<ImmutableArray<RunnerOperationState>> ReadDocumentAsync(
        string journalPath,
        RunnerOwnershipScope expectedScope,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                journalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            JournalDocument? document = await JsonSerializer.DeserializeAsync<JournalDocument>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);

            if (document is null)
            {
                throw new InvalidDataException("The runner state journal is empty.");
            }

            if (document.SchemaVersion != LegacySchemaVersion)
            {
                throw new InvalidDataException($"Unsupported runner state schema version {document.SchemaVersion}.");
            }

            if (document.Scope is null || document.Scope != expectedScope)
            {
                throw new InvalidDataException("The runner state journal belongs to a different ownership scope.");
            }

            if (document.Operations is null)
            {
                throw new InvalidDataException("The runner state journal has no operation collection.");
            }

            ImmutableArray<RunnerOperationState> operations = document.Operations.ToImmutableArray();
            ValidateOperations(operations, expectedScope);
            return operations;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("The runner state journal is malformed or contains unsupported data.", exception);
        }
    }

    private static void ValidateOperations(ImmutableArray<RunnerOperationState> operations, RunnerOwnershipScope scope)
    {
        HashSet<Guid> operationIds = [];
        HashSet<string> runnerNames = new(StringComparer.Ordinal);

        foreach (RunnerOperationState operation in operations)
        {
            if (operation is null || operation.Identity is null || operation.Identity.Scope != scope)
            {
                throw new InvalidDataException("The runner state journal contains an operation outside its ownership scope.");
            }

            if (!operationIds.Add(operation.Identity.OperationId))
            {
                throw new InvalidDataException("The runner state journal contains a duplicate operation ID.");
            }

            if (!runnerNames.Add(operation.Identity.RunnerName))
            {
                throw new InvalidDataException("The runner state journal contains a duplicate runner name.");
            }
        }
    }

    private static FileStream OpenPrivateFile(
        string path, FileMode mode, FileAccess access, int bufferSize, FileOptions options)
    {
        var settings = new FileStreamOptions
        {
            Mode = mode,
            Access = access,
            Share = FileShare.None,
            BufferSize = bufferSize,
            Options = options,
        };
        if (!OperatingSystem.IsWindows())
        {
            settings.UnixCreateMode = PrivateFileMode;
        }

        return new FileStream(path, settings);
    }

    private static bool SameStableIdentity(RunnerIdentity left, RunnerIdentity right) =>
        left.Scope == right.Scope
        && left.OperationId == right.OperationId
        && left.RunnerName.Equals(right.RunnerName, StringComparison.Ordinal);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private sealed record JournalDocument(
        int SchemaVersion,
        RunnerOwnershipScope Scope,
        RunnerOperationState[] Operations);
}
