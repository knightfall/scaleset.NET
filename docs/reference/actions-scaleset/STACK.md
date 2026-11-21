# Go Reference: Technology Stack

This describes the Go reference source, pinned to upstream commit
`716908e507993947ada4273aecf2361c02e5836c` (syncing PRs `#130`–`#145` after the earlier
`e6daac702355cdb5b880b4fbdcf6d85dcd9e48e5` scan). Target design is in
[the product roadmap](../../product/ROADMAP.md). Inspection date: 2026-11-21.

## 1) Runtime Summary

| Area | Value | Evidence |
|---|---|---|
| Language/runtime | Go; module requires 1.26.3 | `go.mod:3` |
| Package/build system | Go modules; `github.com/actions/scaleset` | `go.mod` |
| Development container | Go 1.25 on Debian Bookworm | `.devcontainer/Dockerfile` |
| Target runtime | .NET 10 only, user decision | `../../product/DECISIONS.md`, D03 |

The README says Go 1.25+, which differs from `go.mod`. The local baseline used
Go 1.26.4 on macOS arm64; .NET SDKs 10.0.203 and 10.0.400 were installed.

## 2) Production Frameworks and Dependencies

| Dependency | Version | Source use |
|---|---|---|
| `golang-jwt/jwt/v5` | 5.3.1 | App JWT signing and admin-token expiry parsing |
| `google/uuid` | 1.6.0 | Session IDs and example runner identities |
| `docker/docker` | 28.5.2+incompatible | Docker example provisioning |
| `spf13/cobra` | 1.10.2 | Example CLI flags |
| `golang.org/x/net` | 0.54.0 | Test HTTP proxy support; direct manifest requirement |

Note: `hashicorp/go-retryablehttp` was removed in upstream PR `#130` (`7ac570d7`); retries and `DefaultTransport` now live directly in `common_client.go` and `http_client.go`.

`google/go-github/v79` 79.0.0 is used by the example E2E test, not by the
core scale-set HTTP client. The large indirect dependency list includes lint
and generation tools; it is not a list of runtime framework requirements.

## 3) Development Toolchain

Go `testing`, Testify 1.11.1, `httptest`, mockery 3.6.1,
golangci-lint 2.11.4, deadcode, and GitHub Actions. CI checks formatting,
generated mocks, lint, and race-enabled tests.

## 4) Key Commands

From the Go clone:

```sh
go build ./...
go test ./... -race
go tool github.com/vektra/mockery/v3
go tool github.com/golangci/golangci-lint/v2/cmd/golangci-lint run ./...
```

The executed baseline and its failure are recorded in
[verification](../../product/VERIFICATION.md). Current .NET build and test commands are in the repository README.

## 5) Environment and Config

Library configuration is passed to constructors and functional HTTP options.
The example uses CLI flags. `GITHUB_ACTIONS_FORCE_GHES` changes host detection;
Docker uses its environment-based client configuration. The live test is
gated by `E2E=true` and requires separate workflow/scale-set credentials.
No database configuration is present.

## 6) Evidence

- [Manifest](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/go.mod)
- [HTTP transport](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/common_client.go)
- [HTTP client & retries](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/http_client.go)
- [CI](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/.github/workflows/go.yaml)
- [Devcontainer](https://github.com/actions/scaleset/blob/716908e507993947ada4273aecf2361c02e5836c/.devcontainer/Dockerfile)
- [Saved scan](source-scan.txt) (initial baseline scan; see `ARCHITECTURE.md` and `STRUCTURE.md` for `#130`–`#145` additions)
