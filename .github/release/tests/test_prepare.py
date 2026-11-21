import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
SPEC = importlib.util.spec_from_file_location("release_prepare", Path(__file__).resolve().parents[1] / "prepare.py")
PREPARE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREPARE)


class PrepareSbomDependencyTests(unittest.TestCase):
    def test_verify_sbom_dependencies_accepts_all_non_project_lock_entries(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            lock_path = root / "packages.lock.json"
            spdx_path = root / "manifest.spdx.json"
            lock_path.write_text(
                json.dumps(
                    {
                        "version": 2,
                        "dependencies": {
                            "net10.0": {
                                "Microsoft.Extensions.Logging.Abstractions": {
                                    "type": "Direct",
                                    "resolved": "10.0.12",
                                },
                                "ScaleSet.Client": {
                                    "type": "Project",
                                    "resolved": "0.1.0-alpha.1",
                                },
                            }
                        },
                    }
                ),
                encoding="utf-8",
            )
            spdx_path.write_text(
                json.dumps(
                    {
                        "packages": [
                            {
                                "name": "Microsoft.Extensions.Logging.Abstractions",
                                "versionInfo": "10.0.12",
                            }
                        ]
                    }
                ),
                encoding="utf-8",
            )
            PREPARE.verify_sbom_dependencies(spdx_path, [lock_path])

    def test_verify_sbom_dependencies_rejects_missing_locked_package(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            lock_path = root / "packages.lock.json"
            spdx_path = root / "manifest.spdx.json"
            lock_path.write_text(
                json.dumps(
                    {
                        "version": 2,
                        "dependencies": {
                            "net10.0": {
                                "Roslynator.Analyzers": {
                                    "type": "Direct",
                                    "resolved": "5.0.0",
                                }
                            }
                        },
                    }
                ),
                encoding="utf-8",
            )
            spdx_path.write_text(json.dumps({"packages": []}), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "omitted locked NuGet dependencies"):
                PREPARE.verify_sbom_dependencies(spdx_path, [lock_path])


if __name__ == "__main__":
    unittest.main()
