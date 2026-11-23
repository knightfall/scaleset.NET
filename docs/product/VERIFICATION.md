# Verification Evidence

Updated 2026-11-23 (Australia/Sydney).

The reference baseline is `actions/scaleset` `716908e507993947ada4273aecf2361c02e5836c`.

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

Local .NET 10 Release validation on macOS arm64 completed with 0 warnings
and 0 errors (`dotnet build ScaleSet.slnx --configuration Release --no-restore -m:1 -nr:false`),
and `dotnet test --solution ScaleSet.slnx --configuration Release --no-build`
passed 427/427 non-integration tests (`196` Client, `20` Listener, `164`
Hosting including loopback OTLP telemetry, and `47` Docker unit tests) with 0
failures. The two opt-in `ScaleSet.Docker.IntegrationTests` were skipped. They
require a Linux Docker daemon and `SCALESET_DOCKER_INTEGRATION=1`.

The [release candidate run on main](https://github.com/knightfall/scaleset.NET/actions/runs/44953792103)
on Linux passed 424 tests with 0 failures and 0 skips, including the two Docker
integration tests. It built with 0 warnings and 0 errors, packed the four
`Knightfall.ScaleSet.*` packages and their symbols, validated the SPDX SBOM
and manifest through the artifact round trip, and ran a fresh .NET 10 consumer
against the candidate. The [workflow security run](https://github.com/knightfall/scaleset.NET/actions/runs/44953792165)
passed 19 release automation tests, actionlint, and zizmor.

These results cover deterministic GitHub protocol tests (including GHEC
`github.com/enterprises/...` and `*.ghe.com` + PAT targeting, HTTP/2
`SocketsHttpHandler` configuration, strict JSON/null validation, rate-limit
classification, retry budgets/`OnRateLimit`, and response snapshots) and the
Docker provider/runtime suites. A subsequent Linux arm64 build with Roslynator
enabled passed all 429 tests, including both Docker integration tests.

The package-based worker also built in isolation using the pinned repository
analyzer and `.editorconfig`. A temporary namespace diagnostic (`RCS1110`)
failed the build, confirming that the release candidate treats Roslynator
warnings as errors.

## Alpha quickstart canaries

- The Compose quickstart built the controller, validated configuration, reached
  readiness, and ran the supplied workflow in a private GitHub.com repository.
  The job ran on an ephemeral Linux ARM64 runner, and the controller recorded
  it as gone after cleanup. Docker and GitHub both reported zero remaining
  runners. The bundled collector received logs, metrics, and traces.
- The .NET worker restored all four candidate packages from a local feed into a
  fresh cache. The worker authenticated, resolved the existing scale set,
  opened its own session, and completed the same smoke workflow. It removed
  the runner and closed its session on `SIGTERM`.

The canaries exercised repository scope with the authenticated GitHub CLI
account credential. Live organization, enterprise, and GitHub App runs, AWS
deployments, registry publication, and post-publish registry verification remain
pending.

## Docker restart and drain

The repository-scope Compose canary used source commit
`dc38cda7156886864f176986118705b1535a11dc` and local Linux ARM64 image
`sha256:ed572614e5a3e0d3b62f1187d3af6538408fb41d674261ceb937e1be0f5b6124`.

- The [restart job](https://github.com/knightfall/scaleset-dotnet-alpha-canary/actions/runs/45115829296)
  was in progress when the controller stopped and restarted with the same
  persistent state. The same runner container stayed alive and completed the job.
- The [drain job](https://github.com/knightfall/scaleset-dotnet-alpha-canary/actions/runs/45115949941)
  was in progress when the controller began shutdown. It completed successfully,
  and the controller finished draining within the configured 65-second budget.
- Both runner records reached `Gone`. Docker and GitHub reported zero surviving
  canary runners. A separate restart with the same candidate also recovered the
  exited runner left by an earlier drain attempt.

## Current support and limits

- Deterministic tests cover GitHub.com and GitHub Enterprise Cloud (`*.ghe.com`)
  targets, with GitHub App and PAT authentication for repository and
  organization scopes and PAT authentication for enterprise scope. Custom
  GHES hosts and custom root CA / mTLS
  transport helpers have not been implemented or exercised.
- Docker is the implemented runner provider. ECS task and direct EC2 providers,
  AWS role-based resource management, and Lambda/ECS controller deployment
  remain planned work.
- The source-reference guides describe the Go project; they are not a map of
  the .NET implementation. Product implementation evidence is in
  [`docs/implementation`](../implementation/CLIENT.md).

Re-run the build, tests, and matching canary when changing a supported path.
