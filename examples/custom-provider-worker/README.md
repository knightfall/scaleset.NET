# Custom provider worker

This .NET 10 Generic Host registers a runner provider with `AddScaleSetHosting`.
It starts two hosts in sequence with the same controller identity and state
directory. The first provisions one runner from assigned-job demand. The
second adopts the existing runner and drains it after a completion message.
The example checks provider disposal, session closure, and capacity release.

```sh
dotnet run --project examples/custom-provider-worker
```

The demo uses an in-memory backend and a fake GitHub control plane. It needs
no token, Docker daemon, or telemetry collector. `DemoRunnerProvider.cs`
shows scoped discovery and removal. `DemoControlPlane.cs` supplies test
messages. A real provider can use the default control-plane client by
registering only its `IRunnerProviderFactory` and configuring GitHub credentials.

To try the unpublished packages in an isolated consumer, run these commands
from the repository root:

```sh
dotnet pack ScaleSet.slnx --configuration Release --output artifacts/packages
consumer="$(mktemp -d)"
mkdir -p "$consumer/feed"
cp artifacts/packages/*.nupkg "$consumer/feed/"
cp Directory.Packages.props .editorconfig examples/custom-provider-worker/*.cs examples/custom-provider-worker/*.csproj examples/custom-provider-worker/NuGet.Local.Config "$consumer/"
dotnet restore "$consumer/ScaleSet.CustomProvider.Example.csproj" --configfile "$consumer/NuGet.Local.Config" --packages "$consumer/packages"
dotnet run --project "$consumer/ScaleSet.CustomProvider.Example.csproj" --configuration Release --no-restore
```

See the [Hosting package](../../src/ScaleSet.Hosting/README.md) for provider
ownership, configuration, and shutdown behavior.
