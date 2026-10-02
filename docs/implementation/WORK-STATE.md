# Migration execution checkpoint

Status: **M4 committed and pushed** on `codex/docker-provider-recovery` at
`c5c7173`. PR #4 is open and stacked on the client branch in PR #3. Ubuntu and
macOS CI passed on its first run. Windows also built, but one hosting test
failed because Windows reports `UnauthorizedAccessException` where the test
only allowed `IOException`. Commit `c5330d8` accepts both platform-specific I/O
failure types while retaining the no-temporary-file checks. The focused local
Release test passed, and workflow run #5 passed on Ubuntu, Windows, and macOS.
No PR was merged during this pass.

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
- Client/session PR #3 is open: https://github.com/knightfall/scaleset.NET/pull/3.
  Head `ae063d4eb644585c77944decb2ae056a087c4847`; 220 tests passed locally,
  and Linux/Windows/macOS CI passed. Three libraries packed, none published.

## Current work

### M5.1 durable runner provisioning

Branch: `codex/controller-orchestration`, stacked above the green PR #4 head
`dbf1ad8`. PR #5 is open and CI is green on Ubuntu, Windows, and macOS. The
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

Branch: `codex/scale-set-worker`, stacked above PR #5. PR #6 is open; CI run #9
passed on Ubuntu, Windows, and macOS after the OTLP test was hardened to
aggregate separate metric export batches. Added deterministic request-to-runner
identity mapping and a provider-neutral message handler. It acquires offered
requests before provisioning only GitHub-confirmed IDs, keeps the latest
`TotalAssignedJobs` when queue messages omit statistics, and records
assigned/started/completed activity only when request and runner identities
match. Handler failures propagate so the listener leaves the message
unacknowledged. Focused Hosting Release tests passed 141/141 with no skips.

### M5.3 executable host and per-scale-set runtime

Branch: `codex/autoscaler-host`, stacked above PR #6. Added the environment-only
`run` host, a shared capacity coordinator, and a Docker-backed runtime factory.
Each scale set resolves or creates its GitHub group/set, atomically records a
non-secret identity marker before opening its journal, reconciles provider and
journal inventory into shared capacity, then opens a session and listener.
New scale sets use the Go sample's disabled automatic runner-update setting.
Normal process stop cancels polling and closes sessions within the per-set
cleanup timeout; it never removes runner resources or deletes scale sets.
Focused Hosting Release tests passed 144/144 with no skips, including fake-backed
multi-set startup ordering and marker recovery checks. PR #7 is open, stacked
above PR #6, at head `68f6972`; hosted workflow run #11 is in progress.

PR #4 targets `codex/github-client-transport`; keep the stack until PR #3 merges.
Next is capacity-aware admission/reservations, minimum-spare reconciliation,
profile-change accounting, the full bounded D12 drain, health endpoints,
Compose and release images. Runner inventory remains capacity-accounted as
`Unknown` until authoritative lifecycle evidence resolves it. Linux Docker
integration ran against `/var/run/docker.sock`; cleanup is scoped to unique
test ownership labels. Do not prune or remove unrelated containers or images.

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
