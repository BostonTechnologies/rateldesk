#!/usr/bin/env python3
"""Validate exported source references and retained offline evidence without networking."""

import importlib.util
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
REVISION = "0123456789abcdef0123456789abcdef01234567"
SOURCE_URL = "https://github.com/BostonTechnologies/RatelDesk"
SPEC = importlib.util.spec_from_file_location("package_documentation", ROOT / "tools/release/package-documentation.py")
packager = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(packager)


class SourceLinkExportTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="source links with spaces ")
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.repository = self.directory / "repository"
        self.bundle = self.directory / "bundle"
        for parent in (self.repository, self.bundle):
            (parent / "docs").mkdir(parents=True)

    def write_source(self, relative, content=b"source\n"):
        path = self.repository / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    def export(self, text):
        self.write_source("docs/README.md", text.encode("utf-8"))
        output = self.bundle / "docs/README.md"
        output.write_bytes(text.encode("utf-8"))
        count = packager.export_source_links(output, self.repository, self.bundle, REVISION)
        return count, output.read_bytes().decode("utf-8")

    def test_exact_revision_file_directory_reference_and_html_links(self):
        for path in ("src/Handler.cs", "src/With Space.cs", "tools/ci/example.py", "playwright.admin.config.ts", "Directory.Build.props"):
            self.write_source(path)
        (self.repository / "src/Area").mkdir()
        text = ('[code](../src/Handler.cs#L2 "Code")\r\n'
                '[folder](../src/Area/) and [space](<../src/With%20Space.cs>)\r\n'
                '[ref]: ../tools/ci/example.py "Fixture"\r\n'
                '<a href="../src/Handler.cs?plain=1#L2">source</a>\r\n'
                '[config](../playwright.admin.config.ts) [build](../Directory.Build.props)\r\n')
        expected = text
        for old, route, path in (("../src/Handler.cs", "blob", "src/Handler.cs"),
                                 ("../src/Area/", "tree", "src/Area"),
                                 ("../src/With%20Space.cs", "blob", "src/With%20Space.cs"),
                                 ("../tools/ci/example.py", "blob", "tools/ci/example.py"),
                                 ("../playwright.admin.config.ts", "blob", "playwright.admin.config.ts"),
                                 ("../Directory.Build.props", "blob", "Directory.Build.props")):
            expected = expected.replace(old, f"{SOURCE_URL}/{route}/{REVISION}/{path}")
        count, actual = self.export(text)
        self.assertEqual(7, count)
        self.assertEqual(expected, actual)
        self.assertEqual(text.encode("utf-8"), (self.repository / "docs/README.md").read_bytes())

    def test_local_assets_examples_and_external_links_are_not_rewritten(self):
        self.write_source("src/Handler.cs")
        self.write_source("src/diagram.png", b"historical image")
        self.write_source("src/kept.cs")
        (self.bundle / "src").mkdir()
        (self.bundle / "src/kept.cs").write_text("already bundled\n")
        text = ('![diagram](../src/diagram.png)\n'
                '[image]: ../src/diagram.png\n'
                '<img src="../src/diagram.png"><script src="../src/Handler.cs"></script>\n'
                '[local](../src/kept.cs) [external](https://example.test/source) [route](/api/source)\n'
                '````markdown\n[example](../src/Handler.cs)\n```\n[still an example](../src/Handler.cs)\n````\n'
                '~~~markdown\n[example](../src/Handler.cs)\n~~~\n')
        count, actual = self.export(text)
        self.assertEqual(0, count)
        self.assertEqual(text, actual)

    def test_missing_source_documentation_and_images_still_fail_strict_validation(self):
        self.write_source("src/diagram.png", b"must remain offline")
        self.write_source("docs/required.md")
        text = ('# Missing targets\n\n[missing](../src/Absent.cs)\n'
                '[required guide](required.md)\n![required image](../src/diagram.png)\n')
        count, actual = self.export(text)
        self.assertEqual((0, text), (count, actual))
        (self.bundle / "AGENTS.md").write_text("# Fixture\n")
        result = subprocess.run([sys.executable, ROOT / "tools/ci/validate-docs.py", "--root", self.bundle],
                                capture_output=True, text=True, timeout=30)
        self.assertEqual(1, result.returncode)
        for target in ("../src/Absent.cs", "required.md", "../src/diagram.png"):
            self.assertIn("local target " + target, result.stderr)

    def test_invalid_revision_is_rejected_before_creating_a_bundle(self):
        for revision in ("main", "1234567", "../main", "a" * 39, "A" * 40):
            with self.subTest(revision=revision):
                output = self.directory / "not-created"
                result = subprocess.run([sys.executable, ROOT / "tools/release/package-documentation.py",
                                         output, "--source-revision", revision],
                                        capture_output=True, text=True, timeout=30)
                self.assertEqual(2, result.returncode)
                self.assertIn("full lowercase Git commit SHA", result.stderr)
                self.assertFalse(output.exists())

    @unittest.skipUnless(shutil.which("bash"), "The archive harness requires Bash")
    def test_archive_docs_flag_is_explicit_and_unknown_arguments_are_rejected(self):
        script = ROOT / "tools/release/test-executable-archives.sh"
        missing = str(self.directory / "missing archives")
        for arguments, expected in (([], 64), ([missing], 64), ([missing, "linux-x64"], 1),
                                    ([missing, "linux-x64", "--validate-docs"], 1),
                                    ([missing, "linux-x64", "--unknown"], 64),
                                    ([missing, "linux-x64", "--validate-docs", "extra"], 64)):
            with self.subTest(arguments=arguments):
                result = subprocess.run(["bash", str(script), *arguments], capture_output=True,
                                        text=True, timeout=15)
                self.assertEqual(expected, result.returncode)
                self.assertEqual("", result.stdout)
                self.assertIn("usage:" if expected == 64 else "Asset directory does not exist", result.stderr)

    def test_full_client_and_deployment_bundles_pass_with_immutable_evidence(self):
        for flat in (False, True):
            with self.subTest(flat_deployment=flat):
                output = self.directory / ("deployment" if flat else "client")
                if flat:
                    shutil.copytree(ROOT / "docker", output)
                    shutil.copytree(ROOT / "tools/mcp", output, dirs_exist_ok=True,
                                    ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
                else:
                    output.mkdir()
                shutil.copy2(ROOT / "LICENSE", output / "LICENSE")
                command = [sys.executable, ROOT / "tools/release/package-documentation.py", output,
                           "--source-revision", REVISION]
                if flat:
                    command.append("--flat-deployment")
                packaged = subprocess.run(command, capture_output=True, text=True, timeout=30)
                self.assertEqual(0, packaged.returncode, packaged.stdout + packaged.stderr)
                validated = subprocess.run([sys.executable, ROOT / "tools/ci/validate-docs.py", "--root", output],
                                           capture_output=True, text=True, timeout=30)
                self.assertEqual(0, validated.returncode, validated.stdout + validated.stderr)
                self.assertIn("documentation validation passed", validated.stdout)
                changed_markdown = []
                source_files = {p.relative_to(ROOT / "docs") for p in (ROOT / "docs").rglob("*") if p.is_file()}
                bundled_files = {p.relative_to(output / "docs") for p in (output / "docs").rglob("*") if p.is_file()}
                self.assertEqual(source_files, bundled_files)
                for relative in source_files:
                    source, copied = ROOT / "docs" / relative, output / "docs" / relative
                    if source.suffix != ".md":
                        # Historical JSON, images, fixtures/checksums and logs retain exact bytes.
                        self.assertEqual(source.read_bytes(), copied.read_bytes(), str(relative))
                    elif f"{SOURCE_URL}/blob/{REVISION}/" in copied.read_text() or f"{SOURCE_URL}/tree/{REVISION}/" in copied.read_text():
                        changed_markdown.append(relative)
                self.assertTrue(changed_markdown)
                for tree in (".agents/skills", "licenses"):
                    for source in (ROOT / tree).rglob("*"):
                        if source.is_file():
                            self.assertEqual(source.read_bytes(), (output / source.relative_to(ROOT)).read_bytes())
                for relative in ("docs/mcp-http/setup.md", "docs/integrations/cli-stdio.md"):
                    self.assertTrue((output / relative).is_file())
                self.assertTrue((output / "setup-http-mcp.sh").is_file())


if __name__ == "__main__":
    unittest.main(verbosity=2)
