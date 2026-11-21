# Publishing 0.1.0-alpha.1

The [Package release workflow](../../.github/workflows/publish.yaml) builds and
checks a read-only candidate on pull requests and pushes to `main`.
Publication starts with a manual run on `main` with `publish` enabled.

## Configure GitHub and NuGet

1. Create the `production` environment in the canonical repository. Restrict
   deployments to the `main` branch and configure required release reviewers
   before dispatching a publish run. GitHub can create a referenced environment
   automatically without protection rules, so verify the configured rules in
   repository settings. [GitHub environment protection rules](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments)
2. Set up a tag ruleset for `v*` that restricts tag creation to authorized
   release maintainers and protects tags from updates and deletion.
3. In NuGet.org Trusted Publishing, configure owner `knightfall`, repository
   `scaleset.NET`, workflow filename `publish.yaml`, and environment
   `production`. Scope the policy to the four IDs in
   [the release policy](../../.github/release/policy.json) and allow creating
   new packages for the first publish. Confirm ownership or availability of
   all four IDs. [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
4. Confirm that the repository's Actions token can write its GitHub Packages
   and read them in the consumer job. For existing packages, check their
   repository linkage and Actions access. [GitHub NuGet registry permissions](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)

## Publish from main

1. Select the current `main` commit after the release notes, package metadata,
   and [release policy](../../.github/release/policy.json) match
   `0.1.0-alpha.1`. Confirm the required checks in that policy succeeded on
   this commit, including the candidate build. Create and push the protected
   lightweight tag `v0.1.0-alpha.1` at that exact commit.
2. Run **Package release** on `main` with `publish` enabled and
   `candidate_run_id` empty. Record the run ID and wait for the candidate
   build. Check the commit and candidate before approving the `production`
   deployment.
3. The publish job verifies the tag, checks, and candidate digests, signs
   provenance and the SPDX 2.2 build dependency SBOM, and verifies both
   bundles. It publishes four packages and their symbols to NuGet.org, uploads
   the four packages to GitHub Packages, and attaches evidence to a draft
   GitHub prerelease.
4. Wait for both external consumer jobs to restore and run against their
   registry. Both jobs compare package payloads with the candidate, and the
   NuGet.org job also verifies repository signatures. The announcement job
   publishes the draft after both consumers pass.

## Recover a partial publish

Rerun failed jobs in the original run when possible. To dispatch a new run,
keep the tagged commit at the head of `main`, enable `publish`, and set
`candidate_run_id` to the run that built the original artifact. The workflow
checks the build job in that run and requires the original artifact to be
available. Candidate artifacts have a 30-day retention period.

Existing package versions are checked against the original candidate before
duplicate pushes are accepted. Existing release assets must match byte for
byte. Preserve the original candidate and draft release during recovery. If
the candidate is unavailable or the existing package payloads conflict, stop
and investigate before choosing a new version.

## Verify release artifacts

Download `release.json`, the original `.nupkg` and `.snupkg` files,
`build.spdx.json`, the `*.sigstore.json` bundles, and
`release-evidence.zip` from the GitHub prerelease. The manifest lists the
source commit and the SHA-256 digests of the candidate files. The evidence
ZIP preserves that layout, including the SBOM generation and validation inputs.

With the release tag fetched in a clone of the canonical repository, verify a
package against the release's provenance bundle. Set `PROVENANCE_BUNDLE` to
the downloaded `provenance-*.sigstore.json` file and run this command beside
the downloaded package.

```sh
gh attestation verify Knightfall.ScaleSet.Client.0.1.0-alpha.1.nupkg \
  --bundle "$PROVENANCE_BUNDLE" \
  --repo knightfall/scaleset.NET \
  --signer-workflow knightfall/scaleset.NET/.github/workflows/publish.yaml \
  --source-ref refs/heads/main \
  --source-digest "$(git rev-parse 'refs/tags/v0.1.0-alpha.1^{commit}')" \
  --deny-self-hosted-runners \
  --predicate-type https://slsa.dev/provenance/v1
```

For the matching SBOM attestation, use the `build-sbom-*.sigstore.json` bundle
and predicate type `https://spdx.dev/Document/v2.2`. Verify attestations against
the original GitHub release artifacts. NuGet.org adds a repository signature
to package archives. Verify the registry copy with
`dotnet nuget verify Knightfall.ScaleSet.Client.0.1.0-alpha.1.nupkg --all`.
