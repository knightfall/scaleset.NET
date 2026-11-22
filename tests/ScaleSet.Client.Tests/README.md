# ScaleSet.Client.Tests

These xUnit v3 tests exercise `ScaleSetClient` authentication, target parsing,
administration calls, queue sessions, and HTTP retries through fake transports
and clocks. Use this project when changing how a consumer connects to GitHub
or when updating the [protocol contract](../../docs/product/GITHUB-PROTOCOL.md).

From the repository root with the pinned .NET SDK, run:

```sh
dotnet test --project tests/ScaleSet.Client.Tests/ScaleSet.Client.Tests.csproj --configuration Release
```
