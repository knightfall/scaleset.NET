# Knightfall.ScaleSet.Providers.Docker

Use `DockerRunnerProvider` as the runner backend for a .NET 10 controller.
Give the provider access to a Linux Docker Engine and a `RunnerProfileOptions`
with a pinned image, architecture, CPU and memory limits. The image must have
`/bin/sh`, `sleep`, `mkdir`, `mv`, `rm`, and `rmdir`. After bootstrap, the
provider executes the image entrypoint with the configured `Command` as its
arguments. The default command is `/home/runner/run.sh` and the default user
is `runner`.

```sh
dotnet add package Knightfall.ScaleSet.Providers.Docker --version 0.1.0-alpha.2
```

The provider labels containers by controller, scale set, and operation so a
worker can rediscover them. Create, start, inspection, and removal are separate
steps. GitHub job messages supply activity evidence because a running
container can be either idle or busy. Use a public image or preload a private
image on the Engine.

The first start transfers JIT through a short-lived Docker exec environment and
a file in a private directory on a container-local tmpfs. The bootstrap reads
and removes the file before acknowledging the handoff, then executes the
runner with `ACTIONS_RUNNER_INPUT_JITCONFIG`. A bootstrap without a completed
handoff exits after the Docker request timeout, capped at 30 seconds. The
bootstrap and sender use the same monotonic deadline after the handoff
directory is ready. The image must support fractional seconds for the short
shell polling delay. An exclusive staging directory gives the sender ownership
until the bootstrap claims the file by renaming it. Both sides clean up
their files on ordinary exit, and a forced stop discards the tmpfs.
Container creation data and the journal
carry no JIT value. Call `StartAsync` with the original JIT response immediately
after creation. The built-in provisioner handles this automatically. Recovery
reconciles a stale start by exact identity without resending the handoff.

Docker daemon access remains privileged. `HostSocket` gives jobs control of
the daemon and requires `AllowHostSocket=true`. For the autoscaler, set
`SCALESET__Providers__Docker__AllowHostSocket=true` only for trusted jobs on a
dedicated host.

Unprivileged runners drop Linux capabilities, disallow privilege escalation,
and disable container swap by default. Their process limit defaults to 1024.
In the autoscaler, configure Docker provider settings under `Providers:Docker`,
for example `SCALESET__Providers__Docker__PidsLimit=2048`. Custom images that
require capabilities, setuid, or swap can set
`SCALESET__Providers__Docker__HardenUnprivilegedContainers=false`.

Connect the provider to `ScaleSetRunnerProvisioner` for a custom worker, or run
the supplied autoscaler for a Compose deployment. See the [worker example](https://github.com/knightfall/scaleset.NET/tree/main/examples/dotnet-worker)
and [Docker quickstart](https://github.com/knightfall/scaleset.NET/blob/main/docs/implementation/DOCKER-DEPLOYMENT.md).

Use `DockerRunnerProvider.ActivitySourceName` for Engine request spans and
`DockerRunnerProvider.MeterName` for request counts and duration histograms.
Both subscriptions use `ScaleSet.Providers.Docker`.

Licensed under [Apache-2.0](https://github.com/knightfall/scaleset.NET/blob/main/LICENSE).
