import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location(
    "verify_attestations",
    Path(__file__).resolve().parents[1] / "verify-attestations.py",
)
ATTEST = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ATTEST)


class AttestationCorrelationTests(unittest.TestCase):
    def test_correlate_accepts_complete_subject_set_and_predicate(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            pkg = root / "Knightfall.ScaleSet.Client.0.1.0-alpha.1.nupkg"
            manifest = root / "release.json"
            sbom = root / "manifest.spdx.json"
            pkg.write_bytes(b"pkg")
            manifest.write_bytes(b"{}")
            sbom.write_bytes(b'{"spdxVersion":"SPDX-2.2"}')
            statement = {
                "predicateType": ATTEST.PROVENANCE,
                "subject": [
                    {"name": "packages/" + p.name, "digest": {"sha256": hashlib.sha256(p.read_bytes()).hexdigest()}}
                    for p in (pkg, manifest, sbom)
                ],
                "predicate": {"buildDefinition": {}},
            }
            ATTEST.correlate(statement, ATTEST.PROVENANCE, [pkg, manifest, sbom])

            sbom_statement = {
                "predicateType": ATTEST.SPDX,
                "subject": [{"name": pkg.name, "digest": {"sha256": hashlib.sha256(pkg.read_bytes()).hexdigest()}}],
                "predicate": {"spdxVersion": "SPDX-2.2"},
            }
            ATTEST.correlate(sbom_statement, ATTEST.SPDX, [pkg], {"spdxVersion": "SPDX-2.2"})

    def test_correlate_rejects_missing_or_tampered_subject(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            pkg = root / "Knightfall.ScaleSet.Client.0.1.0-alpha.1.nupkg"
            pkg.write_bytes(b"pkg")
            statement = {
                "predicateType": ATTEST.SPDX,
                "subject": [{"name": pkg.name, "digest": {"sha256": "0" * 64}}],
                "predicate": {},
            }
            with self.assertRaisesRegex(ValueError, "intended artifact bytes"):
                ATTEST.correlate(statement, ATTEST.SPDX, [pkg], {})


if __name__ == "__main__":
    unittest.main()
