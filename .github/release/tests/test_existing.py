import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import urllib.error
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
SPEC = importlib.util.spec_from_file_location(
    "check_existing",
    Path(__file__).resolve().parents[1] / "check_existing.py",
)
EXISTING = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(EXISTING)


class ExistingPackageCheckTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "packages").mkdir()
        self.policy = {
            "version": "0.1.0-alpha.1",
            "packages": ["Knightfall.ScaleSet.Client"],
        }
        self.pkg_path = self.root / "packages/Knightfall.ScaleSet.Client.0.1.0-alpha.1.nupkg"
        self.write_nupkg(self.pkg_path, b"candidate-bytes")

    def write_nupkg(self, path, content, with_sig=False):
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr("lib/net10.0/ScaleSet.Client.dll", content)
            if with_sig:
                archive.writestr(".signature.p7s", b"sig")

    def test_missing_or_identical_signed_packages_pass(self):
        signed_bytes = self.root / "signed.nupkg"
        self.write_nupkg(signed_bytes, b"candidate-bytes", with_sig=True)

        def fake_fetch(url, authorization, limit, allow_missing=False):
            if url == "https://api.nuget.org/v3/index.json":
                return json.dumps({"resources": [{"@id": "https://api.nuget.org/v3-flatcontainer/", "@type": "PackageBaseAddress/3.0.0"}]}).encode()
            if url == "https://nuget.pkg.github.com/knightfall/index.json":
                return json.dumps({"resources": [{"@id": "https://nuget.pkg.github.com/knightfall/download/", "@type": "PackageBaseAddress/3.0.0"}]}).encode()
            if url.startswith("https://api.nuget.org/"):
                return signed_bytes.read_bytes()
            return None

        with patch.dict(os.environ, {"GH_TOKEN": "synthetic"}), patch.object(EXISTING, "fetch", side_effect=fake_fetch):
            matched = EXISTING.check_existing(self.root, self.policy)
        self.assertEqual(matched, {"NuGet": ["Knightfall.ScaleSet.Client"], "GitHub": []})

    def test_conflicting_existing_package_raises(self):
        conflict_bytes = self.root / "conflict.nupkg"
        self.write_nupkg(conflict_bytes, b"different-bytes")

        def fake_fetch(url, authorization, limit, allow_missing=False):
            if url.endswith("/v3/index.json"):
                return json.dumps({"resources": [{"@id": "https://api.nuget.org/flat/", "@type": "PackageBaseAddress/3.0.0"}]}).encode()
            if url.endswith("/knightfall/index.json"):
                return json.dumps({"resources": [{"@id": "https://nuget.pkg.github.com/knightfall/flat/", "@type": "PackageBaseAddress/3.0.0"}]}).encode()
            if url.startswith("https://api.nuget.org/"):
                return conflict_bytes.read_bytes()
            return None

        with patch.dict(os.environ, {"GH_TOKEN": "synthetic"}), patch.object(EXISTING, "fetch", side_effect=fake_fetch):
            with self.assertRaisesRegex(ValueError, "conflicts with candidate"):
                EXISTING.check_existing(self.root, self.policy)

    def test_cross_host_redirect_strips_authorization(self):
        handler = EXISTING.RegistryRedirects()
        req = urllib.request.Request("https://nuget.pkg.github.com/pkg", headers={"Authorization": "Basic abc"})
        redirected = handler.redirect_request(
            req,
            io.BytesIO(),
            302,
            "Found",
            {},
            "https://objects.githubusercontent.com/blob",
        )
        self.assertFalse(redirected.has_header("Authorization"))


if __name__ == "__main__":
    unittest.main()
