using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScaleSet.Hosting.Providers;

namespace ScaleSet.Hosting.State;

/// <summary>
/// File-backed operation journal that holds an exclusive lease for its state
/// directory and replaces the journal through a same-directory temporary file.
/// </summary>
public sealed class FileRunnerStateJournal : IRunnerStateJournal
{
    private const int CurrentSchemaVersion = 1;
    private const string JournalFileName = "runner-state.json";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly string _journalPath;
    private readonly FileStream _lease;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycleLock = new();
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ImmutableArray<RunnerOperationState> _operations;
    private Task? _disposeTask;
    /// <summary>Includes operations admitted but still waiting for the journal gate.</summary>
    private int _activeOperations;
    private bool _disposeStarted;

    private FileRunnerStateJournal(
        RunnerOwnershipScope scope,
        string journalPath,
        FileStream lease,
        ImmutableArray<RunnerOperationState> operations)
    {
        Scope = scope;
        _journalPath = journalPath;
        _lease = lease;
        _operations = operations;
    }

    public RunnerOwnershipScope Scope { get; }

    /// <summary>
    /// Opens a journal for one ownership scope and takes the exclusive lease for
    /// its directory. Use a stable per-scale-set directory below the mounted state root.
    /// </summary>
    public static async Task<FileRunnerStateJournal> OpenAsync(
        string stateDirectory,
        RunnerOwnershipScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        string fullPath = Path.GetFullPath(stateDirectory);
        Directory.CreateDirectory(fullPath);
        string leasePath = Path.Combine(fullPath, ".runner-state.lease");
        FileStream lease = new(
            leasePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.Asynchronous);

        try
        {
            string journalPath = Path.Combine(fullPath, JournalFileName);
            ImmutableArray<RunnerOperationState> operations;
            if (File.Exists(journalPath))
            {
                operations = await ReadDocumentAsync(journalPath, scope, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                operations = ImmutableArray<RunnerOperationState>.Empty;
                await WriteDocumentAsync(journalPath, scope, operations, cancellationToken).ConfigureAwait(false);
            }

            return new FileRunnerStateJournal(scope, journalPath, lease, operations);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ImmutableArray<RunnerOperationState>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _operations;
        }
        finally
        {
            ExitOperation();
        }
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
            return Find(_operations, operationId);
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

            RunnerOperationState? current = Find(_operations, state.Identity.OperationId);
            long? currentRevision = current?.Revision;
            if (currentRevision != (expectedRevision < 0 ? null : expectedRevision))
            {
                throw new InvalidOperationException("The operation state changed before this revision could be saved.");
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

            if (current is null && _operations.Any(existing =>
                    existing.Identity.RunnerName.Equals(state.Identity.RunnerName, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("A runner name cannot be reused while its operation record is retained.");
            }

            ImmutableArray<RunnerOperationState> next = current is null
                ? _operations.Add(state)
                : _operations.Replace(current, state);

            await WriteDocumentAsync(_journalPath, Scope, next, cancellationToken).ConfigureAwait(false);
            _operations = next;
        }
        finally
        {
            ExitOperation();
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
            await _lease.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Dispose();
        }
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

            if (document.SchemaVersion != CurrentSchemaVersion)
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

    private static async Task WriteDocumentAsync(
        string journalPath,
        RunnerOwnershipScope scope,
        ImmutableArray<RunnerOperationState> operations,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(journalPath)
            ?? throw new InvalidOperationException("The journal path has no containing directory.");
        string temporaryPath = Path.Combine(directory, $".{JournalFileName}.{Guid.NewGuid():N}.tmp");
        JournalDocument document = new(CurrentSchemaVersion, scope, operations.ToArray());

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, journalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
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

    private static RunnerOperationState? Find(ImmutableArray<RunnerOperationState> operations, Guid operationId) =>
        operations.FirstOrDefault(operation => operation.Identity.OperationId == operationId);

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
