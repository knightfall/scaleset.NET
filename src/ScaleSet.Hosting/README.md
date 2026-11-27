# Knightfall.ScaleSet.Hosting

Use this .NET 10 library to turn GitHub job demand into ephemeral runner
capacity. Register the controller and a runner provider in your .NET Generic
Host. The controller opens the durable state and owns the listener lifecycle.

```sh
dotnet add package Knightfall.ScaleSet.Hosting --version 0.1.0-alpha.1
```

## Run the controller in a .NET host

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScaleSet.Hosting;
using ScaleSet.Hosting.Providers;
using ScaleSet.Providers.Docker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IRunnerProviderFactory, DockerRunnerProviderFactory>();
builder.Services.AddScaleSetHosting(options);
await builder.Build().RunAsync();
```

Supply a configured `AutoscalerOptions` with a writable persistent state
directory, a stable controller identity, and a credential reference for each
scale set. Set `ScaleSetOptions.Provider` to the registered factory name.
The Docker registration shown here also requires
`Knightfall.ScaleSet.Providers.Docker`. Use the
[Docker quickstart](https://github.com/knightfall/scaleset.NET/tree/main/examples/docker-compose)
for environment settings, or supply typed options directly. Treat options as
immutable after registration.

A factory validates backend-specific settings and creates a separate
`IRunnerProvider` per scale set. Providers discover only resources in the
requested ownership scope and use the durable operation ID to correlate
create, start, inspect, and remove. The runtime owns each created provider
and control-plane client. It closes the session and honors asynchronous
provider disposal during cleanup. .NET dependency injection owns the
registered factory services.

Startup reconstructs the shared count, CPU, and memory ledger from all owned
inventories before listeners advertise capacity. The controller periodically
reconciles inventory and, during shutdown, halts admissions and drains work
within each scale set's configured timeout. It retains potentially active
runners and their durable records for the next start.

The [custom provider example](https://github.com/knightfall/scaleset.NET/tree/main/examples/custom-provider-worker)
exercises provisioning, restart adoption, and drain with an in-memory backend
and the candidate packages. The host uses the production GitHub client by
default. Tests can register an `IScaleSetRuntimeClientFactory` to supply
a separate control-plane client.

## Compose individual components

Use `ScaleSetMessageHandler` with a required `ScaleSetCapacityContext` if
your application owns the worker lifecycle. Share a `HostCapacityCoordinator`
and its demand registry across sets, and pass each listener's
`SetMaxRunners` to its context. `ScaleSetRunnerProvisioner` records intent
before requesting a JIT configuration and creating a runner. `RunnerLifecycleManager`
and `FileRunnerStateJournal` retain operation evidence for restart recovery.
See the [component-level worker example](https://github.com/knightfall/scaleset.NET/tree/main/examples/dotnet-worker).

The built-in file journal stores current operation rows and terminal replay
tombstones in SQLite. Reconciliation queries active rows and reads terminal
records by exact identity. The in-memory capacity coordinator caches only
recent release receipts. Use the journal as the durable authority for replay
protection.

Licensed under [Apache-2.0](https://github.com/knightfall/scaleset.NET/blob/main/LICENSE).
