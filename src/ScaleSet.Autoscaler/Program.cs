using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScaleSet.Autoscaler.Configuration;
using ScaleSet.Autoscaler.Telemetry;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Telemetry;

namespace ScaleSet.Autoscaler;

public static class Program
{
    public static Task<int> Main(string[] args) => AutoscalerCommandRunner.RunAsync(args);
}

public static partial class AutoscalerCommandRunner
{
    public static async Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        output ??= Console.Out;
        error ??= Console.Error;

        if (args.Length != 1)
        {
            await WriteUsageAsync(error).ConfigureAwait(false);
            return 2;
        }

        if (args[0].Equals("validate-config", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateConfiguration(output, error);
        }

        if (args[0].Equals("telemetry-smoke", StringComparison.OrdinalIgnoreCase))
        {
            return await RunTelemetrySmokeAsync(output, error).ConfigureAwait(false);
        }

        await WriteUsageAsync(error).ConfigureAwait(false);
        return 2;
    }

    private static int ValidateConfiguration(TextWriter output, TextWriter error)
    {
        try
        {
            IConfigurationRoot configuration = AutoscalerApplicationConfiguration.BuildFromEnvironment();
            _ = AutoscalerConfiguration.Bind(configuration);
            _ = AutoscalerTelemetryConfiguration.Bind(configuration);
            output.WriteLine("Configuration is valid.");
            return 0;
        }
        catch (AutoscalerConfigurationException exception)
        {
            error.WriteLine(exception.Message);
            return 2;
        }
        catch (TelemetryConfigurationException exception)
        {
            error.WriteLine(exception.Message);
            return 2;
        }
        catch
        {
            error.WriteLine("Configuration could not be validated safely.");
            return 2;
        }
    }

    private static async Task<int> RunTelemetrySmokeAsync(TextWriter output, TextWriter error)
    {
        try
        {
            IConfigurationRoot configuration = AutoscalerApplicationConfiguration.BuildFromEnvironment();
            var telemetry = AutoscalerTelemetryConfiguration.Bind(configuration).ForDiagnosticSmoke();
            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = Array.Empty<string>(),
            });
            builder.Configuration.AddConfiguration(configuration);
            builder.Logging.ClearProviders();
            builder.Services.AddScaleSetTelemetry(telemetry);

            using (var host = builder.Build())
            {
                await host.StartAsync().ConfigureAwait(false);
                try
                {
                    var logger = host.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("ScaleSet.Autoscaler.DiagnosticSmoke");
                    using (var activity = ScaleSetTelemetrySources.StartOperation("telemetry.smoke"))
                    {
                        activity?.SetTag("scaleset.diagnostic", "smoke");
                        LogTelemetrySmokeSignal(logger);
                        host.Services.GetRequiredService<ScaleSetTelemetryMetrics>().RecordOperation(
                            ScaleSetTelemetryOperation.DiagnosticSmoke,
                            ScaleSetTelemetryOutcome.Success,
                            TimeSpan.Zero);
                    }
                }
                finally
                {
                    await host.StopAsync().ConfigureAwait(false);
                }
            }

            output.WriteLine("Telemetry smoke export was attempted and flushed within the configured shutdown budget.");
            return 0;
        }
        catch (TelemetryConfigurationException exception)
        {
            error.WriteLine(exception.Message);
            return 2;
        }
        catch
        {
            error.WriteLine("Telemetry smoke could not complete safely. Check the configured OTLP endpoint and host diagnostics.");
            return 1;
        }
    }

    private static Task WriteUsageAsync(TextWriter output)
    {
        output.WriteLine("Usage: scaleset-autoscaler <validate-config|telemetry-smoke>");
        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "ScaleSet telemetry smoke signal emitted.")]
    private static partial void LogTelemetrySmokeSignal(ILogger logger);
}
