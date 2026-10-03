# Go Reference: External Integrations

Observed source baseline: `actions/scaleset` at
`e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5`.

## 1) Integration Inventory

| System | Purpose | Authentication | Evidence |
|---|---|---|---|
| GitHub REST | Installation and runner-registration tokens | App JWT/installation token or PAT | `client.go`, `jwt_provider.go` |
| Actions administration API | Groups, scale sets, JIT, runner/session management | Discovered Actions admin JWT | `client.go` |
| Session message queue | Poll and acknowledge | Session bearer token | `session_client.go` |
| Actions acquisition endpoint | Acquire available request IDs | Session bearer token on an admin-base URL | `session_client.go` |
| Docker Engine/registry | Example image pull and runner containers | Docker environment/client settings | Example `main.go`, `scaler.go` |
| GitHub workflow API | Live test dispatch/status | Separate workflow credential | Example `e2e_test.go` |

## 2) Data Stores

No application database is configured. Admin/session tokens live in memory.
The Docker example's runner name/container mappings live in mutex-protected
idle/busy maps. Restarts lose those maps. Queue delivery state is external
to the application and requires explicit acknowledgment.

## 3) Secrets and Credentials Handling

Constructors accept PATs, App credentials, or an external JWT provider.
The example exposes credential CLI flags and puts the encoded JIT config in
the runner container environment. App JWTs use RS256 with iat one minute in
the past and exp nine minutes after iat. Admin refresh starts within 60 seconds
of expiry. The testdata keys are test fixtures; do not copy them as operational
credentials. GitHub's current authentication guidance documents the enterprise
scope restriction for App authentication; see the [GitHub protocol contract](../../product/GITHUB-PROTOCOL.md).

## 4) Reliability and Failure Behavior

Default transport: four retries, maximum wait 30 seconds, HTTP timeout five
minutes. Registration exchange specially retries 401/403. Session poll/ack/
acquire refresh queue credentials on 401 and attempt the operation once more.
Concurrent refreshes use state rechecks. Poll 202 is an empty result, not an
error. Handler or acknowledgment failure terminates the listener. No explicit
circuit breaker is configured in this source.

## 5) Observability for Integrations

HTTP/authentication operations use structured logs. Errors retain correlation
IDs, and the user agent carries system/build/scale-set metadata. `DebugInfo`
reports proxy/root-CA presence and system metadata. No dedicated core-client
ActivitySource, metrics API, or telemetry exporter is implemented. Indirect
OpenTelemetry dependencies do not establish core instrumentation coverage.

## 6) Evidence

- [Client](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/client.go)
- [Session transport](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/session_client.go)
- [Shared HTTP](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/common_client.go)
- [Example](https://github.com/actions/scaleset/blob/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5/examples/dockerscaleset/scaler.go)
- [GitHub auth guidance](https://docs.github.com/en/actions/how-tos/manage-runners/use-actions-runner-controller/authenticate-to-the-api)
