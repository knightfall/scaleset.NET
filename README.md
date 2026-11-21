# scaleset.NET

[![.NET CI](https://github.com/knightfall/scaleset.NET/actions/workflows/dotnet.yml/badge.svg?branch=main)](https://github.com/knightfall/scaleset.NET/actions/workflows/dotnet.yml)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/knightfall/scaleset.NET/badge)](https://scorecard.dev/viewer/?uri=github.com/knightfall/scaleset.NET)
[![NuGet prerelease](https://img.shields.io/nuget/vpre/Knightfall.ScaleSet.Client)](https://www.nuget.org/packages/Knightfall.ScaleSet.Client)
[![GitHub release](https://img.shields.io/github/v/release/knightfall/scaleset.NET?include_prereleases)](https://github.com/knightfall/scaleset.NET/releases)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

**scaleset.NET is a .NET-native autoscaler for GitHub Actions self-hosted runners.**
It is built for teams that want to run runners on infrastructure they control
without operating Kubernetes. The controller reads GitHub Runner Scale Set
demand, provisions runners through infrastructure providers, tracks job
activity, and recovers runner state after restarts.

The GitHub client, listener, and runner lifecycle are separate from the compute
provider. This keeps the GitHub integration reusable while allowing the
controller to manage different runner environments.

## Current implementation

The current product includes .NET 10 client and listener libraries, a Docker
runner provider, and a controller service with durable state, shared capacity
planning, health endpoints, and bounded drain-and-preserve shutdown. The
provided container and Compose deployment run the controller without
Kubernetes.

The client supports repository, organization, and enterprise targets on
GitHub.com and GitHub Enterprise Cloud (`*.ghe.com`) with GitHub App or
personal access token (PAT) authentication. Its protocol baseline is
[`actions/scaleset` at `716908e5`](https://github.com/actions/scaleset/tree/716908e507993947ada4273aecf2361c02e5836c).
It includes HTTP/2 keep-alives, rate-limit classification, configurable retry
budgets, and bounded response snapshots. GitHub Enterprise Server (GHES)
support is planned.

## NuGet quickstart

The `0.1.0-alpha.1` prerelease targets .NET 10. Use the install commands once
the packages are available in the registry.

| Package | Purpose |
| --- | --- |
| [`Knightfall.ScaleSet.Client`](https://www.nuget.org/packages/Knightfall.ScaleSet.Client) | GitHub authentication, scale-set administration, and message sessions |
| [`Knightfall.ScaleSet.Listener`](https://www.nuget.org/packages/Knightfall.ScaleSet.Listener) | Polling, callbacks, job acquisition, and acknowledgment |
| [`Knightfall.ScaleSet.Hosting`](https://www.nuget.org/packages/Knightfall.ScaleSet.Hosting) | Provider contracts, scaling, and durable runner lifecycle |
| [`Knightfall.ScaleSet.Providers.Docker`](https://www.nuget.org/packages/Knightfall.ScaleSet.Providers.Docker) | Docker Engine runner provisioning and recovery |

```sh
dotnet add package Knightfall.ScaleSet.Client --version 0.1.0-alpha.1
```

See the [usage guide](docs/usage.md) for a client example, GitHub Packages
configuration, and the Docker controller setup.

## AWS direction

The product direction is to run the controller in the customer's AWS account,
hosted by Lambda or ECS and authorized with an IAM role. Operators configure
which AWS resources the controller can manage. Runner providers will initially
target ECS tasks and direct EC2 instances; detailed AWS topology, IAM policies,
bootstrap, and deployment design remain to be completed.

Controller hosting and runner compute are separate choices: for example, an
ECS-hosted controller can provision runner tasks in a configured ECS cluster.
GitHub credentials (App or PAT) authorize GitHub API calls; the controller's
IAM role authorizes AWS operations.

## Documentation

- [Alpha release notes](docs/releases/0.1.0-alpha.1.md)
- [Publishing and provenance verification](docs/releases/PUBLISHING.md)
- [Security policy](SECURITY.md)
- [Product roadmap](docs/product/ROADMAP.md)
- [Product decisions](docs/product/DECISIONS.md)
- [GitHub protocol contract](docs/product/GITHUB-PROTOCOL.md)
- [Observability and configuration](docs/product/OBSERVABILITY-CONFIGURATION.md)
- [Verification evidence](docs/product/VERIFICATION.md)
- [Docker provider](docs/implementation/DOCKER-PROVIDER.md)
- [Docker deployment](docs/implementation/DOCKER-DEPLOYMENT.md)
- [GitHub client and listener](docs/implementation/CLIENT.md)
- [Implementation status](docs/implementation/WORK-STATE.md)
- [Go reference source notes](docs/reference/actions-scaleset/ARCHITECTURE.md)

## Development

Use the SDK pinned by `global.json` (.NET 10). Roslynator is a private
build-time analyzer dependency. Enabled compiler and analyzer warnings are
build errors.

```sh
dotnet restore ScaleSet.slnx --locked-mode
dotnet build ScaleSet.slnx --configuration Release --no-restore
dotnet test --solution ScaleSet.slnx --configuration Release --no-build
```

Tests use xUnit v3 with native Microsoft Testing Platform. Dependencies are
centrally versioned.

The executable provides these commands:

```sh
dotnet run --project src/ScaleSet.Autoscaler -- validate-config
dotnet run --project src/ScaleSet.Autoscaler -- telemetry-smoke
dotnet run --project src/ScaleSet.Autoscaler -- run
```

`validate-config` checks application settings and required telemetry without
GitHub or Docker side effects. `telemetry-smoke` emits logs, traces, and
metrics to the configured OTLP receiver; verify receipt at that receiver. It
does not require GitHub credentials. `run` starts the autoscaler and health
endpoints. See the [observability and configuration guide](docs/product/OBSERVABILITY-CONFIGURATION.md).
