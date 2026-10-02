using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScaleSet.Autoscaler.Telemetry;
using ScaleSet.Hosting.Telemetry;

namespace ScaleSet.Hosting.Tests.Telemetry;

public sealed partial class OtlpTelemetryIntegrationTests
{
    [Fact]
    public async Task SerilogAndSdkExportCorrelatedLogsCompletedSpansAndMetricsOnce()
    {
        await using var receiver = new OtlpHttpTestReceiver();
        var endpoint = new Uri(receiver.BaseEndpoint, "foundation").AbsoluteUri.TrimEnd('/');
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("Telemetry:Otlp:Endpoint", endpoint),
                new KeyValuePair<string, string?>("Telemetry:Otlp:Protocol", "http/protobuf"),
                new KeyValuePair<string, string?>("Telemetry:Otlp:Headers", "Authorization=export-header-secret"),
                new KeyValuePair<string, string?>("Telemetry:Otlp:TimeoutMilliseconds", "1000"),
                new KeyValuePair<string, string?>("Telemetry:Tracing:SamplingRatio", "1"),
                new KeyValuePair<string, string?>("Telemetry:Tracing:BatchDelayMilliseconds", "100"),
                new KeyValuePair<string, string?>("Telemetry:Metrics:ExportIntervalMilliseconds", "100"),
                new KeyValuePair<string, string?>("Telemetry:FlushTimeoutMilliseconds", "2500"),
                new KeyValuePair<string, string?>("Telemetry:Logs:QueueLimit", "64"),
                new KeyValuePair<string, string?>("Telemetry:Logs:BatchSizeLimit", "16"),
                new KeyValuePair<string, string?>("Telemetry:Logs:BatchPeriodMilliseconds", "50"),
                new KeyValuePair<string, string?>("Telemetry:Resource:ServiceName", "scaleset-telemetry-test"),
                new KeyValuePair<string, string?>("Telemetry:Resource:ServiceNamespace", "scaleset-tests"),
                new KeyValuePair<string, string?>("Telemetry:Resource:ServiceVersion", "1.0.0-test"),
                new KeyValuePair<string, string?>("Telemetry:Resource:ServiceInstanceId", "receiver-test-instance"),
                new KeyValuePair<string, string?>("Telemetry:Resource:DeploymentEnvironment", "test"),
            ])
            .Build();
        AutoscalerTelemetryConfiguration telemetry = AutoscalerTelemetryConfiguration.Bind(configuration).ForDiagnosticSmoke();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = Array.Empty<string>(),
        });
        builder.Configuration.AddConfiguration(configuration);
        builder.Logging.ClearProviders();
        builder.Services.AddScaleSetTelemetry(telemetry, ["integration"]);
        string expectedTraceId;
        string expectedSpanId;

        using (var host = builder.Build())
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                var logger = host.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("ScaleSet.Hosting.TelemetryAcceptance");
                using Activity activity = ScaleSetTelemetrySources.StartOperation("telemetry.acceptance")
                    ?? throw new InvalidOperationException("The acceptance ActivitySource was not subscribed.");
                expectedTraceId = activity.TraceId.ToHexString();
                expectedSpanId = activity.SpanId.ToHexString();
                activity.SetTag("scaleset.test", "otlp-receiver");

                LogAcceptanceMessage(logger, "private-token-sentinel");
                LogAcceptanceException(logger, new InvalidOperationException("token=exception-secret-sentinel"));

                ScaleSetTelemetryMetrics metrics = host.Services.GetRequiredService<ScaleSetTelemetryMetrics>();
                metrics.RecordOperation(
                    ScaleSetTelemetryOperation.DiagnosticSmoke,
                    ScaleSetTelemetryOutcome.Success,
                    TimeSpan.FromMilliseconds(17),
                    "integration",
                    ScaleSetTelemetryProvider.Docker);
                metrics.SetRunnerCount("integration", ScaleSetTelemetryProvider.Docker, RunnerTelemetryState.Idle, 3);
            }
            finally
            {
                await host.StopAsync(TestContext.Current.CancellationToken);
            }
        }

        var logsRequest = await receiver.WaitForMatchingRequestAsync(
            "/otlp/foundation/v1/logs",
            request => OtlpProtobufReader.ReadLogRecords(request.Body).Any(static record =>
                record.Body?.StartsWith("telemetry-acceptance log", StringComparison.Ordinal) == true),
            TimeSpan.FromSeconds(10));
        var tracesRequest = await receiver.WaitForRequestAsync("/otlp/foundation/v1/traces", TimeSpan.FromSeconds(10));
        var metricsRequest = await receiver.WaitForMatchingRequestAsync(
            "/otlp/foundation/v1/metrics",
            request => OtlpProtobufReader.ReadMetricNames(request.Body).Contains("scaleset.operations", StringComparer.Ordinal),
            TimeSpan.FromSeconds(10));

        Assert.Equal("export-header-secret", logsRequest.AuthorizationHeader);
        Assert.NotNull(logsRequest.ContentType);
        Assert.StartsWith("application/x-protobuf", logsRequest.ContentType, StringComparison.OrdinalIgnoreCase);

        var logRequests = receiver.Requests.Where(static request => request.Path == "/otlp/foundation/v1/logs").ToArray();
        var logs = logRequests.SelectMany(static request => OtlpProtobufReader.ReadLogRecords(request.Body)).ToArray();
        var applicationLog = Assert.Single(logs, static record => record.Body?.StartsWith("telemetry-acceptance log", StringComparison.Ordinal) == true);
        var spans = OtlpProtobufReader.ReadTraceSpans(tracesRequest.Body);
        var applicationSpan = Assert.Single(spans, static span => span.Name == "telemetry.acceptance");
        var metricNames = OtlpProtobufReader.ReadMetricNames(metricsRequest.Body);

        Assert.Equal(expectedTraceId, applicationSpan.TraceId);
        Assert.Equal(expectedSpanId, applicationSpan.SpanId);
        Assert.Equal(expectedTraceId, applicationLog.TraceId);
        Assert.Equal(expectedSpanId, applicationLog.SpanId);
        Assert.Contains("scaleset.operations", metricNames);
        Assert.Contains("scaleset.runners", metricNames);
        IReadOnlyDictionary<string, string> expectedResource = new Dictionary<string, string>
        {
            ["service.name"] = "scaleset-telemetry-test",
            ["service.namespace"] = "scaleset-tests",
            ["service.version"] = "1.0.0-test",
            ["service.instance.id"] = "receiver-test-instance",
            ["deployment.environment.name"] = "test",
        };
        AssertResourceMatches(expectedResource, OtlpProtobufReader.ReadLogResource(logsRequest.Body));
        AssertResourceMatches(expectedResource, OtlpProtobufReader.ReadTraceResource(tracesRequest.Body));
        AssertResourceMatches(expectedResource, OtlpProtobufReader.ReadMetricResource(metricsRequest.Body));

        var serializedLogs = string.Concat(logRequests.Select(static request => Encoding.UTF8.GetString(request.Body)));
        Assert.DoesNotContain("private-token-sentinel", serializedLogs, StringComparison.Ordinal);
        Assert.DoesNotContain("exception-secret-sentinel", serializedLogs, StringComparison.Ordinal);
        Assert.DoesNotContain("export-header-secret", serializedLogs, StringComparison.Ordinal);
        Assert.Equal(1, logs.Count(static record => record.Body?.StartsWith("telemetry-acceptance log", StringComparison.Ordinal) == true));
        Assert.Single(spans);
    }

    private static void AssertResourceMatches(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        Assert.True(actual.Count >= expected.Count);
        foreach (var pair in expected)
        {
            Assert.True(actual.TryGetValue(pair.Key, out var value), $"OTLP resource omitted '{pair.Key}'.");
            Assert.Equal(pair.Value, value);
        }
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "telemetry-acceptance log {PrivateToken}")]
    private static partial void LogAcceptanceMessage(ILogger logger, string privateToken);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "telemetry-acceptance exception probe")]
    private static partial void LogAcceptanceException(ILogger logger, Exception exception);
}
