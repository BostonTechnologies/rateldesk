import importlib.util
import os
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("runtime_free", Path(__file__).with_name("runtime-free-clients.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class RuntimeIsolationTests(unittest.TestCase):
    def test_windows_refuses_to_move_runtimes_outside_disposable_hosted_runner(self):
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "false", "RUNNER_ENVIRONMENT": "self-hosted"}), patch.object(module, "windows_roots") as roots:
            with self.assertRaisesRegex(RuntimeError, "disposable"):
                module.windows(None, {})
            roots.assert_not_called()

    def test_windows_restores_frameworks_when_behavior_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            shared = root / "shared"
            shared.mkdir()
            (shared / "fixture.txt").write_text("original framework")
            report = {}
            args = types.SimpleNamespace(archives=root, rid="win-x64", source_revision="a" * 40, output=root)
            def fail_behavior(*args, **kwargs):
                self.assertFalse(shared.exists())
                raise RuntimeError("behavior failed")
            with patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted"}), patch.object(module, "windows_roots", return_value=[root]), patch.object(module.subprocess, "check_output", return_value=""), patch.object(module, "run", side_effect=fail_behavior):
                with self.assertRaisesRegex(RuntimeError, "behavior failed"):
                    module.windows(args, report)
            self.assertEqual((shared / "fixture.txt").read_text(), "original framework")
            self.assertEqual([p.name for p in root.iterdir()], ["shared"])
            self.assertEqual(report["restoration"][0]["status"], "RESTORED")

    def test_windows_rejects_a_remaining_shared_runtime_and_restores(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "shared").mkdir()
            with patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted"}), patch.object(module, "windows_roots", return_value=[root]), patch.object(module.subprocess, "check_output", return_value="Microsoft.NETCore.App 10.0.13 [other-root]"), patch.object(module, "run") as behavior:
                with self.assertRaisesRegex(RuntimeError, "remains"):
                    module.windows(None, {})
                behavior.assert_not_called()
            self.assertTrue((root / "shared").is_dir())


if __name__ == "__main__":
    unittest.main()
