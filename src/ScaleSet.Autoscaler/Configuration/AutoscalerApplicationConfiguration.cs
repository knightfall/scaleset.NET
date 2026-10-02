using Microsoft.Extensions.Configuration;

namespace ScaleSet.Autoscaler.Configuration;

/// <summary>
/// Builds the executable's deliberately small configuration chain. Operational
/// values come from code defaults, standard OTEL_* variables, and SCALESET__*.
/// </summary>
public static class AutoscalerApplicationConfiguration
{
    private const string ProductPrefix = "SCALESET__";

    private static readonly IReadOnlyDictionary<string, string?> Defaults =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Health:ListenUrl"] = "http://0.0.0.0:8080",
            ["Telemetry:Otlp:Protocol"] = "http/protobuf",
            ["Telemetry:Otlp:TimeoutMilliseconds"] = "5000",
            ["Telemetry:FlushTimeoutMilliseconds"] = "3000",
            ["Telemetry:Metrics:ExportIntervalMilliseconds"] = "60000",
            ["Telemetry:Tracing:SamplingRatio"] = "0.1",
        };

    /// <summary>
    /// Creates configuration without appsettings, unprefixed environment, or
    /// command-line providers. Command-line arguments select commands only.
    /// </summary>
    public static IConfigurationRoot BuildFromEnvironment()
    {
        var standardEnvironment = ReadEnvironmentVariables();
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(Defaults)
            .AddInMemoryCollection(MapOpenTelemetryVariables(standardEnvironment))
            .AddEnvironmentVariables(ProductPrefix);

        return builder.Build();
    }

    /// <summary>
    /// Builds the same source order from a supplied environment snapshot. This
    /// is useful for deterministic configuration tests and avoids process-wide
    /// environment mutation.
    /// </summary>
    public static IConfigurationRoot Build(IEnumerable<KeyValuePair<string, string?>> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var variables = environment.ToArray();
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(Defaults)
            .AddInMemoryCollection(MapOpenTelemetryVariables(variables))
            .AddInMemoryCollection(MapProductVariables(variables));

        return builder.Build();
    }

    private static Dictionary<string, string?> MapOpenTelemetryVariables(
        IEnumerable<KeyValuePair<string, string?>> environment)
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in environment)
        {
            var key = pair.Key.ToUpperInvariant();
            var configurationKey = key switch
            {
                "OTEL_SERVICE_NAME" => "Telemetry:Resource:ServiceName",
                "OTEL_RESOURCE_ATTRIBUTES" => "Telemetry:Resource:Attributes",
                "OTEL_EXPORTER_OTLP_ENDPOINT" => "Telemetry:Otlp:Endpoint",
                "OTEL_EXPORTER_OTLP_PROTOCOL" => "Telemetry:Otlp:Protocol",
                "OTEL_EXPORTER_OTLP_HEADERS" => "Telemetry:Otlp:Headers",
                "OTEL_EXPORTER_OTLP_TIMEOUT" => "Telemetry:Otlp:TimeoutMilliseconds",
                "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT" => "Telemetry:Otlp:Traces:Endpoint",
                "OTEL_EXPORTER_OTLP_TRACES_PROTOCOL" => "Telemetry:Otlp:Traces:Protocol",
                "OTEL_EXPORTER_OTLP_TRACES_HEADERS" => "Telemetry:Otlp:Traces:Headers",
                "OTEL_EXPORTER_OTLP_TRACES_TIMEOUT" => "Telemetry:Otlp:Traces:TimeoutMilliseconds",
                "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT" => "Telemetry:Otlp:Metrics:Endpoint",
                "OTEL_EXPORTER_OTLP_METRICS_PROTOCOL" => "Telemetry:Otlp:Metrics:Protocol",
                "OTEL_EXPORTER_OTLP_METRICS_HEADERS" => "Telemetry:Otlp:Metrics:Headers",
                "OTEL_EXPORTER_OTLP_METRICS_TIMEOUT" => "Telemetry:Otlp:Metrics:TimeoutMilliseconds",
                "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT" => "Telemetry:Otlp:Logs:Endpoint",
                "OTEL_EXPORTER_OTLP_LOGS_PROTOCOL" => "Telemetry:Otlp:Logs:Protocol",
                "OTEL_EXPORTER_OTLP_LOGS_HEADERS" => "Telemetry:Otlp:Logs:Headers",
                "OTEL_EXPORTER_OTLP_LOGS_TIMEOUT" => "Telemetry:Otlp:Logs:TimeoutMilliseconds",
                "OTEL_TRACES_SAMPLER" => "Telemetry:Tracing:Sampler",
                "OTEL_TRACES_SAMPLER_ARG" => "Telemetry:Tracing:SamplingRatio",
                _ => null,
            };

            if (configurationKey is not null && pair.Value is not null)
            {
                variables[configurationKey] = pair.Value;
            }
        }

        return variables;
    }

    private static Dictionary<string, string?> MapProductVariables(
        IEnumerable<KeyValuePair<string, string?>> environment)
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in environment)
        {
            if (pair.Key.StartsWith(ProductPrefix, StringComparison.OrdinalIgnoreCase) && pair.Value is not null)
            {
                var key = pair.Key[ProductPrefix.Length..].Replace("__", ":", StringComparison.Ordinal);
                variables[key] = pair.Value;
            }
        }

        return variables;
    }

    private static IEnumerable<KeyValuePair<string, string?>> ReadEnvironmentVariables()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                yield return new KeyValuePair<string, string?>(key, entry.Value as string);
            }
        }
    }
}
