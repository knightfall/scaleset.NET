using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScaleSet.Client;
using ScaleSet.CustomProvider.Example;
using ScaleSet.Hosting;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.Runtime;

using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
CancellationToken token = deadline.Token;
DirectoryInfo state = Directory.CreateTempSubdirectory("scaleset-provider-example-");
try
{
    DemoRunnerBackend backend = new();
    DemoSession firstSession = new();
    RunnerResource original;
    using (IHost first = BuildHost(state.FullName, backend, firstSession, TimeSpan.Zero))
    {
        await first.StartAsync(token).ConfigureAwait(false);
        await firstSession.Polled.WaitAsync(token).ConfigureAwait(false);
        await firstSession.SendAsync(new RunnerScaleSetMessage
        {
            MessageId = 1,
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
        }, token).ConfigureAwait(false);
        original = backend.Current is { Status: RunnerResourceStatus.Running } runner
            ? runner
            : throw new InvalidOperationException("The first host did not provision a running resource.");
        await first.StopAsync(token).ConfigureAwait(false);
        Require(firstSession.ClientDisposedAfterClose && backend.ProvidersDisposed == 1 && backend.Current == original,
            "The first host must close its session and preserve the running resource.");
    }

    DemoSession secondSession = new();
    using (IHost second = BuildHost(state.FullName, backend, secondSession, TimeSpan.FromSeconds(5)))
    {
        await second.StartAsync(token).ConfigureAwait(false);
        await secondSession.Polled.WaitAsync(token).ConfigureAwait(false);
        HostCapacityCoordinator ledger = second.Services.GetRequiredService<HostCapacityCoordinator>();
        Require(second.Services.GetRequiredService<AutoscalerHealthState>().IsReady
            && ledger.CaptureSnapshot().Usage.RunnerCount == 1 && backend.Current == original,
            "Restart must adopt the same resource into the shared capacity ledger.");
        await secondSession.SendAsync(new RunnerScaleSetMessage
        {
            MessageId = 1,
            Statistics = new RunnerScaleSetStatistic { TotalAssignedJobs = 1 },
        }, token).ConfigureAwait(false);
        Require(backend.RunnersCreated == 1, "Restart must use the adopted runner for existing demand.");

        Task stop = second.StopAsync(token);
        Require(!stop.IsCompleted, "Shutdown must wait while the adopted runner could still be active.");
        backend.CompleteRunner();
        await secondSession.SendAsync(new RunnerScaleSetMessage
        {
            MessageId = 2,
            Statistics = new RunnerScaleSetStatistic(),
            JobCompletedMessages =
            [
                new JobCompletedMessage
                {
                    RunnerRequestId = 3,
                    RunnerId = DemoControlPlane.RunnerId,
                    RunnerName = original.Identity.RunnerName,
                },
            ],
        }, token).ConfigureAwait(false);
        await stop.ConfigureAwait(false);
        Require(secondSession.ClientDisposedAfterClose && backend.ProvidersDisposed == 2
            && ledger.CaptureSnapshot().Usage.RunnerCount == 0 && backend.Current is null,
            "Drain must retire the completed runner, release capacity, and dispose the second provider.");
    }

    Console.WriteLine("Custom provider provisioned, adopted after restart, and drained one runner.");
}
finally
{
    state.Delete(recursive: true);
}

static IHost BuildHost(string stateDirectory, DemoRunnerBackend backend, DemoSession session, TimeSpan drainTimeout)
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder();
    builder.Services.AddSingleton<IRunnerProviderFactory>(new DemoRunnerProviderFactory(backend));
    builder.Services.AddSingleton<IScaleSetRuntimeClientFactory>(new DemoControlPlaneFactory(backend, session));
    builder.Services.AddScaleSetHosting(new AutoscalerOptions
    {
        Controller = new ControllerOptions
        {
            Identity = "example-controller",
            StateDirectory = stateDirectory,
            ReconcileInterval = TimeSpan.FromMilliseconds(200),
            ControlOperationTimeout = TimeSpan.FromSeconds(2),
        },
        HostCapacity = new HostCapacityOptions { MaxTotalRunners = 1 },
        Credentials = { ["demo"] = new CredentialOptions { Pat = "example-credential" } },
        ScaleSets =
        [
            new ScaleSetOptions
            {
                Key = "builds",
                GithubUrl = "https://github.com/example/repo",
                Name = "example-runners",
                CredentialsRef = "demo",
                Provider = "Demo",
                Scaling = new ScalingOptions { MaxRunners = 1 },
                RunnerProfile = new RunnerProfileOptions { Cpu = 1, Memory = 1024 },
                Lifecycle = new LifecycleOptions
                {
                    StartupTimeout = TimeSpan.FromSeconds(5),
                    ShutdownDrainTimeout = drainTimeout,
                    CleanupTimeout = TimeSpan.FromSeconds(2),
                },
            },
        ],
    });
    return builder.Build();
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
