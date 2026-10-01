# Migration Decisions

Status: **draft; final operational decisions pending**.
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

## Decisions currently being asked

1. **D12 [ASK USER] Shutdown and scale-set lifecycle.** Proposed: stop new
   acquisition, drain busy jobs, retain scale-set identity, and preserve still
   active resources after a configurable timeout. Destructive deletion is a
   separate explicit command.
2. **D13 [ASK USER] Product/deployment defaults.** Proposed: JSON configuration,
   environment/CLI overrides, mounted credential files, multiple scale sets,
   Linux x64/arm64 images, Compose, structured logs, health endpoints, optional
   OpenTelemetry/Prometheus, and no dashboard.
3. **D14 [ASK USER] Phase-two infrastructure format.** C# CDK, Terraform, or
   CloudFormation YAML. Provider implementations remain C# in every case.
4. **D15 [ASK USER] Engineering-default review.** Review the concrete defaults
   below: package family, test framework, restart-based configuration changes,
   configurable example timing/capacity values, and initial cloud scope.

## Proposed engineering defaults

These are concrete proposals, not statements about existing implementation.
They must be reviewed with the user before the plan is marked final.

- Public package/namespace family: `ScaleSet.Client`, `ScaleSet.Listener`,
  `ScaleSet.Hosting`; executable `ScaleSet.Autoscaler`. Provider assemblies:
  `ScaleSet.Providers.Docker`, then `.Ecs` and `.Ec2`. Package-ID availability
  must be checked before publication; no affiliation with GitHub is implied.
- xUnit tests with local HTTP fixtures and deterministic clocks/fake providers.
  Resolve exact compatible package versions during implementation, not from
  speculative version pins in this plan.
- Configuration changes require restart in v1. Runtime capacity setters remain
  available through the listener library; hot reload is a later feature.
- Defaults proposed for examples: zero minimum, ten maximum runners per set,
  explicit `SpareCapacity` policy, two concurrent creations, 30-second inventory
  reconciliation, 15-minute startup deadline, five-minute shutdown drain, and
  a 60-second bounded acknowledgment/cleanup operation. All are configurable;
  these numbers are design starting points, not measured performance targets.
- Docker workloads are trusted by default. Privileged mode and Docker daemon
  exposure require explicit resource-profile configuration. No claim of a
  hardened hostile multi-tenant execution boundary is made.
- Keep the repository's Apache-2.0 license and include upstream MIT attribution
  in a third-party notice when translating upstream code/fixtures. Verify
  actual copied material and include notices in NuGet/container distributions.
- Phase two initially uses configured existing AWS accounts/regions/network
  settings, On-Demand capacity, and one controller owner per scale set.
  Cross-account management, Spot recovery, and automatic failover are extensions.

## Finalization gate

Resolve D12-D15, including user review of the proposed defaults. Update the plan,
parity checklist, and concerns consistently; no unresolved `[ASK USER]`
remains in the final documents. Implementation-only facts, such as exact
package versions or measured throughput, may remain `[TODO]` with a milestone
and verification requirement. This branch contains planning documents only.

## Intent versus source reality

The user wants a production autoscaler. Upstream explicitly describes its
Docker executable as a demonstration, uses in-memory runner state, and deletes
resources on normal exit. Those are intentional product-design departures,
not failures to preserve the GitHub protocol. The current listener requires
the caller to own job acquisition and session lifecycle. The plan follows
that actual boundary rather than the broader listener comment.
