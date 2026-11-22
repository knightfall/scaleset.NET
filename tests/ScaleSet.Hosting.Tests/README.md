# ScaleSet.Hosting.Tests

These xUnit v3 tests exercise configuration, capacity planning, the runner
state journal, provisioning, cleanup, and the autoscaler runtime. Use the
fake providers and clients in this project to see how ownership, uncertain
operations, restart, and shutdown are handled. The telemetry tests use a
loopback receiver.

From the repository root with the pinned .NET SDK, run:

```sh
dotnet test --project tests/ScaleSet.Hosting.Tests/ScaleSet.Hosting.Tests.csproj --configuration Release
```
