# scaleset.NET Product Roadmap

Status: **Docker-backed autoscaler available; AWS controller and runner
providers are planned**. Detailed AWS resource and deployment design remains
open. The protocol reference is
[`actions/scaleset` at `716908e5`](https://github.com/actions/scaleset/tree/716908e507993947ada4273aecf2361c02e5836c).

## 1. Product objective and scope

scaleset.NET is a .NET-native, Kubernetes-free autoscaler for GitHub Actions
self-hosted runners. Teams run the controller in their environment and grant
it access to the infrastructure it is configured to manage. The controller
turns GitHub runner demand into provisioned capacity, tracks runner activity,
and reconciles resources after restarts.

The controller host and runner compute are separate concerns. The AWS
deployment direction is to host the controller in a customer account using
Lambda or ECS and a scoped IAM role. Operators configure which resources the
controller may manage. Runner providers initially target ECS tasks and direct
EC2 instances. The exact Lambda execution model, AWS resource topology, IAM
policies, bootstrap, state, and rollout remain to be designed.
The current implementation provides Docker runner provisioning and a
container/Compose deployment.

The GitHub client, listener, and runner lifecycle remain infrastructure
neutral. Docker and future AWS providers are adapters around those shared
contracts. GitHub App or PAT credentials authorize GitHub API calls; AWS IAM
authorizes operations on configured AWS resources.

GitHub.com and GitHub Enterprise Cloud (`*.ghe.com`) repository, organization,
and enterprise scopes are supported. Repository and organization scopes accept
GitHub App or PAT credentials. Enterprise scope uses a classic PAT.
GHES, concrete remote JWT signers, a web dashboard, automatic failover, and
cross-account AWS control remain outside the current product scope.

Product decisions are tracked in [DECISIONS.md](DECISIONS.md). The product
uses reusable NuGet client/listener libraries, a configurable controller,
deployment artifacts, ILogger/Serilog OTLP logging, mandatory OpenTelemetry
traces and metrics, environment configuration, and a configured OTLP endpoint.
Runtime Parameter Store and Secrets Manager integration is planned for AWS
deployment. The selected infrastructure-as-code direction is CDK v2 in
TypeScript; detailed AWS design remains open.

Success means protocol and provider tests pass, Docker lifecycle/recovery/drain
scenarios pass, a real workflow runs through the controller in a dedicated
test scope, and deployments recover after controller restarts without
duplicating or force-stopping active runners. No startup-latency or concurrency
guarantee is claimed before measurement.

## 2. GitHub protocol reference

The `actions/scaleset` Go project is a reference for GitHub API and message
contracts, not the product boundary for scaleset.NET. Its source has about
3,029 lines across the core client, listener, and Docker example inspected
(excluding tests). Source notes are in the [reference guides](../reference/actions-scaleset/ARCHITECTURE.md).

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
  details and unverified environments are in [VERIFICATION.md](VERIFICATION.md).

### Product boundaries relative to the Go reference

| Area | Pinned Go reference | scaleset.NET product |
|---|---|---|
| GitHub protocol | Client, sessions, listener and caller-owned provisioning | .NET client/listener libraries preserve supported wire/auth/poll/acquire/ack contracts |
| Docker provisioning | Example directly calls Docker Engine APIs | Controller manages Docker runners with recovery, ownership and lifecycle management |
| Deployment | Example installed/run as a Go CLI; development-container Dockerfile | Docker container and Compose deployment available; AWS controller hosting planned |
| Scale sets per process | Example has one registration URL/name and one session | Multiple configured scale sets with shared host-capacity limits; Go library can also be composed for multiple sets by a consumer |
| Configuration/logging | Library constructor options; example CLI flags and slog | Controller environment configuration, ILogger/Serilog OTLP logs, required SDK traces/metrics; AWS runtime configuration is planned |
| Shutdown/state | Example in-memory maps; force-removes runners and deletes scale set | Confirmed drain-and-preserve with retained identity and restart reconciliation |
| Scaling policies | Docker example uses additive minimum/spare capacity | SpareCapacity and TotalFloor selectable by configuration |
| Health/UI | Example has no health host or dashboard | Health endpoints; no dashboard |
| GitHub targets | GitHub.com, GitHub Enterprise Cloud, and GHES with scope-specific credentials | GitHub.com and GitHub Enterprise Cloud repo/org/enterprise scopes. Enterprise uses a classic PAT. GHES is deferred. |

Compose deploys the controller and its dependencies; runner containers are
still created through the provider's Docker API. Multi-architecture packaging
is a deployment deliverable, not a change to the GitHub runner protocol.
The provider lifecycle, recovery, and customer AWS controller are product
capabilities in their own right. GitHub protocol compatibility remains a
tested product requirement. Current target coverage is narrower than the Go
client and must be advertised explicitly. Evidence: the reference example,
client, URL configuration, JWT source, and saved source tree.

## 3. Solution layout and dependency rules

The current solution layout and planned AWS extensions are:

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
    ScaleSet.Autoscaler/          # Environment config, commands, required telemetry
  tests/
    ScaleSet.Client.Tests/
    ScaleSet.Listener.Tests/
    ScaleSet.Hosting.Tests/
    ScaleSet.Docker.IntegrationTests/
    ScaleSet.EndToEnd.Tests/
    fixtures/protocol/
  deploy/docker/                 # Dockerfile, Compose, example env, runbook
  docs/product/                 # Product roadmap, contracts, decisions, verification
  docs/reference/actions-scaleset/ # Go reference source notes
  # Planned AWS integrations:
    src/ScaleSet.Providers.Ecs/
    src/ScaleSet.Providers.Ec2/
    src/ScaleSet.Configuration.Aws/ # Parameter Store/Secrets Manager integration
  infra/                         # AWS CDK v2 TypeScript; design remains open
```

Client has no Docker/AWS/hosting dependency. Listener references Client only
plus minimal logging abstractions. Hosting uses Client and Listener and
defines the provider contract; adapters reference that contract. The controller
composition selects a provider and runs under a host runtime. Planned ECS and
Lambda hosting adapters stay separate from runner-provider packages.
Executable composition owns AWS IAM access, Serilog, and trace/metric exporters.
Libraries use Microsoft.Extensions.Logging abstractions and ActivitySource/Meter;
SDKs, sinks, and AWS configuration providers stay out of reusable protocol
packages. Avoid cycles and avoid putting AWS SDKs into core libraries.

Use nullable references, explicit JSON wire names, async Task APIs with
CancellationToken, immutable session snapshots, validated options, and
TimeProvider for clocks/delays. Do not use Task.Run to wrap synchronous HTTP.
Client factories validate local config without network access; opening a
message session is explicitly asynchronous.

### Roslynator code-quality requirements (D21)

Include `Roslynator.Analyzers` in all handwritten production and test C#
projects from M1. Manage its version centrally and set `PrivateAssets=all`
so published libraries do not impose analyzer dependencies on consumers.
The [current package](https://www.nuget.org/packages/Roslynator.Analyzers) is
5.0.0 as inspected on 2026-10-02; verify compiler compatibility with the pinned
.NET 10 SDK in M1 before selecting the exact dependency version.

Use `.editorconfig` as the shared rule/options/severity authority. Enable
applicable quality rules, supply required options, and document generated-code
exclusions. CI builds treat enabled quality warnings as errors; style choices
are explicitly configured to avoid conflicting policies. Suppressions must
be narrow and explain the public/wire contract or generated-code reason.
Do not mass-suppress diagnostics or automatically fix code in ways that change
API/protocol behavior. Coordinate formatting with built-in .NET analyzers.

NuGet analyzers provide repeatable local/build/CI diagnostics; an IDE extension
is not a CI prerequisite. Ordinary product code does not need the Roslyn
API-specific analyzer package. No global CLI installation is required.
Verify analyzer loading and demonstrate that an enabled diagnostic fails the
quality gate during M1 setup, then remove the temporary probe and record the
enforcement evidence. All delegated implementation work packages must pass
the same quality gate alongside their behavioral checks.
[Roslynator tools](https://github.com/dotnet/roslynator),
[configuration](https://josefpihrt.github.io/docs/roslynator/configuration/).

## 4. Product component map

| GitHub reference source | scaleset.NET responsibility | Completion evidence |
|---|---|---|
| `types.go` | Wire models, parsed job messages, JSON context/converters | Golden JSON fixtures, omission/casing/large-ID cases |
| `config.go` | GitHub target/scope parser and endpoint resolver | GitHub.com and GHEC repo/org/enterprise URL matrix and GHES rejection |
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

Exact GitHub API and listener requirements are in
[GITHUB-PROTOCOL.md](GITHUB-PROTOCOL.md).

## 5. Transport, auth, and API design

Proposed public surface:

- `ScaleSetClient`: find/list/get/create/update/delete scale sets, resolve
  runner groups, generate JIT, get/find/remove runners, open message sessions.
- `MessageSessionClient`: get message, acknowledge, acquire jobs, inspect a
  safe session snapshot, and explicit async close with bounded cancellation.
- `ScaleSetListener.RunAsync(handler, cancellationToken)` plus thread-safe
  maximum-capacity update. Callback receives initial, empty, or real message.
- `ScaleSetApiException` includes HTTP status, domain code, method, safe URI,
  activity and request IDs, and a bounded response snapshot with redacted
  diagnostics.

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

The current retry policy classifies requests before retrying. Read-only
control-plane calls can retry bounded transient failures with jitter. The
default policy honors valid `Retry-After` values on 429/503 up to one minute
and leaves longer waits to the caller. `ScaleSetRetryOptions` provides
rate-limit handling and elapsed-time budgets. `ScaleSetRequestRetry` allows
per-call policy and deadlines. Registration discovery has scoped 401/403
retry behavior, and queue 401 triggers one coordinated refresh and retry.
Mutations use operation-specific replay or post-failure reconciliation.
See the [client implementation](../implementation/CLIENT.md).

App/PAT descriptors are mutually exclusive. Bind credential values/references
from environment configuration in the executable, keep token caches per
target/credential scope, and publish token
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
Credentials[]: named App/clientID/installationID/PEM or PAT; planned AWS secret references
```

Confirmed D18: the controller binds validated environment variables over code
defaults, using a product prefix and double-underscore hierarchy. Remove
implicit JSON/command-line configuration providers from the application's
configuration chain. CLI arguments select commands/explicit action targets.
Keep standard OTEL_* exporter/resource settings and deliberately bind any
unsupported settings rather than assuming automatic SDK/sink support.
Validate all sets and required telemetry configuration before listeners start;
redact secrets from config dumps and diagnostics.

Confirmed D19: AWS deployments use runtime Parameter Store settings and Secrets
Manager credential providers, with caching and credential refresh in a separate
AWS module. Operational settings are a validated startup snapshot and changes
require restart initially. Define source precedence and safe credential-generation
replacement/cache invalidation. Confirmed D13 adds Compose, Linux x64/arm64,
multiple scale sets, health endpoints, and no dashboard.
See [the detailed design](OBSERVABILITY-CONFIGURATION.md).

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
before acknowledgment. Do not introduce a fire-and-forget worker queue and acknowledge
before work is either completed or durably accepted under a defined recovery
contract. The current design uses bounded parallel work awaited by the handler.
For sustained polling responsiveness, benchmark long creation bursts before
adding any durable asynchronous job queue.

## 8. Current Docker controller behavior

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
- A healthy runner exits after one job. Startup discovery reconciles owned
  provider resources with journal state before polling. During runtime,
  completion events drive cleanup of known terminal runners. Periodic
  reconciliation verifies scoped inventory and GitHub registration evidence
  before releasing capacity for unexpected exits or partial resources.
- `IdleTimeout` accepts null or zero. Retirement of live runners requires
  authoritative per-runner idle evidence and protection against a concurrent
  assignment. Busy and uncertain runners retain capacity.
  Configuration can explicitly select Docker builds or privileged mode; safe
  default is neither.
- Confirmed shutdown policy (D12): drain and preserve. Stop new acquisitions
  and provisioning, continue processing/acking lifecycle messages during the
  configurable drain period, and remove only confirmed terminal resources.
  At the deadline persist recovery state, close the session with a separate
  bounded budget, and exit while busy/unknown runners continue independently.
  Retain scale-set identity; reconcile survivors before acquiring on restart.
  Reaching the deadline never implies force-stop or deletion. Proposed commands:
  run, validate-config, status, drain, and explicit delete; destructive commands
  require an explicit target and documented active-job behavior.
- Handle SIGTERM as well as interactive cancellation. Separate stopping,
  successful-message acknowledgment, job drain, and cleanup budgets. Do not
  use the cancelled host token for post-success ack/session closure.
- Separate controller and runner resource lifetimes; deployment teardown must
  not implicitly remove preserved runners. Docker-host shutdown still stops
  those runners. Configure Compose's stop grace to exceed drain plus bounded
  acknowledgment/closure/persistence/telemetry-flush overhead; its default alone is insufficient
  for a multi-minute drain. Cleanup of runners that finish while the controller
  is absent happens on recovery. See [Compose stop grace](https://docs.docker.com/reference/compose-file/services/#stop_grace_period).

## 9. Operations and distribution

The `0.1.0-alpha.1` distribution defines four .NET 10 NuGet packages:
`Knightfall.ScaleSet.Client`, `Knightfall.ScaleSet.Listener`,
`Knightfall.ScaleSet.Hosting`, and `Knightfall.ScaleSet.Providers.Docker`.
The source includes the controller executable project, a multi-architecture
Docker build, and a Compose example. Package publishing and verification are
documented in the [release runbook](../releases/PUBLISHING.md).

Confirmed D16-D17: use Microsoft ILogger<T>, routed by Serilog.Extensions.Hosting
to Serilog.Sinks.OpenTelemetry for OTLP logs. The executable separately registers
OpenTelemetry tracing/metrics SDK providers and exporters; do not register a
duplicate OTel ILogger exporter or export Activity-derived spans through both
pipelines. Resources and trace/span correlation agree across all three signals.
Public libraries emit ActivitySource/Meter and accept ILogger without initializing
the SDK. Confirmed D20: export all three signals to the configured OTLP endpoint.
The receiving service is operator-supplied; backend selection and Collector
deployment are outside the product scope. Validate the endpoint/protocol and
consistent SDK/sink settings, keeping signal-specific overrides explicit.

Tracing and metrics are mandatory. Instrument HTTP/authentication, queue
operations, scaling, provider lifecycle, recovery, and drain; standard HTTP/
runtime instrumentation supplements custom business signals. Metric dimensions
are bounded; job/runner/trace IDs are not labels. Sampling, semantic conventions,
redaction, export lifecycle, and acceptance are specified in
[OBSERVABILITY-CONFIGURATION.md](OBSERVABILITY-CONFIGURATION.md).

Liveness indicates process progress. Readiness indicates valid ownership,
configuration, and completed initial reconciliation; degraded GitHub/provider
connectivity should be visible without causing a restart loop that destroys
recovery context. No management mutation API or dashboard is included unless
requested; health/metrics listeners should be restricted to intended networks.

CI: Roslynator/build diagnostics, Linux protocol tests, Docker integration tests,
library portability tests
on macOS/Windows where feasible without claiming their runner backends are
supported; release packaging; image scans/signing/provenance appropriate to
the publishing destination; opt-in live canaries isolated from ordinary PRs.
Never execute untrusted PR code with publishing/live-canary credentials.

## 10. Planned ECS task provider

Future provider requirements and design proposals follow. Detailed cloud
infrastructure planning is deferred under D14; these do not finalize stacks,
networking, roles, bootstrap deployment, or capacity choices.

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
policy. If ECS uses EC2-backed capacity, distinguish runner-task demand from
underlying container-instance scaling; avoid competing capacity controllers
and validate instance draining against long-running CI jobs.

## 11. Planned direct EC2 provider

Future provider requirements/design proposals; deployment and infrastructure
choices must be revisited during the later AWS design.

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
an explicit configured forced-cleanup policy. On-Demand/Spot selection and
interruption handling are decisions for the later AWS design, with their own
acceptance tests. Direct EC2 provider lifecycle is distinct from managing
EC2 container instances underneath ECS.

## 12. AWS controller and infrastructure design

Confirmed D14: AWS CDK v2 in TypeScript is the selected infrastructure tool.
The controller is intended to run in a customer AWS account as a Lambda
function or ECS service, with a scoped IAM role and configuration identifying
which runner resources it can manage. The client, listener, and hosting
contracts remain provider-neutral. The `infra/` path is reserved; no CDK
project or cloud deployment is included in the current implementation.

[TODO, AWS design] Define the Lambda invocation/session lifecycle, ECS service
deployment, AWS topology/stacks, account/network assumptions, IAM policies,
controller state, runtime configuration sources, runner bootstrap delivery,
capacity choices, teardown, and environment-specific acceptance criteria.
Revisit sections 10/11 before implementation; they are provider requirements,
not finalized infrastructure designs.

This AWS work follows the current Docker controller release.
Cloud implementation/deployment milestones depend on completing that later
design; they do not start automatically when the Docker release finishes.

## 13. Product milestones and acceptance gates

The proposed [Listener extensibility feature](LISTENER-EXTENSIBILITY.md) adds
opt-in checkpointed, stepwise execution and external operation authorization.
It preserves the existing listener API and leaves storage, ownership,
credential rotation, and remote-session recovery policy with the consumer.
Its LE1–LE5 work packages are planned work, not completed M3 functionality.

| Milestone | Deliverables | Dependencies | Gate |
|---|---|---|---|
| M0 Product definition | Roadmap, pinned source, GitHub contract matrix, attribution inventory | Product decisions | Product scope and protocol requirements documented |
| M1 Solution/contracts | .NET 10 layout, API/tests, env schema, Roslynator, ILogger/Serilog and OTel providers | M0 | Build/dependency/API review; analyzer loading/severity enforcement verified; all three signals reach a test OTLP receiver |
| M2 Wire/transport/auth | DTOs, exceptions, auth cache, HTTP, admin/session APIs and telemetry | M1 | Protocol-contract, concurrency/TLS/cancellation, and correlated/redacted HTTP telemetry tests pass |
| M3 Listener | Initial/empty/real callbacks, dynamic capacity, acquire interface, ack sequencing | M2 | No ack before success; cancellation-safe bounded ack; replay/refresh tests pass |
| M4 Docker lifecycle | Provider, policies, journal, discovery, idempotent cleanup, aggregate budgets | M3 | Restart/partial-create/duplicate-event/container-exit/drain scenarios pass |
| M5 Docker release | CLI/service, env/docs, telemetry, Compose, analyzer/package/image CI | M4 | Roslynator gate passes; repo+org canaries, config/install/recovery, correlation/metrics/export outage/shutdown tests |
| M6 ECS provider | Future task lifecycle/config/CDK TypeScript infra/tests | M5 and later AWS design | Later planning confirms scope/gates; AWS/GitHub canary, recovery/drain, credential rotation and telemetry |
| M7 EC2 provider | Future instance/AMI/config/CDK TypeScript infra/tests | M5 and later AWS/bootstrap design | Later planning confirms scope/gates; canary, bootstrap/recovery, source/rotation and telemetry tests |
| M8 Release/maintenance | Supported environment matrix, upgrade checklist, published artifacts | Relevant release gates | Documented evidence for each advertised target/provider; recoverable upgrade |

After the later AWS design, M6 and M7 may develop in parallel once
common bootstrap/state contracts are agreed. Each milestone produces a small reviewable change with evidence and
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
For D12, verify both completion during drain and a job outlasting the drain
timeout: no forced runner stop, stable scale-set identity, bounded controller
exit, restart adoption, and later terminal-resource cleanup. Verify deployment
grace permits the configured shutdown sequence and that host teardown is
documented as outside the runner-preservation guarantee.
Test bursts at configurable sizes; publish observed limits and avoid long
sleep-dependent unit tests.

Live tests require a dedicated GitHub.com repository/organization and App/PAT
support evidence. They submit workflows, check ephemeral completion, restart
the controller during a job, drain, and verify owned-resource cleanup. AWS
canaries additionally need isolated account/region/network configuration and
failure cleanup. Local PR tests do not silently acquire live credentials.

The roadmap itself does not execute tests. Current verification results and remaining live-canary work are recorded in [VERIFICATION.md](VERIFICATION.md).
Mandatory telemetry tests verify registered sources/meters, log/trace and resource
correlation, no duplicate exports, bounded metric dimensions, secret redaction,
export during operations, outage behavior, and bounded flushing during D12 drain.
Configuration tests cover environment hierarchy/arrays, validation/precedence,
and AWS credential caching/rotation with synthetic credentials.

## 15. Engineering delivery

Deliver each product milestone as a reviewable change with defined ownership,
dependencies, and acceptance evidence. Integrate changes to shared contracts,
project wiring, and protocol fixtures deliberately. Verification reports must
identify the commands run and their results; planned work is not implementation
evidence.

## 16. GitHub protocol and release maintenance

Maintain the upstream baseline SHA and a method/wire/behavior change ledger.
For an upstream update, inspect its diff, refresh fixtures, document deliberate
differences, and run protocol compatibility plus relevant lifecycle tests.
Never replace the baseline silently with main. Keep supported targets/providers explicit and
separate experimental/deferred capabilities.

Preserve the existing Apache-2.0 repository license. Add upstream GitHub MIT
copyright/license notices for translated source/fixtures and include them in
published packages and images. The package pipeline uses locked restore,
SourceLink, NuGet auditing, a build dependency SBOM, and signed attestations.
Publication requires a protected tag and a manually approved run on `main`.
See the [alpha release notes](../releases/0.1.0-alpha.1.md).
