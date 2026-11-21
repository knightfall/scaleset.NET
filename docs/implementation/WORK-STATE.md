# Implementation Status

Updated: 2026-11-21 (Australia/Sydney). Upstream `actions/scaleset`
(`716908e507993947ada4273aecf2361c02e5836c`) parity work is tracked per issue in
`#14`–`#19`: `#14`–`#18` (PRs `#22`–`#26`) are merged into `main` (`694f9b0`),
`#19` refreshes the documentation baseline, and `#20`–`#21` track deferred backlog
items.

## Available today

- .NET 10 GitHub Runner Scale Set client and listener libraries with GitHub App
  and PAT authentication for GitHub.com (`github.com`/`www.github.com`) and
  GitHub Enterprise Cloud (`github.com/enterprises/{enterprise}`, `*.ghe.com`)
  repository, organization, and enterprise targets, aligned to upstream
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

The Docker product can run without Kubernetes. The current executable is wired
to the Docker provider; AWS resource management is not implemented yet.

## Verification

- The latest .NET 10 Release build (`dotnet build ScaleSet.slnx --configuration Release --no-restore -m:1 -nr:false`) completed with 0 warnings and 0 errors.
- The latest Release test run on macOS arm64 (`dotnet test --solution ScaleSet.slnx --configuration Release --no-build`) passed 422/422 non-integration tests (194 Client, 20 Listener, 161 Hosting including loopback OTLP telemetry, and 47 Docker unit tests), with 2 opt-in `ScaleSet.Docker.IntegrationTests` skipped (requires a Linux Docker daemon and `SCALESET_DOCKER_INTEGRATION=1`).

These checks cover deterministic protocol behavior and the Docker controller
unit/hosting suites. They do not establish live GitHub or AWS compatibility. A
designated GitHub canary and AWS test environment are still required.

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
- Mutable runtime `SystemInfo` (`SetSystemInfo`) and Docker live E2E execution on this host are out of the current client-parity wave.
- Private-registry Docker image authentication is not implemented.
- Live GitHub workflows and AWS-backed runner provisioning have not been
  exercised.
- No package or container image has been published.
