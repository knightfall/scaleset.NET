# Verification Evidence

Updated: 2026-11-21 (Australia/Sydney)
Baseline: `actions/scaleset` `716908e507993947ada4273aecf2361c02e5836c` (Australia/Sydney).

## Reference protocol source

- Repository: [`actions/scaleset`](https://github.com/actions/scaleset).
- Source commit used for protocol and architecture review:
  `716908e507993947ada4273aecf2361c02e5836c`.
- Source observations and test patterns are recorded in
  [`docs/reference/actions-scaleset`](../reference/actions-scaleset/ARCHITECTURE.md).

The saved Go race-enabled test run on macOS arm64 exited with code 1 because
`TestServerWithSelfSignedCertificates/client_without_ca_certs` asserted an
OS-specific error-string suffix. The preceding assertion confirmed that
certificate validation failed. The precise returned wording was not captured;
no source change or skipped-test workaround was made. Other reported packages
passed, and the output contained no race-detector report. The live Go example
test was gated off.

## scaleset.NET verification

The latest .NET 10 Release validation on macOS arm64 completed with 0 warnings
and 0 errors (`dotnet build ScaleSet.slnx --configuration Release --no-restore -m:1 -nr:false`),
and `dotnet test --solution ScaleSet.slnx --configuration Release --no-build`
passed 422/422 non-integration tests (`194` Client, `20` Listener, `161`
Hosting including loopback OTLP telemetry, and `47` Docker unit tests) with 0
failures and `2` opt-in `ScaleSet.Docker.IntegrationTests` skipped (requires a
Linux Docker daemon and `SCALESET_DOCKER_INTEGRATION=1`).

These results cover deterministic GitHub protocol tests (including GHEC
`github.com/enterprises/...` and `*.ghe.com` + PAT targeting, HTTP/2
`SocketsHttpHandler` configuration, strict JSON/null validation, rate-limit
classification, retry budgets/`OnRateLimit`, and response snapshots) and the
Docker provider/runtime unit suites. They do not verify a live GitHub runner
workflow or Docker live E2E daemon execution on macOS. No live GitHub canary,
AWS resource operation, AWS deployment, NuGet publication, or container
publication has been performed.

## Current support and limits

- GitHub.com (repository, organization, enterprise) and GitHub Enterprise
  Cloud (`*.ghe.com`) targets with GitHub App and PAT authentication are covered
  by deterministic tests. Custom GHES hosts and custom root CA / mTLS
  transport helpers have not been implemented or exercised.
- Docker is the implemented runner provider. ECS task and direct EC2 providers,
  AWS role-based resource management, and Lambda/ECS controller deployment
  remain planned work.
- The source-reference guides describe the Go project; they are not a map of
  the .NET implementation. Product implementation evidence is in
  [`docs/implementation`](../implementation/CLIENT.md).

Re-run the appropriate build and test gates after code changes. A designated
GitHub.com canary and AWS integration environment are required before claiming
live GitHub or AWS compatibility.
