using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using ScaleSet.Hosting.Telemetry;

namespace ScaleSet.Hosting.Tests.Telemetry;

public sealed class ScaleSetTelemetryMetricsTests
{
    [Fact]
    public void OperationsRecordCountDurationSecondsAndBoundedDimensions()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var metrics = new ScaleSetTelemetryMetrics(factory, ["builds"]);
        var integers = new List<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        var durations = new List<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => integers.Add((instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => durations.Add((instrument.Name, value, tags.ToArray())));
        listener.Start();

        metrics.RecordOperation(ScaleSetTelemetryOperation.Provision, ScaleSetTelemetryOutcome.Success,
            TimeSpan.FromMilliseconds(17), "untrusted-runner-id", ScaleSetTelemetryProvider.Docker);
        metrics.RecordDroppedLog();

        var operation = Assert.Single(integers, item => item.Name == "scaleset.operations");
        Assert.Equal(1, operation.Value);
        Assert.Contains(new KeyValuePair<string, object?>("scale_set", "other"), operation.Tags);
        Assert.Contains(new KeyValuePair<string, object?>("operation", "provision"), operation.Tags);
        Assert.Contains(new KeyValuePair<string, object?>("outcome", "success"), operation.Tags);
        Assert.Contains(new KeyValuePair<string, object?>("provider", "docker"), operation.Tags);
        Assert.DoesNotContain(operation.Tags, tag => Equals(tag.Value, "untrusted-runner-id"));
        Assert.Equal(0.017, Assert.Single(durations).Value, precision: 6);
        Assert.Equal("scaleset.operation.duration", Assert.Single(durations).Name);
        Assert.Equal(1, Assert.Single(integers, item => item.Name == "scaleset.telemetry.logs.dropped").Value);
    }

    [Fact]
    public void RunnerGaugeNormalizesUnknownEnumsAndRemovesZeroCounts()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var metrics = new ScaleSetTelemetryMetrics(factory, ["builds"]);
        var observed = new List<(long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Name == "scaleset.runners")
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => observed.Add((value, tags.ToArray())));
        listener.Start();

        metrics.SetRunnerCount("arbitrary-one", (ScaleSetTelemetryProvider)100, (RunnerTelemetryState)100, 3);
        metrics.SetRunnerCount("arbitrary-two", (ScaleSetTelemetryProvider)200, (RunnerTelemetryState)200, 5);
        listener.RecordObservableInstruments();

        var measurement = Assert.Single(observed);
        Assert.Equal(5, measurement.Value);
        Assert.Contains(new KeyValuePair<string, object?>("scale_set", "other"), measurement.Tags);
        Assert.Contains(new KeyValuePair<string, object?>("provider", "other"), measurement.Tags);
        Assert.Contains(new KeyValuePair<string, object?>("state", "unknown"), measurement.Tags);

        observed.Clear();
        metrics.SetRunnerCount("arbitrary-three", ScaleSetTelemetryProvider.Other, RunnerTelemetryState.Unknown, 0);
        listener.RecordObservableInstruments();
        Assert.Empty(observed);
    }
}
