# ScaleSet.Docker.Tests

These xUnit v3 tests use fake Docker Engine responses to check API negotiation,
image selection, container flags, ownership labels, recovery, and cleanup.
They run without a daemon and show how the provider translates a runner
profile into Docker requests.

From the repository root with the pinned .NET SDK, run:

```sh
dotnet test --project tests/ScaleSet.Docker.Tests/ScaleSet.Docker.Tests.csproj --configuration Release
```
