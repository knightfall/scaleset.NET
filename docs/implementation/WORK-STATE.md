# Implementation Status

Updated 2026-11-22 (Australia/Sydney). The protocol baseline is
[`actions/scaleset` at `716908e5`](https://github.com/actions/scaleset/tree/716908e507993947ada4273aecf2361c02e5836c).
Issues `#14`–`#19` track the implemented client parity work and baseline
refresh. Issues `#20`–`#21` track GHES and custom TLS transport work.

## Available today

- .NET 10 GitHub Runner Scale Set client and listener libraries for GitHub.com
  and GitHub Enterprise Cloud (`*.ghe.com`). Repository and organization scopes
  accept GitHub App or PAT credentials. Enterprise scope uses a classic PAT.
  The libraries align to upstream
  `actions/scaleset` (`716908e507993947ada4273aecf2361c02e5836c`) for:
  `SocketsHttpHandler` HTTP/2 keep-alive pings with HTTP/2 negotiation
  (`HttpVersion.Version20` with HTTP/1.1 fallback), strict JSON/null validation,
  rate-limit classification (`ScaleSetRateLimitException`), configurable
  retry/rate-limit/`MaxElapsed`/`Deadline` budgets (`ScaleSetRetryOptions`,
  `ScaleSetMaxElapsedExceededException`), per-call retry scopes
  (`ScaleSetRequestRetry`), and bounded inspectable response snapshots
  (`ScaleSetResponseSnapshot`).
- Provider-neutral runner ownership, capacity planning, durable lifecycle
  state, reconciliation, and drain-and-preserve shutdown.
- Docker Engine runner provider and a controller service with health endpoints,
  Linux x64/arm64 container packaging, and a Compose deployment.
- OpenTelemetry traces and metrics plus structured logs exported to a configured
  OTLP receiver.
- Four .NET 10 packages at `0.1.0-alpha.1` in the `Knightfall.ScaleSet.*` family.
  See the [usage guide](../usage.md) and [release runbook](../releases/PUBLISHING.md).

The Docker product can run without Kubernetes. The current executable is wired
to the Docker provider; AWS resource management is not implemented yet.

## Verification

The latest Linux arm64 run passed 429 tests, including both Docker integration
tests. The Compose controller and the standalone .NET worker each completed
a real GitHub Actions job on an ephemeral Linux ARM64 runner. The worker used
all four locally packed alpha packages. See the [verification record](../product/VERIFICATION.md).

## AWS product direction

The target is to run the controller in the customer's AWS account, hosted by
Lambda or ECS and authorized through a scoped IAM role. Operators configure
which AWS resources it can manage. The planned runner providers are ECS tasks
and direct EC2 instances. Parameter Store and Secrets Manager are planned for
runtime settings and credentials. The controller host, IAM policy, AWS
topology, bootstrap, state, and deployment model still need detailed design.

The GitHub App or PAT remains separate from AWS IAM: GitHub credentials
authorize GitHub API operations; the workload role authorizes AWS resource
operations. No AWS resources have been inspected, deployed, or changed.

## Known limits

- Custom GHES hosts (`#20`) and custom root CA / mTLS transport factories (`#21`) are not implemented (GHEC `github.com/enterprises/...` and `*.ghe.com` targets are supported).
- Mutable runtime `SystemInfo` (`SetSystemInfo`) is outside the current client-parity wave.
- Private-registry Docker image authentication is not implemented.
- Live engineering coverage is limited to the GitHub.com repository canaries.
  AWS-backed runner provisioning remains planned.
- Registry publication and registry consumer verification remain pending.
