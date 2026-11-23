using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Listener;

namespace ScaleSet.Hosting.Telemetry;

public static class ScaleSetTelemetrySources
{
    public const string Autoscaler = "ScaleSet.Autoscaler";
    public const string Client = "ScaleSet.Client";
    public const string Hosting = "ScaleSet.Hosting";
    /// <summary>Meter name for capacity counters. Capacity activities use <see cref="Hosting"/>.</summary>
    public const string HostingCapacity = Hosting + ".Capacity";

    public const string Listener = ScaleSetListener.ActivitySourceName;
    public const string DockerProvider = "ScaleSet.Providers.Docker";

    public static ActivitySource ActivitySource { get; } =
        new(Hosting, typeof(ScaleSetTelemetrySources).Assembly.GetName().Version?.ToString(3));

    public static Activity? StartOperation(string operation, ActivityKind kind = ActivityKind.Internal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return ActivitySource.StartActivity(operation, kind);
    }
}

public enum ScaleSetTelemetryOperation
{
    DiagnosticSmoke = 0,
    Authentication = 1,
    QueuePoll = 2,
    JobAcquire = 3,
    Acknowledge = 4,
    Reconcile = 5,
    Provision = 6,
    RunnerStart = 7,
    RunnerCompletion = 8,
    Recovery = 9,
    Drain = 10,
}

public enum ScaleSetTelemetryOutcome
{
    Success = 0,
    Failure = 1,
    Empty = 2,
    Cancelled = 3,
    Unknown = 4,
}

public enum ScaleSetTelemetryProvider
{
    Docker = 0,
    Ecs = 1,
    Ec2 = 2,
    Other = 3,
}

public enum RunnerTelemetryState
{
    Reserved = 0,
    Creating = 1,
    Starting = 2,
    Idle = 3,
    Busy = 4,
    Unknown = 5,
    Removing = 6,
    Gone = 7,
    Failed = 8,
}

/// <summary>
/// Host-side instruments shared by the autoscaler and its hosting services.
/// Dynamic dimensions are reduced to a startup allow-list and finite enums.
/// </summary>
public sealed class ScaleSetTelemetryMetrics : IDisposable
{
    private const int MaximumScaleSetDimensions = 64;
    private readonly Meter _meter;
    private readonly Counter<long> _operations;
    private readonly Histogram<double> _operationDuration;
    private readonly Counter<long> _droppedLogs;
    private readonly ConcurrentDictionary<RunnerMetricKey, long> _runnerCounts = new();
    private readonly HashSet<string> _configuredScaleSetKeys;
    private readonly HostCapacityCoordinator? _capacityCoordinator;
    private readonly ScaleSetTelemetryProvider _capacityProvider;

    public ScaleSetTelemetryMetrics(IMeterFactory meterFactory, IEnumerable<string>? configuredScaleSetKeys = null)
        : this(meterFactory, configuredScaleSetKeys, null, ScaleSetTelemetryProvider.Other)
    {
    }

    /// <summary>Creates a meter that can read runner counts directly from the host capacity ledger.</summary>
    /// <param name="meterFactory">Factory that owns the metrics meter.</param>
    /// <param name="configuredScaleSetKeys">Allow-list of scale-set dimensions.</param>
    /// <param name="capacityCoordinator">Ledger to observe in place of manually reported runner counts.</param>
    /// <param name="capacityProvider">Backend shared by the ledger's scale sets.</param>
    public ScaleSetTelemetryMetrics(
        IMeterFactory meterFactory,
        IEnumerable<string>? configuredScaleSetKeys,
        HostCapacityCoordinator? capacityCoordinator,
        ScaleSetTelemetryProvider capacityProvider)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _capacityCoordinator = capacityCoordinator;
        _capacityProvider = Enum.IsDefined(capacityProvider) ? capacityProvider : ScaleSetTelemetryProvider.Other;
        var version = typeof(ScaleSetTelemetryMetrics).Assembly.GetName().Version?.ToString(3);
        _meter = meterFactory.Create(ScaleSetTelemetrySources.Hosting, version);
        _operations = _meter.CreateCounter<long>("scaleset.operations", "{operation}", "Completed autoscaler operations.");
        _operationDuration = _meter.CreateHistogram<double>("scaleset.operation.duration", "s", "Autoscaler operation duration.");
        _droppedLogs = _meter.CreateCounter<long>("scaleset.telemetry.logs.dropped", "{log}", "Log events dropped before export.");
        _configuredScaleSetKeys = (configuredScaleSetKeys ?? Array.Empty<string>())
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumScaleSetDimensions)
            .ToHashSet(StringComparer.Ordinal);
        _ = _meter.CreateObservableGauge<long>("scaleset.runners", ObserveRunners, "{runner}", "Current runner count by finite lifecycle state.");
    }

    public void RecordOperation(
        ScaleSetTelemetryOperation operation,
        ScaleSetTelemetryOutcome outcome,
        TimeSpan duration,
        string? scaleSetKey = null,
        ScaleSetTelemetryProvider provider = ScaleSetTelemetryProvider.Other)
    {
        var tags = CreateTags(ToTag(operation), ToTag(outcome), scaleSetKey, ToTag(provider));
        _operations.Add(1, tags);
        _operationDuration.Record(Math.Max(0, duration.TotalSeconds), tags);
    }

    public void RecordDroppedLog() => _droppedLogs.Add(1);

    /// <summary>Reports a runner count for meters created without a capacity ledger.</summary>
    /// <param name="scaleSetKey">Configured local scale-set key.</param>
    /// <param name="provider">Runner backend.</param>
    /// <param name="state">Runner lifecycle state.</param>
    /// <param name="count">Current count, or zero to remove the series.</param>
    public void SetRunnerCount(
        string? scaleSetKey,
        ScaleSetTelemetryProvider provider,
        RunnerTelemetryState state,
        long count)
    {
        if (_capacityCoordinator is not null)
        {
            return;
        }

        // Keep both exported dimensions and the backing inventory bounded.
        provider = Enum.IsDefined(provider) ? provider : ScaleSetTelemetryProvider.Other;
        state = Enum.IsDefined(state) ? state : RunnerTelemetryState.Unknown;
        var key = new RunnerMetricKey(ResolveScaleSetKey(scaleSetKey), provider, state);
        if (count <= 0)
        {
            _runnerCounts.TryRemove(key, out _);
            return;
        }

        _runnerCounts[key] = count;
    }

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<long>> ObserveRunners()
    {
        IEnumerable<KeyValuePair<RunnerMetricKey, long>> counts = _capacityCoordinator is null
            ? _runnerCounts
            : ReadCapacityCounts(_capacityCoordinator);
        foreach (var item in counts)
        {
            var tags = new TagList
            {
                { "scale_set", item.Key.ScaleSetKey },
                { "provider", ToTag(item.Key.Provider) },
                { "state", ToTag(item.Key.State) },
            };
            yield return new Measurement<long>(item.Value, tags);
        }
    }

    private Dictionary<RunnerMetricKey, long> ReadCapacityCounts(HostCapacityCoordinator coordinator)
    {
        Dictionary<RunnerMetricKey, long> counts = new();
        foreach (ScaleSetCapacityUsageSnapshot scaleSet in coordinator.ReadOccupancy().ScaleSets.Values)
        {
            string scaleSetKey = ResolveScaleSetKey(scaleSet.ScaleSetKey);
            foreach ((CapacityOccupancyState state, long count) in scaleSet.Usage.StateCounts)
            {
                if (count <= 0)
                {
                    continue;
                }

                RunnerTelemetryState runnerState = state switch
                {
                    CapacityOccupancyState.Reserved => RunnerTelemetryState.Reserved,
                    CapacityOccupancyState.JitIssued or CapacityOccupancyState.Creating => RunnerTelemetryState.Creating,
                    CapacityOccupancyState.Starting => RunnerTelemetryState.Starting,
                    CapacityOccupancyState.Idle => RunnerTelemetryState.Idle,
                    CapacityOccupancyState.Busy => RunnerTelemetryState.Busy,
                    CapacityOccupancyState.Removing => RunnerTelemetryState.Removing,
                    CapacityOccupancyState.Failed => RunnerTelemetryState.Failed,
                    _ => RunnerTelemetryState.Unknown,
                };
                var key = new RunnerMetricKey(scaleSetKey, _capacityProvider, runnerState);
                counts[key] = counts.GetValueOrDefault(key) + count;
            }
        }

        return counts;
    }

    private TagList CreateTags(string operation, string outcome, string? scaleSetKey, string provider) => new()
    {
        { "operation", ToTag(operation) },
        { "outcome", ToTag(outcome) },
        { "scale_set", ResolveScaleSetKey(scaleSetKey) },
        { "provider", ToTag(provider) },
    };

    private string ResolveScaleSetKey(string? scaleSetKey)
    {
        if (_configuredScaleSetKeys.Count == 0)
        {
            return "unspecified";
        }

        return scaleSetKey is not null && _configuredScaleSetKeys.Contains(scaleSetKey)
            ? scaleSetKey
            : "other";
    }

    private static string ToTag(string value) => value.ToLowerInvariant();

    private static string ToTag(ScaleSetTelemetryOperation value) => value switch
    {
        ScaleSetTelemetryOperation.DiagnosticSmoke => "diagnostic_smoke",
        ScaleSetTelemetryOperation.Authentication => "authentication",
        ScaleSetTelemetryOperation.QueuePoll => "queue_poll",
        ScaleSetTelemetryOperation.JobAcquire => "job_acquire",
        ScaleSetTelemetryOperation.Acknowledge => "acknowledge",
        ScaleSetTelemetryOperation.Reconcile => "reconcile",
        ScaleSetTelemetryOperation.Provision => "provision",
        ScaleSetTelemetryOperation.RunnerStart => "runner_start",
        ScaleSetTelemetryOperation.RunnerCompletion => "runner_completion",
        ScaleSetTelemetryOperation.Recovery => "recovery",
        ScaleSetTelemetryOperation.Drain => "drain",
        _ => "other",
    };

    private static string ToTag(ScaleSetTelemetryOutcome value) => value switch
    {
        ScaleSetTelemetryOutcome.Success => "success",
        ScaleSetTelemetryOutcome.Failure => "failure",
        ScaleSetTelemetryOutcome.Empty => "empty",
        ScaleSetTelemetryOutcome.Cancelled => "cancelled",
        ScaleSetTelemetryOutcome.Unknown => "unknown",
        _ => "other",
    };

    private static string ToTag(ScaleSetTelemetryProvider value) => value switch
    {
        ScaleSetTelemetryProvider.Docker => "docker",
        ScaleSetTelemetryProvider.Ecs => "ecs",
        ScaleSetTelemetryProvider.Ec2 => "ec2",
        ScaleSetTelemetryProvider.Other => "other",
        _ => "other",
    };

    private static string ToTag(RunnerTelemetryState value) => value switch
    {
        RunnerTelemetryState.Reserved => "reserved",
        RunnerTelemetryState.Creating => "creating",
        RunnerTelemetryState.Starting => "starting",
        RunnerTelemetryState.Idle => "idle",
        RunnerTelemetryState.Busy => "busy",
        RunnerTelemetryState.Unknown => "unknown",
        RunnerTelemetryState.Removing => "removing",
        RunnerTelemetryState.Gone => "gone",
        RunnerTelemetryState.Failed => "failed",
        _ => "other",
    };

    private readonly record struct RunnerMetricKey(
        string ScaleSetKey,
        ScaleSetTelemetryProvider Provider,
        RunnerTelemetryState State);
}
