"""Verify release data without loading or building candidate code."""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import sys
import xml.etree.ElementTree as ET
import zipfile


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def verify_release(root, manifest_hash, commit, policy):
    root = Path(root).resolve(strict=True)
    require(sha256(root / "release.json") == manifest_hash, "Release manifest digest differs")
    manifest = json.loads((root / "release.json").read_text(encoding="utf-8"))
    require(manifest["schemaVersion"] == 1, "Unknown release manifest version")
    require(manifest["repository"] == policy["repository"], "Release repository differs")
    require(manifest["version"] == policy["version"] and manifest["commit"] == commit, "Release version/commit differs")
    files = manifest["files"]
    expected = {"release.json", *files}
    actual = set()
    for path in root.rglob("*"):
        require(not path.is_symlink(), "Symbolic links are not release artifacts")
        if path.is_file():
            actual.add(path.relative_to(root).as_posix())
    require(actual == expected, "Release contains missing or unmanifested files")
    for name, digest in files.items():
        path = PurePosixPath(name)
        require(
            not path.is_absolute() and ".." not in path.parts and "\\" not in name and ":" not in name,
            "Unsafe artifact path",
        )
        require((root / name).resolve().is_relative_to(root), "Artifact escapes release directory")
        require(sha256(root / name) == digest, "Artifact digest differs: " + name)
    packages = {
        f'packages/{name}.{policy["version"]}{extension}'
        for name in policy["packages"]
        for extension in (".nupkg", ".snupkg")
    }
    require({name for name in files if name.startswith("packages/")} == packages, "Release package set differs")
    for package in packages:
        name = package.rsplit("/", 1)[1].removesuffix(".snupkg").removesuffix(".nupkg")
        package_id = name.removesuffix("." + policy["version"])
        with zipfile.ZipFile(root / package) as archive:
            for entry in archive.infolist():
                entry_path = PurePosixPath(entry.filename)
                require(
                    not entry_path.is_absolute() and ".." not in entry_path.parts and "\\" not in entry.filename,
                    "Unsafe package entry: " + package,
                )
            metadata = ET.fromstring(archive.read(next(item for item in archive.namelist() if item.endswith(".nuspec"))))
            fields = {element.tag.rsplit("}", 1)[-1]: element for element in metadata.iter()}
            require(
                fields["id"].text == package_id and fields["version"].text == policy["version"],
                "Package identity differs",
            )
            require(fields["repository"].get("commit") == commit, "Package source commit differs")
            require(
                fields["repository"].get("url") == "https://github.com/" + policy["repository"],
                "Package repository differs",
            )
            # SDK symbol packages retain their nuspec/source identity, but omit package content files.
            if package.endswith(".nupkg"):
                require("security/provenance.json" in archive.namelist(), "Package verification locator is missing")
                require("licenses/LICENSE" in archive.namelist(), "Package license file is missing")
                require("README.md" in archive.namelist(), "Package README is missing")
                require("THIRD-PARTY-NOTICES.md" in archive.namelist(), "Package third-party notices are missing")
                locator = json.loads(archive.read("security/provenance.json"))
                require(
                    locator["version"] == policy["version"]
                    and locator["workflow"] == ".github/workflows/publish.yaml"
                    and locator["sourceRef"] == "refs/heads/main"
                    and locator["repository"] == "https://github.com/" + policy["repository"],
                    "Package verification locator differs",
                )
    require("_manifest/spdx_2.2/manifest.spdx.json" in files, "Missing build SPDX SBOM")
    return manifest


def verify_context(environment):
    require(environment.get("GITHUB_REPOSITORY") == "knightfall/scaleset.NET", "Wrong release repository")
    require(environment.get("GITHUB_EVENT_NAME") == "workflow_dispatch", "Publishing requires manual dispatch")
    require(environment.get("GITHUB_REF") == "refs/heads/main", "Publishing requires main")
    require(environment.get("RELEASE_PUBLISH") == "true", "Publishing was not selected")


if __name__ == "__main__":
    verify_context(os.environ)
    verify_release(
        sys.argv[1],
        os.environ["RELEASE_MANIFEST_SHA256"],
        os.environ["GITHUB_SHA"],
        json.loads(Path(__file__).with_name("policy.json").read_text(encoding="utf-8")),
    )
    print("Verified release context, complete artifact set, SBOM, and source-bound packages.")
