using System.Collections.Immutable;
using ScaleSet.Hosting.Providers;

namespace ScaleSet.Hosting.State;

/// <summary>Persists non-secret runner operation state for one ownership scope.</summary>
public interface IRunnerStateJournal : IAsyncDisposable
{
    RunnerOwnershipScope Scope { get; }

    Task<ImmutableArray<RunnerOperationState>> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads operations that may still own resources or unconfirmed effects.</summary>
    async Task<ImmutableArray<RunnerOperationState>> ReadActiveAsync(CancellationToken cancellationToken = default)
    {
        var operations = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return operations.Where(static operation => !IsTerminal(operation)).ToImmutableArray();
    }

    internal static bool IsTerminal(RunnerOperationState operation) =>
        operation.Stage == RunnerLifecycleStage.Gone
        || (operation.Stage == RunnerLifecycleStage.Failed && operation.ProviderResourceId is null);

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
