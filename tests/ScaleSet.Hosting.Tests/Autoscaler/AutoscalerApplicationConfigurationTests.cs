using Microsoft.Extensions.Configuration;
using ScaleSet.Autoscaler.Configuration;

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
}
