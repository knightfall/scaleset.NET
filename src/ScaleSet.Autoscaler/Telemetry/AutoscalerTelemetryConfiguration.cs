using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace ScaleSet.Autoscaler.Telemetry;

public enum OtlpTransportProtocol
{
    Grpc = 0,
    HttpProtobuf = 1,
}

public enum TraceSamplerKind
{
    ParentBasedTraceIdRatio = 0,
    TraceIdRatio = 1,
    ParentBasedAlwaysOn = 2,
    AlwaysOn = 3,
}

public sealed record OtlpSignalConfiguration(
    Uri Endpoint,
    OtlpTransportProtocol Protocol,
    IReadOnlyDictionary<string, string> Headers,
    int TimeoutMilliseconds);

public sealed class TelemetryResourceIdentity
{
    private TelemetryResourceIdentity(IReadOnlyDictionary<string, string> attributes)
    {
        Attributes = attributes;
        ServiceName = attributes["service.name"];
        ServiceNamespace = attributes["service.namespace"];
        ServiceVersion = attributes["service.version"];
        ServiceInstanceId = attributes["service.instance.id"];
    }

    public string ServiceName { get; }

    public string ServiceNamespace { get; }

    public string ServiceVersion { get; }

    public string ServiceInstanceId { get; }

    public IReadOnlyDictionary<string, string> Attributes { get; }

    internal static TelemetryResourceIdentity Create(
        IConfiguration configuration,
        SecretRedactor redactor,
        string? serviceVersion,
        string? serviceInstanceId)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["service.name"] = "scaleset-autoscaler",
            ["service.namespace"] = "scaleset",
            ["service.version"] = serviceVersion ?? ReadApplicationVersion(),
            ["service.instance.id"] = serviceInstanceId ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ["deployment.environment.name"] = "production",
        };

        AddResourceAttributes(attributes, configuration["Telemetry:Resource:Attributes"], redactor);

        SetConfiguredAttribute(attributes, "service.name", configuration["Telemetry:Resource:ServiceName"], redactor);
        SetConfiguredAttribute(attributes, "service.namespace", configuration["Telemetry:Resource:ServiceNamespace"], redactor);
        SetConfiguredAttribute(attributes, "service.version", configuration["Telemetry:Resource:ServiceVersion"], redactor);
        SetConfiguredAttribute(attributes, "service.instance.id", configuration["Telemetry:Resource:ServiceInstanceId"], redactor);
        SetConfiguredAttribute(
            attributes,
            "deployment.environment.name",
            configuration["Telemetry:Resource:DeploymentEnvironment"],
            redactor);

        foreach (var key in attributes.Keys.ToArray())
        {
            attributes[key] = redactor.Redact(attributes[key]);
        }

        return new TelemetryResourceIdentity(attributes);
    }

    private static void AddResourceAttributes(
        Dictionary<string, string> attributes,
        string? rawAttributes,
        SecretRedactor redactor)
    {
        if (string.IsNullOrWhiteSpace(rawAttributes))
        {
            return;
        }

        foreach (var item in rawAttributes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf('=');
            if (separator <= 0)
            {
                throw new TelemetryConfigurationException(
                    "Telemetry:Resource:Attributes must contain comma-separated key=value pairs.");
            }

            var key = Uri.UnescapeDataString(item[..separator].Trim());
            var value = Uri.UnescapeDataString(item[(separator + 1)..].Trim());
            if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
            {
                throw new TelemetryConfigurationException("Telemetry resource attribute names must be non-empty and safe.");
            }

            if (SecretRedactor.IsSensitiveName(key))
            {
                continue;
            }

            attributes[key] = redactor.Redact(value);
        }
    }

    private static void SetConfiguredAttribute(
        Dictionary<string, string> attributes,
        string name,
        string? value,
        SecretRedactor redactor)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            attributes[name] = redactor.Redact(value.Trim());
        }
    }

    private static string ReadApplicationVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(TelemetryResourceIdentity).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informationalVersion.Split('+', 2)[0];
    }
}

public sealed class AutoscalerTelemetryConfiguration
{
    private AutoscalerTelemetryConfiguration(
        OtlpSignalConfiguration logs,
        OtlpSignalConfiguration traces,
        OtlpSignalConfiguration metrics,
        TelemetryResourceIdentity resource,
        SecretRedactor secretRedactor,
        TraceSamplerKind sampler,
        double samplingRatio,
        int traceBatchDelayMilliseconds,
        int metricsExportIntervalMilliseconds,
        int flushTimeoutMilliseconds,
        int logQueueLimit,
        int logBatchSizeLimit,
        int logBatchPeriodMilliseconds)
    {
        Logs = logs;
        Traces = traces;
        Metrics = metrics;
        Resource = resource;
        SecretRedactor = secretRedactor;
        Sampler = sampler;
        SamplingRatio = samplingRatio;
        TraceBatchDelayMilliseconds = traceBatchDelayMilliseconds;
        MetricsExportIntervalMilliseconds = metricsExportIntervalMilliseconds;
        FlushTimeoutMilliseconds = flushTimeoutMilliseconds;
        LogQueueLimit = logQueueLimit;
        LogBatchSizeLimit = logBatchSizeLimit;
        LogBatchPeriodMilliseconds = logBatchPeriodMilliseconds;
    }

    public OtlpSignalConfiguration Logs { get; }

    public OtlpSignalConfiguration Traces { get; }

    public OtlpSignalConfiguration Metrics { get; }

    public TelemetryResourceIdentity Resource { get; }

    public SecretRedactor SecretRedactor { get; }

    public TraceSamplerKind Sampler { get; }

    public double SamplingRatio { get; }

    public int TraceBatchDelayMilliseconds { get; }

    public int MetricsExportIntervalMilliseconds { get; }

    public int FlushTimeoutMilliseconds { get; }

    public int LogQueueLimit { get; }

    public int LogBatchSizeLimit { get; }

    public int LogBatchPeriodMilliseconds { get; }

    public AutoscalerTelemetryConfiguration ForDiagnosticSmoke() => new(
        Logs,
        Traces,
        Metrics,
        Resource,
        SecretRedactor,
        TraceSamplerKind.AlwaysOn,
        1,
        TraceBatchDelayMilliseconds,
        MetricsExportIntervalMilliseconds,
        FlushTimeoutMilliseconds,
        LogQueueLimit,
        LogBatchSizeLimit,
        LogBatchPeriodMilliseconds);

    /// <summary>
    /// Reads and validates the explicit OTLP configuration. Endpoint values and
    /// header contents are never included in validation errors.
    /// </summary>
    public static AutoscalerTelemetryConfiguration Bind(
        IConfiguration configuration,
        string? serviceVersion = null,
        string? serviceInstanceId = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var baseEndpointText = configuration["Telemetry:Otlp:Endpoint"];
        if (string.IsNullOrWhiteSpace(baseEndpointText))
        {
            throw new TelemetryConfigurationException("Telemetry:Otlp:Endpoint is required.");
        }

        var baseEndpoint = ParseEndpoint(baseEndpointText, "Telemetry:Otlp:Endpoint");
        var defaultProtocol = ParseProtocol(
            configuration["Telemetry:Otlp:Protocol"] ?? "http/protobuf",
            "Telemetry:Otlp:Protocol");
        var defaultTimeout = ParsePositiveInteger(
            configuration["Telemetry:Otlp:TimeoutMilliseconds"],
            5000,
            "Telemetry:Otlp:TimeoutMilliseconds",
            100,
            30000);

        var baseHeaders = ParseHeaders(configuration["Telemetry:Otlp:Headers"], "Telemetry:Otlp:Headers");
        var logHeaderText = configuration["Telemetry:Otlp:Logs:Headers"];
        var traceHeaderText = configuration["Telemetry:Otlp:Traces:Headers"];
        var metricHeaderText = configuration["Telemetry:Otlp:Metrics:Headers"];
        var logHeaders = logHeaderText is null ? baseHeaders : ParseHeaders(logHeaderText, "Telemetry:Otlp:Logs:Headers");
        var traceHeaders = traceHeaderText is null ? baseHeaders : ParseHeaders(traceHeaderText, "Telemetry:Otlp:Traces:Headers");
        var metricHeaders = metricHeaderText is null ? baseHeaders : ParseHeaders(metricHeaderText, "Telemetry:Otlp:Metrics:Headers");

        var sensitiveValues = baseHeaders.Concat(logHeaders).Concat(traceHeaders).Concat(metricHeaders)
            .Where(static header => SecretRedactor.IsSensitiveName(header.Key))
            .Select(static header => header.Value)
            .Concat(ReadSensitiveConfigurationValues(configuration));
        var redactor = new SecretRedactor(sensitiveValues);
        var resource = TelemetryResourceIdentity.Create(configuration, redactor, serviceVersion, serviceInstanceId);

        var logs = CreateSignal(configuration, "Logs", "logs", baseEndpoint, defaultProtocol, defaultTimeout, logHeaders);
        var traces = CreateSignal(configuration, "Traces", "traces", baseEndpoint, defaultProtocol, defaultTimeout, traceHeaders);
        var metrics = CreateSignal(configuration, "Metrics", "metrics", baseEndpoint, defaultProtocol, defaultTimeout, metricHeaders);

        var samplerText = configuration["Telemetry:Tracing:Sampler"] ?? "parentbased_traceidratio";
        var sampler = ParseSampler(samplerText);
        var samplingRatio = ParseDouble(
            configuration["Telemetry:Tracing:SamplingRatio"],
            0.1,
            "Telemetry:Tracing:SamplingRatio");
        if (samplingRatio is <= 0 or > 1)
        {
            throw new TelemetryConfigurationException("Telemetry:Tracing:SamplingRatio must be greater than 0 and no greater than 1.");
        }

        var traceBatchDelay = ParsePositiveInteger(
            configuration["Telemetry:Tracing:BatchDelayMilliseconds"],
            1000,
            "Telemetry:Tracing:BatchDelayMilliseconds",
            50,
            30000);

        var metricsInterval = ParsePositiveInteger(
            configuration["Telemetry:Metrics:ExportIntervalMilliseconds"],
            60000,
            "Telemetry:Metrics:ExportIntervalMilliseconds",
            100,
            300000);
        var flushTimeout = ParsePositiveInteger(
            configuration["Telemetry:FlushTimeoutMilliseconds"],
            3000,
            "Telemetry:FlushTimeoutMilliseconds",
            100,
            30000);
        var logQueueLimit = ParsePositiveInteger(configuration["Telemetry:Logs:QueueLimit"], 2048, "Telemetry:Logs:QueueLimit", 64, 100000);
        var logBatchSizeLimit = ParsePositiveInteger(configuration["Telemetry:Logs:BatchSizeLimit"], 128, "Telemetry:Logs:BatchSizeLimit", 1, logQueueLimit);
        var logBatchPeriod = ParsePositiveInteger(
            configuration["Telemetry:Logs:BatchPeriodMilliseconds"],
            1000,
            "Telemetry:Logs:BatchPeriodMilliseconds",
            50,
            30000);

        return new AutoscalerTelemetryConfiguration(
            logs,
            traces,
            metrics,
            resource,
            redactor,
            sampler,
            samplingRatio,
            traceBatchDelay,
            metricsInterval,
            flushTimeout,
            logQueueLimit,
            logBatchSizeLimit,
            logBatchPeriod);
    }

    private static OtlpSignalConfiguration CreateSignal(
        IConfiguration configuration,
        string section,
        string signalPath,
        Uri baseEndpoint,
        OtlpTransportProtocol defaultProtocol,
        int defaultTimeout,
        IReadOnlyDictionary<string, string> headers)
    {
        var sectionPath = $"Telemetry:Otlp:{section}";
        var protocol = ParseProtocol(configuration[$"{sectionPath}:Protocol"] ?? ProtocolName(defaultProtocol), $"{sectionPath}:Protocol");
        var endpointText = configuration[$"{sectionPath}:Endpoint"];
        var endpoint = endpointText is null
            ? ResolveBaseEndpoint(baseEndpoint, protocol, signalPath)
            : ResolveSignalEndpoint(ParseEndpoint(endpointText, $"{sectionPath}:Endpoint"), protocol, signalPath, sectionPath);
        var timeout = ParsePositiveInteger(
            configuration[$"{sectionPath}:TimeoutMilliseconds"],
            defaultTimeout,
            $"{sectionPath}:TimeoutMilliseconds",
            100,
            30000);
        return new OtlpSignalConfiguration(endpoint, protocol, headers, timeout);
    }

    private static Uri ResolveBaseEndpoint(Uri endpoint, OtlpTransportProtocol protocol, string signalPath)
    {
        if (protocol == OtlpTransportProtocol.Grpc)
        {
            return endpoint;
        }

        if (IsSignalPath(endpoint.AbsolutePath))
        {
            throw new TelemetryConfigurationException(
                "Telemetry:Otlp:Endpoint must be a base URL; signal-specific /v1 paths belong in per-signal endpoint settings.");
        }

        return new Uri($"{endpoint.AbsoluteUri.TrimEnd('/')}/v1/{signalPath}", UriKind.Absolute);
    }

    private static Uri ResolveSignalEndpoint(Uri endpoint, OtlpTransportProtocol protocol, string signalPath, string settingPath)
    {
        if (protocol == OtlpTransportProtocol.Grpc)
        {
            return endpoint;
        }

        var expectedSuffix = $"/v1/{signalPath}";
        if (!endpoint.AbsolutePath.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new TelemetryConfigurationException(
                $"{settingPath}:Endpoint must include the OTLP /v1/{signalPath} path when using http/protobuf.");
        }

        return endpoint;
    }

    private static bool IsSignalPath(string path) =>
        path.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/v1/traces", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/v1/metrics", StringComparison.OrdinalIgnoreCase);

    private static Uri ParseEndpoint(string value, string settingPath)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new TelemetryConfigurationException(
                $"{settingPath} must be an absolute HTTP or HTTPS URL without embedded credentials, query, or fragment.");
        }

        return endpoint;
    }

    private static OtlpTransportProtocol ParseProtocol(string value, string settingPath) =>
        value.Trim().ToLowerInvariant() switch
        {
            "grpc" => OtlpTransportProtocol.Grpc,
            "http/protobuf" => OtlpTransportProtocol.HttpProtobuf,
            _ => throw new TelemetryConfigurationException($"{settingPath} must be 'grpc' or 'http/protobuf'."),
        };

    private static TraceSamplerKind ParseSampler(string value) => value.Trim().ToLowerInvariant() switch
    {
        "parentbased_traceidratio" => TraceSamplerKind.ParentBasedTraceIdRatio,
        "traceidratio" => TraceSamplerKind.TraceIdRatio,
        "parentbased_always_on" => TraceSamplerKind.ParentBasedAlwaysOn,
        "always_on" => TraceSamplerKind.AlwaysOn,
        "parentbased_always_off" or "always_off" => throw new TelemetryConfigurationException(
            "Telemetry:Tracing:Sampler cannot disable required tracing."),
        _ => throw new TelemetryConfigurationException(
            "Telemetry:Tracing:Sampler must be a supported always_on or traceidratio sampler."),
    };

    private static string ProtocolName(OtlpTransportProtocol protocol) => protocol switch
    {
        OtlpTransportProtocol.Grpc => "grpc",
        OtlpTransportProtocol.HttpProtobuf => "http/protobuf",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    private static Dictionary<string, string> ParseHeaders(string? value, string settingPath)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf('=');
            if (separator <= 0)
            {
                throw new TelemetryConfigurationException($"{settingPath} must contain comma-separated key=value pairs.");
            }

            var name = Uri.UnescapeDataString(item[..separator].Trim());
            var headerValue = Uri.UnescapeDataString(item[(separator + 1)..].Trim());
            if (!IsValidHeaderName(name) || headerValue.Any(static ch => ch is '\r' or '\n') ||
                !headers.TryAdd(name, headerValue))
            {
                throw new TelemetryConfigurationException($"{settingPath} contains an invalid or duplicate header.");
            }
        }

        return headers;
    }

    private static bool IsValidHeaderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        const string allowedPunctuation = "!#$%&'*+-.^_`|~";
        return name.All(character => char.IsAsciiLetterOrDigit(character) || allowedPunctuation.Contains(character, StringComparison.Ordinal));
    }

    private static IEnumerable<string> ReadSensitiveConfigurationValues(IConfiguration configuration)
    {
        Stack<IConfigurationSection> sections = new(configuration.GetChildren());
        while (sections.Count > 0)
        {
            IConfigurationSection section = sections.Pop();
            if (section.Value is not null && SecretRedactor.IsSensitiveName(section.Key))
            {
                yield return section.Value;
            }

            foreach (IConfigurationSection child in section.GetChildren())
            {
                sections.Push(child);
            }
        }
    }

    private static int ParsePositiveInteger(string? value, int defaultValue, string settingPath, int minimum, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new TelemetryConfigurationException(
                $"{settingPath} must be an integer between {minimum} and {maximum}.");
        }

        return parsed;
    }

    private static double ParseDouble(string? value, double defaultValue, string settingPath)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
        {
            throw new TelemetryConfigurationException($"{settingPath} must be a finite number between 0 and 1.");
        }

        return parsed;
    }
}

public sealed class TelemetryConfigurationException : InvalidOperationException
{
    public TelemetryConfigurationException()
    {
    }

    public TelemetryConfigurationException(string message)
        : base(message)
    {
    }

    public TelemetryConfigurationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
