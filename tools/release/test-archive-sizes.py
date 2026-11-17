#!/usr/bin/env python3
"""Exercise all seven production budget identities with small disposable archives."""

import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile

SCRIPT = Path(__file__).with_name("check-archive-sizes.py")
spec = importlib.util.spec_from_file_location("check_archive_sizes", SCRIPT)
budgets = importlib.util.module_from_spec(spec)
spec.loader.exec_module(budgets)
VERSION = "0.0.0-test"


class ArchiveSizeTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.directory = Path(tmp.name)
        self.policy = json.loads(budgets.BUDGETS.read_text())
        for family, rid in budgets.ARCHIVES:
            name = budgets.archive_name(family, rid, VERSION)
            self.archive(name)

    def archive(self, name, relative="docs/README.md"):
        path = self.directory / name
        root = name.removesuffix(".tar.gz").removesuffix(".zip")
        data = b"example"
        if path.suffix == ".zip":
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr(f"{root}/{relative}", data)
        else:
            with tarfile.open(path, "w:gz") as archive:
                member = tarfile.TarInfo(f"{root}/{relative}")
                member.size = len(data)
                archive.addfile(member, io.BytesIO(data))

    def run_gate(self):
        return subprocess.run([sys.executable, SCRIPT, self.directory, VERSION, "synthetic", "10.0.404"],
                              capture_output=True, text=True)

    def test_all_archives_are_measured(self):
        result = self.run_gate()
        self.assertEqual(0, result.returncode, result.stderr)
        report = json.loads((self.directory / "archive-sizes.json").read_text())
        self.assertEqual(7, len(report["archives"]))
        self.assertTrue(all(x["expandedBytesByCategory"]["documentation"] == 7 for x in report["archives"]))

    def test_excess_size_fails_with_all_measurements(self):
        self.policy["archives"][0]["maximumCompressedBytes"] = 1
        report = {"archives": [], "errors": []}
        self.assertFalse(budgets.verify(self.directory, VERSION, self.policy, report))
        self.assertEqual(7, len(report["archives"]))
        self.assertIn("exceeds 1", report["errors"][0])

    def test_unexpected_and_missing_archive_block_validation(self):
        (self.directory / budgets.archive_name("rateldesk-deployment", None, VERSION)).unlink()
        self.archive("extra.zip")
        result = self.run_gate()
        self.assertEqual(1, result.returncode)
        self.assertIn("missing", result.stderr)
        self.assertIn("extra.zip", result.stderr)

    def test_developer_collateral_is_rejected(self):
        self.archive(budgets.archive_name("rateldesk-cli", "linux-x64", VERSION), "docs/implementation/gallery.png")
        result = self.run_gate()
        self.assertEqual(1, result.returncode)
        self.assertIn("unexpected developer material", result.stderr)


if __name__ == "__main__":
    unittest.main()
