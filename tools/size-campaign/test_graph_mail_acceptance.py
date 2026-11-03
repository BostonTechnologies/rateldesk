import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("graph_gate", Path(__file__).with_name("graph-mail-acceptance.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ControlInputsTests(unittest.TestCase):
    def test_only_the_deliberately_deleted_client_can_be_missing_in_control(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "kept.cs").write_text("source")
            deleted = module.CLIENT + "/Generated/Model.cs"
            with patch.object(module.host, "git", return_value=("kept.cs\0" + deleted + "\0").encode()):
                module.normalize_checkout(root, 1234567890, control=True)
                self.assertEqual(int((root / "kept.cs").stat().st_mtime), 1234567890)
                with self.assertRaisesRegex(RuntimeError, "Unexpected missing"):
                    module.normalize_checkout(root, 1234567890, control=False)
            with patch.object(module.host, "git", return_value=b"unrelated.cs\0"):
                with self.assertRaisesRegex(RuntimeError, "Unexpected missing"):
                    module.normalize_checkout(root, 1234567890, control=True)


if __name__ == "__main__":
    unittest.main()
