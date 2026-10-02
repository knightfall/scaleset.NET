# Docker deployment

The phase-one controller runs as one container per owned state directory. The
example in [`examples/docker-compose/compose.yaml`](../../examples/docker-compose/compose.yaml)
mounts durable state and the Docker Engine socket; runner containers are
created separately by the Docker provider.

## Prepare and validate

Copy `examples/docker-compose/.env.example` to `.env`. Set the GitHub.com
repository or organization URL, scale-set name, a pinned runner image digest,
the PAT, and the OTLP endpoint. The sample uses a PAT; GitHub App credentials
are also supported through `SCALESET__Credentials__github__ClientId`,
`SCALESET__Credentials__github__InstallationId`, and
`SCALESET__Credentials__github__PrivateKeyPem`.

Set `DOCKER_GID` to the group ID that can access `/var/run/docker.sock` on the
Docker host. On Linux, `stat -c '%g' /var/run/docker.sock` reports its current
group. Create the state directory and make it writable by `SCALESET_UID` and
`SCALESET_GID` before starting the container. Keep `.env` private; it is
excluded from Git.

From the repository root, validate the composed configuration and the
application configuration, then build and start the service:

```sh
docker compose --env-file examples/docker-compose/.env -f examples/docker-compose/compose.yaml config --quiet
docker compose --env-file examples/docker-compose/.env -f examples/docker-compose/compose.yaml run --rm autoscaler validate-config
docker compose --env-file examples/docker-compose/.env -f examples/docker-compose/compose.yaml build
docker compose --env-file examples/docker-compose/.env -f examples/docker-compose/compose.yaml up -d
```

The compose file binds health port 8080 to loopback on the host. `/health/live`
reports whether the worker is running and not shutting down. `/health/ready`
returns 503 until scale-set identity and runner inventory reconciliation finish.
Set `SCALESET__Health__ListenUrl` to change the container listener URL and
`HEALTH_PORT` to change the host-side port. Expose readiness only to the
orchestrator and networks that need it. The listener uses HTTP; terminate TLS
at a trusted reverse proxy when remote health access is required.

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

Keep the named state volume across upgrades and container replacement. It holds
the scale-set identity and non-secret runner recovery journal. Run only one
active controller for a given state directory and scale set. If an old runner
profile remains active after configuration changes, capacity accounting uses
the observed Docker limits when available; new runners use the new profile.

The controller's Docker socket mount grants host-level Docker control. Runner
containers do not receive the controller PAT or App private key; they receive
only their short-lived JIT configuration. Docker builds and privileged runner
execution remain disabled unless explicitly enabled in the runner profile.

## Shutdown and signals

The sample sets a ten-minute Compose stop grace. The application drains for
the configured per-scale-set period, closes sessions with a separate bounded
cleanup timeout, and preserves busy or uncertain runners. If you raise drain
or cleanup limits or configure many scale sets, increase
`SCALESET_STOP_GRACE_PERIOD` so it covers the maximum parallel drain plus
sequential session cleanup, acknowledgment, and telemetry flush budgets.
Removing the controller container does not remove preserved runners or delete
the GitHub scale set. Docker-host shutdown still stops runner containers.

Logs use `ILogger` routed through Serilog to the configured OTLP endpoint.
Traces and metrics are exported separately by the OpenTelemetry SDK. Configure
`OTEL_EXPORTER_OTLP_ENDPOINT` and the OTLP protocol; the receiver is operator
supplied. The image does not include an OTLP backend or Collector.

Live GitHub canaries and live Docker recovery tests require a dedicated test
scope and are not part of ordinary image builds. The opt-in Docker integration
tests require `SCALESET_DOCKER_INTEGRATION=1` and a Linux Docker daemon.
