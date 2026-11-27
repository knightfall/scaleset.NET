"""Verify source checks, the protected tag, and any reused candidate run."""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys


def get_api_json(path, failure, *, paginate=False):
    options = ["--paginate", "--slurp"] if paginate else []
    try:
        result = subprocess.run(["gh", "api", *options, path], check=True, stdout=subprocess.PIPE, text=True)
    except (OSError, subprocess.CalledProcessError) as error:
        raise RuntimeError(failure) from error

    try:
        return json.loads(result.stdout)
    except ValueError as error:
        raise RuntimeError(f"{failure}: invalid JSON") from error


def verify_preflight(policy, commit, candidate_run_id):
    repository = policy["repository"]
    tag = f"v{policy['version']}"
    tag_failure = "The protected lightweight release tag must identify this exact source commit"
    reference = get_api_json(f"repos/{repository}/git/ref/tags/{tag}", tag_failure)["object"]
    if reference["type"] != "commit" or reference["sha"] != commit:
        raise RuntimeError(tag_failure)

    if candidate_run_id:
        if re.fullmatch(r"[0-9]+", candidate_run_id) is None:
            raise RuntimeError("Invalid candidate_run_id")
        run_path = f"repos/{repository}/actions/runs/{candidate_run_id}"
        run = get_api_json(run_path, "Cannot read reused candidate run")
        if (
            run["path"] != ".github/workflows/publish.yaml"
            or run["head_repository"]["full_name"] != repository
            or run["head_branch"] != "main"
            or run["head_sha"] != commit
            or run["event"] not in ("push", "workflow_dispatch")
        ):
            raise RuntimeError("Reused candidate run must be from publish.yaml on main at this exact commit")
        jobs = get_api_json(f"{run_path}/jobs", "Cannot read reused candidate jobs")["jobs"]
        build = next((job for job in jobs if job["name"] == "Build release candidate"), None)
        if build is None or build["status"] != "completed" or build["conclusion"] != "success":
            raise RuntimeError("Reused candidate run must have a completed successful Build release candidate job")

    pages = get_api_json(
        f"repos/{repository}/commits/{commit}/check-runs?filter=all&per_page=100",
        "Cannot read source verification checks",
        paginate=True,
    )
    checks = [check for page in pages for check in page["check_runs"]]
    for name, app in policy["requiredChecks"].items():
        # A retry uses the completed build from the selected run instead of its own skipped build.
        if candidate_run_id and name == "Build release candidate":
            continue
        matching = (check for check in checks if check["name"] == name and check["app"]["slug"] == app)
        latest = max(matching, key=lambda check: int(check["id"]), default=None)
        if latest is None or latest["status"] != "completed" or latest["conclusion"] != "success":
            raise RuntimeError(f"Required source verification is not successful: {name}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate-run-id", default="")
    args = parser.parse_args()
    policy = json.loads(Path(__file__).with_name("policy.json").read_text(encoding="utf-8"))
    try:
        verify_preflight(policy, os.environ["GITHUB_SHA"], args.candidate_run_id)
    except (KeyError, TypeError, ValueError, RuntimeError) as error:
        sys.exit(str(error))


if __name__ == "__main__":
    main()
