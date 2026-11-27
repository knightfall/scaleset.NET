"""Publish a verified candidate and attach the exact release evidence."""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

from check_existing import REGISTRIES, check_existing
from release_archive import create_archive
from verify import sha256


RELEASE_SCRIPTS = Path(__file__).resolve().parent


class DraftRelease:
    def __init__(self, repository, tag, scratch):
        self.repository = repository
        self.tag = tag
        self.scratch = scratch
        self.assets = set()

    def run(self, command, *args, failure, stdout=None):
        try:
            result = subprocess.run(
                ["gh", "release", command, self.tag, *args, "--repo", self.repository],
                stdout=stdout, text=True, check=True,
            )
        except subprocess.CalledProcessError as error:
            raise RuntimeError(failure) from error
        return result.stdout

    def inspect(self):
        result = subprocess.run(
            ["gh", "release", "view", self.tag, "--repo", self.repository],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        if result.returncode != 0:
            return False
        details = self.run("view", "--json", "assets", failure="Cannot inspect existing release assets", stdout=subprocess.PIPE)
        self.assets = {asset["name"].casefold() for asset in json.loads(details)["assets"]}
        return True

    def has_asset(self, name):
        return name.casefold() in self.assets

    def verify_asset(self, path):
        with tempfile.TemporaryDirectory(prefix="previous-", dir=self.scratch) as directory:
            self.run(
                "download", "--pattern", path.name, "--dir", directory,
                failure=f"Cannot verify existing release asset: {path.name}",
            )
            if sha256(Path(directory) / path.name) != sha256(path):
                raise RuntimeError(
                    f"Existing release asset differs from the candidate: {path.name}. "
                    "Rerun failed jobs or pass the original candidate_run_id."
                )

    def attach(self, path):
        if self.has_asset(path.name):
            self.verify_asset(path)
            return
        self.run("upload", path, failure=f"Cannot attach release evidence: {path.name}")
        self.assets.add(path.name.casefold())


def push(path, source, env, failure, *options):
    try:
        subprocess.run(["dotnet", "nuget", "push", path, "--source", source, *options], env=env, check=True)
    except subprocess.CalledProcessError as error:
        raise RuntimeError(failure) from error


def publish(root, policy):
    env = os.environ.copy()
    scratch = Path(env["RUNNER_TEMP"])
    version = policy["version"]
    tag = f"v{version}"
    release = DraftRelease(policy["repository"], tag, scratch)
    release_exists = release.inspect()
    manifest = root / "release.json"
    if release.has_asset(manifest.name):
        release.verify_asset(manifest)
    elif any(name.endswith(".snupkg") for name in release.assets):
        raise RuntimeError(f"Release {tag} has symbol assets without a candidate manifest.")

    confirmed_symbols = set()
    for package_id in policy["packages"]:
        symbols = root / "packages" / f"{package_id}.{version}.snupkg"
        if release.has_asset(symbols.name):
            release.verify_asset(symbols)
            confirmed_symbols.add(package_id)

    existing = check_existing(root, policy)
    if not release_exists:
        notes = RELEASE_SCRIPTS.parents[1] / "docs" / "releases" / f"{version}.md"
        notes_args = ("--notes-file", notes) if notes.is_file() else ("--generate-notes",)
        release.run(
            "create", "--verify-tag", "--draft", "--prerelease", "--title", tag,
            *notes_args, failure="Cannot create draft release",
        )
    release.attach(manifest)

    sources = dict(REGISTRIES)
    symbol_env = {**env, "NUGET_SYMBOL_API_KEY": env["NUGET_API_KEY"]}
    github_env = {**env, "NUGET_API_KEY": env["GH_TOKEN"]}
    for package_id in policy["packages"]:
        package = root / "packages" / f"{package_id}.{version}.nupkg"
        # Skip duplicates only for registry payloads already compared with this candidate.
        duplicate = ("--skip-duplicate",) if package_id in existing["NuGet"] else ()
        push(package, sources["NuGet"], env, f"NuGet publication failed: {package_id}", *duplicate, "--no-symbols")
        if package_id not in confirmed_symbols:
            symbols = package.with_suffix(".snupkg")
            push(symbols, sources["NuGet"], symbol_env, f"NuGet symbol publication failed: {package_id}")
            # The original archive records acknowledgement for this exact symbol payload.
            release.attach(symbols)
        duplicate = ("--skip-duplicate",) if package_id in existing["GitHub"] else ()
        push(package, sources["GitHub"], github_env, f"GitHub Packages publication failed: {package_id}", *duplicate, "--no-symbols")

    with tempfile.TemporaryDirectory(prefix="release-assets-", dir=scratch) as directory:
        assets = Path(directory)
        for package in (root / "packages").glob("*.nupkg"):
            shutil.copyfile(package, assets / package.name)
        shutil.copyfile(root / "_manifest" / "spdx_2.2" / "manifest.spdx.json", assets / "build.spdx.json")
        for name, variable in (("provenance", "PROVENANCE_BUNDLE"), ("build-sbom", "BUILD_SBOM_BUNDLE")):
            bundle = Path(env[variable])
            shutil.copyfile(bundle, assets / f"{name}-{sha256(bundle)}.sigstore.json")
        create_archive(root, assets / "release-evidence.zip")
        for path in sorted(assets.iterdir()):
            release.attach(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release-directory", required=True, type=Path)
    args = parser.parse_args()
    policy = json.loads((RELEASE_SCRIPTS / "policy.json").read_text(encoding="utf-8"))
    try:
        publish(args.release_directory.resolve(), policy)
    except (KeyError, OSError, ValueError, RuntimeError) as error:
        sys.exit(str(error))


if __name__ == "__main__":
    main()
