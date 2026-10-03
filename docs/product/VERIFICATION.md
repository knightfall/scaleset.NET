# Verification Evidence

Updated: 2026-10-03 (Australia/Sydney).

## Reference protocol source

- Repository: [`actions/scaleset`](https://github.com/actions/scaleset).
- Source commit used for protocol and architecture review:
  `e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5`.
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

The latest recorded .NET 10 Release validation for the Docker product passed
the full native test suite with 335/335 tests, including the opt-in Docker
integration cases. Those integration cases used a local Linux arm64 Docker
daemon and synthetic JIT data. The Release build completed with zero warnings
and errors. The recorded hosted workflow passed on Linux, macOS, and Windows,
including the multi-platform container build.

These results cover deterministic GitHub protocol tests and the Docker
provider/runtime. They do not verify a live GitHub runner workflow. No live
GitHub canary, AWS resource operation, AWS deployment, NuGet publication, or
container publication has been performed.

## Current support and limits

- GitHub.com repository and organization targets are covered by deterministic
  tests. GHES and enterprise targets have not been implemented or exercised.
- Docker is the implemented runner provider. ECS task and direct EC2 providers,
  AWS role-based resource management, and Lambda/ECS controller deployment
  remain planned work.
- The source-reference guides describe the Go project; they are not a map of
  the .NET implementation. Product implementation evidence is in
  [`docs/implementation`](../implementation/CLIENT.md).

Re-run the appropriate build and test gates after code changes. A designated
GitHub.com canary and AWS integration environment are required before claiming
live GitHub or AWS compatibility.
