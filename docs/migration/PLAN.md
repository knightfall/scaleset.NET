# Go to C# Migration Plan

Status: **draft; do not begin implementation until planning decisions are resolved**.
Date: 2026-10-02. Branch: `codex/go-to-csharp-migration-plan`.
Source baseline: [actions/scaleset at e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5](https://github.com/actions/scaleset/tree/e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5).
Target starting commit: `ab0c4cf` (README, Apache-2.0 license, gitignore;
no source, solution, CI, or test projects).

## 1. Objective and scope

Build a .NET 10 autoscaler with reusable NuGet client/listener libraries,
a configurable executable service, and deployment artifacts. Preserve GitHub
API/protocol contracts with idiomatic C#. Docker is phase one. Separate ECS
task and direct EC2 instance providers are phase two. The first supported
GitHub matrix is GitHub.com repository/organization scopes with App or PAT
authentication. GHES, enterprise scope, concrete remote JWT signers, a web
dashboard, automatic failover, and cross-account AWS control are not presumed
phase-one requirements.

Confirmed and proposed choices are tracked in [DECISIONS.md](DECISIONS.md).
[ASK USER] D12-D15, including review of engineering defaults, must be resolved before
marking this plan final.

Success means protocol tests pass, Docker lifecycle/recovery/drain scenarios
pass, a real workflow runs through the autoscaler in a dedicated test scope,
and documented deployments survive a controller restart without duplicating
or force-stopping active runners. No startup-latency or concurrency guarantee
is claimed before measurement.

## 2. Source findings that shape the migration

Upstream has approximately 3,029 lines across the eight core/listener and
three Docker-example production files inspected (excluding tests). It is
small enough to migrate by behavioral slices rather than automatic translation.
The source analysis is in [seven codebase guides](../codebase/ARCHITECTURE.md).

- The root client owns auth exchange, API calls, message parsing, and system
  metadata; its largest source file is 1,184 lines.
- MessageSessionClient owns queue credentials and refresh. Listener receives
  an existing session and delegates job acquisition and provisioning.
- The encoded message body, separate auth domains, capacity header, and
  callback-before-ack sequencing are easy to translate incorrectly.
- The Docker example is expressly a demonstration: in-memory state, sequential
  provisioning, panic-on-unknown events, and destructive exit cleanup.
- The listener public interface changed in September 2026. Use the pinned
  clone, not older ARC listener assumptions or mutable README examples.
- The source's local race-enabled baseline has one failing macOS TLS assertion;
  details and unverified environments are in [VALIDATION.md](VALIDATION.md).

## 3. Proposed solution layout and dependency rules

Names below are proposed until the final review; none is an existing project.

```text
scaleset.NET/
  ScaleSet.slnx
  global.json
  Directory.Build.props
  Directory.Packages.props
  .editorconfig
  src/
    ScaleSet.Client/              # HTTP, auth, wire models, sessions, errors
    ScaleSet.Listener/            # Initial/poll/callback/ack contract
    ScaleSet.Hosting/             # Policy, reconciliation, ownership, lifecycle
    ScaleSet.Providers.Docker/    # Docker Engine adapter
    ScaleSet.Autoscaler/          # Configuration, CLI, service, telemetry wiring
  tests/
    ScaleSet.Client.Tests/
    ScaleSet.Listener.Tests/
    ScaleSet.Hosting.Tests/
    ScaleSet.Docker.IntegrationTests/
    ScaleSet.EndToEnd.Tests/
    fixtures/protocol/
  deploy/docker/                 # Dockerfile, Compose, example config, runbook
  docs/migration/                # This plan, decisions, parity, validation
  docs/codebase/                 # Pinned upstream evidence
  # Phase two:
  src/ScaleSet.Providers.Ecs/
  src/ScaleSet.Providers.Ec2/
  infra/                        # Format depends on D14
```

Client has no Docker/AWS/hosting dependency. Listener references Client only
plus minimal logging abstractions. Hosting uses Client and Listener and
defines the provider contract; adapters reference that contract. Executable
composes concrete providers and telemetry exporters. Avoid cycles and avoid
putting AWS SDKs into reusable protocol packages.

Use nullable references, explicit JSON wire names, async Task APIs with
CancellationToken, immutable session snapshots, validated options, and
TimeProvider for clocks/delays. Do not use Task.Run to wrap synchronous HTTP.
Client factories validate local config without network access; opening a
message session is explicitly asynchronous.

## 4. Component migration map

| Go source | C# responsibility | Completion evidence |
|---|---|---|
| `types.go` | Wire models, parsed job messages, JSON context/converters | Golden JSON fixtures, omission/casing/large-ID cases |
| `config.go` | GitHub target/scope parser and endpoint resolver | Repo/org hosted URL matrix; reject deferred targets clearly |
| `jwt_provider.go` | App credential provider and async JWT seam | Signature/claims/key-format/time/cancellation tests |
| `errors.go` | ScaleSetApiException and status/domain classifications | Correlation IDs, invalid/plain/empty error payloads, redaction |
| `common_client.go` | Transport factory, per-request auth, retries, BOM, diagnostics | Timeout/proxy/TLS/disposal tests |
| `client.go` auth | Administration connection cache | Concurrency and near-expiry refresh fixtures |
| `client.go` APIs | ScaleSetClient, group/runner/JIT operations | Every row in protocol inventory |
| `session_client.go` | MessageSessionClient, immutable snapshot, async closure | Poll/ack/acquire/refresh/failure/concurrent refresh fixtures |
| `listener/listener.go` | ScaleSetListener and IScaleSetMessageHandler | Initial -1, empty polls, serial callback, ack/cancel/replay tests |
| Example `scaler.go` | Scaling policy + reconciler + Docker provider | Lifecycle/fault/restart integration scenarios |
| Example `main.go`, `config.go` | Hosted application and validated config | Startup/run/drain/config/exit-code integration scenarios |
| Upstream CI/example | C# CI, images, deployment/runbooks | Build/test/package/image/install smoke evidence |

Exact wire and listener requirements are in [PROTOCOL-PARITY.md](PROTOCOL-PARITY.md).

## 5. Transport, auth, and API design

Proposed public surface:

- `ScaleSetClient`: find/list/get/create/update/delete scale sets, resolve
  runner groups, generate JIT, get/find/remove runners, open message sessions.
- `MessageSessionClient`: get message, acknowledge, acquire jobs, inspect a
  safe session snapshot, and explicit async close with bounded cancellation.
- `ScaleSetListener.RunAsync(handler, cancellationToken)` plus thread-safe
  maximum-capacity update. Callback receives initial, empty, or real message.
- `ScaleSetApiException`: HTTP status, domain code, method, safe URI,
  ActivityId, GitHub request ID, and bounded redacted body preview.

Separate transport policies for GitHub REST, administration, queue long polls,
and short control/cleanup operations. Use reusable HttpClient/handlers with
configured connection lifetimes; support caller-supplied transport and host
DI without forcing every library consumer to use Generic Host. Microsoft
documents both [long-lived clients and IHttpClientFactory](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines).

Do not apply the default resilience pipeline blindly to long polls. Define
a poll attempt timeout that exceeds the approximately 50-second upstream
poll window, plus a configurable total budget. Avoid hedging session polls or
mutations. Set Authorization per request so admin/queue credentials cannot
race through shared default headers.

Retry matrix: read-only requests may retry bounded transient failures with
backoff/jitter; registration exchange has its scoped 401/403 retry behavior;
queue 401 performs one coordinated refresh/retry. Mutations require an
explicit operation-specific replay strategy or post-failure reconciliation,
especially scale-set creation and JIT generation. Preserve response/status
contracts while documenting this safer transport-policy departure from Go's
generic retries. Microsoft notes that [standard retries apply to all methods
unless configured](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience).

App/PAT descriptors are mutually exclusive. Load PEM/token files in the
executable, keep token caches per target/credential scope, and publish token
plus tenant URL together under one refresh gate. Queue refresh rechecks both
session ID and credential generation. Do not hold a state lock across network
I/O or share refresh errors forever.

Core DTOs preserve casing/omission; avoid serializing public diagnostic session
snapshots with secret tokens. Preserve raw wire DTOs internally if public
models are narrower. Inject custom roots, proxy configuration, and optional
host-scoped mTLS through transport; never make permissive TLS the default.
[TODO, M2] Validate .NET/OS trust-store differences with integration tests.

## 6. Scaling policy and configuration

Let A = latest TotalAssignedJobs, m = configured minimum, M = maximum:

| Policy | Desired total runners | Meaning |
|---|---|---|
| SpareCapacity | min(M, A + m) | Retain extra capacity above assigned workload; matches Go example intent |
| TotalFloor | min(M, max(A, m)) | Minimum total fleet size |

Validate `0 <= m <= M <= Int32.MaxValue`, use overflow-safe arithmetic, and
support M=0 to pause acquisition/provisioning. A cannot be derived from job
message counts. Cache the last valid statistics for convergence on empty
polls, but do not acquire new work from stale/absent observations.

Proposed configuration shape (conceptual schema, not executable config yet):

```text
Controller: identity, stateDirectory, reconcileInterval, health/telemetry
HostCapacity: maxTotalRunners, cpuBudget, memoryBudget, creationConcurrency
ScaleSets[]:
  key, githubUrl, name, runnerGroup, labels, credentialsRef
  scaling: policy, minRunners, maxRunners, idleTimeout
  lifecycle: startupTimeout, shutdownDrainTimeout, cleanupTimeout
  provider: docker | ecs | ec2
  runnerProfile:
    imageOrTemplate, architecture, cpu, memory, workFolder
    mounts, network, environmentRefs, dockerBuildMode, privileged
Credentials[]: named App/clientID/installationID/keyFile or PAT/tokenFile
```

[ASK USER, D13] Finalize the JSON/environment/CLI mechanism, Compose, Linux
image architectures, multiple-set support, and observability defaults.
Proposed precedence is file < environment < command line; arrays/credential
references need documented override semantics. Validate all sets before
starting any listeners. Redact secrets from config dumps and diagnostics.

One serial loop per configured scale set; shared bounded provisioner and host
budget across loops. Include reservations/Starting/Unknown resources in the
capacity accounting. Advertise feasible allocation through the capacity
header, not an unfulfillable sum of independent maxima. Reserve capacity
before acquiring jobs, accept partial acquisition results, and avoid letting
one set monopolize shared slots. This scheduler must remain deterministic
under competing demand; test aggregate limits and starvation behavior.

## 7. Provider contract and lifecycle state

Define `IRunnerProvider` in the provider-neutral hosting layer with async
discovery, create/start, inspect, and remove operations. Each resource exposes
stable owner/scale-set/operation/runner identity and lifecycle evidence.
Capability metadata describes architecture/resources, Docker-build modes,
and discovery guarantees. Keep cloud-specific placement/task/template fields
inside provider options, not in GitHub wire types.

Model:

```text
Reserved -> JitIssued -> Creating -> Starting -> Idle -> Busy -> Completed
                                \-> Failed                  \-> Removing -> Gone
Discovery with uncertain job activity -> Unknown (protected until resolved)
```

Use idempotent transitions and tombstones for completed/removed resources.
Persist non-secret operation intent before side effects and provider IDs after
discovery/response. A proposed local state journal uses atomic writes under
an exclusive mounted state directory; test crash recovery/corrupt records.
Keep JIT payloads and tokens out of the durable journal. Existing provider
resources are independent discovery evidence if a write/response is lost.

Creation reserves capacity and uses a stable operation ID/runner name. If
Docker create succeeds and start fails, discover/retry that same resource;
do not create another runner blindly. If JIT succeeds but no container is
confirmed, check the stable runner identity and use bounded compensation
before issuing a replacement. Cancellation/timeout means outcome uncertain,
not proof that no resource exists.

Handle duplicate/out-of-order/missing runner events without panic. A duplicate
completion after cleanup succeeds is harmless. Unknown names trigger scoped
discovery, not deletion of unrelated resources. Retain a cleanup record until
provider absence is confirmed; missing resources are idempotent at this layer.

GitHub's inspected RunnerReference has no per-runner busy flag. After restart,
protect Unknown resources; count them toward capacity, and resolve through
job events/provider terminal evidence or a future verified server API. Global
statistics alone cannot prove which resource is idle. A label or running
container state alone does not prove a job is safe to kill.

The reusable listener waits until the handler has completed replay-safe work
before ack. Do not introduce a fire-and-forget worker queue and acknowledge
before work is either completed or durably accepted under a defined recovery
contract. Phase-one design uses bounded parallel work awaited by the handler.
For sustained polling responsiveness, benchmark long creation bursts before
adding any durable asynchronous job queue.

## 8. Docker phase-one product behavior

- Label every resource with product/controller/scale-set/operation identity;
  discover only that ownership scope. Use configurable pinned runner images,
  persistent resource naming, and explicit create/start separation.
- Deploy the controller with a mounted state directory and Docker Engine
  access. Do not give the controller's GitHub App/PAT credentials to runner
  containers; they receive only their JIT configuration.
- Startup: validate -> lock state -> initialize transport -> resolve group ->
  find/create scale set -> verify compatibility -> discover/reconcile owned
  runners -> open session -> process initial statistics -> poll.
- On conflicting session/ownership, fail visibly; do not delete another
  instance's session to take over. Exclusive local state plus one-owner
  deployment is not distributed leader election. [TODO, M3] Verify actual
  service conflict behavior in local fixtures and live canary.
- A healthy runner exits after one job. Reconciliation checks exited/failed
  containers independently of completion events and cleans leaked partial
  resources. Removal is scoped and idempotent.
- Idle scale-down uses known idle state and a configured grace period;
  shrinking max capacity never terminates known busy/unknown runners just to
  force the count down. Configuration can explicitly select Docker builds or
  privileged mode; safe default is neither.
- [ASK USER, D12] Shutdown/drain policy and scale-set retention must be fixed.
  Proposed commands: run, validate-config, status, drain, and explicit delete.
  Delete requires an explicit target and documented active-job behavior.
- Handle SIGTERM as well as interactive cancellation. Separate stopping,
  successful-message acknowledgment, job drain, and cleanup budgets. Do not
  use the cancelled host token for post-success ack/session closure.

## 9. Operations and distribution

[ASK USER, D09/D13] Finalize package identity and operating defaults at review.
Proposed artifacts: Client/Listener/Hosting NuGet packages, provider assembly,
CLI binary, versioned multi-architecture controller image, Compose example,
validated example config, upgrade/drain/recovery runbooks, and package notices.

Use structured logs with scale-set/provider/operation/correlation IDs and no
raw credentials/JIT payloads. Libraries emit ActivitySource/Meter telemetry;
the executable configures optional exporters. Proposed metrics: assigned jobs,
desired/idle/busy/starting/unknown runners, reserved capacity, provision/cleanup
duration/failure, queue polls/acks/replays, and refresh failures. Avoid job IDs
and runner names as unbounded metric dimensions.

Liveness indicates process progress. Readiness indicates valid ownership,
configuration, and completed initial reconciliation; degraded GitHub/provider
connectivity should be visible without causing a restart loop that destroys
recovery context. No management mutation API or dashboard is included unless
requested; health/metrics listeners should be restricted to intended networks.

CI: Linux protocol tests; Docker integration tests; library portability tests
on macOS/Windows where feasible without claiming their runner backends are
supported; release packaging; image scans/signing/provenance appropriate to
the publishing destination; opt-in live canaries isolated from ordinary PRs.
Never execute untrusted PR code with publishing/live-canary credentials.

## 10. Phase-two ECS provider

Provision one ephemeral runner task per requested resource through ECS RunTask,
not an ECS service that automatically replaces completed one-job runners.
Controller deployment may separately use an ECS service. Require configured
cluster, pinned task-definition revision, placement/capacity selection,
subnets/security groups, log configuration, and resource profile.

Use stable operation IDs as RunTask client tokens, preserve the request across
retries, and discover by tags/startedBy plus recorded task ARN. AWS documents
[RunTask idempotency](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ECS_Idempotency.html);
it has a limited retention window, so tags/discovery remain necessary.
Inspect placement failures, task status, and stop reasons. Handle eventual
consistency and never treat an empty first discovery as proof of absence.

The workload's Docker-build/privilege requirement selects compatible ECS
capacity. Fargate cannot use privileged containers or host Docker sockets;
EC2-backed capacity is needed for those modes, or the workload must use a
compatible alternative build engine. This constraint is documented in
[AWS's container-building guidance](https://aws.amazon.com/blogs/containers/building-container-images-on-amazon-ecs-on-aws-fargate/).
[TODO, M6] Pin the tested ECS capacity/support matrix with current task
definition documentation; do not equate controller-container compatibility
with runner-workload compatibility.

JIT is a secret: do not put it in plaintext task definitions/diagnostic output.
Design a short-lived, scoped bootstrap delivery mechanism for a task identity,
or inject a per-runner secret reference. Validate cleanup/TTL and credential
permissions. Long-lived GitHub credentials stay in the controller. Distinguish
controller role, task execution role, and any limited runner task role.

Scale-in drains runners before StopTask; lifecycle failure/expiry has explicit
policy. If ECS uses an ASG capacity provider, distinguish runner-task demand
from underlying container-instance scaling; avoid competing desired-capacity
controllers and validate instance draining against long-running CI jobs.

## 11. Phase-two direct EC2 provider

Use versioned launch templates and a configured AMI/architecture/resource
profile. Create one ephemeral runner instance per resource initially. Use
stable client-token/ownership tags and recorded instance IDs. Discover and
inspect pending/running/terminal states; count instances awaiting bootstrap
as occupied capacity. Validate quotas, placement failure, and eventual
consistency without promising a particular instance family or regional limit.
Keep Region, subnet/AZ, and request parameters fixed for each recorded launch
intent: [EC2's zonal idempotency](https://docs.aws.amazon.com/ec2/latest/devguide/ec2-api-idempotency.html)
can allow the same token to launch resources in different Availability Zones.
Placement fallback therefore needs a distinct reconciled launch intent, not
an assumption that reusing the token across AZs prevents duplicates.

The runner AMI/bootstrap must obtain its short-lived JIT configuration through
a scoped secret/bootstrap channel. Never embed PAT/App private keys or JIT
payloads in EC2 user data. Define credential isolation from CI job processes,
delete bootstrap material after use, and require bounded startup completion.
Use least-privilege instance profiles, IMDSv2, encrypted storage, and SSM-based
diagnostics appropriate to the chosen AMI. [TODO, M7] Validate the bootstrap
design and instance-token idempotency against current EC2 documentation.

Terminate only owned resources with known terminal/drained state, subject to
an explicit configured forced-cleanup policy. On-Demand is the initial cloud
baseline; Spot interruption/requeue behavior is a later capability with its
own acceptance tests. Direct EC2 provider lifecycle is distinct from managing
EC2 container instances underneath ECS.

## 12. Infrastructure scope

[ASK USER, D14] Select the artifact format. Plan reusable modules/stacks for:

- Controller deployment, owned state, configuration and credential references.
- ECS task definitions/cluster selection/capacity/network/logging and roles.
- EC2 launch templates/AMI parameters/network/storage/roles and bootstrap.
- Non-secret outputs, ownership tags, optional existing-resource inputs,
  local validation/synthesis, disposable test environment, and safe teardown.

Do not deploy AWS resources while executing a planning milestone. Regions,
accounts, networks, resource profiles, and scale limits are configuration;
deployment validation must use an explicitly selected environment. Cloud
network/IAM/bootstrap details receive a dedicated design review in M6/M7.

## 13. Milestones and acceptance gates

| Milestone | Deliverables | Dependencies | Gate |
|---|---|---|---|
| M0 Decisions/baseline | Final plan, pinned source, parity matrix, attribution inventory | User decisions | No unresolved product decision; source evidence verified |
| M1 Solution/contracts | .NET 10 layout, shared conventions, test projects, public API skeleton, config schema | M0 | Build succeeds; dependency graph respects boundaries; public API review |
| M2 Wire/transport/auth | DTOs, exceptions, target resolver, auth cache, HTTP policies, all admin/session APIs | M1 | Every parity row traced to fixtures; refresh concurrency/TLS/cancellation tests pass |
| M3 Listener | Initial/empty/real callbacks, dynamic capacity, acquire interface, ack sequencing | M2 | No ack before success; cancellation-safe bounded ack; replay/refresh tests pass |
| M4 Docker lifecycle | Provider, policies, journal, discovery, idempotent cleanup, aggregate budgets | M3 | Restart/partial-create/duplicate-event/container-exit/drain scenarios pass |
| M5 Docker release | CLI/service, config/docs/telemetry, Compose, NuGet/image CI | M4 | Install/config smoke, real GitHub.com repo+org canaries, retained-identity restart, operator runbook |
| M6 ECS provider | Task lifecycle/bootstrap, infrastructure, provider contract tests | M5 | Dedicated AWS/GitHub canary, idempotent retry/recovery, workload compatibility, drain/cleanup |
| M7 EC2 provider | Instance/AMI/bootstrap lifecycle, infrastructure, contract tests | M5 and bootstrap design | Dedicated canary, lost-response/restart/bootstrap-failure/termination recovery |
| M8 Release/maintenance | Supported environment matrix, upgrade checklist, published artifacts | Relevant release gates | Documented evidence for each advertised target/provider; recoverable upgrade |

M6 and M7 may develop in parallel after common bootstrap/state contracts are
agreed. Each milestone produces a small reviewable change with evidence and
updated docs. No time estimate is committed before workload measurements and
provider/infrastructure dependencies are known.

## 14. Test strategy

Protocol fixtures preserve requests, query encoding, headers, success statuses,
optional/null/default wire fields, BOMs, inner encoded JSON, domain/status
errors, and correlation IDs. Use synthetic credentials in fixtures; any
copied upstream fixture keeps its attribution.

Use deterministic clocks/fake HTTP/providers for expiry, backoff, idle timers,
partial acquisition, fairness, and lifecycle transitions. Include simultaneous
admin/queue refresh, stale snapshot, cancelled refresh waiter, failed refresh,
and response disposal. OS trust rejection assertions must not depend on
English platform error suffixes.

Docker integration exercises lost responses, crash before/after each durable
write and provider side effect, duplicate/out-of-order events, restart with
busy/unknown runners, failed starts/removals, orphan cleanup, capacity=0,
min/max boundaries, both scaling policies, and multiple competing sets.
Test bursts at configurable sizes; publish observed limits and avoid long
sleep-dependent unit tests.

Live tests require a dedicated GitHub.com repository/organization and App/PAT
support evidence. They submit workflows, check ephemeral completion, restart
the controller during a job, drain, and verify owned-resource cleanup. AWS
canaries additionally need isolated account/region/network configuration and
failure cleanup. Local PR tests do not silently acquire live credentials.

No C# tests are executed in this planning branch because no implementation
exists. Select and document the repository-compatible .NET test runner in M1.

## 15. Implementation delegation policy

User instruction: use GPT-6 Luna subagents at max reasoning to execute the plan.
For implementation delegation use `model=gpt-6-luna`,
`reasoning_effort=max`, and a context fork compatible with a model override
(`none` or a bounded number of turns). Provide each agent the relevant plan,
contracts, instructions, baseline, file ownership, and acceptance criteria.
Available session capacity is four agents including the parent, so run at
most three child work packages concurrently unless capacity changes.

Suggested work packages after M1:

1. Wire/URL/error contracts and protocol fixtures.
2. Transport/authentication/cache with a stable shared interface.
3. Session/listener after the client contracts are agreed.
4. Docker provider versus hosting/configuration after lifecycle contracts.
5. ECS and EC2 adapters after common cloud bootstrap contracts.

Do not let agents concurrently edit the same contracts, project wiring, or
shared fixtures. Parent owns integration, source-parity review, meaningful
verification, decision conflicts, and milestone completion. An agent's report
alone does not establish tests passed. Planning does not start implementation.

## 16. Upgrade and release policy

Maintain the upstream baseline SHA and a method/wire/behavior change ledger.
For an upstream update, inspect its diff, refresh fixtures, document deliberate
differences, and run parity plus relevant lifecycle tests. Never replace the
baseline silently with main. Keep supported targets/providers explicit and
separate experimental/deferred capabilities.

Preserve the existing Apache-2.0 repository license. Add upstream GitHub MIT
copyright/license notices for translated source/fixtures and include them in
published packages/images. Check public package IDs and publishing ownership
before release. Actual publication/deployment is a separate execution step;
this branch creates no NuGet release, image push, AWS deployment, or PR.
