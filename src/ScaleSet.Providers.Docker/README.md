# Knightfall.ScaleSet.Providers.Docker

Use `DockerRunnerProvider` as the runner backend for a .NET 10 controller.
Give the provider access to a Linux Docker Engine and a `RunnerProfileOptions`
with a pinned image, architecture, CPU and memory limits. The default command
runs `/home/runner/run.sh` as `runner` with the JIT configuration supplied in
`ACTIONS_RUNNER_INPUT_JITCONFIG`.

```sh
dotnet add package Knightfall.ScaleSet.Providers.Docker --version 0.1.0-alpha.1
```

The provider labels containers by controller, scale set, and operation so a
worker can rediscover them. Create, start, inspection, and removal are separate
steps. GitHub job messages supply activity evidence because a running
container can be either idle or busy. Use a public image or preload a private
image on the Engine.

Connect the provider to `ScaleSetRunnerProvisioner` for a custom worker, or run
the supplied autoscaler for a Compose deployment. See the [worker example](https://github.com/knightfall/scaleset.NET/tree/main/examples/dotnet-worker)
and [Docker quickstart](https://github.com/knightfall/scaleset.NET/blob/main/docs/implementation/DOCKER-DEPLOYMENT.md).

Licensed under [Apache-2.0](https://github.com/knightfall/scaleset.NET/blob/main/LICENSE).
