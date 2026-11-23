using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;

namespace ScaleSet.Hosting.Runtime;

/// <summary>Owns the resources and message loop for one configured scale set.</summary>
public interface IScaleSetRuntime : IAsyncDisposable
{
    /// <summary>Gets the stable local key used by configuration and diagnostics.</summary>
    string ScaleSetKey { get; }

    /// <summary>Reconciles journal and provider state before a message session is opened.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Opens the message session and runs its listener until cancellation or failure.</summary>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>Stops new job admission and waits for known active work within the configured drain budget.</summary>
    Task DrainAsync(CancellationToken cancellationToken);

    /// <summary>Closes the message session within its configured cleanup budget; runner resources are preserved.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>Creates one runner scale-set runtime from a validated scale-set option.</summary>
public interface IScaleSetRuntimeFactory
{
    /// <summary>Creates a runtime that shares the host-wide capacity ledger.</summary>
    Task<IScaleSetRuntime> CreateAsync(
        ScaleSetOptions scaleSet,
        HostCapacityCoordinator capacityCoordinator,
        CancellationToken cancellationToken);
}
