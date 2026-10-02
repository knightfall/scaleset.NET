# Migration execution checkpoint

Status: **Phase 1 complete**. Implementation PRs #1–#8 and security remediation
PR #10 are merged; no migration PR is open. Local `main` and `origin/main` are
clean at merge commit `d3137ff`. PR #10 redacts queue URL paths from
`ScaleSetApiException.SafeUri`; its GitHub `.NET` workflow passed, and the
focused client suite passed 143/143 locally. The completed security scan had
zero reportable vulnerabilities and one low-confidence deferred candidate,
which PR #10 addresses defensively. Consumer-configured `HttpClient`
OpenTelemetry instrumentation may still capture full URLs depending on its
configuration.

The next work package is **Phase 2 AWS design/planning**, not provider
implementation: ECS task and direct EC2 runner providers plus runtime Parameter
Store/Secrets Manager configuration and credential refresh are confirmed by
D05 and D18–D20. Follow D14 and the deferred-planning section of
`docs/migration/PLAN.md` before implementing cloud providers. The user selected
AWS CDK v2 in TypeScript. No cloud infrastructure has been deployed.

## Docker milestone implementation and verification

The M4 implementation is committed and pushed at `c5c7173` on
`codex/docker-provider-recovery`: provider contracts, lifecycle/file journal,
Docker Engine adapter, capacity coordinator, direct unit tests, two opt-in live
integration cases, test project/solution wiring, Linux CI integration opt-in
and Docker package activation. State drains admitted/queued reads and writes
before releasing its exclusive lease, with a concurrency regression. Docker
uses an injected TimeProvider, 60-second control defaults and added timeout,
missing-inspect and GitHub-ID-mismatch tests. No suppressions were added.

The escalated Release solution build succeeded with 0 warnings and 0 errors
(build-coordinated-6.log). Hosting passed 126/126, Docker provider 46/46, and
the opt-in Docker integration project 2/2. The final native-MTP solution test
ran with SCALESET_DOCKER_INTEGRATION=1 and passed 335/335 with 0 failures or
skips. Client passed 141/141 and Listener 20/20 in separate no-build runs.
Release packing created four local package artifacts; nothing was published.
Artifacts: `ScaleSet.Client.0.1.0-alpha.1.nupkg`,
`ScaleSet.Listener.0.1.0-alpha.1.nupkg`, `ScaleSet.Hosting.0.1.0-alpha.1.nupkg`,
and `ScaleSet.Providers.Docker.0.1.0-alpha.1.nupkg` under
`artifacts/package/release/`.

An ordinary-sandbox build stalled without output; only its owned PID11028 was
stopped. Use `sandbox_permissions=require_escalated` for .NET build/test IPC;
subsequent builds completed in seconds. Never kill unrelated dotnet processes.

The daemon reported Engine 29.8.1, API 1.56, minimum API 1.40, arm64. The
integration fixtures used synthetic JIT data, network isolation, unique
controller/scale-set labels, and the pinned image
`busybox@sha256:bdf57e528e45e4433820e045b29b4597825a1c9e38353532d90a01445013f82e`.
Their scoped cleanup left no containers matching the fixture's product and
`docker-integration` scale-set labels. No unrelated Docker resources were
changed or removed.

Detailed verification logs and the heuristic Roslyn source-pairing report are
under `/private/tmp/scaleset-docker-m4-tests`. The workflow matrix targets
Ubuntu, Windows, and macOS; Docker integration is opt-in on Linux and package
creation runs only on Linux. PR #4 workflow run #5 is green on all three
platforms. Private-registry auth remains unsupported. No package was published
or infrastructure deployed.

## Completed

- Planning PR #1 merged; confirmed decisions remain in `docs/migration/DECISIONS.md`.
- Foundation PR #2 merged at `cbc58413a6cdc742d8d65ebd158d2d4e143061a0`.
- Client/session PR #3 is merged: https://github.com/knightfall/scaleset.NET/pull/3.
  Head `ae063d4eb644585c77944decb2ae056a087c4847`; 220 tests passed locally,
  and Linux/Windows/macOS CI passed. Three libraries packed, none published.

## Completed Phase 1 implementation milestones

### M5.1 durable runner provisioning

Delivered by merged PR #5 from `codex/controller-orchestration`; CI passed on
Ubuntu, Windows, and macOS. The
provider-neutral provisioner and JIT-client adapter persist reservation, JIT,
GitHub runner association, create, and start intents before their corresponding
side effects. JIT data stays in memory. Ownership-scoped discovery and exact
resource inspection prevent duplicate creates.

The Hosting Release test project passed 135/135 tests with no skips, including
nine ordering, recovery, and concurrency cases. Recovery retries start once
only when a journaled `Starting` operation inspects as `Created` with no
start/finish time; it adopts the result as `Unknown`. Running and other
recovered resources are adopted as `Unknown`. An unresolved `Creating` intent
becomes `Unknown` and fails closed instead of blindly recreating a runner.
Overlapping calls are coalesced by `(ownership scope, operation ID)`; separate
operations/scopes remain concurrent, and a canceled waiter cannot release the
active operation's gate. M5.3 now wires the provisioner into the executable host.

### M5.2 per-scale-set message handling

Delivered by merged PR #6 from `codex/scale-set-worker`; CI run #9 passed on
Ubuntu, Windows, and macOS after the OTLP test was hardened to
aggregate separate metric export batches. Added deterministic request-to-runner
identity mapping and a provider-neutral message handler. It acquires offered
requests before provisioning only GitHub-confirmed IDs, keeps the latest
`TotalAssignedJobs` when queue messages omit statistics, and records
assigned/started/completed activity only when request and runner identities
match. Handler failures propagate so the listener leaves the message
unacknowledged. Focused Hosting Release tests passed 141/141 with no skips.

### M5.3 executable host and per-scale-set runtime

Delivered by merged PR #7 from `codex/autoscaler-host`. Added the environment-only
`run` host, a shared capacity coordinator, and a Docker-backed runtime factory.
Each scale set resolves or creates its GitHub group/set, atomically records a
non-secret identity marker before opening its journal, reconciles provider and
journal inventory into shared capacity, then opens a session and listener.
New scale sets use the Go sample's disabled automatic runner-update setting.
The initial stop implementation preserved runners and closed sessions within
the per-set cleanup timeout; M5.4 replaces this with the confirmed bounded
drain sequence. Focused Hosting Release tests passed 144/144 with no skips,
including fake-backed multi-set startup ordering and marker recovery checks.
Hosted workflow run #12 passed on Ubuntu, Windows, and macOS.

### M5.4 capacity, lifecycle cleanup, and bounded drain

Branch: `codex/autoscaler-capacity-drain`, based on the M5.3 PR #7 head. The
capacity-aware handler updates shared demand and reserves feasible host permits
before acquiring GitHub requests. Provisioning demand comes from
`TotalAssignedJobs` with the selected SpareCapacity or TotalFloor policy;
message counts only bound admission. Admission permits do not count as
materialized runners, and per-runner creation uses the shared concurrency gate.
Job activity resolves against the exact scoped GitHub runner ID and name.

Runner identities persist their creation-time CPU/memory profile. Docker
discovery reports observed NanoCPUs and memory, so restart accounting uses the
existing runner's observed resources after configuration changes; missing
observations remain unknown and block constrained budgets. Completed runners
are removed only after provider inspection confirms terminal state, always
without force, then the lifecycle tombstone and capacity release are recorded.

Normal shutdown now closes admission before the drain, keeps the listener alive
to process activity until the bounded deadline, then cancels polling and closes
sessions with the independent cleanup timeout. Busy and uncertain runners and
the scale-set identity are preserved. Generic Host shutdown timeout covers the
parallel drain, sequential session cleanup, acknowledgment/control budget, and
telemetry flush. Added focused tests cover policy demand, message-count
independence, fresh-stat acquisition, host-limit rejection, drain ordering,
drain timeout/recovery, non-forced terminal cleanup, and changed-profile
capacity accounting.

The full local .NET 10 solution test command
`dotnet test --solution ScaleSet.slnx --configuration Release` passed: 365
total, 363 succeeded, 0 failed, 2 skipped. The skipped cases require
`SCALESET_DOCKER_INTEGRATION=1` and a Linux Docker daemon. This run was on the
local arm64 host.

### M5.5 health, container, and Compose release path

Implemented `/health/live` and `/health/ready`; readiness becomes true only
after all scale-set identities and inventories initialize. The health listener
is configurable through `SCALESET__Health__ListenUrl`, and `health-probe` is
used by container health checks. Added a non-root Linux x64/arm64 Dockerfile,
Compose deployment, environment template, and
[`DOCKER-DEPLOYMENT.md`](DOCKER-DEPLOYMENT.md). The CI workflow validates the
Compose file and builds a multi-platform OCI image without publishing it.

`docker compose ... config --quiet` passed with `.env.example`. A local Buildx
OCI export compiled the final app for both `linux/amd64` and `linux/arm64` and
completed successfully under `/private/tmp`; the image was not loaded into a
registry. The first hosted PR run exposed the default Buildx driver limitation;
the workflow now creates a container-backed multi-platform builder. PR #8 is
merged above PR #7. Hosted workflow run `37041745255` passed on Linux,
Windows, macOS, and the multi-platform container job. A dedicated live
GitHub/Docker canary still requires a designated test
scale set; no package or container image was published and no infrastructure
was deployed.

The work has reached the Phase 2 planning boundary. ECS tasks, direct EC2
runners, and runtime Parameter Store / Secrets Manager remain the confirmed
scope, but AWS topology, bootstrap, roles, state, and rollout planning are
deferred by D14 and must precede cloud implementation. The live GitHub/Docker
canary and two opt-in Docker integration cases remain unrun; they require a
designated test scale set and a Linux Docker daemon. Linux Docker integration
cleanup is scoped to unique test ownership labels. Do not prune or remove
unrelated containers or images.

## Constraints for resumed execution

Use GPT-6 Luna subagents at max reasoning effort for implementation. Preserve
the .NET 10 pin, Roslynator/other analyzer gates, ILogger/Serilog OTLP logs and
mandatory OTel traces/metrics. User-confirmed shutdown is drain and preserve;
busy/unknown runners and scale-set identity survive controller shutdown.
JIT/PAT/App secrets must never enter durable state or diagnostics.
AWS detailed planning remains deferred; later infrastructure is CDK v2 TypeScript.

The user requested quota-aware execution: at 90% used in the five-hour window,
start no new substantial work batches and save a recoverable checkpoint; at
95%, stop implementation after saving state. Resume after the five-hour reset
with usage near zero, allowing the small amount used by the check itself.
Also avoid exhausting the weekly quota. Never buy credits, consume a reset
credit, change the requested model, or bypass an approval review failure.
The in-chat follow-up checks every 30 minutes. Quota checks are advisory rather
than an exact interruption guarantee for work already in flight.

Read current Git status, this checkpoint and the current usage result on resume.
Do not redo merged/verified milestones. Update this file at work-package boundaries.
No PR merge, package publication or deployment is authorized merely by a
scheduled follow-up; apply the actual user instructions for those actions.
