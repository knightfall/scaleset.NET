# ScaleSet.Autoscaler

This .NET 10 executable composes the Client, Listener, Hosting, and Docker
provider into a controller. Build the repository image and use the
[Docker quickstart](../../docs/implementation/DOCKER-DEPLOYMENT.md) to run it.

The application reads `SCALESET__*` settings. At startup it resolves each
scale set, loads durable state, and reconciles Docker inventory before it
opens the GitHub message sessions. A shared capacity coordinator limits
runner count, CPU, memory, and concurrent creation. On shutdown the controller
stops admissions, drains known work within the configured deadline, and
preserves unfinished runners for recovery.

`validate-config` checks local settings. `telemetry-smoke` sends sample logs,
metrics, and traces to the configured collector. `run` starts the controller
and exposes `/health/live` and `/health/ready`. See the
[configuration guide](../../docs/product/OBSERVABILITY-CONFIGURATION.md).
