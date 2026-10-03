# Go Reference: Testing Patterns

Source baseline: `actions/scaleset` at
`e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5`.

## 1) Test Stack and Commands

Go standard `testing`, Testify 1.11.1, local `httptest` servers, and generated
Testify listener mocks. CI runs `go test ./... -race`; `script/test` forwards
arguments to `go test ./...`. `go test ./... -cover` is an available Go
measurement command, but coverage was not measured during this analysis.
Live E2E requires `E2E=true`, Docker, and its documented E2E environment.

## 2) Test Layout

Tests live alongside client, transport, configuration, errors, JWT, and
listener source. `internal/testserver/server.go` handles the authentication
chain for local fixtures. TLS fixtures are in `testdata`. Docker E2E lives
under the example. `client_benchmark_test.go` defines parallel metadata,
client-operation, and session-operation benchmarks; no benchmark was run.

## 3) Test Scope Matrix

| Scope | Source evidence | Analysis execution |
|---|---|---|
| URL/JSON/auth/error unit and local API tests | Root `*_test.go` | Executed with race detector; root package failed one TLS subtest |
| Session concurrency/refresh | `session_client_test.go` | Included in root test run |
| Listener callback/ack contracts | `listener/listener_test.go` | Package passed |
| Real Docker/GitHub workflow | `examples/dockerscaleset/e2e_test.go` | Disabled; example package passed with E2E skipped |
| Benchmarks | `client_benchmark_test.go` | Not executed |

## 4) Mocking and Isolation Strategy

Local servers return controlled HTTP payloads and auth tokens. Tests close
servers using cleanup hooks. Listener mocks assert callback/ack order and
absence of acknowledgment after handler failure. Concurrent queue-token
refresh tests coordinate eight goroutines and assert one refresh request.
TLS tests exercise custom roots and verification settings.

## 5) Coverage and Quality Signals

[TODO] Measured line/branch coverage and a numeric CI threshold are unknown.
The 2026-10-02 baseline failed
`TestServerWithSelfSignedCertificates/client_without_ca_certs` at
`client_test.go:1468`: the macOS error-string suffix assertion was false.
The preceding non-nil-error assertion passed. Actual returned error text was
not printed, so the exact cause of the wording difference is unverified.
There was no race-detector report in the captured output.

The scaleset.NET test strategy distinguishes local protocol tests, Docker recovery
tests, and credential-dependent live GitHub/GHES tests. Passing local tests
alone cannot establish supported live environments.

## 6) Evidence

- [Executed baseline](../../product/VERIFICATION.md)
- [Session tests](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/session_client_test.go)
- [Listener tests](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/listener/listener_test.go)
- [TLS assertion](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/client_test.go#L1468)
- [E2E gate](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/examples/dockerscaleset/e2e_test.go#L22)
