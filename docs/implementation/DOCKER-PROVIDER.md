# Docker provider and recovery foundation

This milestone adds provider-neutral ownership and lifecycle contracts, a
non-secret file journal, host-capacity planning/reservations, and a Docker
Engine adapter. The CLI does not yet compose these pieces into a running
autoscaler. Controller reconciliation and polling, drain/shutdown orchestration,
health endpoints, Compose deployment and release images remain subsequent work.

## Ownership and recovery

`RunnerOwnershipScope` identifies a controller and GitHub scale set.
`RunnerIdentity` adds a durable operation ID and runner name; the GitHub runner
ID may be linked after JIT generation. Provider state is evidence about the
resource itself: a running container does not reveal whether its runner is
idle or busy.

Provisioning persists the `Reserved` intent before side effects, then records
`JitIssued` and its linked runner ID, `Creating`, and the provider resource ID
before starting the container. After restart, a surviving running container
is adopted as `Unknown`; old `Idle` or `Busy` journal values do not prove its
current job. Removal starts only with terminal evidence or a created container
proven never to have started, and `Gone` is recorded only after the provider
confirms absence.

`FileRunnerStateJournal` holds an exclusive per-scale-set directory lease,
uses compare-and-write revisions, and atomically replaces the journal from a
temporary file in that directory. It rejects scope/schema mismatches and drains
admitted reads and writes before releasing the lease. Its records contain scope,
operation identity, lifecycle stage, resource IDs, timestamps and provider
evidence; they have no PAT, App key, session token or JIT field.

The capacity coordinator validates versioned plans again when reserving. Existing,
starting and uncertain resources consume count, CPU and memory budgets. `Busy`,
`Unknown`, `Removing` and `Failed` resources do not add free queue slots.
Round-robin allocation shares feasible headroom across sets; generation and
revision guards reject stale handles, and a separate gate bounds parallel
creation.

## Docker Engine behavior

`DockerRunnerProvider` uses the Engine HTTP API, negotiates the highest compatible
version up to 1.47, and supports versions 1.40 through 1.47 on Linux daemons. It
defaults to `/var/run/docker.sock`. Remote HTTP or TLS endpoints use a
caller-supplied `HttpClient`. Control requests default to 60 seconds, and image
pulls use a bounded 15-minute timeout with bounded progress records.

Images require a pinned version tag or SHA-256 digest. Containers carry product,
controller, scale-set, operation, runner name and optional GitHub runner ID
labels. Discovery filters on those labels and verifies ownership again before
start and remove. Create and start are separate; an uncertain create is
reconciled by its stable operation identity rather than blindly replayed. The
default container receives only `ACTIONS_RUNNER_INPUT_JITCONFIG`, with no App or
PAT credentials. The provider preserves the image working directory unless
explicitly overridden, does not auto-remove containers, and does not force-stop
running resources during normal cleanup. Host-socket and Docker-in-Docker modes
require explicit runner profiles.

Private-registry pull authentication is not implemented yet. Images therefore
need to be public or preloaded on the Engine host. A caller-owned HTTP client is
responsible for TLS, redirects, proxy and connection lifetime.

## Verification

The Release build completed with zero warnings and errors. The native-MTP
solution suite passed 335 tests with no failures or skips: 141 Client, 20
Listener, 126 Hosting, 46 Docker-unit and 2 opt-in Docker-integration tests.
The integration run used the local Docker 29.8.1 Linux arm64 daemon and only a
pinned public BusyBox image with synthetic JIT data. It verified ownership
scoping, survival after provider disposal, safe terminal cleanup and journal
reopen. Scoped cleanup left no test-owned containers. Four local alpha packages
were created and none published. Hosted GitHub Actions for this branch has not
run yet.

The direct evidence is in [Docker unit tests](../../tests/ScaleSet.Docker.Tests),
[live integration tests](../../tests/ScaleSet.Docker.IntegrationTests),
[provider contract tests](../../tests/ScaleSet.Hosting.Tests/Providers),
[lifecycle tests](../../tests/ScaleSet.Hosting.Tests/State), and
[capacity tests](../../tests/ScaleSet.Hosting.Tests/Capacity).
