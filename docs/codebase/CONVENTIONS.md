# Coding Conventions

Observed upstream conventions at commit
`e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5`. C# recommendations are separate
in [the migration plan](../migration/PLAN.md).

## 1) Naming Rules

| Item | Rule/example | Evidence |
|---|---|---|
| Files | snake_case, `common_client.go` | Source tree |
| Exports | PascalCase, `GenerateJitRunnerConfig` | `client.go` |
| Private members | lower camelCase, `refreshMu` | `client.go`, `session_client.go` |
| Interfaces | Role names, `JWTProvider`, `Scaler` | `jwt_provider.go`, listener |
| Environment | Upper snake case, `GITHUB_ACTIONS_FORCE_GHES` | `config.go` |
| JSON | Explicit tags, including exceptional `RunnerSetting` | `types.go` |

## 2) Formatting and Linting

CI runs `go fmt ./...` and rejects its diff. Lint configuration version 2
sets a five-minute timeout, an errcheck exclusion for ResponseWriter.Write,
and a standard-error-handling preset. Do not infer a broad explicit rule set
from this small configuration. Mockery formats generated mocks with goimports.
Local `script/lint` also invokes deadcode; CI's lint step directly runs
golangci-lint.

## 3) Import and Module Conventions

Representative source groups standard-library imports separately from external
module imports. Docker clients use aliases when their short names would clash.
Uppercase Go symbols determine export visibility. The root and listener are
public packages; `internal/testserver` is internal test infrastructure.

## 4) Error and Logging Conventions

Methods accept `context.Context`, return values plus `error`, and add context
through `%w`. Status sentinels coexist with runner-domain sentinels, allowing
`errors.Is` matching. Error details include method, URL, HTTP status, ActivityId,
and X-GitHub-Request-Id. Errors may include response bodies. `slog` handles
structured logging and defaults to a discard logger. Source advises treating
JIT configs as secrets; no universal response-body redaction layer is visible.

## 5) Testing Conventions

Colocated `*_test.go`, `Test*` and named `t.Run` cases, Testify assert/require,
`httptest` API fixtures, and generated Testify listener mocks. Several tests
use `t.Parallel`. CI runs the race detector. [TODO] No numeric coverage
threshold or measured coverage was found in the inspected CI/configuration.

## 6) Evidence

- [Errors](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/errors.go)
- [Types](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/types.go)
- [Lint config](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/.golangci.yaml)
- [Mock generator](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/.mockery.yml)
- [Listener tests](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/listener/listener_test.go)
