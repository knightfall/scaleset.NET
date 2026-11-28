# .NET worker example

This .NET 10 console worker uses the four `Knightfall.ScaleSet.*` packages to
run a single Docker-backed scale set with capacity for one runner. It uses an
existing scale set so it can focus on package composition and session lifetime.
Create the set with the [Docker quickstart](../../docs/implementation/DOCKER-DEPLOYMENT.md)
or a call to `ScaleSetClient.CreateScaleSetAsync`. Stop any other controller
for that scale set before starting this worker.

## Restore local candidate packages

The project builds against source within the repository. To exercise it as a
package consumer, build the packages and copy the example outside the checkout
with a fresh package cache. Run these commands from the repository root in a
shell like Bash.

```sh
dotnet pack ScaleSet.slnx --configuration Release --output "$PWD/artifacts/packages"
worker=$(mktemp -d)
mkdir "$worker/feed"
cp artifacts/packages/Knightfall.ScaleSet.*.0.1.0-alpha.2.nupkg "$worker/feed/"
cp examples/dotnet-worker/*.cs examples/dotnet-worker/*.csproj examples/dotnet-worker/NuGet.Local.Config "$worker/"
dotnet restore "$worker/ScaleSet.Worker.Example.csproj" \
  --configfile "$worker/NuGet.Local.Config" --packages "$worker/packages"
```

After publication, restore directly from a registry using the four package
references in the example project. Keep their versions aligned.

## Check GitHub access

Set `SCALESET_GITHUB_PAT` in the process environment from your secret store.
Use a credential for the chosen target scope, as described in the
[authentication guide](../../docs/usage.md#choose-a-github-credential).

```sh
export SCALESET_TARGET_URL=https://github.com/OWNER/REPOSITORY
export SCALESET_NAME=ci-linux
dotnet run --project "$worker/ScaleSet.Worker.Example.csproj" --no-restore -- --check
```

The check prints the existing scale set's ID and runner group after two
authenticated lookups. Set `SCALESET_RUNNER_GROUP` for a group other than `Default`.

## Process a job

```sh
export SCALESET_WORKER_STATE="$worker/state"
export SCALESET_RUNNER_IMAGE=ghcr.io/actions/actions-runner@sha256:6d0b2d80d80f143f77dcb676e50920100bbb547e39e883454d57f45ab8c31300
dotnet run --project "$worker/ScaleSet.Worker.Example.csproj" --no-restore
```

Use `SCALESET_RUNNER_ARCHITECTURE=Arm64` for an arm64 runner. The worker needs
a Unix socket connected to a Linux Docker Engine. Set `SCALESET_DOCKER_SOCKET`
if your socket differs from `/var/run/docker.sock`. On macOS with the default
Docker Desktop context, use `$HOME/.docker/run/docker.sock`.
The workflow's `runs-on` value must match a label on
the existing scale set. Dispatch the [one-job workflow](../docker-compose/runner-smoke.yml)
with `runs-on: ci-linux` when the worker prints `Listening for jobs`.

The worker opens a durable file journal, constructs the provisioner and
capacity coordinator, and opens a GitHub session. The Hosting message handler
acquires jobs and asks the Docker provider to create runners using JIT
configuration. On Ctrl+C or Unix `SIGTERM`, the worker drains for up to 30
seconds, cancels polling, and closes the session with a separate timeout.

The handler's `ScaleSetCapacityContext` connects the host capacity coordinator
and its demand registry to the listener's advertised maximum. A worker that
runs multiple scale sets should share the coordinator and registry across
their handlers so they observe the same host budget. The example sizes
capacity for one scale set. This worker uses callback-driven terminal cleanup.
The autoscaler also runs periodic inventory reconciliation at
`Controller.ReconcileInterval`. Idle retirement is disabled.

The example starts with empty owned Docker inventory and a journal with no
unfinished operations. Use the [autoscaler runtime](../../src/ScaleSet.Hosting/Runtime/ScaleSetRuntimeFactory.cs)
as the reference for reconciling unfinished operations after a restart.
