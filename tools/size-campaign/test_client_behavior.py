"""An unverified or stale archive cannot produce a native behavior PASS."""

import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location("tested_client_behavior", Path(__file__).with_name("client-behavior.py"))
behavior = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(behavior)


class CandidateIdentityTests(unittest.TestCase):
    def test_missing_manifest_preserves_failure_receipt_without_executing_clients(self):
        self.reject_manifest(None, "No such file")

    def test_other_source_manifest_is_rejected_before_any_client_execution(self):
        self.reject_manifest({"sourceRevision": "b" * 40}, "Archive source differs")

    def reject_manifest(self, manifest, expected):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archives = root / "archives"
            archives.mkdir()
            if manifest is not None:
                (archives / "release-manifest.json").write_text(json.dumps(manifest))
            output = root / "evidence"
            args = ["client-behavior.py", "--archives", str(archives), "--rid", "linux-x64",
                    "--source-revision", "a" * 40, "--output", str(output)]
            with mock.patch.object(sys, "argv", args), mock.patch.object(behavior, "cli_cases") as cli, \
                    mock.patch.object(behavior.batch, "native_rid", return_value="linux-x64"), mock.patch("sys.stdout", new_callable=io.StringIO):
                self.assertEqual(1, behavior.main())
                cli.assert_not_called()
            receipt = json.loads((output / "report.json").read_text())
            self.assertEqual("FAIL", receipt["status"])
            self.assertIn(expected, receipt["error"])
