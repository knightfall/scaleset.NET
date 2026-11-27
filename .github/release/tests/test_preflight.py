"""Exercise release preflight with a local native gh fixture."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


RELEASE = Path(__file__).resolve().parents[1]
POLICY = json.loads((RELEASE / "policy.json").read_text(encoding="utf-8"))
SHA = "a" * 40
GH_FIXTURE = """
import os, sys
args = sys.argv[1:]
if "/git/ref/tags/" in args[-1]:
    print(os.environ["PREFLIGHT_FIXTURE_TAG"])
elif args[-1].endswith("/actions/runs/123"):
    print(os.environ["PREFLIGHT_FIXTURE_RUN"])
elif args[-1].endswith("/actions/runs/123/jobs"):
    print(os.environ["PREFLIGHT_FIXTURE_JOBS"])
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


@unittest.skipUnless(os.name == "posix", "Requires POSIX fixture executables")
class PreflightTests(unittest.TestCase):
    def checks(self):
        return [
            {"id": index, "name": name, "app": {"slug": slug}, "status": "completed", "conclusion": "success"}
            for index, (name, slug) in enumerate(POLICY["requiredChecks"].items(), start=1)
        ]

    def preflight(self, pages, *, native_failure=False, candidate="", tag_type="commit", run_details=None, jobs=None):
        run = {"path": ".github/workflows/publish.yaml", "head_repository": {"full_name": POLICY["repository"]},
               "head_branch": "main", "head_sha": SHA, "event": "push", **(run_details or {})}
        if jobs is None:
            jobs = [{"name": "Build release candidate", "status": "completed", "conclusion": "success"}]
        with tempfile.TemporaryDirectory(prefix="scaleset-preflight-fixture-") as directory:
            gh = Path(directory) / "gh"
            gh.write_text(f"#!{sys.executable}\n{GH_FIXTURE}", encoding="utf-8")
            gh.chmod(0o700)
            return subprocess.run(
                [sys.executable, str(RELEASE / "preflight.py"), f"--candidate-run-id={candidate}"],
                env={**os.environ, "GITHUB_SHA": SHA, "PATH": directory + os.pathsep + os.environ["PATH"],
                     "PREFLIGHT_FIXTURE_CHECKS": json.dumps(pages),
                     "PREFLIGHT_FIXTURE_TAG": json.dumps({"object": {"type": tag_type, "sha": SHA}}),
                     "PREFLIGHT_FIXTURE_RUN": json.dumps(run),
                     "PREFLIGHT_FIXTURE_JOBS": json.dumps({"jobs": jobs}),
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
        self.assertNotIn("invalid JSON", result.stderr)

    def test_annotated_tag_does_not_authorize_publication(self):
        result = self.preflight([{"check_runs": self.checks()}], tag_type="tag")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("The protected lightweight release tag", result.stderr)

    def test_retry_uses_only_the_successful_build_from_the_exact_candidate_run(self):
        checks = [check for check in self.checks() if check["name"] != "Build release candidate"]
        pages = [{"check_runs": checks}]
        success = self.preflight(pages, candidate="123")
        self.assertEqual(0, success.returncode, success.stderr)

        for options, message in (
            ({"candidate": "123/456"}, "Invalid candidate_run_id"),
            ({"candidate": "123", "run_details": {"head_sha": "b" * 40}}, "on main at this exact commit"),
            ({"candidate": "123", "jobs": []}, "must have a completed successful Build release candidate job"),
        ):
            with self.subTest(options=options):
                result = self.preflight(pages, **options)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(message, result.stderr)
