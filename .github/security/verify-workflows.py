"""Verify pinned GitHub Actions SHAs and run pinned actionlint + zizmor."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import urllib.parse
import urllib.request

SHA_PIN_PATTERN = re.compile(r"^[^@]+@[0-9a-f]{40}$")
USES_PATTERN = re.compile(r"^\s*(?:-\s*)?uses:\s*([^\s#]+)")


class HttpsOnlyRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, target):
        if urllib.parse.urlsplit(target).scheme != "https":
            raise ValueError(f"Non-HTTPS redirect blocked: {target}")
        return super().redirect_request(request, response, code, message, headers, target)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def detect_platform() -> str:
    system = platform.system()
    machine = platform.machine().lower()
    if system == "Darwin" and machine in ("arm64", "aarch64"):
        return "osx-arm64"
    if system == "Linux" and machine in ("x86_64", "amd64"):
        return "linux-x64"
    raise RuntimeError(f"Unsupported platform for pinned security tools: {system}/{machine}")


def ensure_tool(name: str, spec: dict, cache_dir: Path) -> Path:
    url = spec["url"]
    if urllib.parse.urlsplit(url).scheme != "https":
        raise ValueError(f"Tool URL must use HTTPS: {url}")
    expected_sha = spec["sha256"].lower()
    tool_dir = cache_dir / name / expected_sha
    tool_dir.mkdir(parents=True, exist_ok=True)
    archive_entry = spec.get("archiveEntry")
    download_name = PurePosixPath(urllib.parse.urlsplit(url).path).name
    download_path = tool_dir / download_name

    if not download_path.exists() or sha256_file(download_path) != expected_sha:
        opener = urllib.request.build_opener(HttpsOnlyRedirectHandler())
        temp_path = tool_dir / f"{download_name}.tmp"
        with opener.open(urllib.request.Request(url), timeout=60) as response, temp_path.open("wb") as out:
            shutil.copyfileobj(response, out, length=1024 * 1024)
        actual_sha = sha256_file(temp_path)
        if actual_sha != expected_sha:
            temp_path.unlink(missing_ok=True)
            raise ValueError(f"SHA-256 mismatch for {name}: expected {expected_sha}, got {actual_sha}")
        os.replace(temp_path, download_path)

    if archive_entry:
        binary_path = tool_dir / archive_entry
        if not binary_path.exists():
            with tarfile.open(download_path, "r:gz") as tar:
                matches = [
                    member for member in tar.getmembers()
                    if member.isfile() and PurePosixPath(member.name).name == archive_entry
                ]
                if len(matches) != 1:
                    raise ValueError(f"Expected 1 '{archive_entry}' entry in {download_name}, found {len(matches)}")
                extracted = tar.extractfile(matches[0])
                if extracted is None:
                    raise ValueError(f"Unable to extract '{archive_entry}' from {download_name}")
                temp_bin = tool_dir / f"{archive_entry}.tmp"
                with extracted, temp_bin.open("wb") as out:
                    shutil.copyfileobj(extracted, out, length=1024 * 1024)
                os.replace(temp_bin, binary_path)
    else:
        binary_path = download_path

    binary_path.chmod(0o755)
    return binary_path


def verify_action_pins(workflows_dir: Path) -> None:
    workflows = sorted(
        [*workflows_dir.glob("*.yml"), *workflows_dir.glob("*.yaml")]
    )
    if not workflows:
        raise RuntimeError(f"No workflow files found under {workflows_dir}")
    for workflow in workflows:
        for line_number, raw_line in enumerate(workflow.read_text(encoding="utf-8").splitlines(), start=1):
            stripped = raw_line.lstrip()
            if stripped.startswith("#"):
                continue
            match = USES_PATTERN.match(raw_line)
            if not match:
                continue
            ref = match.group(1).strip("'\"")
            if ref.startswith("./"):
                continue
            if not SHA_PIN_PATTERN.fullmatch(ref):
                raise ValueError(
                    f"{workflow}:{line_number}: action reference '{ref}' must be pinned to a full 40-char commit SHA"
                )


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify workflow SHA pins and run actionlint + zizmor.")
    parser.add_argument("--cache", required=True, help="Directory outside the repo for caching verified tool binaries.")
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parents[2]
    tools = json.loads((Path(__file__).resolve().with_name("tools.json")).read_text(encoding="utf-8"))
    plat = detect_platform()
    cache_dir = Path(args.cache).resolve()

    verify_action_pins(repo_root / ".github" / "workflows")
    print("Verified 40-character SHA pins in all workflow files.")

    actionlint = ensure_tool("actionlint", tools["actionlint"][plat], cache_dir)
    zizmor = ensure_tool("zizmor", tools["zizmor"][plat], cache_dir)

    subprocess.run([str(actionlint), "-shellcheck="], cwd=repo_root, check=True)
    print("actionlint passed.")
    subprocess.run(
        [str(zizmor), "--offline", "--min-severity", "low", ".github/workflows"],
        cwd=repo_root,
        check=True,
    )
    print("zizmor passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
