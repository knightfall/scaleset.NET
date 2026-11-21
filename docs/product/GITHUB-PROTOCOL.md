# GitHub API and Message Protocol Contract

Baseline: [actions/scaleset, 716908e507993947ada4273aecf2361c02e5836c](https://github.com/actions/scaleset/tree/716908e507993947ada4273aecf2361c02e5836c).
The .NET client and listener implement the listed GitHub API and message
contracts for the currently supported GitHub.com and GitHub Enterprise Cloud
targets. Deterministic wire/fake-handler tests cover these behaviors; they do
not establish live GitHub workflow compatibility. See the
[client verification evidence](../implementation/CLIENT.md).

## URL and credential domains

Keep three distinct origins/credential purposes: GitHub REST, the discovered
Actions administration service, and the returned message-queue URL. Never
assume the queue URL is the GitHub API host. Configure credentials per request
rather than shared mutable default Authorization headers.

| Contract | Source | Required C# behavior |
|---|---|---|
| Registration scope | `config.go`, `createRegistrationTokenPath` | Repo/org/enterprise URL parser and `/repos/{org}/{repo}`, `/orgs/{org}`, or `/enterprises/{enterprise}` paths (GitHub App and PAT auth) |
| Hosted API host | `config.go` | `github.com`/`www.github.com` map to `api.github.com`; valid GHEC `*.ghe.com` hosts map to `api.<host>`; validate supported origins |
| Other hosts/custom TLS | `config.go` | Custom GHES hosts and custom root CA/mTLS transport factories are deferred; reject unsupported hosts clearly rather than advertising unvalidated GHES compatibility |
| App token | `jwt_provider.go`, `fetchAccessToken` | RS256; issuer ClientID; iat now-1 minute; exp iat+9 minutes; installation-token POST returns 201 |
| PAT | `getRunnerRegistrationToken` | Bearer PAT; mutually exclusive with App auth |
| Registration token | `getRunnerRegistrationToken` | POST scoped runners/registration-token; expect 201 |
| Admin discovery | `getActionsServiceAdminConnection` | POST `/actions/runner-registration`; body `url`, `runner_event=register`; `RemoteAuth` registration token |
| Admin credential refresh | `updateTokenIfNeeded` | Refresh when missing/within 60 seconds of expiry; coalesce concurrent refresh; publish token plus discovered URL together |
| API version | `actionsServiceAdminToken.requestURL` | Actions administration requests default query `api-version=6.0-preview`; merge and encode existing query correctly |
| Metadata | `common_client.go`, `SystemInfo` | System/subsystem/version/commit/scale-set metadata; valid user-agent and secret-free debug info |

Keep a replaceable async JWT-provider seam. Concrete remote signers and GHES
support are outside the current GitHub target scope. JWT expiry parsing is scheduling
metadata, not a substitute for server identity/token verification.

## Administration API inventory

Paths below are relative to the discovered Actions service URL. JSON request
content type is application/json and Authorization is the admin bearer token.

| Reference operation | scaleset.NET operation | Verb/path and success/result contract |
|---|---|---|
| GetRunnerScaleSet | `FindScaleSetAsync` | GET `_apis/runtime/runnerscalesets?runnerGroupId=...&name=...`; 200; zero returns null, one returns object, multiple/null-JSON fail |
| ListRunnerScaleSets | `ListScaleSetsAsync` | GET same collection with runnerGroupId only; decode `value` array; reject `null`/invalid JSON |
| GetRunnerScaleSetByID | `GetScaleSetAsync` | GET collection/{id}; deserialize object/null as received |
| GetRunnerGroupByName | `GetRunnerGroupByNameAsync` | GET `_apis/runtime/runnergroups/?groupName=...`; zero/multiple groups or `null`/invalid JSON fail |
| CreateRunnerScaleSet | `CreateScaleSetAsync` | POST scale-set collection; default absent labels to scale-set name; label type defaults to System; reject `null`/invalid JSON |
| UpdateRunnerScaleSet | `UpdateScaleSetAsync` | PATCH collection/{id}; default supplied label types, without create-only name-label fallback; reject `null`/invalid JSON |
| DeleteRunnerScaleSet | `DeleteScaleSetAsync` | DELETE collection/{id}; 204 |
| GenerateJitRunnerConfig | `GenerateJitConfigAsync` | POST collection/{id}/generatejitconfig; name/workFolder body; runner plus `encodedJITConfig`; reject `null`/invalid JSON |
| GetRunner | `GetRunnerAsync` | GET `_apis/distributedtask/pools/0/agents/{id}`; 200 (preserves JSON `null`) |
| GetRunnerByName | `FindRunnerByNameAsync` | GET agents?agentName=...; zero=null; multiple or `null`/invalid JSON fail |
| RemoveRunner | `RemoveRunnerAsync` | DELETE agents/{id}; preserve JobStillRunning/NotFound classifications |
| MessageSessionClient | `CreateMessageSessionAsync` | POST scale-set/{id}/sessions; ownerName body; initialized session object |

Application-level get-or-create and destructive cleanup policies sit above
these client methods. Client operations should not silently turn arbitrary
conflicts/not-found responses into success.

## Session and queue contracts

| Contract | Source | Required behavior |
|---|---|---|
| Session identity | `RunnerScaleSetSession` | GUID, ownerName, queue URL/token, optional scale-set/statistics |
| Poll | `getMessage` | GET returned queue URL; `Accept: application/json; api-version=6.0-preview`; queue bearer token; `X-ScaleSetMaxCapacity` |
| Cursor | `getMessage` | Add `lastMessageId` only when >0, preserving other queue query parameters |
| Empty poll | `getMessage` | HTTP 202 -> nullable empty result; callback still runs |
| Payload poll | `getMessage` | HTTP 200 -> decode outer envelope and inner JSON string |
| Acknowledge | `deleteMessage` | DELETE queue URL/{messageId}; queue bearer token; 204 |
| Acquire | `acquireJobs` | POST scale-set/{id}/acquirejobs; JSON int64 request-ID array; admin-service origin but queue bearer token; 200 `value` int64 array |
| Refresh | `refreshMessageSession` | PATCH scale-set/{id}/sessions/{guid}; admin bearer token; 200 replacement snapshot |
| Queue 401 | GetMessage/DeleteMessage/AcquireJobs | Refresh once and retry once; compare session ID/token after locking to avoid duplicate concurrent refresh |
| Close | `Close`, `deleteMessageSession` | DELETE sessions/{guid}; admin bearer token; 204; host owns bounded cleanup |

## Serialization and errors

- Preserve explicit wire names: `RunnerSetting` has capital R/S, while
  `encodedJITConfig`, `messageQueueAccessToken`, `runnerRequestId`,
  `workflowRunId`, `runnerScaleSetId`, and statistics use their source spelling.
- Encode omission rules deliberately; C# CLR defaults must not accidentally
  add fields that Go `omitempty` excludes. Preserve explicit false/default
  behavior where wire fixtures demand it; do not mutate caller-owned models.
- The envelope's `body` is a JSON-encoded string containing an array, not a
  directly nested JSON array. Supported outer type: RunnerScaleSetJobMessages.
- Decode JobAvailable, JobAssigned, JobStarted, and JobCompleted, including
  base job fields/timestamps. Ignore unknown inner message types as Go does;
  unsupported outer types and malformed known payloads fail.
- IDs that may exceed 32 bits use `long`; GUIDs use `Guid`; instants use
  `DateTimeOffset`. Max capacity remains bounded to 0..Int32.MaxValue.
- Strip a UTF-8 BOM before JSON parsing; dispose response/request/content on
  both success and failure. Retain ActivityId and X-GitHub-Request-Id.
- Preserve status classification (400/401/404/409) and domain classification
  (AgentExists, AgentNotFound, JobStillRunning, queue-token expiry) together.
  Use a typed exception with status/domain properties; redact and bound
  response previews before logging.

## Listener invariants

1. Requires a valid initialized session GUID and statistics.
2. Calls the scaler once with initial synthetic message ID -1 before polling;
   this synthetic message is never acknowledged.
3. Calls the scaler on every successful poll, including empty results.
4. Executes callbacks serially; capacity can change safely between polls.
5. Acquisition belongs to the scaler/application. JobAvailable must be
   submitted for acquisition to become assigned work.
6. Uses statistics for desired capacity. Message batches are truncated and
   are not an authoritative backlog count.
7. Acknowledges only after callback success. Failure leaves the message
   unacknowledged and ends that listener run. Replay must be tolerated.
8. Advances the in-process cursor after handler success; a failed ack causes
   the run to stop. Do not persist a cursor across a new session without a
   service-supported recovery contract.
9. Acknowledgment survives host cancellation after successful processing.
   C# uses a separate bounded token, not an already-cancelled stopping token.
   Timeout still leaves a replayable message; never claim exactly-once work.
10. Host owns session creation/closure and restart strategy. A hosted wrapper
    may recreate a listener after transient failure without altering the
    fail-fast reusable listener contract.

## Deliberate product departures from the Docker example

Stable scale-set identity; owned-resource discovery; replay-safe state;
bounded parallel provisioning; two named scaling policies; explicit idle
retirement; safe drain; configurable resources; packaged deployment and
telemetry. These are application changes, not invented changes to GitHub's
HTTP/message semantics. The owned `SocketsHttpHandler` disables auto-redirects,
enables HTTP/2 keep-alive pings (`KeepAlivePingDelay = 30s`,
`KeepAlivePingTimeout = 10s`), and negotiates `HttpVersion.Version20`
(`RequestVersionOrLower`, propagated onto outgoing requests). Default
`ScaleSetClientOptions` retries bounded read-only transient failures and honors
`Retry-After` up to one minute, while opt-in `ScaleSetRetryOptions` (including
`CreateUpstreamDefault()`, `Disabled`, custom `ShouldRetry`/`Backoff`,
`OnRateLimit`/`ScaleSetRateLimitPolicies`, and `ScaleSetRequestRetry` per-call
scopes) adds upstream-compatible retry and rate-limit policies. Primary and
secondary rate limits surface as `ScaleSetRateLimitException`, expired
`MaxElapsed` budgets raise `ScaleSetMaxElapsedExceededException`, expired
`Deadline` budgets raise `OperationCanceledException`, and `ScaleSetApiException.Response`
carries a bounded inspectable `ScaleSetResponseSnapshot`. It does
not automatically retry long-poll `queue` receives; queue-token 401 refresh and
registration-discovery auth retries remain operation-specific.

Confirmed D12 adds drain-and-preserve shutdown: stop acquisition/provisioning,
allow a bounded drain, then exit without stopping busy/unknown runners or
deleting the scale set. Session closure and post-success acknowledgment remain
bounded and independent of host cancellation. Recovery adopts surviving owned
resources; preservation does not guarantee survival of Docker-host shutdown.

## Evidence

- [Client](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/client.go)
- [Session client](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/session_client.go)
- [Wire types](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/types.go)
- [Listener](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/listener/listener.go)
- [HTTP behavior](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/common_client.go)
