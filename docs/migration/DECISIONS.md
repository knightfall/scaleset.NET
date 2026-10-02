# Migration Decisions

Status: **phase-one decisions finalized**. Detailed AWS infrastructure planning
is deferred under D14.
Recorded on 2026-10-02, Australia/Sydney.

## Confirmed by the user

| ID | Decision | Consequence |
|---|---|---|
| D01 | Full autoscaler product with deployment tooling | Go client parity is necessary but insufficient; recovery, lifecycle, configuration, packaging, and operations are deliverables |
| D02 | Preserve API/protocol behavior with idiomatic C# public APIs | Preserve endpoint/payload/auth/message contracts; do not mechanically reproduce Go naming, mutexes, or example limitations |
| D03 | .NET 10 only | All implementation projects target `net10.0`; no .NET 8 or netstandard compatibility layer |
| D04 | Docker first with an extensible provider interface | Implement and validate the common lifecycle against Docker before cloud providers |
| D05 | ECS tasks and direct EC2 runner instances are two phase-two providers | Separate provisioning adapters; ECS-on-EC2 is a possible ECS capacity choice, not the direct-EC2 adapter |
| D06 | One active instance per scale set, restart reconciliation, preserved scale-set identity | Recover owned resources and refuse conflicting ownership; defer automatic failover/shared leadership |
| D07 | Runner scale, resource/architecture needs, and Docker/privilege behavior should be configurable | Per-scale-set resource profiles and hard limits; do not bake in one workload size or advertise an unmeasured throughput guarantee |
| D08 | GitHub.com repository/organization scopes, GitHub App and PAT first | GHES, enterprise scope, and concrete KMS/HSM integrations are later work; external JWT seam can be kept without a cloud dependency |
| D09 | NuGet client/listener libraries plus CLI/service and container image | Keep API/protocol packages reusable without Docker or AWS dependencies |
| D10 | Support both spare-capacity and total-floor scaling policies | Require a named policy in deployment examples; test both formulas |
| D11 | Execute implementation using GPT-6 Luna subagents at max reasoning | Use `gpt-6-luna`, `reasoning_effort=max`, explicit work packages, and parent integration/review |
| D12 | Normal shutdown uses A: drain and preserve | Stop new acquisition/provisioning, drain for a configurable period, retain the scale set, and leave unfinished/uncertain runners intact for reconciliation on restart |
| D13 | Compose, Linux x64/arm64 images, multiple scale sets, health endpoints, and no dashboard | Product deployment/operations features extend the upstream Docker demonstration while preserving supported GitHub protocol contracts |
| D14 | AWS infrastructure uses CDK v2 in TypeScript; detailed infrastructure planning is deferred until later | Reserve this direction for phase two; do not finalize cloud topology/stacks/IAM/deployment design in the phase-one plan |
| D15 | Accept the package family, xUnit and configurable starting limits/timers | Use the accepted engineering defaults below; package availability/performance still need release verification |
| D16 | Structured logging uses Microsoft ILogger with Serilog and its OpenTelemetry export sink | Code logs through ILogger<T>; the executable wires Serilog.Extensions.Hosting and Serilog.Sinks.OpenTelemetry |
| D17 | Tracing and metrics are mandatory and follow OpenTelemetry best practices | Libraries emit ActivitySource/Meter instrumentation; the executable registers SDK providers and OTLP export with correlation, semantic conventions, bounded cardinality, and verification |
| D18 | Environment-variable configuration first; Parameter Store and Secrets Manager focus in phase two | Replace proposed JSON/CLI configuration providers with environment binding; Parameter Store supplies settings and Secrets Manager supplies credentials in AWS |
| D19 | Phase-two runtime providers with caching and credential refresh; operational changes remain restart-based initially | Load AWS sources in the application, cache complete validated settings, refresh credential generations safely, and apply operational changes on restart |
| D20 | Keep telemetry export simple: a configured OTLP endpoint | Export logs, traces, and metrics to the supplied endpoint; backend selection and Collector deployment stay outside the product scope |
| D21 | Include Roslynator for C# code quality | Reference Roslynator.Analyzers in production/test builds, configure shared rule options/severities, and enforce selected diagnostics in CI |

## Decision resolution

All phase-one intent questions have been answered. There are no outstanding
user decisions blocking this plan. Future AWS infrastructure details remain
intentionally deferred rather than inferred.

## Accepted engineering defaults

These are accepted design requirements, not existing implementation or measured
runtime results.

- Public package/namespace family: `ScaleSet.Client`, `ScaleSet.Listener`,
  `ScaleSet.Hosting`; executable `ScaleSet.Autoscaler`. Provider assemblies:
  `ScaleSet.Providers.Docker`, then `.Ecs` and `.Ec2`. Package-ID availability
  must be checked before publication; no affiliation with GitHub is implied.
- xUnit tests with local HTTP fixtures and deterministic clocks/fake providers.
  Resolve exact compatible package versions during implementation, not from
  speculative version pins in this plan.
- Roslynator.Analyzers is a build-time dependency with private package assets.
  Configure repository-wide analyzer rules/options in .editorconfig, include
  it in production/test builds, and enforce selected diagnostics in CI.
- Configuration changes require restart in v1. Runtime capacity setters remain
  available through the listener library; hot reload is a later feature.
  Confirmed D19 adds runtime credential refresh in phase two without hot-applying
  operational setting changes.
- Accepted starting defaults: zero minimum, ten maximum runners per set,
  explicit `SpareCapacity` policy, two concurrent creations, 30-second inventory
  reconciliation, 15-minute startup deadline, five-minute shutdown drain, and
  a 60-second control-operation budget including acknowledgment/cleanup. All are configurable;
  these numbers are design starting points, not measured performance targets.
- Docker workloads are trusted by default. Privileged mode and Docker daemon
  exposure require explicit resource-profile configuration. No claim of a
  hardened hostile multi-tenant execution boundary is made.
- Keep the repository's Apache-2.0 license and include upstream MIT attribution
  in a third-party notice when translating upstream code/fixtures. Verify
  actual copied material and include notices in NuGet/container distributions.
- Phase-two infrastructure topology, account/network setup, capacity choices,
  and cloud rollout details are deferred to later planning (D14). ECS/EC2
  providers and runtime Parameter Store/Secrets Manager direction remain confirmed.

## Finalization result

All user decisions, including D15 and D21, are incorporated. The plan,
parity checklist, concerns and operational design are consistent, with no
unresolved phase-one product questions. At plan finalization, implementation-
only facts such as verified package compatibility or throughput remained
milestone-owned `[TODO]` items; current implementation and verification status
is tracked in [WORK-STATE.md](../implementation/WORK-STATE.md). Detailed AWS
infrastructure planning is intentionally deferred and does not block the
phase-one migration. It remains a prerequisite to future cloud implementation.

## Confirmed shutdown contract (D12)

Normal stop/restart/upgrade stops new job acquisition and provisioning, allows
existing jobs to finish within the configured drain period, and cleans up
resources whose terminal state is confirmed. At the deadline the controller
records recovery state, closes its session within a separate bounded budget,
and exits without force-stopping busy or uncertain runners. Scale-set identity
is retained. The next controller start discovers those owned resources before
resuming acquisition. No job resumption is implied if a runner or its host
was itself stopped.

Runner resources must outlive the controller process/container. This policy
assumes their Docker host/cloud compute remains available. Finished resources
may wait for cleanup until the controller restarts. Deployment stop grace
must exceed drain plus bounded acknowledgment/closure/persistence/telemetry-flush overhead.
Force-stop and permanent scale-set deletion are separate explicit operator
actions; reaching the normal drain timeout must never trigger either.

## Confirmed observability/configuration contract (D16-D20)

See [OBSERVABILITY-CONFIGURATION.md](OBSERVABILITY-CONFIGURATION.md) for the
log pipeline, independent SDK tracing/metrics pipeline, environment schema,
AWS source boundaries, instrumentation inventory, and acceptance tests.
The executable must enable and export traces and metrics. Library consumers
own their SDK/exporter composition. ILogger remains the public logging
abstraction; Serilog is configured by the executable.
All signals use the configured OTLP destination. Require a valid endpoint
and honor the selected protocol; operators supply the receiving service.
No backend-specific deployment or Collector provisioning is required.

## Intent versus source reality

The user wants a production autoscaler. Upstream explicitly describes its
Docker executable as a demonstration, uses in-memory runner state, and deletes
resources on normal exit. Those are intentional product-design departures,
not failures to preserve the GitHub protocol. The current listener requires
the caller to own job acquisition and session lifecycle. The plan follows
that actual boundary rather than the broader listener comment.
