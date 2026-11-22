using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ScaleSet.Client;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Controller;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;
using ScaleSet.Listener;
using ScaleSet.Providers.Docker;
using ScaleSet.Worker.Example;

const string controllerId = "sdk-worker";
bool checkOnly = args is ["--check"];
if (args.Length != 0 && !checkOnly)
{
    Console.Error.WriteLine("Usage: ScaleSet.Worker.Example [--check]");
    return 2;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
using var terminate = OperatingSystem.IsWindows()
    ? null
    : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        shutdown.Cancel();
    });

try
{
    using var logs = LoggerFactory.Create(builder => builder.AddSimpleConsole());
    GitHubTarget target = GitHubTarget.Parse(RequiredSetting("SCALESET_TARGET_URL"));
    var credentials = ScaleSetCredentials.FromPersonalAccessToken(RequiredSetting("SCALESET_GITHUB_PAT"));
    using var client = new ScaleSetClient(target, credentials, logger: logs.CreateLogger<ScaleSetClient>());

    string scaleSetName = RequiredSetting("SCALESET_NAME");
    string groupName = Environment.GetEnvironmentVariable("SCALESET_RUNNER_GROUP") ?? "Default";
    RunnerGroup group = await client.GetRunnerGroupByNameAsync(groupName, shutdown.Token);
    RunnerScaleSet scaleSet = await client.FindScaleSetAsync(group.Id, scaleSetName, shutdown.Token)
        ?? throw new InvalidOperationException($"Scale set '{scaleSetName}' was not found in group '{groupName}'.");
    Console.WriteLine($"Found scale set '{scaleSet.Name}' (ID {scaleSet.Id}) in group '{group.Name}'.");
    if (checkOnly)
    {
        return 0;
    }

    var settings = new ScaleSetOptions
    {
        Key = scaleSetName,
        Scaling = new ScalingOptions { MaxRunners = 1 },
        RunnerProfile = new RunnerProfileOptions
        {
            Image = RequiredSetting("SCALESET_RUNNER_IMAGE"),
            Architecture = ReadArchitecture(),
            Cpu = 1,
            Memory = 2048,
        },
    };
    var scope = new RunnerOwnershipScope(controllerId, scaleSetName, scaleSet.Id);
    var dockerOptions = new DockerProviderOptions
    {
        SocketPath = Environment.GetEnvironmentVariable("SCALESET_DOCKER_SOCKET") ?? "/var/run/docker.sock",
    };
    using var provider = new DockerRunnerProvider(settings.RunnerProfile, dockerOptions, logger: logs.CreateLogger<DockerRunnerProvider>());
    await using var journal = await FileRunnerStateJournal.OpenAsync(RequiredSetting("SCALESET_WORKER_STATE"), scope, shutdown.Token);
    var records = await journal.ReadAllAsync(shutdown.Token);
    var inventory = await provider.DiscoverAsync(scope, shutdown.Token);
    if (records.Any(record => record.Stage is not (RunnerLifecycleStage.Gone or RunnerLifecycleStage.Failed)) || !inventory.IsEmpty)
    {
        throw new InvalidOperationException("The sample needs an empty runner inventory and no unfinished journal records. Reconcile the previous run before restarting.");
    }

    using var lifecycle = new RunnerLifecycleManager(journal, TimeProvider.System, logs.CreateLogger<RunnerLifecycleManager>());
    using var provisioner = new ScaleSetRunnerProvisioner(
        scaleSet.Id, settings.RunnerProfile.WorkFolder, new JitConfigClient(client), provider, journal,
        logs.CreateLogger<ScaleSetRunnerProvisioner>());
    var coordinator = new HostCapacityCoordinator(new HostCapacityOptions { MaxTotalRunners = 1 }, [settings]);
    MessageSessionClient session = await client.CreateMessageSessionAsync(scaleSet.Id, controllerId, shutdown.Token);
    try
    {
        var listener = new ScaleSetListener(session, new ScaleSetListenerOptions { ScaleSetId = scaleSet.Id, MaxRunners = 1 },
            logs.CreateLogger<ScaleSetListener>());
        var capacity = new ScaleSetCapacityContext(settings, coordinator, coordinator.DemandRegistry, listener.SetMaxRunners);
        var handler = new ScaleSetMessageHandler(scope, session, provisioner, lifecycle, journal,
            logs.CreateLogger<ScaleSetMessageHandler>(), capacity);
        Console.WriteLine("Listening for jobs. Press Ctrl+C to drain and close the session.");
        await ListenAsync(listener, handler, shutdown.Token);
    }
    finally
    {
        using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.CloseAsync(closeTimeout.Token);
        Console.WriteLine("Closed the GitHub message session.");
    }

    return 0;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    return 0;
}

static async Task ListenAsync(ScaleSetListener listener, ScaleSetMessageHandler handler, CancellationToken stoppingToken)
{
    using var polling = new CancellationTokenSource();
    Task listening = listener.RunAsync(handler, polling.Token);
    try
    {
        await listening.WaitAsync(stoppingToken);
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
        if (!await handler.WaitForDrainAsync(TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None))
        {
            Console.Error.WriteLine("Drain deadline elapsed. Unfinished runners are preserved.");
        }
    }
    finally
    {
        await polling.CancelAsync();
        try
        {
            await listening;
        }
        catch (OperationCanceledException) when (polling.IsCancellationRequested)
        {
            // The drain ended and the pending long poll was canceled.
        }
    }
}

static string RequiredSetting(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value
    : throw new InvalidOperationException($"Set {name}.");

static RunnerArchitecture ReadArchitecture() => Environment.GetEnvironmentVariable("SCALESET_RUNNER_ARCHITECTURE") switch
{
    null or "X64" => RunnerArchitecture.X64,
    "Arm64" => RunnerArchitecture.Arm64,
    _ => throw new InvalidOperationException("Set SCALESET_RUNNER_ARCHITECTURE to X64 or Arm64."),
};
