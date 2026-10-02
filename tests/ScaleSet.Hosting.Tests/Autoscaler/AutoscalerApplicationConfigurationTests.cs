using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ScaleSet.Autoscaler;
using ScaleSet.Autoscaler.Configuration;
using ScaleSet.Autoscaler.Runtime;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;

namespace ScaleSet.Hosting.Tests.Autoscaler;

public sealed class AutoscalerApplicationConfigurationTests
{
    [Fact]
    public void Build_ProvidesContainerHealthAddressAndAllowsEnvironmentOverride()
    {
        IConfigurationRoot defaults = AutoscalerApplicationConfiguration.Build(
            Array.Empty<KeyValuePair<string, string?>>());
        IConfigurationRoot configured = AutoscalerApplicationConfiguration.Build(
        [
            new KeyValuePair<string, string?>("SCALESET__Health__ListenUrl", "http://127.0.0.1:9080"),
        ]);

        Assert.Equal("http://0.0.0.0:8080", defaults["Health:ListenUrl"]);
        Assert.Equal("http://127.0.0.1:9080", configured["Health:ListenUrl"]);
    }

    [Fact]
    public void BuildRunHost_MapsHealthRoutesAndExtendsHostShutdownBudget()
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Controller:Identity"] = "health-test",
            ["Controller:StateDirectory"] = "/tmp/scaleset-health-state",
            ["Controller:ControlOperationTimeout"] = "00:00:07",
            ["ScaleSets:0:Key"] = "linux-x64",
            ["ScaleSets:0:GithubUrl"] = "https://github.com/org/repo",
            ["ScaleSets:0:Name"] = "ci-linux",
            ["ScaleSets:0:CredentialsRef"] = "github",
            ["ScaleSets:0:Scaling:MinRunners"] = "0",
            ["ScaleSets:0:Scaling:MaxRunners"] = "10",
            ["ScaleSets:0:Lifecycle:ShutdownDrainTimeout"] = "00:00:11",
            ["ScaleSets:0:Lifecycle:CleanupTimeout"] = "00:00:13",
            ["ScaleSets:0:RunnerProfile:Image"] = "example/runner:1",
            ["ScaleSets:0:RunnerProfile:Architecture"] = "X64",
            ["ScaleSets:0:RunnerProfile:Cpu"] = "1",
            ["ScaleSets:0:RunnerProfile:Memory"] = "1024",
            ["Credentials:github:Pat"] = "synthetic-pat",
            ["Telemetry:Otlp:Endpoint"] = "http://localhost:4318",
            ["Telemetry:FlushTimeoutMilliseconds"] = "1700",
            ["Health:ListenUrl"] = "http://127.0.0.1:9088",
        };
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using WebApplication app = (WebApplication)AutoscalerCommandRunner.BuildRunHost(configuration, new UnusedRuntimeFactory());

        string[] healthRoutes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .Where(static route => route is "/health/live" or "/health/ready")
            .OrderBy(static route => route, StringComparer.Ordinal)
            .ToArray();
        HostOptions hostOptions = app.Services.GetRequiredService<IOptions<HostOptions>>().Value;
        IScaleSetRuntimeFactory runtimeFactory = app.Services.GetRequiredService<IScaleSetRuntimeFactory>();

        Assert.Equal(["/health/live", "/health/ready"], healthRoutes);
        Assert.Equal(TimeSpan.FromMilliseconds(37_700), hostOptions.ShutdownTimeout);
        Assert.IsType<UnusedRuntimeFactory>(runtimeFactory);
    }

    private sealed class UnusedRuntimeFactory : IScaleSetRuntimeFactory
    {
        public Task<IScaleSetRuntime> CreateAsync(
            ScaleSetOptions scaleSet,
            HostCapacityCoordinator capacityCoordinator,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The test only inspects host construction and does not start the service.");
    }
}
