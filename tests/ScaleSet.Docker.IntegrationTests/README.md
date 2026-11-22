# ScaleSet.Docker.IntegrationTests

These opt-in xUnit v3 tests exercise ownership, restart discovery, journal
reopening, and cleanup against an accessible Docker Engine on Linux. The
fixtures use a pinned BusyBox image and synthetic JIT data. Use the
[Compose smoke workflow](../../examples/docker-compose/runner-smoke.yml) to
verify a real GitHub Actions runner instead.

From the repository root on Linux with Docker access, run:

```sh
SCALESET_DOCKER_INTEGRATION=1 dotnet test --project tests/ScaleSet.Docker.IntegrationTests/ScaleSet.Docker.IntegrationTests.csproj --configuration Release
```
