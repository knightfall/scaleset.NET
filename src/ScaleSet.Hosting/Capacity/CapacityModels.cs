using System.Text.Json.Serialization;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.Scaling;

namespace ScaleSet.Hosting.Capacity;

/// <summary>CPU and memory quantities used by host-capacity accounting.</summary>
public readonly record struct CapacityResourceQuantities
{
    /// <summary>Creates known or partially known CPU and memory quantities.</summary>
    [JsonConstructor]
    public CapacityResourceQuantities(decimal? cpuCores, long? memoryMiB)
    {
        if (cpuCores is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cpuCores), cpuCores, "CPU cores must be zero or greater when known.");
        }

        if (memoryMiB is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryMiB), memoryMiB, "Memory must be zero or greater when known.");
        }

        CpuCores = cpuCores;
        MemoryMiB = memoryMiB;
    }

    /// <summary>Gets the virtual CPU core quantity, or <see langword="null"/> when unknown.</summary>
    public decimal? CpuCores { get; }

    /// <summary>Gets the memory quantity in mebibytes, or <see langword="null"/> when unknown.</summary>
    public long? MemoryMiB { get; }

    /// <summary>Gets whether both resource quantities are known.</summary>
    public bool IsFullyKnown => CpuCores.HasValue && MemoryMiB.HasValue;

    /// <summary>Creates a fully known quantity from a configured runner profile.</summary>
    public static CapacityResourceQuantities FromRunnerProfile(RunnerProfileOptions profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new CapacityResourceQuantities(profile.Cpu, profile.Memory);
    }

    internal static CapacityResourceQuantities MergeObservation(
        CapacityResourceQuantities current,
        CapacityResourceQuantities observed,
        out bool conflict)
    {
        conflict = false;
        decimal? cpu = Merge(current.CpuCores, observed.CpuCores, ref conflict);
        long? memory = Merge(current.MemoryMiB, observed.MemoryMiB, ref conflict);
        return new CapacityResourceQuantities(cpu, memory);
    }

    private static decimal? Merge(decimal? current, decimal? observed, ref bool conflict)
    {
        if (!current.HasValue)
        {
            return observed;
        }

        if (!observed.HasValue || current.Value == observed.Value)
        {
            return current;
        }

        conflict = true;
        return null;
    }

    private static long? Merge(long? current, long? observed, ref bool conflict)
    {
        if (!current.HasValue)
        {
            return observed;
        }

        if (!observed.HasValue || current.Value == observed.Value)
        {
            return current;
        }

        conflict = true;
        return null;
    }
}

/// <summary>Aggregate limits for one controller host.</summary>
public sealed record HostCapacityLimits
{
    /// <summary>Creates validated aggregate host limits.</summary>
    public HostCapacityLimits(
        int? maxTotalRunners,
        decimal? cpuBudget,
        long? memoryBudgetMiB,
        int creationConcurrency)
    {
        if (maxTotalRunners is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTotalRunners), maxTotalRunners, "The runner limit must be zero or greater when configured.");
        }

        if (cpuBudget is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cpuBudget), cpuBudget, "The CPU budget must be greater than zero when configured.");
        }

        if (memoryBudgetMiB is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryBudgetMiB), memoryBudgetMiB, "The memory budget must be greater than zero when configured.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(creationConcurrency);

        MaxTotalRunners = maxTotalRunners;
        CpuBudget = cpuBudget;
        MemoryBudgetMiB = memoryBudgetMiB;
        CreationConcurrency = creationConcurrency;
    }

    /// <summary>Gets the optional aggregate runner-count limit.</summary>
    public int? MaxTotalRunners { get; }

    /// <summary>Gets the optional aggregate CPU budget in virtual cores.</summary>
    public decimal? CpuBudget { get; }

    /// <summary>Gets the optional aggregate memory budget in mebibytes.</summary>
    public long? MemoryBudgetMiB { get; }

    /// <summary>Gets the shared maximum number of concurrent runner creations.</summary>
    public int CreationConcurrency { get; }

    /// <summary>Maps the accepted configuration model to the capacity-neutral limits.</summary>
    public static HostCapacityLimits FromOptions(HostCapacityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new HostCapacityLimits(
            options.MaxTotalRunners,
            options.CpuBudget,
            options.MemoryBudget,
            options.CreationConcurrency);
    }
}

/// <summary>Per-scale-set scaling policy and resource profile used by the scheduler.</summary>
public sealed record ScaleSetCapacityDefinition
{
    /// <summary>Creates a validated per-scale-set capacity definition.</summary>
    public ScaleSetCapacityDefinition(
        string key,
        ScalingPolicy policy,
        int minimumRunners,
        int maximumRunners,
        CapacityResourceQuantities runnerResources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "The scaling policy is not defined.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(minimumRunners);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRunners);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumRunners, maximumRunners, nameof(maximumRunners));
        if (!runnerResources.IsFullyKnown)
        {
            throw new ArgumentException("A configured runner profile must have known CPU and memory quantities.", nameof(runnerResources));
        }

        Key = key;
        Policy = policy;
        MinimumRunners = minimumRunners;
        MaximumRunners = maximumRunners;
        RunnerResources = runnerResources;
    }

    /// <summary>Gets the local scale-set key.</summary>
    public string Key { get; }

    /// <summary>Gets the selected desired-capacity policy.</summary>
    public ScalingPolicy Policy { get; }

    /// <summary>Gets the minimum total runner count.</summary>
    public int MinimumRunners { get; }

    /// <summary>Gets the maximum total runner count.</summary>
    public int MaximumRunners { get; }

    /// <summary>Gets the fully known resource profile for a new runner.</summary>
    public CapacityResourceQuantities RunnerResources { get; }

    /// <summary>Maps a validated scale-set option to a capacity-neutral definition.</summary>
    public static ScaleSetCapacityDefinition FromOptions(ScaleSetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Key))
        {
            throw new ArgumentException("A scale-set key is required.", nameof(options));
        }

        ArgumentNullException.ThrowIfNull(options.Scaling);
        ArgumentNullException.ThrowIfNull(options.RunnerProfile);

        return new ScaleSetCapacityDefinition(
            options.Key,
            options.Scaling.Policy,
            options.Scaling.MinRunners,
            options.Scaling.MaxRunners,
            CapacityResourceQuantities.FromRunnerProfile(options.RunnerProfile));
    }
}

/// <summary>Capacity-accounted lifecycle intent for one runner operation.</summary>
public enum CapacityOccupancyState
{
    /// <summary>Capacity is held before a job is acquired.</summary>
    Reserved = 0,

    /// <summary>A just-in-time runner configuration has been issued.</summary>
    JitIssued = 1,

    /// <summary>Runner resource creation is in progress.</summary>
    Creating = 2,

    /// <summary>The created runner resource is starting.</summary>
    Starting = 3,

    /// <summary>The runner is known to be idle.</summary>
    Idle = 4,

    /// <summary>The runner is known to be handling a job.</summary>
    Busy = 5,

    /// <summary>Activity is uncertain and the runner must remain protected.</summary>
    Unknown = 6,

    /// <summary>Removal is in progress; capacity remains held until absence is confirmed.</summary>
    Removing = 7,

    /// <summary>A failure has not yet been confirmed absent, so it remains capacity-accounted.</summary>
    Failed = 8,
}

/// <summary>Requests a capacity reservation for one stable runner operation.</summary>
public sealed record CapacityReservationRequest
{
    /// <summary>Creates a reservation request with fully known configured quantities.</summary>
    public CapacityReservationRequest(RunnerIdentity identity, CapacityResourceQuantities resources)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!resources.IsFullyKnown)
        {
            throw new ArgumentException("A new runner reservation requires known CPU and memory quantities.", nameof(resources));
        }

        Identity = identity;
        Resources = resources;
    }

    /// <summary>Gets the stable provider-neutral runner identity.</summary>
    public RunnerIdentity Identity { get; }

    /// <summary>Gets the CPU and memory quantities being reserved.</summary>
    public CapacityResourceQuantities Resources { get; }
}

/// <summary>Describes an already-existing runner that must be adopted into accounting.</summary>
public sealed record CapacityInventoryItem
{
    /// <summary>Creates an inventory item; unknown resource quantities are allowed.</summary>
    public CapacityInventoryItem(
        RunnerIdentity identity,
        CapacityResourceQuantities resources,
        CapacityOccupancyState state)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "The capacity state is not defined.");
        }

        Identity = identity;
        Resources = resources;
        State = state;
    }

    /// <summary>Gets the stable provider-neutral runner identity.</summary>
    public RunnerIdentity Identity { get; }

    /// <summary>Gets known resource quantities; null dimensions are treated conservatively.</summary>
    public CapacityResourceQuantities Resources { get; }

    /// <summary>Gets the lifecycle state that remains capacity-accounted.</summary>
    public CapacityOccupancyState State { get; }
}

/// <summary>A generation-guarded handle for one operation's capacity entry.</summary>
public sealed class CapacityReservationHandle
{
    internal CapacityReservationHandle(
        RunnerIdentity identity,
        CapacityResourceQuantities resources,
        long generation)
    {
        Identity = identity;
        Resources = resources;
        Generation = generation;
    }

    /// <summary>Gets the runner operation identity.</summary>
    public RunnerIdentity Identity { get; }

    /// <summary>Gets the resource quantities represented by this handle.</summary>
    public CapacityResourceQuantities Resources { get; }

    /// <summary>Gets the capacity-local generation guarding this handle from stale use.</summary>
    public long Generation { get; }
}

/// <summary>A stable operation handle with its current capacity-local state revision.</summary>
public sealed record CapacityReservationLease(CapacityReservationHandle Handle, long Revision);

/// <summary>Outcome of an atomic capacity reservation attempt.</summary>
public enum CapacityReservationStatus
{
    /// <summary>All newly requested reservations were committed.</summary>
    Reserved = 0,

    /// <summary>Every requested operation was already reserved with matching identity and quantities.</summary>
    AlreadyReserved = 1,

    /// <summary>The plan predates a reservation, release, state, or inventory change.</summary>
    StalePlan = 2,

    /// <summary>The current plan or aggregate budgets do not have enough capacity.</summary>
    InsufficientCapacity = 3,

    /// <summary>An operation identity or its resource quantities conflict with existing state.</summary>
    IdentityConflict = 4,

    /// <summary>The operation was released already and its operation ID cannot be reserved again.</summary>
    OperationAlreadyReleased = 5,

    /// <summary>The plan was produced by a different capacity coordinator.</summary>
    InvalidPlan = 6,

    /// <summary>A monotonically increasing coordinator version or handle generation is exhausted.</summary>
    VersionExhausted = 7,
}

/// <summary>Reservation result for one operation.</summary>
public sealed record CapacityReservationAttempt(
    CapacityReservationStatus Status,
    CapacityReservationHandle? Handle,
    long Revision);

/// <summary>Reservation result for an atomic set of operations.</summary>
public sealed record CapacityReservationBatchAttempt(
    CapacityReservationStatus Status,
    IReadOnlyList<CapacityReservationLease> Leases);

/// <summary>Outcome of adopting provider-discovered inventory.</summary>
public enum CapacityInventoryAdoptionStatus
{
    /// <summary>A new inventory entry was added.</summary>
    Adopted = 0,

    /// <summary>The matching inventory entry already existed without changes.</summary>
    AlreadyAdopted = 1,

    /// <summary>The lifecycle state or newly observed quantities updated the existing entry.</summary>
    Updated = 2,

    /// <summary>Observed quantities disagreed; affected dimensions are now unknown and block new use.</summary>
    ResourceConflict = 3,

    /// <summary>The operation ID is already assigned to a different stable runner identity.</summary>
    IdentityConflict = 4,

    /// <summary>A coordinator version or handle generation cannot advance without overflowing.</summary>
    VersionExhausted = 5,
}

/// <summary>Inventory adoption result and current generation-guarded handle.</summary>
public sealed record CapacityInventoryAdoptionAttempt(
    CapacityInventoryAdoptionStatus Status,
    CapacityReservationHandle? Handle,
    long Revision);

/// <summary>Outcome of an operation-state update against a capacity revision.</summary>
public enum CapacityStateUpdateStatus
{
    /// <summary>The state changed and the capacity-local revision advanced.</summary>
    Updated = 0,

    /// <summary>The requested state was already current.</summary>
    AlreadyCurrent = 1,

    /// <summary>The supplied handle belongs to an older operation generation.</summary>
    StaleHandle = 2,

    /// <summary>The expected capacity-local revision is stale.</summary>
    StaleRevision = 3,

    /// <summary>The matching generation has already been released.</summary>
    AlreadyReleased = 4,

    /// <summary>The capacity-local revision cannot advance without overflowing.</summary>
    RevisionExhausted = 5,
}

/// <summary>State update result with the current capacity-local revision.</summary>
public sealed record CapacityStateUpdateAttempt(CapacityStateUpdateStatus Status, long CurrentRevision);

/// <summary>Outcome of releasing a generation- and revision-guarded capacity entry.</summary>
public enum CapacityReleaseStatus
{
    /// <summary>The occupancy entry was released.</summary>
    Released = 0,

    /// <summary>This exact generation and revision had already been released.</summary>
    AlreadyReleased = 1,

    /// <summary>The supplied handle belongs to a different or older operation generation.</summary>
    StaleHandle = 2,

    /// <summary>The operation has advanced beyond the supplied capacity-local revision.</summary>
    StaleRevision = 3,

    /// <summary>The coordinator version cannot advance without overflowing.</summary>
    VersionExhausted = 4,
}

/// <summary>Runner counts, resource totals, and state counts for one host or scale set.</summary>
/// <param name="RunnerCount">All capacity-accounted runner resources and intents.</param>
/// <param name="CpuCoresUsed">Exact CPU total when all CPU quantities are known.</param>
/// <param name="MemoryMiBUsed">Exact memory total when all memory quantities are known.</param>
/// <param name="KnownCpuCoresUsed">Known CPU subtotal, even if other entries are unknown.</param>
/// <param name="KnownMemoryMiBUsed">Known memory subtotal, even if other entries are unknown.</param>
/// <param name="UnknownCpuCount">Entries whose CPU quantity is unknown.</param>
/// <param name="UnknownMemoryCount">Entries whose memory quantity is unknown.</param>
/// <param name="CpuOverflowed">Whether the checked CPU sum overflowed.</param>
/// <param name="MemoryOverflowed">Whether the checked memory sum overflowed.</param>
/// <param name="StateCounts">Occupancy counts by protected lifecycle state.</param>
public sealed record CapacityUsageSnapshot(
    long RunnerCount,
    decimal? CpuCoresUsed,
    long? MemoryMiBUsed,
    decimal? KnownCpuCoresUsed,
    long? KnownMemoryMiBUsed,
    long UnknownCpuCount,
    long UnknownMemoryCount,
    bool CpuOverflowed,
    bool MemoryOverflowed,
    IReadOnlyDictionary<CapacityOccupancyState, long> StateCounts);

/// <summary>A per-scale-set view of current capacity occupancy.</summary>
public sealed record ScaleSetCapacityUsageSnapshot(string ScaleSetKey, CapacityUsageSnapshot Usage);

/// <summary>Consistent view of host occupancy at one coordinator version.</summary>
public sealed record HostCapacitySnapshot(
    long Version,
    CapacityUsageSnapshot Usage,
    IReadOnlyDictionary<string, ScaleSetCapacityUsageSnapshot> ScaleSets,
    int ActiveCreationLeases,
    int CreationConcurrencyLimit);
