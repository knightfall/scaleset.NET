# Knightfall.ScaleSet.Hosting

Use this .NET 10 library to turn GitHub job demand into ephemeral runner
capacity. Provide a runner backend, open a durable journal for the scale set,
and connect the message handler to a `ScaleSetListener`.

```sh
dotnet add package Knightfall.ScaleSet.Hosting --version 0.1.0-alpha.1
```

`ScaleSetMessageHandler` processes job offers and lifecycle events.
`HostCapacityCoordinator` plans and reserves count, CPU, and memory capacity
using GitHub's assigned-job statistics. Construct the handler with a required
`ScaleSetCapacityContext` before the optional logger. Share one coordinator
and its demand registry across the host's scale sets, and pass each listener's
`SetMaxRunners` to its context. GitHub started and completed events correlate
by the scoped runner ID and name. A completed runner continues to use
capacity until provider inspection confirms its absence.

`ScaleSetRunnerProvisioner` records
intent before requesting a JIT configuration and creating a runner.
`RunnerLifecycleManager` and `FileRunnerStateJournal` record evidence about
each operation for reconciliation after restart.

Implement `IRunnerProvider` to discover resources by stable ownership, create
or start a runner, inspect its status, and remove it safely. The Docker package
implements this contract. Your worker owns startup reconciliation, message
session lifetime, and shutdown. See the [worker example](https://github.com/knightfall/scaleset.NET/tree/main/examples/dotnet-worker)
and [provider implementation](https://github.com/knightfall/scaleset.NET/blob/main/docs/implementation/DOCKER-PROVIDER.md).
