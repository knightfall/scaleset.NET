# Knightfall.ScaleSet.Client

Use this .NET 10 library to connect to GitHub Actions runner scale sets from
your own application. `ScaleSetClient` resolves runner groups and scale sets,
creates scale sets and message sessions, and requests just-in-time (JIT)
configuration for ephemeral runners.

```sh
dotnet add package Knightfall.ScaleSet.Client --version 0.1.0-alpha.1
```

Parse the GitHub URL with `GitHubTarget.Parse` and construct a client with
`ScaleSetCredentials`. Target a repository, organization, or enterprise on
GitHub.com or GitHub Enterprise Cloud (`*.ghe.com`). GitHub Enterprise Server
support is deferred. For repository and organization targets, use a PAT or
an installed GitHub App with runner administration permission at that scope.
For enterprise targets, use a classic PAT with `manage_runners:enterprise`.
Load credentials from a secret store or an environment variable.

Use `GetRunnerGroupByNameAsync` and `ListScaleSetsAsync` to verify access.
`CreateMessageSessionAsync` returns a `MessageSessionClient` whose caller owns
closure. Use `CloseAsync` with a bounded token after polling stops. The client
manages registration and queue tokens, HTTP timeouts, and retry policy.
Use `IMessageSession` for code that owns closure and `IMessageSessionClient`
for code that only processes the queue. Close sessions before disposing the
client. If you supplied an `HttpClient`, its lifetime remains yours.

Use the Listener package for the message loop and Hosting for provisioning
through a runner provider. See the [authenticated quickstart](https://github.com/knightfall/scaleset.NET/blob/main/docs/usage.md)
and [client behavior](https://github.com/knightfall/scaleset.NET/blob/main/docs/implementation/CLIENT.md).

Licensed under [Apache-2.0](https://github.com/knightfall/scaleset.NET/blob/main/LICENSE).
