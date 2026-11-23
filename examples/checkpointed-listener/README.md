# Checkpointed Listener example

This .NET 10 console app uses packaged Client and Listener assemblies. Its
scripted session, in-memory JSON checkpoint store, ownership signal, and
reservation ledger model the application interfaces. They run in one process
and provide no cross-process durability or distributed ownership.

Inside the repository the project uses source references so repository-wide
restore can discover it before publication. From the repository root, pack and
run the app in an isolated directory to verify package consumption. Its
NuGet configuration selects the candidate feed for the exact SDK versions and
NuGet.org for external dependencies.

```sh
consumer="$(mktemp -d)"
mkdir "$consumer/feed"
dotnet pack ScaleSet.slnx --configuration Release --output "$consumer/feed"
cp global.json Directory.Packages.props .editorconfig examples/checkpointed-listener/*.cs \
  examples/checkpointed-listener/*.csproj examples/checkpointed-listener/NuGet.Local.Config "$consumer/"
dotnet restore "$consumer/ScaleSet.CheckpointedListener.Example.csproj" \
  --configfile "$consumer/NuGet.Local.Config" --packages "$consumer/packages" --no-http-cache
dotnet run --project "$consumer/ScaleSet.CheckpointedListener.Example.csproj" \
  --configuration Release --no-restore
```

The executable asserts receipt-before-handling, current capacity after a
blocked poll, replay with the original observation time, acknowledgement-only
resume from `Processed`, duplicate effect suppression, cooldown deferral,
ownership cancellation, and safe replacement after an uncertain
acknowledgement. It reconstructs a pump while the same session client is still
open. The caller closes sessions and owns the store, handler, policy, and
ownership signal.

A production store must atomically enforce the expected revision and
ownership condition. A production handler must journal acquisitions and keep
uncertain reservations until they can be reconciled. The API contract describes
the recovery path and storage bounds. [Listener extensibility](../../docs/product/LISTENER-EXTENSIBILITY.md)
