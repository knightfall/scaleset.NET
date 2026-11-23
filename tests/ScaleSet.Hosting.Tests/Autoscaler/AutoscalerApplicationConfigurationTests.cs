using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScaleSet.Autoscaler;
using ScaleSet.Autoscaler.Configuration;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Runtime;

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
    public async Task BuildRunHost_CombinesDockerProviderValidationWithProductionTelemetry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await Task.Run(() =>
        {
            IConfigurationRoot configuration = AutoscalerApplicationConfiguration.Build(new Dictionary<string, string?>
            {
                ["SCALESET__Controller__StateDirectory"] = Path.GetTempPath(),
                ["SCALESET__Credentials__github__Pat"] = "test-credential",
                ["SCALESET__ScaleSets__0__Key"] = "builds",
                ["SCALESET__ScaleSets__0__GithubUrl"] = "https://github.com/example/repo",
                ["SCALESET__ScaleSets__0__Name"] = "example-runners",
                ["SCALESET__ScaleSets__0__CredentialsRef"] = "github",
                ["SCALESET__ScaleSets__0__RunnerProfile__Image"] = "example/runner:1",
                ["SCALESET__ScaleSets__0__RunnerProfile__Architecture"] = "X64",
                ["SCALESET__ScaleSets__0__RunnerProfile__Cpu"] = "1",
                ["SCALESET__ScaleSets__0__RunnerProfile__Memory"] = "1024",
            });
            using var configurationLifetime = configuration as IDisposable;
            using IHost host = AutoscalerCommandRunner.BuildRunHost(configuration);

            Assert.Single(host.Services.GetServices<IHostedService>().OfType<ScaleSetAutoscalerService>());
            Assert.Equal(0, host.Services.GetRequiredService<HostCapacityCoordinator>().CaptureSnapshot().Usage.RunnerCount);
        }, token).WaitAsync(TimeSpan.FromSeconds(15), token);
    }
}
