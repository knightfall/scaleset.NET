# ScaleSet.Listener.Tests

These xUnit v3 tests exercise the listener loop with fake sessions and message
handlers. They cover initial statistics, polling capacity, handler failures,
acknowledgment order, and cancellation. The tests describe the contract a
consumer can rely on when writing an `IScaleSetMessageHandler`.

From the repository root with the pinned .NET SDK, run:

```sh
dotnet test --project tests/ScaleSet.Listener.Tests/ScaleSet.Listener.Tests.csproj --configuration Release
```
