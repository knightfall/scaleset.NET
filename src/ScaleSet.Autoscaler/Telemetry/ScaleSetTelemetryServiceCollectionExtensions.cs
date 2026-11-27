using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Telemetry;

namespace ScaleSet.Autoscaler.Telemetry;

public static partial class ScaleSetTelemetryServiceCollectionExtensions
{
    private static readonly string[] SourceAndMeterNames =
    [
        ScaleSetTelemetrySources.Autoscaler,
        ScaleSetTelemetrySources.Client,
        ScaleSetTelemetrySources.Hosting,
        ScaleSetTelemetrySources.Listener,
        ScaleSetTelemetrySources.DockerProvider,
    ];

    /// <summary>
    /// Registers one Serilog OTLP log route and separate OpenTelemetry trace
    /// and metric providers. This method intentionally does not register an
    /// OpenTelemetry ILogger provider or SerilogTracing span route.
    /// </summary>
    public static IServiceCollection AddScaleSetTelemetry(
        this IServiceCollection services,
        AutoscalerTelemetryConfiguration configuration,
        IEnumerable<string>? configuredScaleSetKeys = null,
        ScaleSetTelemetryProvider capacityProvider = ScaleSetTelemetryProvider.Other)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(AutoscalerTelemetryConfiguration)))
        {
            throw new InvalidOperationException("ScaleSet telemetry has already been registered for this service collection.");
        }

        services.AddSingleton(configuration);
        services.AddSingleton(configuration.SecretRedactor);
        services.AddMetrics();
        services.AddSingleton(serviceProvider => new ScaleSetTelemetryMetrics(
            serviceProvider.GetRequiredService<IMeterFactory>(),
            configuredScaleSetKeys,
            serviceProvider.GetService<HostCapacityCoordinator>(),
            capacityProvider));
        services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                resource.Clear();
                resource.AddTelemetrySdk();
                resource.AddAttributes(configuration.Resource.Attributes.Select(attribute =>
                    new KeyValuePair<string, object>(attribute.Key, attribute.Value)));
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(SourceAndMeterNames)
                    .SetSampler(CreateSampler(configuration))
                    // Client and provider sources emit sanitized request spans.
                    .AddOtlpExporter(options =>
                    {
                        ConfigureExporter(options, configuration.Traces);
                        options.BatchExportProcessorOptions.ScheduledDelayMilliseconds =
                            configuration.TraceBatchDelayMilliseconds;
                        options.BatchExportProcessorOptions.ExporterTimeoutMilliseconds =
                            configuration.Traces.TimeoutMilliseconds;
                    });
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(SourceAndMeterNames)
                    .AddMeter(ScaleSetTelemetrySources.HostingCapacity)
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddOtlpExporter((options, readerOptions) =>
                    {
                        ConfigureExporter(options, configuration.Metrics);
                        readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                            configuration.MetricsExportIntervalMilliseconds;
                        readerOptions.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds =
                            configuration.Metrics.TimeoutMilliseconds;
                    });
            });

        services.AddSerilog((serviceProvider, loggerConfiguration) =>
        {
            var redactor = serviceProvider.GetRequiredService<SecretRedactor>();
            var metrics = serviceProvider.GetRequiredService<ScaleSetTelemetryMetrics>();
            var exportLogger = CreateOtlpLogLogger(configuration);
            var sink = new RedactingLogEventSink(
                exportLogger,
                redactor,
                configuration.LogQueueLimit,
                TimeSpan.FromMilliseconds(configuration.FlushTimeoutMilliseconds),
                metrics.RecordDroppedLog);

            loggerConfiguration
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.Sink(sink, restrictedToMinimumLevel: LogEventLevel.Information);
        });

        services.AddHostedService<TelemetryFlushHostedService>();
        return services;
    }

    private static Logger CreateOtlpLogLogger(AutoscalerTelemetryConfiguration configuration)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.OpenTelemetry(options =>
            {
                // Only logs use this sink. Trace spans are exported from
                // ActivitySource through the SDK's tracer provider.
                options.Endpoint = null;
                options.LogsEndpoint = configuration.Logs.Endpoint.AbsoluteUri;
                options.TracesEndpoint = null;
                options.Protocol = configuration.Logs.Protocol.ToSerilogProtocol();
                options.Headers = new Dictionary<string, string>(configuration.Logs.Headers, StringComparer.OrdinalIgnoreCase);
                options.ResourceAttributes = configuration.Resource.Attributes.ToDictionary(
                    attribute => attribute.Key,
                    attribute => (object)attribute.Value,
                    StringComparer.Ordinal);
                options.IncludedData = IncludedData.TraceIdField |
                                      IncludedData.SpanIdField |
                                      IncludedData.MessageTemplateTextAttribute |
                                      IncludedData.SpecRequiredResourceAttributes;
                options.BatchingOptions.BufferingTimeLimit = TimeSpan.FromMilliseconds(configuration.LogBatchPeriodMilliseconds);
                options.BatchingOptions.BatchSizeLimit = configuration.LogBatchSizeLimit;
                options.BatchingOptions.QueueLimit = configuration.LogQueueLimit;
                options.BatchingOptions.RetryTimeLimit = TimeSpan.FromMilliseconds(configuration.Logs.TimeoutMilliseconds);
                options.HttpMessageHandler = new TimeoutDelegatingHandler(
                    new SocketsHttpHandler
                    {
                        AllowAutoRedirect = false,
                        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    },
                    TimeSpan.FromMilliseconds(configuration.Logs.TimeoutMilliseconds));
                options.OnBeginSuppressInstrumentation = SuppressInstrumentationScope.Begin;
            }, ignoreEnvironment: true)
            .CreateLogger();
    }

    private static void ConfigureExporter(OtlpExporterOptions options, OtlpSignalConfiguration signal)
    {
        options.Endpoint = signal.Endpoint;
        options.Protocol = signal.Protocol.ToOpenTelemetryProtocol();
        options.Headers = EncodeHeaders(signal.Headers);
        options.TimeoutMilliseconds = signal.TimeoutMilliseconds;
    }

    private static string? EncodeHeaders(IReadOnlyDictionary<string, string> headers) =>
        headers.Count == 0
            ? null
            : string.Join(',', headers.Select(header =>
                $"{Uri.EscapeDataString(header.Key)}={Uri.EscapeDataString(header.Value)}"));

    private static Sampler CreateSampler(AutoscalerTelemetryConfiguration configuration)
    {
        return configuration.Sampler switch
        {
            TraceSamplerKind.ParentBasedTraceIdRatio => new ParentBasedSampler(new TraceIdRatioBasedSampler(configuration.SamplingRatio)),
            TraceSamplerKind.TraceIdRatio => new TraceIdRatioBasedSampler(configuration.SamplingRatio),
            TraceSamplerKind.ParentBasedAlwaysOn => new ParentBasedSampler(new AlwaysOnSampler()),
            TraceSamplerKind.AlwaysOn => new AlwaysOnSampler(),
            _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
        };
    }

    private sealed partial class TelemetryFlushHostedService(
        TracerProvider tracerProvider,
        MeterProvider meterProvider,
        AutoscalerTelemetryConfiguration configuration,
        ILogger<TelemetryFlushHostedService> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            var timeout = cancellationToken.IsCancellationRequested
                ? 0
                : configuration.FlushTimeoutMilliseconds;
            var started = Stopwatch.GetTimestamp();
            var tracesFlushed = tracerProvider.ForceFlush(timeout);
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var remaining = Math.Max(0, timeout - (int)elapsedMilliseconds);
            var metricsFlushed = meterProvider.ForceFlush(remaining);

            if (!tracesFlushed || !metricsFlushed)
                LogFlushDeadline(logger);

            return Task.CompletedTask;
        }

        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Warning,
            Message = "Telemetry flush reached its bounded shutdown deadline.")]
        private static partial void LogFlushDeadline(ILogger<TelemetryFlushHostedService> logger);
    }

    private sealed class TimeoutDelegatingHandler(HttpMessageHandler innerHandler, TimeSpan timeout) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            return await base.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
        }
    }
}

internal static class OtlpProtocolConversions
{
    public static OtlpExportProtocol ToOpenTelemetryProtocol(this OtlpTransportProtocol protocol) => protocol switch
    {
        OtlpTransportProtocol.Grpc => OtlpExportProtocol.Grpc,
        OtlpTransportProtocol.HttpProtobuf => OtlpExportProtocol.HttpProtobuf,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    public static OtlpProtocol ToSerilogProtocol(this OtlpTransportProtocol protocol) => protocol switch
    {
        OtlpTransportProtocol.Grpc => OtlpProtocol.Grpc,
        OtlpTransportProtocol.HttpProtobuf => OtlpProtocol.HttpProtobuf,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };
}
