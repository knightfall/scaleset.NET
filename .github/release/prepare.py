"""Prepare a source-bound release candidate, evidence bundle, and validated SPDX SBOM."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import shutil
import subprocess
import sys
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

from verify import sha256, verify_release


class HttpsOnlyRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, target):
        if urllib.parse.urlsplit(target).scheme != "https":
            raise ValueError(f"Non-HTTPS redirect blocked: {target}")
        return super().redirect_request(request, response, code, message, headers, target)


def detect_platform() -> str:
    system = platform.system()
    machine = platform.machine().lower()
    if system == "Darwin" and machine in ("arm64", "aarch64"):
        return "osx-arm64"
    if system == "Linux" and machine in ("x86_64", "amd64"):
        return "linux-x64"
    raise RuntimeError(f"Unsupported platform for pinned sbom-tool: {system}/{machine}")


def ensure_sbom_tool(repo_root: Path, cache_dir: Path) -> Path:
    tools = json.loads((repo_root / ".github/security/tools.json").read_text(encoding="utf-8"))
    spec = tools["sbom-tool"][detect_platform()]
    url = spec["url"]
    if urllib.parse.urlsplit(url).scheme != "https":
        raise ValueError(f"sbom-tool URL must use HTTPS: {url}")
    expected_sha = spec["sha256"].lower()
    tool_dir = cache_dir / "sbom-tool" / expected_sha
    tool_dir.mkdir(parents=True, exist_ok=True)
    name = PurePosixPath(urllib.parse.urlsplit(url).path).name
    tool_path = tool_dir / name
    if not tool_path.exists() or sha256(tool_path) != expected_sha:
        opener = urllib.request.build_opener(HttpsOnlyRedirectHandler())
        temp_path = tool_dir / f"{name}.tmp"
        with opener.open(urllib.request.Request(url), timeout=120) as response, temp_path.open("wb") as out:
            shutil.copyfileobj(response, out, length=1024 * 1024)
        actual = sha256(temp_path)
        if actual != expected_sha:
            temp_path.unlink(missing_ok=True)
            raise ValueError(f"SHA-256 mismatch for sbom-tool: expected {expected_sha}, got {actual}")
        os.replace(temp_path, tool_path)
    tool_path.chmod(0o755)
    return tool_path


def verify_sbom_dependencies(spdx_path: Path, lock_files: list[Path]) -> None:
    spdx = json.loads(spdx_path.read_text(encoding="utf-8"))
    recorded = {
        (pkg.get("name", "").lower(), pkg.get("versionInfo", ""))
        for pkg in spdx.get("packages", [])
    }
    expected = set()
    for lock_file in lock_files:
        lock = json.loads(lock_file.read_text(encoding="utf-8"))
        for deps in lock.get("dependencies", {}).values():
            for name, info in deps.items():
                if info.get("type") == "Project":
                    continue
                resolved = info.get("resolved")
                if not resolved:
                    raise ValueError(f"Missing resolved version for {name} in {lock_file}")
                expected.add((name.lower(), resolved))
    missing = sorted(expected - recorded)
    if missing:
        raise ValueError(f"SPDX manifest omitted locked NuGet dependencies: {missing}")


def verify_package_archive(path: Path, package_id: str, version: str, commit: str, repo_slug: str) -> None:
    expected_notes = f"https://github.com/{repo_slug}/releases/tag/v{version}"
    expected_repo_url = f"https://github.com/{repo_slug}"
    with zipfile.ZipFile(path) as archive:
        for entry in archive.infolist():
            entry_path = PurePosixPath(entry.filename)
            if entry_path.is_absolute() or ".." in entry_path.parts or "\\" in entry.filename:
                raise ValueError(f"Unsafe package entry '{entry.filename}' in {path}")
        nuspec_name = next(item for item in archive.namelist() if item.endswith(".nuspec"))
        metadata = ET.fromstring(archive.read(nuspec_name))
        fields = {element.tag.rsplit("}", 1)[-1]: element for element in metadata.iter()}
        if fields["id"].text != package_id or fields["version"].text != version:
            raise ValueError(f"Release package identity/version differs: {path}")
        if fields.get("releaseNotes") is None or fields["releaseNotes"].text != expected_notes:
            raise ValueError(f"Release package releaseNotes differs: {path}")
        repo = fields["repository"]
        if repo.get("commit") != commit or repo.get("url") != expected_repo_url:
            raise ValueError(f"Release package source commit/repository differs: {path}")


def prepare(repo_root: Path, output_dir: Path, cache_dir: Path, packages_dir: Path) -> None:
    if output_dir.exists():
        raise ValueError("Release output must be a new directory")

    dirty = subprocess.run(
        ["git", "status", "--porcelain"],
        cwd=repo_root,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()
    if dirty:
        raise ValueError("Release preparation requires a clean committed checkout")

    commit = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=repo_root,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()

    policy_path = repo_root / ".github/release/policy.json"
    policy = json.loads(policy_path.read_text(encoding="utf-8"))
    version = policy["version"]
    repo_slug = policy["repository"]

    (output_dir / "packages").mkdir(parents=True)
    for package_id in policy["packages"]:
        for ext in (".nupkg", ".snupkg"):
            filename = f"{package_id}.{version}{ext}"
            src_pkg = packages_dir / filename
            if not src_pkg.is_file():
                raise ValueError(f"Missing packed release artifact: {src_pkg}")
            verify_package_archive(src_pkg, package_id, version, commit, repo_slug)
            shutil.copy2(src_pkg, output_dir / "packages" / filename)

    evidence_dir = output_dir / "evidence"
    evidence_paths = [
        "global.json",
        "NuGet.Config",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        ".github/security/tools.json",
        ".github/release/policy.json",
        "docs/releases/provenance.json",
    ]
    for rel in evidence_paths:
        target = evidence_dir / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(repo_root / rel, target)

    lock_files = sorted(
        path for path in (repo_root / "src").rglob("packages.lock.json")
        if "bin" not in path.parts and "obj" not in path.parts
    )
    if not lock_files:
        raise ValueError("No packages.lock.json files found under src/")
    for lock_file in lock_files:
        rel = lock_file.relative_to(repo_root)
        target = evidence_dir / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(lock_file, target)

    sbom_tool = ensure_sbom_tool(repo_root, cache_dir)
    subprocess.run(
        [
            str(sbom_tool),
            "generate",
            "-b",
            str(output_dir),
            "-bc",
            str(repo_root),
            "-pn",
            "Knightfall.ScaleSet",
            "-pv",
            version,
            "-ps",
            "knightfall",
            "-nsb",
            f"https://github.com/{repo_slug}/sbom",
            "-mi",
            "SPDX:2.2",
            "-F",
            "false",
            "-pm",
            "true",
        ],
        cwd=repo_root,
        check=True,
    )
    subprocess.run(
        [
            str(sbom_tool),
            "validate",
            "-b",
            str(output_dir),
            "-mi",
            "SPDX:2.2",
            "-n",
            "true",
            "-o",
            str(output_dir / "sbom-validation.json"),
        ],
        cwd=repo_root,
        check=True,
    )
    verify_sbom_dependencies(output_dir / "_manifest/spdx_2.2/manifest.spdx.json", lock_files)

    files = {}
    for path in sorted(output_dir.rglob("*")):
        if path.is_symlink():
            raise ValueError("Release files cannot be symbolic links")
        if path.is_file():
            files[path.relative_to(output_dir).as_posix()] = sha256(path)
    manifest = {
        "schemaVersion": 1,
        "repository": repo_slug,
        "version": version,
        "commit": commit,
        "files": files,
    }
    manifest_path = output_dir / "release.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    verify_release(output_dir, sha256(manifest_path), commit, policy)
    print(f"Release candidate prepared with verified packages, lock evidence, and validated SPDX SBOM: {output_dir}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Prepare source-bound release candidate and SPDX SBOM.")
    parser.add_argument("--output", required=True, help="New output directory for release candidate artifacts.")
    parser.add_argument("--cache", required=True, help="External directory for caching pinned sbom-tool.")
    parser.add_argument("--packages", default="artifacts/packages", help="Directory containing packed nupkg/snupkg files.")
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parents[2]
    prepare(repo_root, Path(args.output).resolve(), Path(args.cache).resolve(), (repo_root / args.packages).resolve())
    return 0


if __name__ == "__main__":
    sys.exit(main())
