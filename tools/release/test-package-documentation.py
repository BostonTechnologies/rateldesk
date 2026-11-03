#!/usr/bin/env python3
"""Exercise the real offline bundle helpers without .NET, Docker or networking."""

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True


def hashes(directory):
    return {path.relative_to(directory).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in directory.rglob("*") if path.is_file()
            and "__pycache__" not in path.parts and path.suffix != ".pyc"}


def load_module(path):
    spec = importlib.util.spec_from_file_location("packaged_" + path.stem.replace("-", "_"), path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class PackageDocumentationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="rateldesk package with spaces ")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.bundle = self.directory / "extracted client with spaces"
        self.bundle.mkdir()
        self.working = self.directory / "unrelated working directory"
        self.working.mkdir()
        # The actual helpers must use their vendored files, not ambient modules.
        for name in ("yaml.py", "compose_toolchain.py", "source_review.py"):
            (self.working / name).write_text('raise AssertionError("ambient helper imported")\n')
        self.environment = dict(os.environ, PYTHONDONTWRITEBYTECODE="1")
        shutil.copy2(ROOT / "LICENSE", self.bundle / "LICENSE")
        result = self.run_command([sys.executable, ROOT / "tools/release/package-documentation.py", self.bundle])
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def run_command(self, command):
        return subprocess.run(command, cwd=self.working, env=self.environment,
                              capture_output=True, text=True, timeout=30)

    def test_complete_offline_documentation_skills_and_notices_are_unchanged(self):
        for relative in ("docs", ".agents/skills", "licenses", "docker"):
            with self.subTest(tree=relative):
                expected = hashes(ROOT / relative)
                self.assertTrue(expected)
                self.assertEqual(expected, hashes(self.bundle / relative))
        for name in ("LICENSE", "AGENTS.md", "CONTRIBUTING.md", "SECURITY.md", ".env.example"):
            self.assertEqual((ROOT / name).read_bytes(), (self.bundle / name).read_bytes())

    def test_one_complete_helper_tree_and_importable_forwarders(self):
        for relative, digest in hashes(ROOT / "tools/mcp").items():
            self.assertEqual(digest, hashlib.sha256((self.bundle / relative).read_bytes()).hexdigest())
        aliases = self.bundle / "tools/mcp"
        self.assertFalse((aliases / "vendor").exists())
        self.assertFalse(any(path.is_symlink() for path in self.bundle.rglob("*")))
        for name, function in (("setup-http-mcp.py", "native_executable"),
                               ("compose_toolchain.py", "digest_file"),
                               ("source_review.py", "parse_source")):
            for directory in (self.bundle, aliases):
                with self.subTest(entrypoint=str(directory / name)):
                    module = load_module(directory / name)
                    self.assertEqual(self.bundle / name, Path(module.__file__))
                    self.assertIs(getattr(module, function).__globals__, module.__dict__)

    def test_root_and_nested_entrypoints_preserve_output_and_nonzero_exits(self):
        for name in ("setup-http-mcp.py", "source_review.py"):
            for argument in ("--help", "--unsupported-package-test-option"):
                results = [self.run_command([sys.executable, directory / name, argument])
                           for directory in (self.bundle, self.bundle / "tools/mcp")]
                with self.subTest(entrypoint=name, argument=argument):
                    self.assertEqual([0, 0] if argument == "--help" else [2, 2],
                                     [result.returncode for result in results])
                    self.assertEqual(results[0].stdout, results[1].stdout)
                    self.assertEqual(results[0].stderr, results[1].stderr)
                    self.assertTrue(results[0].stdout if argument == "--help" else results[0].stderr)
        if os.name != "nt":
            for directory in (self.bundle, self.bundle / "tools/mcp"):
                shell = self.run_command([directory / "setup-http-mcp.sh", "--help"])
                python = self.run_command([sys.executable, directory / "setup-http-mcp.py", "--help"])
                self.assertEqual((python.returncode, python.stdout, python.stderr),
                                 (shell.returncode, shell.stdout, shell.stderr))

    def test_source_review_uses_real_vendored_yaml_and_preserves_unrelated_bytes(self):
        content = (b'\xef\xbb\xbf# owner-managed\r\nservices:\r\n  api: {image: synthetic-api}\r\n'
                   b'  web:\r\n    image: synthetic-web # retain\r\n    environment:\r\n'
                   b'      TIMEOUT: "20" # retain comment\r\n      UNCHANGED: "literal"\r\n')
        expected = content.replace(b'TIMEOUT: "20"', b'TIMEOUT: "21"')
        for directory in (self.bundle, self.bundle / "tools/mcp"):
            with self.subTest(entrypoint=str(directory)):
                review = load_module(directory / "source_review.py")
                self.assertEqual(self.bundle / "vendor/yaml/__init__.py", Path(review.yaml.__file__))
                self.assertEqual(expected, review.patch_source(review.parse_source(content),
                                                               {"web": {"TIMEOUT": "21"}}))
                self.assertEqual("21", review.yaml.safe_load(expected)["services"]["web"]["environment"]["TIMEOUT"])

    def test_native_discovery_and_followup_commands_use_the_root_payload(self):
        native = self.bundle / "rateldesk"
        native.write_text("synthetic executable identity; never executed\n")
        native.chmod(0o755)
        identities = []
        for directory in (self.bundle, self.bundle / "tools/mcp"):
            launcher = load_module(directory / "setup-http-mcp.py")
            args = launcher.parser().parse_args([])
            executable, identity = launcher.native_executable(args)
            self.assertEqual(str(native), executable)
            identities.append(identity)
            deployment = SimpleNamespace(args=args, output=self.working, directory=self.bundle,
                project="synthetic", manager="external-compose", intent="verify-existing",
                sources=[self.bundle / "compose.yml"], env_files=[], compose=["compose"],
                docker=SimpleNamespace(process_environment=lambda: {}, selection={}, daemon_identity="synthetic"))
            # Isolate the private-file write; exercise the actual command construction.
            with patch.object(launcher, "write_private") as write:
                environment = launcher.native_environment(deployment)
                write.assert_called_once()
            command = json.loads(environment["RATELDESK_SETUP_SOURCE_REVIEW_COMMAND"])
            self.assertEqual([sys.executable, str(self.bundle / "source_review.py"), "--context",
                              str(self.working / "source-review-context.json")], command)
            self.assertEqual(str(self.bundle / "setup-http-mcp.sh"),
                             shlex.split(launcher.resume_command(deployment, "--verify"))[0])
        self.assertEqual(identities[0], identities[1])

    def test_nested_launcher_reads_adjacent_root_release_metadata(self):
        # Malformed metadata proves discovery before any deployment/private-storage action.
        (self.bundle / "setup-release.json").write_text("{")
        docker = SimpleNamespace(run=lambda *args, **kwargs: b"")
        for directory in (self.bundle, self.bundle / "tools/mcp"):
            launcher = load_module(directory / "setup-http-mcp.py")
            with self.assertRaisesRegex(launcher.Prerequisite, "release_metadata"):
                launcher.Deployment(launcher.parser().parse_args([]), docker)

    def test_missing_root_implementation_fails_without_fallback_or_recursion(self):
        (self.bundle / "setup-http-mcp.py").unlink()
        result = self.run_command([sys.executable, self.bundle / "tools/mcp/setup-http-mcp.py", "--help"])
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("", result.stdout)
        self.assertIn("FileNotFoundError", result.stderr)

    def test_flat_deployment_helpers_and_offline_link_rewrites_are_unchanged(self):
        deployment = self.directory / "extracted deployment with spaces"
        shutil.copytree(ROOT / "docker", deployment)
        shutil.copytree(ROOT / "tools/mcp", deployment, dirs_exist_ok=True,
                        ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
        shutil.copy2(ROOT / "LICENSE", deployment / "LICENSE")
        result = self.run_command([sys.executable, ROOT / "tools/release/package-documentation.py",
                                   deployment, "--flat-deployment"])
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        for relative, digest in hashes(ROOT / "tools/mcp").items():
            self.assertEqual(digest, hashlib.sha256((deployment / relative).read_bytes()).hexdigest())
        self.assertFalse((deployment / "tools/mcp").exists())
        for source in (ROOT / "docs").rglob("*"):
            if source.is_file():
                expected = source.read_bytes()
                if source.suffix == ".md":
                    expected = source.read_text().replace("](../../docker/", "](../../").encode("utf-8")
                self.assertEqual(expected, (deployment / "docs" / source.relative_to(ROOT / "docs")).read_bytes())
        for relative in (".agents/skills", "licenses"):
            self.assertEqual(hashes(ROOT / relative), hashes(deployment / relative))


if __name__ == "__main__":
    unittest.main(verbosity=2)
