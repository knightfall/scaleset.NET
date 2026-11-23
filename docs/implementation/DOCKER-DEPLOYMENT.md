# Docker deployment

The [Compose quickstart](../../examples/docker-compose/compose.yaml) runs a
controller and an OpenTelemetry Collector. It mounts durable state and the
Docker Engine socket. The controller creates an ephemeral Docker container for
each runner. Use a Linux Docker Engine and a private GitHub repository for the
first job. The example uses a public, digest-pinned GitHub Actions runner image.

## Prepare and validate

Copy `examples/docker-compose/.env.example` to `examples/docker-compose/.env`.
Set `SCALESET__ScaleSets__0__GithubUrl` to your repository URL and set
`GITHUB_TOKEN` in `.env` to a PAT with runner administration access. The sample
uses `ci-linux` as the scale-set name and workflow label. See the
[credential permissions](../usage.md#choose-a-github-credential). To use a GitHub App,
replace the sample's `SCALESET__Credentials__github__Pat` mapping with
`SCALESET__Credentials__github__ClientId`,
`SCALESET__Credentials__github__InstallationId`, and
`SCALESET__Credentials__github__PrivateKeyPem`.

Set `DOCKER_GID` to the group ID of `/var/run/docker.sock` on the Docker host.
On native Linux, run `stat -c '%g' /var/run/docker.sock`. For Docker Desktop,
check the socket group after building the autoscaler image. If the state
directory is in your home directory on macOS, use your `id -u` and `id -g`
for `SCALESET_UID` and `SCALESET_GID` so the container can write to it.
Set `SCALESET__ScaleSets__0__RunnerProfile__Architecture` to `Arm64` on an arm64
Engine. Create `examples/docker-compose/state` (or the path in `SCALESET_STATE_DIR`)
and make it writable by `SCALESET_UID` and `SCALESET_GID`. Keep `.env` private.

From the repository root, validate the Compose configuration and build the
image.

```sh
docker compose --env-file examples/docker-compose/.env -f examples/docker-compose/compose.yaml config --quiet
docker compose --env-file examples/docker-compose/.env -f examples/docker-compose/compose.yaml build
```

On Docker Desktop, check the socket group with the built image before running
`validate-config` and `up`. Put the resulting group ID in `DOCKER_GID`.

```sh
docker run --rm --mount type=bind,src=/var/run/docker.sock,dst=/var/run/docker.sock,readonly \
  --entrypoint /usr/bin/stat scaleset-autoscaler:local -c '%g' /var/run/docker.sock
```

Validate the application settings and start the controller and collector.

```sh
docker compose --env-file examples/docker-compose/.env -f examples/docker-compose/compose.yaml run --rm autoscaler validate-config
docker compose --env-file examples/docker-compose/.env -f examples/docker-compose/compose.yaml up -d
```

The compose file binds health port 8080 to loopback on the host. `/health/live`
reports whether the worker is running and not shutting down. `/health/ready`
returns 503 until scale-set identity and runner inventory reconciliation finish.
Set `SCALESET__Health__ListenUrl` to change the container listener URL and
`HEALTH_PORT` to change the host-side port. Expose readiness only to the
orchestrator and networks that need it. The listener uses HTTP; terminate TLS
at a trusted reverse proxy when remote health access is required.

## Run the first job

1. Wait for `curl -fsS http://127.0.0.1:8080/health/ready` to return `ready`.
2. Copy [the smoke workflow](../../examples/docker-compose/runner-smoke.yml) as
   `.github/workflows/runner-smoke.yml` in the target repository's default
   branch. If you changed the scale-set name, set `runs-on` to its label.
3. In GitHub, open **Actions**, select **Scale set runner smoke**, and choose
   **Run workflow**. The step verifies that the job ran on a self-hosted Linux
   runner and prints its name and architecture. The controller removes the
   ephemeral container after it observes the job and runner finish.

The bundled collector receives OTLP/HTTP at `http://otel-collector:4318` on
the Compose network and prints signal counts in its logs. Check it with
`docker compose --env-file examples/docker-compose/.env -f examples/docker-compose/compose.yaml logs otel-collector`.
If you use an existing receiver, set `OTEL_EXPORTER_OTLP_ENDPOINT` and
`OTEL_EXPORTER_OTLP_PROTOCOL` for that receiver.

A definitive rejection during first-time scale-set creation reports the
GitHub API error. Correct the cause and restart. If the outcome is uncertain,
the controller keeps the pending identity and attempts to resolve the scale
set by name. Check GitHub for the existing scale set before modifying durable
state.

## Architecture and state

The image supports Linux x64 and arm64. Build a multi-platform OCI image with
Buildx using the exact runner architecture and runner image combination you
intend to deploy:

```sh
docker buildx build --platform linux/amd64,linux/arm64 \
  --tag scaleset-autoscaler:local \
  --output type=oci,dest=/tmp/scaleset-autoscaler.oci.tar .
```

The default scaling policy is `SpareCapacity`; set
`SCALESET__ScaleSets__0__Scaling__Policy=TotalFloor` for a total runner floor.
Both policies use `TotalAssignedJobs`; available-message count does not define
scaling demand. Configure host runner, CPU, memory, and creation limits with
`SCALESET__HostCapacity__*`. Multiple scale sets can be added with contiguous
numeric indices under `SCALESET__ScaleSets__` and separate credential refs.

Keep the state directory across upgrades and container replacement. It holds
the scale-set identity and non-secret runner recovery journal. Run only one
active controller for a given state directory and scale set. If an old runner
profile remains active after configuration changes, capacity accounting uses
the observed Docker limits when available; new runners use the new profile.

The controller's Docker socket mount grants host-level Docker control. Runner
containers do not receive the controller PAT or App private key; they receive
only their short-lived JIT configuration. Docker builds and privileged runner
execution remain disabled unless explicitly enabled in the runner profile.

## Shutdown and signals

```sh
docker compose --env-file examples/docker-compose/.env -f examples/docker-compose/compose.yaml down
```

The sample sets a ten-minute Compose stop grace. The application drains for
the configured per-scale-set period, closes sessions with a separate bounded
cleanup timeout, and preserves busy or uncertain runners. If you raise drain
or cleanup limits or configure many scale sets, increase
`SCALESET_STOP_GRACE_PERIOD` so it covers the maximum parallel drain plus
sequential session cleanup, acknowledgment, and telemetry flush budgets.
Removing the controller container does not remove preserved runners or delete
the GitHub scale set. Docker-host shutdown still stops runner containers.

Logs use `ILogger` routed through Serilog to the configured OTLP endpoint.
Traces and metrics are exported separately by the OpenTelemetry SDK. Compose
runs the collector as a separate service.

### GitHub session recovery

The autoscaler reconnects a scale set after temporary queue transport
failures, expired sessions, or session-open conflicts. The worker closes its
known session before it requests a replacement. A conflict during session
creation is retried without closing another controller's session. An
unconfirmed close stops recovery so two sessions cannot run concurrently.
Successful message acknowledgment or an empty poll resets the retry budget.

The worker makes up to three consecutive retries with delays of one, two,
and four seconds. It honors GitHub rate-limit hints up to 30 seconds.
A failed acknowledgment passes through the same recovery policy. The
replacement listener processes job activity against the same handler,
journal, and capacity ledger. Authentication, invalid responses, application
failures, and exhausted retry budgets stop the autoscaler under its existing
worker supervision. Other scale sets keep polling while a worker is
recovering.

### Runtime telemetry

Capacity spans use the `ScaleSet.Hosting` activity source. The
`ScaleSet.Hosting.Capacity` meter exports `scaleset.capacity.operations` and
`scaleset.capacity.creation_lease.operations` with operation and outcome tags.
The `ScaleSet.Hosting` meter exports `scaleset.operations` and
`scaleset.operation.duration` for startup inventory reconciliation, job
acquisition, runner provisioning, queue polls, acknowledgments, session
recovery, and drain.
Empty poll responses and cancellations have distinct outcomes. A drain that
reaches its deadline reports `unknown` because work may still be active.

`scaleset.runners` reports current capacity occupancy by scale set, provider,
and state. It includes reservations and uncertain runners that still hold
capacity. A released reservation disappears from the next observation.
Operation and state tags use fixed values. Scale-set tags use up to 64
configured keys, with the remaining keys grouped under `other`.

Live GitHub canaries and live Docker recovery tests require a dedicated test
scope and are not part of ordinary image builds. The opt-in Docker integration
tests require `SCALESET_DOCKER_INTEGRATION=1` and a Linux Docker daemon.
