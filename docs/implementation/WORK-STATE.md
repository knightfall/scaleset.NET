# Implementation Status

Updated: 2026-10-03 (Australia/Sydney). The repository is on `main` at
`6ec0142`.

## Available today

- .NET 10 GitHub Runner Scale Set client and listener libraries with GitHub App
  and PAT authentication for GitHub.com repository and organization targets.
- Provider-neutral runner ownership, capacity planning, durable lifecycle
  state, reconciliation, and drain-and-preserve shutdown.
- Docker Engine runner provider and a controller service with health endpoints,
  Linux x64/arm64 container packaging, and a Compose deployment.
- OpenTelemetry traces and metrics plus structured logs exported to a configured
  OTLP receiver.

The Docker product can run without Kubernetes. The current executable is wired
to the Docker provider; AWS resource management is not implemented yet.

## Verification

- The recorded .NET 10 Release build completed with zero warnings and errors.
- The latest recorded full native test run passed 335/335 tests, including the
  opt-in Docker integration cases. Local Docker verification used a Linux
  arm64 daemon and synthetic JIT data.
- Hosted CI passed on Linux, macOS, and Windows, including the multi-platform
  container build.
- Compose configuration validation and local OCI builds for Linux x64 and
  arm64 completed. No image or NuGet package was published.

These checks cover deterministic protocol behavior and the Docker controller.
They do not establish live GitHub or AWS compatibility. A designated GitHub
canary and AWS test environment are still required.

## AWS product direction

The target is to run the controller in the customer's AWS account, hosted by
Lambda or ECS and authorized through a scoped IAM role. Operators configure
which AWS resources it can manage. The planned runner providers are ECS tasks
and direct EC2 instances. Parameter Store and Secrets Manager are planned for
runtime settings and credentials. The controller host, IAM policy, AWS
topology, bootstrap, state, and deployment model still need detailed design.

The GitHub App or PAT remains separate from AWS IAM: GitHub credentials
authorize GitHub API operations; the workload role authorizes AWS resource
operations. No AWS resources have been inspected, deployed, or changed.

## Known limits

- GHES and enterprise GitHub targets are not implemented.
- Private-registry Docker image authentication is not implemented.
- Live GitHub workflows and AWS-backed runner provisioning have not been
  exercised.
- No package or container image has been published.
