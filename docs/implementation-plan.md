# Reusable scale-set hosting

This design addresses [issue #44](https://github.com/knightfall/scaleset.NET/issues/44).
A .NET Generic Host should be able to run the same controller lifecycle as the
Docker autoscaler with a provider selected through dependency injection.

## Package and API boundary

- Move runtime creation, worker supervision, health state, identity locking,
  recovery, session handling, reconciliation, and drain to
  `ScaleSet.Hosting.Runtime`. Keep a single implementation of each lifecycle
  path so embedded hosts inherit the safeguards exercised by the autoscaler.
- Expose `AddScaleSetHosting(IServiceCollection, AutoscalerOptions)` for a
  configured host. Register the shared capacity coordinator, runtime factory,
  health state, and hosted service once per service provider. Use the caller's
  configured logging and `TimeProvider` with system defaults.
- Register reusable `IRunnerProviderFactory` services by provider name. A
  factory validates that provider's settings and creates a distinct provider
  for each configured scale set. The runtime owns the returned provider and
  honors asynchronous disposal where implemented. Factory services remain
  container-owned. The Docker package supplies the Docker factory.
- Use a replaceable narrow runtime-client factory with a default adapter over
  `ScaleSetClient` and the explicit `IMessageSession` ownership contract.
  The runtime owns the created client and session. Stop polling and wait for
  active work before closing a session, then dispose resources in dependency
  order.
- Keep ASP.NET health endpoints, Serilog, OpenTelemetry exporters, Docker
  registration, and application configuration sources in the executable's
  composition root. The Hosting package uses .NET hosting and DI abstractions.

## Configuration and state

- Bind the existing environment shape. Validate names, credentials, lifetimes,
  scaling limits, and capacity resources in Hosting. Run Docker image,
  architecture, and build-mode validation through the Docker factory. Custom
  factories can obtain their own typed settings from the consumer's DI
  container. Their scale sets use the shared CPU, memory, and runner limits.
- Preserve existing Docker profile properties and environment keys. A standalone
  configuration caller can supply its provider factories for validation of
  backend-specific settings. The hosted service rejects unknown or ambiguous
  provider names before starting a worker.
- Retain the existing on-disk lease, identity marker, and journal formats.
  Each configured set takes its exclusive local state lease before creating or
  discovering runners. The service adopts every set's inventory into the same
  capacity coordinator before it advertises capacity through any listener.
- Keep readiness tied to successful initialization of all configured inventories.
  Keep the bounded per-set shutdown drain and independent session-close budget.
  Startup failure and terminal worker failure stop the host workers and release
  controller resources while preserving protected runner resources.

## Delivery and verification

1. Move the runtime without changing its lifecycle rules. Run the existing
   restart, reservation, message replay, session recovery, reconciliation, and
   disposal tests against the Hosting implementation.
2. Wire provider selection and validation through the shared DI entry point.
   Verify provider and client ownership, cleanup during partial startup, and
   multiple sets competing within the same capacity budget.
3. Add an external Generic Host example that registers a test provider. Exercise
   a real hosting session lifecycle against a deterministic test endpoint,
   provision a runner, restart with the same state and adopt it, then drain.
   Build and run the example against the candidate packages in release CI.
4. Verify the Docker executable uses the same Hosting registration and keeps
   its documented Compose configuration and telemetry behavior.
