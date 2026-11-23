using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScaleSet.Autoscaler.Configuration;
using ScaleSet.Autoscaler.Telemetry;
using ScaleSet.Hosting;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.Runtime;
using ScaleSet.Hosting.Telemetry;
using ScaleSet.Providers.Docker;

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

        if (args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            return await RunAutoscalerAsync(error).ConfigureAwait(false);
        }

        if (args[0].Equals("health-probe", StringComparison.OrdinalIgnoreCase))
        {
            return await RunHealthProbeAsync().ConfigureAwait(false);
        }

        await WriteUsageAsync(error).ConfigureAwait(false);
        return 2;
    }

    private static int ValidateConfiguration(TextWriter output, TextWriter error)
    {
        try
        {
            IConfigurationRoot configuration = AutoscalerApplicationConfiguration.BuildFromEnvironment();
            _ = AutoscalerConfiguration.Bind(configuration, [new DockerRunnerProviderFactory()]);
            _ = AutoscalerTelemetryConfiguration.Bind(configuration);
            _ = GetHealthListenUrl(configuration);
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

    /// <summary>Builds a production host from validated environment configuration.</summary>
    public static IHost BuildRunHost(
        IConfigurationRoot configuration,
        IScaleSetRuntimeFactory? runtimeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        AutoscalerOptions options = AutoscalerConfiguration.Bind(configuration, [new DockerRunnerProviderFactory()]);
        AutoscalerTelemetryConfiguration telemetry = AutoscalerTelemetryConfiguration.Bind(configuration);
        string healthListenUrl = GetHealthListenUrl(configuration);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(configuration);
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(healthListenUrl);
        builder.Services.AddScaleSetTelemetry(telemetry, options.ScaleSets.Select(static scaleSet => scaleSet.Key!));
        builder.Services.AddSingleton<IRunnerProviderFactory, DockerRunnerProviderFactory>();
        if (runtimeFactory is not null)
        {
            builder.Services.AddSingleton(runtimeFactory);
        }

        builder.Services.AddScaleSetHosting(options);
        builder.Services.PostConfigure<HostOptions>(host =>
        {
            long flushTicks = TimeSpan.FromMilliseconds(telemetry.FlushTimeoutMilliseconds).Ticks;
            host.ShutdownTimeout = TimeSpan.FromTicks(Math.Min(host.ShutdownTimeout.Ticks, long.MaxValue - flushTicks) + flushTicks);
        });
        WebApplication app = builder.Build();
        app.MapGet("/health/live", static (AutoscalerHealthState healthState) =>
            healthState.IsLive
                ? Results.Ok(new { status = "live" })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        app.MapGet("/health/ready", static (AutoscalerHealthState healthState) =>
            healthState.IsReady
                ? Results.Ok(new { status = "ready" })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        return app;
    }

    private static string GetHealthListenUrl(IConfiguration configuration)
    {
        const string defaultUrl = "http://0.0.0.0:8080";
        string value = configuration["Health:ListenUrl"] ?? defaultUrl;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new AutoscalerConfigurationException("Health:ListenUrl must be an absolute HTTP base URL without credentials, path, query, or fragment.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static async Task<int> RunAutoscalerAsync(TextWriter error)
    {
        try
        {
            IConfigurationRoot configuration = AutoscalerApplicationConfiguration.BuildFromEnvironment();
            try
            {
                using IHost host = BuildRunHost(configuration);
                await host.RunAsync().ConfigureAwait(false);
                return 0;
            }
            finally
            {
                (configuration as IDisposable)?.Dispose();
            }
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
            error.WriteLine("The autoscaler stopped after a runtime failure. Check structured logs for sanitized diagnostics.");
            return 1;
        }
    }

    private static async Task<int> RunHealthProbeAsync()
    {
        string address = Environment.GetEnvironmentVariable("SCALESET_HEALTH_URL")
            ?? "http://127.0.0.1:8080/health/live";
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return 1;
        }

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(2) };
            using HttpResponseMessage response = await client.GetAsync(uri, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static Task WriteUsageAsync(TextWriter output)
    {
        output.WriteLine("Usage: scaleset-autoscaler <validate-config|telemetry-smoke|run|health-probe>");
        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "ScaleSet telemetry smoke signal emitted.")]
    private static partial void LogTelemetrySmokeSignal(ILogger logger);
}
