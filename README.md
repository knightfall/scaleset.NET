# scaleset.NET

.NET 10 implementation of GitHub Actions Runner Scale Set contracts and a
Docker autoscaler, developed in the milestones below.

The migration preserves GitHub API and message protocol behavior while using
idiomatic asynchronous C# APIs. Phase one targets Docker. Phase two adds
separate ECS-task and direct EC2-instance providers.

## Planning documents

- [Migration plan](docs/migration/PLAN.md)
- [Confirmed decisions](docs/migration/DECISIONS.md)
- [Protocol parity checklist](docs/migration/PROTOCOL-PARITY.md)
- [Observability and configuration design](docs/migration/OBSERVABILITY-CONFIGURATION.md)
- [Analysis and validation evidence](docs/migration/VALIDATION.md)
- [Upstream architecture](docs/codebase/ARCHITECTURE.md)
- [Stack](docs/codebase/STACK.md), [structure](docs/codebase/STRUCTURE.md),
  [conventions](docs/codebase/CONVENTIONS.md),
  [integrations](docs/codebase/INTEGRATIONS.md),
  [testing](docs/codebase/TESTING.md), and
  [concerns](docs/codebase/CONCERNS.md)

Implementation status: **foundation milestone implemented and locally verified**. The planning PR
is merged. Wire contracts, a serial listener, validated environment settings,
scaling policies and OTLP composition are implemented and tested.
GitHub HTTP/auth/session transport and Docker provisioning are later milestones.
Detailed AWS infrastructure planning is deferred. The analysis baseline is
[`actions/scaleset` at e6daac7](https://github.com/actions/scaleset/tree/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5).

## Development

Use the SDK pinned by `global.json` (.NET 10). Roslynator is a private build-time
analyzer dependency. Enabled compiler/analyzer warnings are build errors.

```sh
dotnet restore ScaleSet.slnx
dotnet build ScaleSet.slnx --configuration Release --no-restore
dotnet test --solution ScaleSet.slnx --configuration Release --no-build
```

Tests use xUnit v3 with native Microsoft Testing Platform; solution discovery
is part of the validation. Dependencies are centrally versioned.

The executable exposes bounded foundation commands:

```sh
dotnet run --project src/ScaleSet.Autoscaler -- validate-config
dotnet run --project src/ScaleSet.Autoscaler -- telemetry-smoke
```

`validate-config` checks product settings and required telemetry without GitHub
or Docker side effects. `telemetry-smoke` emits logs, traces and metrics to the
configured OTLP receiver; verify receipt at that receiver. It does not require
GitHub credentials. See the [environment and telemetry schema](docs/migration/OBSERVABILITY-CONFIGURATION.md).
No complete autoscaler run loop is advertised at this milestone.
