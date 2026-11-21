# Go Reference: Source Structure

Source: `actions/scaleset` at `716908e507993947ada4273aecf2361c02e5836c`.
Paths below refer to that repository, not proposed C# directories.

## 1) Top-Level Map

| Path | Responsibility |
|---|---|
| `client.go` | Authentication exchange, scale-set/group/runner operations, JIT, message decoding |
| `common_client.go` | Per-request retry loop, `MaxElapsed`/deadline wait skipping, response buffering, `WithRequestRetry`, user-agent metadata |
| `http_client.go` | `HTTPClient` interface, `NewHTTPClient`, `DefaultTransport` (HTTP/2 pings), `RetryConfig`, `RateLimitRetries`, `DefaultBackoff` |
| `session_client.go` | Session creation/refresh/closure; poll (`MessagePollTimeout`), acknowledge, acquire jobs |
| `config.go` | Registration URL, scope (`enterprises`/`orgs`/`repos`), hosted/`*.ghe.com`/GHES endpoint selection |
| `jwt_provider.go` | PEM and external-signer App JWT providers |
| `types.go`, `errors.go` | Wire DTOs, parsed messages, `RequestResponseError`, `RateLimitError`, `MaxElapsedExceededError`, URL redaction |
| `listener/` | Serial poll/handler/ack loop and generated test mocks |
| `examples/dockerscaleset/` | Executable Docker demonstration and live workflow test |
| `internal/testserver/` | In-process authentication/API test server |
| `testdata/` | TLS test certificates, keys, regeneration script |
| `.github/`, `script/` | CI, dependency updates, ownership, test/lint commands |
| `.devcontainer/` | Development image and editor configuration |

## 2) Entry Points

The core package has constructor entry points rather than a `main` executable.
`examples/dockerscaleset/main.go` is the sole application entry point and
dispatches a Cobra command to `run`. `listener.New` and `Listener.Run` require
an already-created message session client.

## 3) Module Boundaries

| Boundary | Current source owns | Outside this boundary |
|---|---|---|
| Root package | GitHub/Actions HTTP and wire protocol | Provisioning policy |
| Listener | Initial statistics, polling, serial callbacks, acknowledgment | Job acquisition policy, runner lifecycle |
| Docker example | Acquire jobs, interpret events, provision/remove containers | Reusable HA/state management |
| Internal test server | Local HTTP fixtures | Live runner provisioning |

The listener's comment claims session creation/deletion management; its actual
interface and `Run` use a session created and closed by the caller.

## 4) Naming and Organization Rules

Go files use snake_case (`session_client.go`); exported symbols use PascalCase;
package directories use lowercase. Tests are colocated `*_test.go`. Imports
use module paths, with explicit aliases for Docker clients. There is one Go
module, including the example.

## 5) Evidence

- [Saved tree and metrics](source-scan.txt)
- [Application entry](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/examples/dockerscaleset/main.go)
- [Listener](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/listener/listener.go)
- [HTTP client and retries](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/http_client.go)
- [Session creation](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/client.go)
