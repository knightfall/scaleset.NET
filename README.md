# scaleset.NET

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

The initial GitHub target is GitHub.com at repository or organization scope,
using a GitHub App or PAT. GHES and enterprise scope are not supported yet.

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
dotnet restore ScaleSet.slnx
dotnet build ScaleSet.slnx --configuration Release --no-restore
dotnet test --solution ScaleSet.slnx --configuration Release
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
