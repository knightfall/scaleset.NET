"""Exercise publish ordering and retries without calling the registries or GitHub."""

import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile


RELEASE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(RELEASE))
from publish import publish


POLICY = json.loads((RELEASE / "policy.json").read_text(encoding="utf-8"))
PACKAGE_ID = POLICY["packages"][0]
POLICY["packages"] = [PACKAGE_ID]
PACKAGE = f"{PACKAGE_ID}.{POLICY['version']}.nupkg"
SYMBOLS = f"{PACKAGE_ID}.{POLICY['version']}.snupkg"
NUGET = "https://api.nuget.org/v3/index.json"
GITHUB = "https://nuget.pkg.github.com/knightfall/index.json"


class PublishTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        directory = Path(temporary.name)
        self.root = directory / "release"
        self.inputs = {
            "release.json": b"candidate manifest",
            f"packages/{PACKAGE}": b"package payload",
            f"packages/{SYMBOLS}": b"symbol payload",
            "_manifest/spdx_2.2/manifest.spdx.json": b"build SBOM",
            "evidence/.github/release/policy.json": b"hidden evidence",
        }
        for name, content in self.inputs.items():
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
        self.bundles = {"PROVENANCE_BUNDLE": b"provenance", "BUILD_SBOM_BUNDLE": b"sbom attestation"}
        paths = {}
        for variable, content in self.bundles.items():
            path = directory / variable
            path.write_bytes(content)
            paths[variable] = str(path)
        self.env = {"RUNNER_TEMP": str(directory), "NUGET_API_KEY": "nuget-fixture", "GH_TOKEN": "github-fixture",
                    "NUGET_SYMBOL_API_KEY": "original-symbol-key", **paths}
        self.events = []
        self.assets = {}
        self.draft_exists = False
        self.existing = {"NuGet": [], "GitHub": []}
        self.fail_push = None
        self.fail_asset = None
        self.registry_conflict = False

    def check_registries(self, root, policy):
        self.assertEqual(self.root, root)
        self.assertEqual(POLICY, policy)
        self.events.append(("check_registries",))
        if self.registry_conflict:
            raise ValueError("Existing package conflicts with candidate")
        return {name: list(ids) for name, ids in self.existing.items()}

    def run_cli(self, args, *, check=False, env=None, **_):
        status, output = 0, None
        if args[0] == "gh":
            self.assertEqual(("--repo", POLICY["repository"]), tuple(args[-2:]))
            self.assertEqual(["gh", "release"], args[:2])
            self.assertEqual(f"v{POLICY['version']}", args[3])
            command = args[2]
            if command == "view":
                status = 0 if self.draft_exists else 1
                if "--json" in args:
                    self.assertTrue(check)
                    output = json.dumps({"assets": [{"name": name} for name in self.assets]})
            elif command == "download":
                self.assertTrue(check)
                name = args[args.index("--pattern") + 1]
                destination = Path(args[args.index("--dir") + 1]) / name
                destination.write_bytes(self.assets[name])
                self.events.append(("download", name))
            elif command == "create":
                self.assertTrue(check)
                self.assertTrue({"--draft", "--prerelease", "--verify-tag"}.issubset(args))
                self.events.append(("create",))
                self.draft_exists = True
            elif command == "upload":
                self.assertTrue(check)
                path = args[4]
                self.assertNotIn(path.name, self.assets)
                self.events.append(("upload", path.name))
                if path.name == self.fail_asset:
                    status = 1
                else:
                    self.assets[path.name] = path.read_bytes()
            else:
                self.fail(f"unexpected GitHub command: {command}")
        else:
            self.assertTrue(check)
            self.assertEqual(["dotnet", "nuget", "push"], args[:3])
            self.assertEqual("--source", args[4])
            path = args[3]
            source = args[5]
            options = tuple(args[6:])
            self.assertIn(source, (NUGET, GITHUB))
            self.assertEqual(self.inputs[f"packages/{path.name}"], path.read_bytes())
            symbol_push = path.name == SYMBOLS
            key = env["NUGET_SYMBOL_API_KEY" if symbol_push else "NUGET_API_KEY"]
            self.assertEqual("github-fixture" if source == GITHUB else "nuget-fixture", key)
            self.assertEqual("nuget-fixture", os.environ["NUGET_API_KEY"])
            self.assertEqual("original-symbol-key", os.environ["NUGET_SYMBOL_API_KEY"])
            self.events.append(("push", source, path.name, options))
            if (source, path.name) == self.fail_push:
                status = 1
            elif not symbol_push:
                registry = "NuGet" if source == NUGET else "GitHub"
                if PACKAGE_ID not in self.existing[registry]:
                    self.existing[registry].append(PACKAGE_ID)

        result = subprocess.CompletedProcess(args, status, stdout=output)
        if check:
            result.check_returncode()
        return result

    def run_publish(self):
        with mock.patch.dict(os.environ, self.env), mock.patch("publish.check_existing", self.check_registries), \
             mock.patch("subprocess.run", side_effect=self.run_cli):
            publish(self.root, POLICY)

    def test_retry_uses_only_verified_duplicates_and_an_exact_symbol_receipt(self):
        self.fail_push = (GITHUB, PACKAGE)
        with self.assertRaisesRegex(RuntimeError, "GitHub Packages publication failed"):
            self.run_publish()
        self.assertEqual([
            ("check_registries",), ("create",), ("upload", "release.json"),
            ("push", NUGET, PACKAGE, ("--no-symbols",)), ("push", NUGET, SYMBOLS, ()),
            ("upload", SYMBOLS), ("push", GITHUB, PACKAGE, ("--no-symbols",)),
        ], self.events)
        self.assertEqual({"release.json", SYMBOLS}, set(self.assets))

        self.events.clear()
        self.fail_push = None
        self.run_publish()
        self.assertEqual([
            ("download", "release.json"), ("download", SYMBOLS), ("check_registries",), ("download", "release.json"),
            ("push", NUGET, PACKAGE, ("--skip-duplicate", "--no-symbols")),
            ("push", GITHUB, PACKAGE, ("--no-symbols",)),
        ], self.events[:6])
        self.assertEqual(self.inputs[f"packages/{SYMBOLS}"], self.assets[SYMBOLS])
        with zipfile.ZipFile(io.BytesIO(self.assets["release-evidence.zip"])) as archive:
            self.assertEqual(self.inputs, {name: archive.read(name) for name in archive.namelist()})
        for name, variable in (("provenance", "PROVENANCE_BUNDLE"), ("build-sbom", "BUILD_SBOM_BUNDLE")):
            bundle = self.bundles[variable]
            digest = hashlib.sha256(bundle).hexdigest()
            self.assertEqual(bundle, self.assets[f"{name}-{digest}.sigstore.json"])

        self.events.clear()
        self.run_publish()
        self.assertIn(("push", GITHUB, PACKAGE, ("--skip-duplicate", "--no-symbols")), self.events)
        self.assertFalse(any(event[0] == "upload" or (event[0] == "push" and event[2] == SYMBOLS) for event in self.events))

    def test_unverified_symbol_duplicate_or_failed_receipt_stops_publish(self):
        for reason in ("symbol publication failed", "Cannot attach release evidence"):
            with self.subTest(reason=reason):
                self.events.clear()
                self.fail_push = (NUGET, SYMBOLS) if reason == "symbol publication failed" else None
                self.fail_asset = SYMBOLS if self.fail_push is None else None
                with self.assertRaisesRegex(RuntimeError, reason):
                    self.run_publish()
                self.assertIn(("push", NUGET, SYMBOLS, ()), self.events)
                self.assertNotIn(SYMBOLS, self.assets)
                self.assertFalse(any(event[:2] == ("push", GITHUB) for event in self.events))

    def test_symbol_receipt_without_the_exact_manifest_and_payload_is_rejected(self):
        self.draft_exists = True
        for manifest, symbols, reason in (
            (None, self.inputs[f"packages/{SYMBOLS}"], "symbol assets without a candidate manifest"),
            (b"other candidate", self.inputs[f"packages/{SYMBOLS}"], "differs from the candidate: release.json"),
            (self.inputs["release.json"], b"other symbols", "differs from the candidate: .*snupkg"),
        ):
            with self.subTest(reason=reason):
                self.events.clear()
                self.assets = {SYMBOLS: symbols}
                if manifest is not None:
                    self.assets["release.json"] = manifest
                with self.assertRaisesRegex(RuntimeError, reason):
                    self.run_publish()
                self.assertTrue(all(event[0] == "download" for event in self.events))

    def test_registry_conflict_stops_publish_before_the_first_mutation(self):
        self.registry_conflict = True
        with self.assertRaisesRegex(ValueError, "Existing package conflicts"):
            self.run_publish()
        self.assertEqual([("check_registries",)], self.events)
