"""Exercise PowerShell preflight with a local native gh fixture."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


RELEASE = Path(__file__).resolve().parents[1]
POLICY = json.loads((RELEASE / "policy.json").read_text(encoding="utf-8"))
PWSH = shutil.which("pwsh")
SHA = "a" * 40
GH_FIXTURE = """
import json, os, sys
args = sys.argv[1:]
if "/git/ref/tags/" in args[-1]:
    print(json.dumps({"object": {"type": "commit", "sha": os.environ["GITHUB_SHA"]}}))
elif "/check-runs?" in args[-1]:
    assert args[:3] == ["api", "--paginate", "--slurp"], args
    assert "filter=all&per_page=100" in args[-1]
    if os.environ["PREFLIGHT_FIXTURE_FAILURE"] == "1":
        print("{incomplete native failure")
        sys.exit(3)
    print(os.environ["PREFLIGHT_FIXTURE_CHECKS"])
else:
    raise AssertionError(args)
"""


@unittest.skipUnless(PWSH and os.name == "posix", "Requires PowerShell and POSIX fixture executables")
class PreflightTests(unittest.TestCase):
    def checks(self):
        return [
            {"id": index, "name": name, "app": {"slug": slug}, "status": "completed", "conclusion": "success"}
            for index, (name, slug) in enumerate(POLICY["requiredChecks"].items(), start=1)
        ]

    def preflight(self, pages, native_failure=False):
        with tempfile.TemporaryDirectory(prefix="scaleset-preflight-fixture-") as directory:
            gh = Path(directory) / "gh"
            gh.write_text(f"#!{sys.executable}\n{GH_FIXTURE}", encoding="utf-8")
            gh.chmod(0o700)
            return subprocess.run(
                [PWSH, "-NoProfile", "-NonInteractive", "-File", str(RELEASE / "preflight.ps1")],
                env={**os.environ, "GITHUB_SHA": SHA, "PATH": directory + os.pathsep + os.environ["PATH"],
                     "PREFLIGHT_FIXTURE_CHECKS": json.dumps(pages),
                     "PREFLIGHT_FIXTURE_FAILURE": "1" if native_failure else "0"},
                capture_output=True, text=True, timeout=15,
            )

    def test_required_check_can_appear_only_on_the_second_page(self):
        checks = self.checks()
        result = self.preflight([{"check_runs": checks[:-1]}, {"check_runs": checks[-1:]}])
        self.assertEqual(0, result.returncode, result.stderr)

    def test_newer_failed_rerun_overrides_success_from_prior_page(self):
        checks = self.checks()
        failure = {**checks[-1], "id": 500, "conclusion": "failure"}
        result = self.preflight([{"check_runs": checks}, {"check_runs": [failure]}])
        self.assertNotEqual(0, result.returncode)
        self.assertIn(f"Required source verification is not successful: {failure['name']}", result.stderr)

    def test_empty_results_report_missing_required_checks(self):
        for pages in ([], [{"check_runs": []}]):
            with self.subTest(pages=pages):
                result = self.preflight(pages)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("Required source verification is not successful", result.stderr)

    def test_native_failure_takes_precedence_over_malformed_json(self):
        result = self.preflight([], native_failure=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Cannot read source verification checks", result.stderr)
        self.assertNotIn("Conversion from JSON", result.stderr)
