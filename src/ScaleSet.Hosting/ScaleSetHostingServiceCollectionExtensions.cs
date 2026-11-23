using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.Runtime;

namespace ScaleSet.Hosting;

/// <summary>Registers the scale-set controller with a .NET Generic Host.</summary>
public static class ScaleSetHostingServiceCollectionExtensions
{
    /// <summary>Adds a controller that manages the configured scale sets as a single host service.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="options">Fixed settings for this host. Treat the options as immutable after registration.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Register an <see cref="IRunnerProviderFactory"/> for each selected backend.
    /// The controller validates the configuration before it starts any workers.
    /// </remarks>
    public static IServiceCollection AddScaleSetHosting(this IServiceCollection services, AutoscalerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.TryAddSingleton<IScaleSetRuntimeClientFactory, DefaultScaleSetRuntimeClientFactory>();
        services.TryAddSingleton<IScaleSetRuntimeFactory, ScaleSetRuntimeFactory>();
        services.TryAddSingleton<AutoscalerHealthState>();
        services.TryAddSingleton(serviceProvider =>
        {
            IReadOnlyList<string> errors = AutoscalerConfiguration.Validate(options, serviceProvider.GetServices<IRunnerProviderFactory>());
            if (errors.Count > 0)
            {
                throw new AutoscalerConfigurationException(errors);
            }

            return options;
        });
        services.TryAddSingleton(serviceProvider =>
        {
            AutoscalerOptions validated = serviceProvider.GetRequiredService<AutoscalerOptions>();
            return new HostCapacityCoordinator(validated.HostCapacity, validated.ScaleSets);
        });
        services.AddOptions<HostOptions>().PostConfigure<AutoscalerOptions>((host, validated) =>
        {
            TimeSpan minimum = GetControllerShutdownTimeout(validated);
            if (host.ShutdownTimeout < minimum)
            {
                host.ShutdownTimeout = minimum;
            }
        });
        services.AddHostedService<ScaleSetAutoscalerService>();
        return services;
    }

    private static TimeSpan GetControllerShutdownTimeout(AutoscalerOptions options)
    {
        long drainTicks = 0;
        long remainingTicks = SaturatingAdd(TimeSpan.FromSeconds(5).Ticks, options.Controller.ControlOperationTimeout.Ticks);
        foreach (ScaleSetOptions scaleSet in options.ScaleSets)
        {
            drainTicks = Math.Max(drainTicks, scaleSet.Lifecycle.ShutdownDrainTimeout.Ticks);
            remainingTicks = SaturatingAdd(remainingTicks, scaleSet.Lifecycle.CleanupTimeout.Ticks);
        }

        return TimeSpan.FromTicks(SaturatingAdd(remainingTicks, drainTicks));
    }

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
}
