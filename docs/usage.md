# Usage

The `0.1.0-alpha.1` packages target .NET 10. Use the libraries in an existing
application or build the Docker controller from this repository.

## Use the client library

Once the prerelease is available, create a .NET 10 console project and install
the client package from NuGet.org.

```sh
dotnet new console --framework net10.0 --output scaleset-client
cd scaleset-client
dotnet add package Knightfall.ScaleSet.Client --version 0.1.0-alpha.1
```

Replace `Program.cs` with the following example. Set `SCALESET_TARGET_URL` to
your repository, organization, or enterprise URL. The example uses
`GITHUB_TOKEN` for a PAT with permission to administer runners at that scope.
Load the token from your environment or secret store.

```csharp
using ScaleSet.Client;

string url = Environment.GetEnvironmentVariable("SCALESET_TARGET_URL")
    ?? throw new InvalidOperationException("Set SCALESET_TARGET_URL.");
string token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
    ?? throw new InvalidOperationException("Set GITHUB_TOKEN.");

using var client = new ScaleSetClient(
    GitHubTarget.Parse(url),
    ScaleSetCredentials.FromPersonalAccessToken(token));

Console.WriteLine(client.Target.ApiBaseUri);
```

Run `dotnet run` to verify the installation and target parsing. A client sends
requests when you call an API such as `ListScaleSetsAsync(runnerGroupId)`.
For GitHub App authentication, use
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
access to Docker Engine, a compatible pinned runner image, a persistent state
directory, a runner-administration credential, and an OTLP receiver. The
controller exposes liveness at `/health/live` and readiness at `/health/ready`.
Follow the [deployment guide](implementation/DOCKER-DEPLOYMENT.md) for
configuration, startup, and drain-and-preserve shutdown.
