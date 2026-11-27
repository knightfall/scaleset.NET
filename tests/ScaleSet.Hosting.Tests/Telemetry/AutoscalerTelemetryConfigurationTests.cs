using Microsoft.Extensions.Configuration;
using ScaleSet.Autoscaler.Configuration;
using ScaleSet.Autoscaler.Telemetry;

namespace ScaleSet.Hosting.Tests.Telemetry;

public sealed class AutoscalerTelemetryConfigurationTests
{
    [Fact]
    public void Bind_RequiresAConfiguredOtlpEndpointWithoutEchoingValues()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().Build();

        TelemetryConfigurationException exception = Assert.Throws<TelemetryConfigurationException>(
            () => AutoscalerTelemetryConfiguration.Bind(configuration));

        Assert.Contains("Telemetry:Otlp:Endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("http/json")]
    [InlineData("grpc-private-secret")]
    public void Bind_RejectsUnsupportedOtlpProtocolsWithoutEchoingValue(string protocol)
    {
        IConfigurationRoot configuration = Configuration(
            ("Telemetry:Otlp:Endpoint", "http://127.0.0.1:4318"),
            ("Telemetry:Otlp:Protocol", protocol));

        TelemetryConfigurationException exception = Assert.Throws<TelemetryConfigurationException>(
            () => AutoscalerTelemetryConfiguration.Bind(configuration));

        Assert.Equal("Telemetry:Otlp:Protocol must be 'grpc' or 'http/protobuf'.", exception.Message);
    }

    [Theory]
    [InlineData("always_off", "1")]
    [InlineData("parentbased_always_off", "1")]
    [InlineData("parentbased_traceidratio", "0")]
    [InlineData("always_on", "0")]
    public void Bind_RejectsConfigurationsThatDisableRequiredTracing(string sampler, string ratio)
    {
        IConfigurationRoot configuration = Configuration(
            ("Telemetry:Otlp:Endpoint", "http://127.0.0.1:4318"),
            ("Telemetry:Tracing:Sampler", sampler),
            ("Telemetry:Tracing:SamplingRatio", ratio));

        TelemetryConfigurationException exception = Assert.Throws<TelemetryConfigurationException>(
            () => AutoscalerTelemetryConfiguration.Bind(configuration));

        Assert.DoesNotContain("always_off", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Telemetry:Tracing:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_ResolvesSharedAndSignalSpecificHttpProtobufEndpoints()
    {
        IConfigurationRoot configuration = Configuration(
            ("Telemetry:Otlp:Endpoint", "http://127.0.0.1:4318/base"),
            ("Telemetry:Otlp:Protocol", "http/protobuf"),
            ("Telemetry:Otlp:Traces:Endpoint", "http://127.0.0.1:4320/custom/v1/traces"));

        AutoscalerTelemetryConfiguration telemetry = AutoscalerTelemetryConfiguration.Bind(
            configuration,
            serviceVersion: "1.2.3",
            serviceInstanceId: "instance-test");

        Assert.Equal("http://127.0.0.1:4318/base/v1/logs", telemetry.Logs.Endpoint.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:4320/custom/v1/traces", telemetry.Traces.Endpoint.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:4318/base/v1/metrics", telemetry.Metrics.Endpoint.AbsoluteUri);
        Assert.Equal(OtlpTransportProtocol.HttpProtobuf, telemetry.Logs.Protocol);
        Assert.Equal("1.2.3", telemetry.Resource.ServiceVersion);
        Assert.Equal("instance-test", telemetry.Resource.ServiceInstanceId);
    }

    [Fact]
    public void Bind_RejectsSignalEndpointMissingItsHttpProtobufPath()
    {
        IConfigurationRoot configuration = Configuration(
            ("Telemetry:Otlp:Endpoint", "http://127.0.0.1:4318"),
            ("Telemetry:Otlp:Logs:Endpoint", "http://127.0.0.1:4319/logs"));

        TelemetryConfigurationException exception = Assert.Throws<TelemetryConfigurationException>(
            () => AutoscalerTelemetryConfiguration.Bind(configuration));

        Assert.Contains("/v1/logs", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_RedactsHeaderValuesFromResourcesAndLogText()
    {
        IConfigurationRoot configuration = Configuration(
            ("Telemetry:Otlp:Endpoint", "http://127.0.0.1:4318"),
            ("Telemetry:Otlp:Headers", "Authorization=export-secret"),
            ("Telemetry:Resource:Attributes", "service.name=test-service,deployment.environment.name=export-secret"),
            ("Credentials:GitHubApp:Pat", "github-pat-secret"));

        AutoscalerTelemetryConfiguration telemetry = AutoscalerTelemetryConfiguration.Bind(configuration);

        Assert.Equal("test-service", telemetry.Resource.ServiceName);
        Assert.DoesNotContain("export-secret", telemetry.Resource.Attributes.Values);
        Assert.Equal("[REDACTED]", telemetry.SecretRedactor.Redact("export-secret"));
        Assert.DoesNotContain("github-pat-secret", telemetry.SecretRedactor.Redact("github-pat-secret"), StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_PreservesNonSecretOtlpHeaderValuesInLogText()
    {
        IConfigurationRoot configuration = Configuration(
            ("Telemetry:Otlp:Endpoint", "http://127.0.0.1:4318"),
            ("Telemetry:Otlp:Headers", "x-tenant=prod,x-env=1,Authorization=export-secret"));

        AutoscalerTelemetryConfiguration telemetry = AutoscalerTelemetryConfiguration.Bind(configuration);

        Assert.Equal("production 1 [REDACTED]", telemetry.SecretRedactor.Redact("production 1 export-secret"));
    }

    [Theory]
    [InlineData("authorization=\"RemoteAuth sample-secret\"", "sample-secret")]
    [InlineData("credential: 'value with spaces'", "with spaces")]
    [InlineData("signed eyJfixture.payload.signature", "eyJfixture")]
    [InlineData("ghs_syntheticToken", "syntheticToken")]
    [InlineData("GitHub_Pat_syntheticToken", "syntheticToken")]
    [InlineData("-----begin device-bound private key-----\nsynthetic-key\n-----end device-bound private key-----", "synthetic-key")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nsynthetic-key", "synthetic-key")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nsynthetic-key\n-----END PRIVATE KEY-----", "synthetic-key")]
    public void Redact_SanitizesCredentialForms(string text, string secret)
    {
        Assert.DoesNotContain(secret, new SecretRedactor().Redact(text), StringComparison.Ordinal);
    }

    [Fact]
    public void ProductPrefixOverridesExplicitlyMappedStandardOtelEnvironmentValues()
    {
        IConfigurationRoot configuration = AutoscalerApplicationConfiguration.Build(
        [
            new KeyValuePair<string, string?>("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4318/otel"),
            new KeyValuePair<string, string?>("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
            new KeyValuePair<string, string?>("OTEL_SERVICE_NAME", "otel-service"),
            new KeyValuePair<string, string?>("OTEL_RESOURCE_ATTRIBUTES", "deployment.environment.name=ci"),
            new KeyValuePair<string, string?>("UNRELATED_SETTING", "ignored"),
            new KeyValuePair<string, string?>("SCALESET__TELEMETRY__OTLP__ENDPOINT", "https://127.0.0.1:4317/collector"),
            new KeyValuePair<string, string?>("SCALESET__TELEMETRY__RESOURCE__SERVICEVERSION", "2.0.0"),
        ]);

        AutoscalerTelemetryConfiguration telemetry = AutoscalerTelemetryConfiguration.Bind(configuration);

        Assert.Equal("https://127.0.0.1:4317/collector/v1/logs", telemetry.Logs.Endpoint.AbsoluteUri);
        Assert.Equal("otel-service", telemetry.Resource.ServiceName);
        Assert.Equal("ci", telemetry.Resource.Attributes["deployment.environment.name"]);
        Assert.Equal("2.0.0", telemetry.Resource.ServiceVersion);
        Assert.Null(configuration["UNRELATED_SETTING"]);
    }

    [Fact]
    public void BuildFromEnvironment_UsesOnlyTheProductPrefixAndOtelMappings()
    {
        IConfigurationRoot configuration = AutoscalerApplicationConfiguration.Build(
        [
            new KeyValuePair<string, string?>("SCALESET__CONTROLLER__IDENTITY", "controller-a"),
            new KeyValuePair<string, string?>("CONTROLLER__IDENTITY", "ignored"),
        ]);

        Assert.Equal("controller-a", configuration["Controller:Identity"]);
        Assert.Equal("controller-a", configuration["CONTROLLER:IDENTITY"]);
        Assert.Null(configuration["SCALESET__CONTROLLER__IDENTITY"]);
    }

    private static IConfigurationRoot Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(static value =>
                new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
}
