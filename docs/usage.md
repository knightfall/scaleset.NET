# Usage

The `0.1.0-alpha.1` packages target .NET 10. Use the libraries in an existing
application or build the Docker controller from this repository.

## Choose a GitHub credential

Use an account with administrative access to the selected runner scope. For a
GitHub App, install it on the target and grant the corresponding permission.

| Target | GitHub App or fine-grained PAT permission | Classic PAT scope |
| --- | --- | --- |
| Repository | `Administration` (write) on the repository | `repo` |
| Organization | `Self-hosted runners` (write) in the organization | `admin:org` |
| Enterprise | Use a classic PAT | `manage_runners:enterprise` |

See GitHub's registration-token API for [repositories](https://docs.github.com/en/rest/actions/self-hosted-runners#create-a-registration-token-for-a-repository),
[organizations](https://docs.github.com/en/rest/actions/self-hosted-runners#create-a-registration-token-for-an-organization),
and [enterprises](https://docs.github.com/en/enterprise-cloud@latest/rest/actions/self-hosted-runners#create-a-registration-token-for-an-enterprise).

## Use the client library

Once the prerelease is available, create a .NET 10 console project and install
the client package from NuGet.org.

```sh
dotnet new console --framework net10.0 --output scaleset-client
cd scaleset-client
dotnet add package Knightfall.ScaleSet.Client --version 0.1.0-alpha.1
```

Replace `Program.cs` with the following example. Set `SCALESET_TARGET_URL` to
your repository, organization, or enterprise URL. Set `SCALESET_GITHUB_PAT`
from your environment or secret store. Set `SCALESET_RUNNER_GROUP` if your
runner group uses a name other than `Default`.

```csharp
using ScaleSet.Client;

string url = Environment.GetEnvironmentVariable("SCALESET_TARGET_URL")
    ?? throw new InvalidOperationException("Set SCALESET_TARGET_URL.");
string token = Environment.GetEnvironmentVariable("SCALESET_GITHUB_PAT")
    ?? throw new InvalidOperationException("Set SCALESET_GITHUB_PAT.");
string groupName = Environment.GetEnvironmentVariable("SCALESET_RUNNER_GROUP") ?? "Default";

using var client = new ScaleSetClient(
    GitHubTarget.Parse(url),
    ScaleSetCredentials.FromPersonalAccessToken(token));

RunnerGroup group = await client.GetRunnerGroupByNameAsync(groupName);
IReadOnlyList<RunnerScaleSet> scaleSets = await client.ListScaleSetsAsync(group.Id);
Console.WriteLine($"Authenticated to runner group '{group.Name}' (ID {group.Id}).");
foreach (RunnerScaleSet scaleSet in scaleSets)
{
    Console.WriteLine($"{scaleSet.Name} (ID {scaleSet.Id})");
}
```

Run `dotnet run` to verify access to the group and list its scale sets. An
empty list means the group has no scale sets. For GitHub App authentication, use
`ScaleSetCredentials.FromGitHubApp(clientId, installationId, privateKeyPem)`.
See the [client and listener guide](implementation/CLIENT.md) for session
lifecycle, retry behavior, and HTTP response diagnostics.

| Scope | GitHub.com example | GitHub Enterprise Cloud example |
| --- | --- | --- |
| Repository | `https://github.com/example-org/example-repo` | `https://example.ghe.com/example-org/example-repo` |
| Organization | `https://github.com/example-org` | `https://example.ghe.com/example-org` |
| Enterprise | `https://github.com/enterprises/example` | `https://example.ghe.com/enterprises/example` |

Add `Knightfall.ScaleSet.Listener` to run the listener in your own process.
Add `Knightfall.ScaleSet.Hosting` for the provider contracts and runner
lifecycle, or `Knightfall.ScaleSet.Providers.Docker` to use the Docker Engine
provider. Pin all direct `Knightfall.ScaleSet.*` references to the same version.
C# namespaces use `ScaleSet.*`.

The [runnable .NET worker](../examples/dotnet-worker/README.md) uses all four
packages to resolve an existing scale set, open a session, provision Docker
runners, drain jobs, and close the session. It includes instructions for
restoring the prerelease from local package files.

## Restore from GitHub Packages

To consume the GitHub Packages copy, configure this `NuGet.Config` alongside
the consumer project. Source mapping selects GitHub Packages for
`Knightfall.ScaleSet.*` and NuGet.org for other dependencies.

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="github" value="https://nuget.pkg.github.com/knightfall/index.json" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="github">
      <package pattern="Knightfall.ScaleSet.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

Set the environment variable `NuGetPackageSourceCredentials_github` to
`Username=<GitHub login>;Password=<token>;ValidAuthenticationTypes=Basic`.
Use a classic PAT with `read:packages` and access to the packages. A GitHub
Actions workflow can use its `GITHUB_TOKEN` when its repository has package
read access. Keep credentials out of `NuGet.Config` and the repository.
See the [GitHub NuGet registry docs](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry).

## Run the Docker controller

Build and run the controller with the repository's Compose example. It needs
access to a Linux Docker Engine, a persistent state directory, and a
runner-administration credential. The example supplies a pinned runner image,
an OTLP collector, and a smoke workflow. Follow the
[deployment guide](implementation/DOCKER-DEPLOYMENT.md) to run your first job.
