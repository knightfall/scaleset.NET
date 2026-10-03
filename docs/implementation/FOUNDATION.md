# Shared Platform Foundation

This note records the shared .NET platform established in PR #2. It provides
the contracts and runtime conventions used by the GitHub client, listener,
controller, and runner providers. See [Implementation Status](WORK-STATE.md)
for the current product state.

## Foundation capabilities

- .NET 10 solution, centralized package versions, nullable references,
  deterministic builds, and analyzer warnings as errors.
- GitHub Runner Scale Set wire models and encoded job-message parsing.
- GitHub.com repository/organization targets and typed status/domain errors.
- A serial listener that handles initial statistics, empty polls, cursor and
  capacity updates, callback-before-ack, and cancellation-safe acknowledgment.
- Environment-bound controller configuration, validated scale-set ownership,
  and both overflow-safe scaling policies.
- Structured logging and OpenTelemetry traces/metrics with redaction.
- xUnit v3/native MTP test wiring and cross-platform CI.

The foundation has no AWS dependency. Infrastructure behavior is added through
runner-provider adapters in the hosting layer. The current implementation
includes a Docker adapter; the AWS controller and ECS/EC2 runner providers are
planned separately.

## Foundation verification record

At the foundation milestone, local verification on macOS arm64 with .NET SDK
10.0.400 reported:

- Solution tests: 108 passed, 0 failed, 0 skipped.
- Three alpha library packages were created locally and not published.
- Configuration validation passed with synthetic environment values and made
  no GitHub or Docker requests.
- Roslynator rejected a temporary invalid example; the corrected example
  built with zero warnings or errors.

The later Docker and controller work has its own evidence in
[Implementation Status](WORK-STATE.md), [Client](CLIENT.md), and
[Docker provider](DOCKER-PROVIDER.md). These foundation results do not establish
live GitHub, AWS, or production runner compatibility.
