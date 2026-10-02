using System.Diagnostics;
using System.Diagnostics.Metrics;
using ScaleSet.Hosting.Telemetry;

namespace ScaleSet.Hosting.Capacity;

internal static class CapacityTelemetry
{
    private static readonly Meter Meter = new(
        $"{ScaleSetTelemetrySources.Hosting}.Capacity",
        typeof(CapacityTelemetry).Assembly.GetName().Version?.ToString(3));

    private static readonly Counter<long> CapacityOperations = Meter.CreateCounter<long>(
        "scaleset.capacity.operations",
        "{operation}",
        "Capacity plans, reservations, inventory updates, and releases.");

    private static readonly Counter<long> CreationLeaseOperations = Meter.CreateCounter<long>(
        "scaleset.capacity.creation_lease.operations",
        "{operation}",
        "Shared runner-creation lease acquisition and release outcomes.");

    public static Activity? StartActivity(string operation) =>
        ScaleSetTelemetrySources.ActivitySource.StartActivity($"scaleset.capacity.{operation}", ActivityKind.Internal);

    public static void RecordCapacityOperation(string operation, string outcome)
    {
        TagList tags = new()
        {
            { "operation", operation },
            { "outcome", outcome },
        };
        CapacityOperations.Add(1, tags);
    }

    public static void RecordCreationLeaseOperation(string outcome)
    {
        TagList tags = new()
        {
            { "operation", "creation_lease" },
            { "outcome", outcome },
        };
        CreationLeaseOperations.Add(1, tags);
    }
}
