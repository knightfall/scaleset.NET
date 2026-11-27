"""Verify the consumer restore and registry validation commands."""

import os
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import xml.etree.ElementTree as ET
import zipfile


RELEASE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(RELEASE))
verify_published = runpy.run_path(str(RELEASE / "verify-published.py"))["verify_published"]
PACKAGE = "Knightfall.ScaleSet.Client"
VERSION = "0.1.0-alpha.1"
POLICY = {"packages": [PACKAGE], "version": VERSION}
CREDENTIALS = "NuGetPackageSourceCredentials_release"


def write_package(path, data, *, signed=False):
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("lib/net10.0/ScaleSet.Client.dll", data)
        if signed:
            archive.writestr(".signature.p7s", b"fixture signature")


class VerifyPublishedTests(unittest.TestCase):
    def verify_with_cli_fixture(self, registry, *, changed=False, unavailable=False, invalid_signature=False):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "release & evidence"
            write_package(root / "packages" / f"{PACKAGE}.{VERSION}.nupkg", b"candidate")
            calls = []

            def run(args, *, cwd, env, check=False):
                command, *options = args[1:]
                calls.append(command if command != "restore" else options[1])
                self.assertEqual("dotnet", args[0])
                self.assertTrue((cwd / "global.json").is_file())
                config = ET.parse(cwd / "NuGet.Config")
                source = config.find("./packageSources/add[@key='release']").get("value")
                package_map = config.find("./packageSourceMapping/packageSource[@key='release']/package")
                if registry == "candidate":
                    self.assertEqual(str((root / "packages").resolve()), source)
                elif registry == "nuget":
                    self.assertEqual("https://api.nuget.org/v3/index.json", source)
                else:
                    self.assertEqual("https://nuget.pkg.github.com/knightfall/index.json", source)
                if registry == "github":
                    self.assertEqual("Username=knightfall;Password=fixture;ValidAuthenticationTypes=Basic", env[CREDENTIALS])
                else:
                    self.assertNotIn(CREDENTIALS, env)
                self.assertEqual("*" if registry == "nuget" else "Knightfall.ScaleSet.*", package_map.get("pattern"))

                status = 0
                if command == "restore" and "--no-http-cache" in options:
                    self.assertEqual(["--packages", cwd / "obj" / "packages", "--no-http-cache"], options[1:])
                    if unavailable or len(calls) == 1:
                        status = 1
                    else:
                        name = PACKAGE.lower()
                        downloaded = options[2] / name / VERSION / f"{name}.{VERSION}.nupkg"
                        write_package(downloaded, b"changed" if changed else b"candidate", signed=registry != "candidate")
                elif command == "restore":
                    self.assertEqual(["--locked-mode", "--packages", cwd / "obj" / "packages"], options[1:])
                elif command == "nuget":
                    self.assertEqual("verify", options[0])
                    self.assertEqual("--all", options[-1])
                    status = 1 if invalid_signature else 0
                else:
                    self.assertEqual(["--project", cwd / "Consumer.csproj", "--configuration", "Release", "--no-restore"], options)
                result = subprocess.CompletedProcess(args, status)
                if check:
                    result.check_returncode()
                return result

            with mock.patch.dict(os.environ, {"RUNNER_TEMP": directory, "GH_TOKEN": "fixture", CREDENTIALS: "ambient"}), \
                 mock.patch("subprocess.run", side_effect=run), mock.patch("time.sleep") as sleep:
                if unavailable:
                    with self.assertRaisesRegex(RuntimeError, "did not become available"):
                        verify_published(registry, root, POLICY)
                    self.assertEqual([mock.call(10)] * 12, sleep.call_args_list)
                elif changed or invalid_signature:
                    reason = "content differs" if changed else "signature verification failed"
                    with self.assertRaisesRegex(RuntimeError, reason):
                        verify_published(registry, root, POLICY)
                else:
                    verify_published(registry, root, POLICY)
                    sleep.assert_called_once_with(10)
                self.assertEqual("ambient", os.environ[CREDENTIALS])
            self.assertEqual([root], list(Path(directory).iterdir()))
            return calls

    def test_registry_cases_restore_with_isolated_cache_and_verify_before_execution(self):
        for registry in ("candidate", "nuget", "github"):
            with self.subTest(registry=registry):
                calls = self.verify_with_cli_fixture(registry)
                expected = ["--packages", "--packages", "--locked-mode"]
                if registry == "nuget":
                    expected.append("nuget")
                self.assertEqual([*expected, "run"], calls)

    def test_retry_is_bounded_when_package_stays_unavailable(self):
        self.assertEqual(["--packages"] * 12, self.verify_with_cli_fixture("github", unavailable=True))

    def test_changed_payload_or_invalid_signature_prevents_execution(self):
        self.assertEqual(["--packages", "--packages", "--locked-mode"], self.verify_with_cli_fixture("candidate", changed=True))
        self.assertEqual(["--packages", "--packages", "--locked-mode", "nuget"],
                         self.verify_with_cli_fixture("nuget", invalid_signature=True))
