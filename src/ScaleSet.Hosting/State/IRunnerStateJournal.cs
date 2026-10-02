using System.Collections.Immutable;
using ScaleSet.Hosting.Providers;

namespace ScaleSet.Hosting.State;

/// <summary>Persists non-secret runner operation state for one ownership scope.</summary>
public interface IRunnerStateJournal : IAsyncDisposable
{
    RunnerOwnershipScope Scope { get; }

    Task<ImmutableArray<RunnerOperationState>> ReadAllAsync(CancellationToken cancellationToken = default);

    Task<RunnerOperationState?> FindAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the next operation revision. Use -1 as the expected revision when
    /// reserving a new operation.
    /// </summary>
    Task WriteAsync(
        RunnerOperationState state,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}
