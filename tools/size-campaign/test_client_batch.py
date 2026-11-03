"""Boundaries that must fail closed before native candidate acceptance can pass."""

import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock
import zipfile


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    value = importlib.util.module_from_spec(spec)
    sys.modules[name] = value
    spec.loader.exec_module(value)
    return value


batch = module("tested_client_batch", "client-batch.py")
discovery = module("tested_client_discovery", "client-discovery.py")


class ArchiveShellTests(unittest.TestCase):
    def test_exact_native_shell_path_survives_spaces_and_keeps_all_archive_gates(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            shell = root / "Git Bash/bin/bash.exe"
            shell.parent.mkdir(parents=True)
            shell.write_bytes(b"synthetic executable path fixture")
            archives = root / "measured archives"
            command = batch.archive_smoke_command(shell, archives, "win-x64")
            self.assertEqual(str(shell), command[0])
            self.assertEqual(["tools/release/test-executable-archives.sh", archives.as_posix(),
                              "win-x64", "--validate-docs"], command[1:])

    def test_bare_or_missing_shell_cannot_fall_back_to_a_different_path_executable(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            for shell in (Path("bash"), root / "missing-bash.exe"):
                with self.subTest(shell=shell), self.assertRaisesRegex(batch.BatchFailure, "absolute Bash"):
                    batch.archive_smoke_command(shell, root / "archives", "win-x64")

    def test_native_command_dispatches_with_explicit_shell_and_preserves_failure_exit(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            shell = root / "Git Bash/bash.exe"
            arguments = ["client-batch.py", "run", "--output", str(root / "out"),
                         "--rid", "win-x64", "--sdk", "10.0.403", "--bash", str(shell)]
            with mock.patch.object(sys, "argv", arguments), mock.patch.object(batch, "execute_batch", return_value=1) as execute:
                self.assertEqual(1, batch.main())
                execute.assert_called_once_with(root / "out", "win-x64", "10.0.403", shell)


class ReceiptTests(unittest.TestCase):
    def fixture(self, root):
        original = {"schemaVersion": 1, "originalSourceRevision": batch.ORIGINAL_REVISION, "sdk": "10.0.403", "files": []}
        script = root / "tools/size-campaign/prepare-client-dependencies.sh"
        script.parent.mkdir(parents=True, exist_ok=True)
        script.write_bytes(b"synthetic generator fixture")
        receipt = {"schemaVersion": 1, "originalSourceRevision": batch.ORIGINAL_REVISION, "sdk": "10.0.403",
                   "preparedFromSourceRevision": "a" * 40, "preparationScriptSha256": batch.sha256(script), "files": []}
        for path in batch.PROJECT_PATHS:
            before, after = ("original " + path).encode(), ("generated " + path).encode()
            original_hash = hashlib.sha256(before).hexdigest()
            (root / path).parent.mkdir(parents=True, exist_ok=True)
            (root / path).write_bytes(after)
            original["files"].append({"path": path, "sha256": original_hash})
            receipt["files"].append({"path": path, "originalSha256": original_hash, "candidateSha256": hashlib.sha256(after).hexdigest()})
        return original, receipt

    def test_exact_generated_files_are_accepted(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            original, receipt = self.fixture(root)
            batch.validate_receipt(root, original, receipt, "10.0.403")

    def test_one_byte_changed_after_generation_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            original, receipt = self.fixture(root)
            with (root / batch.PROJECT_PATHS[0]).open("ab") as output:
                output.write(b" ")
            with self.assertRaisesRegex(batch.BatchFailure, "does not equal"):
                batch.validate_receipt(root, original, receipt, "10.0.403")

    def test_missing_duplicate_wrong_baseline_and_wrong_sdk_are_rejected(self):
        for mode in ("missing", "duplicate", "baseline", "sdk", "generator", "source"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                original, receipt = self.fixture(root)
                if mode == "missing":
                    receipt["files"].pop()
                elif mode == "duplicate":
                    receipt["files"][-1] = receipt["files"][0]
                elif mode == "baseline":
                    receipt["files"][0]["originalSha256"] = "0" * 64
                elif mode == "sdk":
                    receipt["sdk"] = "10.0.400"
                elif mode == "generator":
                    receipt["preparationScriptSha256"] = "f" * 64
                else:
                    receipt.pop("preparedFromSourceRevision")
                with self.assertRaises(batch.BatchFailure):
                    batch.validate_receipt(root, original, receipt, "10.0.403")

    def test_project_delta_preserves_properties_and_unrelated_dependencies(self):
        old = b'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App"/><PackageReference Include="Termina" Version="0.17.0"/></ItemGroup></Project>'
        good = b'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Version="10.0.13" Include="Microsoft.Extensions.Hosting"/><PackageReference Include="Termina" Version="0.17.0"/></ItemGroup></Project>'
        batch.validate_project_delta(old, good, "Setup")
        for changed in (
            good.replace(b'</PropertyGroup>', b'<PublishAot>true</PublishAot></PropertyGroup>'),
            good.replace(b'0.17.0', b'0.18.0'),
            good.replace(b'<PackageReference Include="Termina" Version="0.17.0"/>', b''),
        ):
            with self.subTest(changed=changed), self.assertRaises(batch.BatchFailure):
                batch.validate_project_delta(old, changed, "Setup")

    def test_unsafe_archive_cannot_write_outside_its_destination(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive = root / "bad.zip"
            with zipfile.ZipFile(archive, "w") as output:
                output.writestr("../outside.txt", "untrusted member")
            with self.assertRaises(batch.BatchFailure):
                batch.extract_archive(archive, root / "extracted")
            self.assertFalse((root / "outside.txt").exists())


class PaginationTests(unittest.TestCase):
    def test_empty_cursor_fetches_the_next_page_and_all_fields_survive(self):
        calls = []
        pages = [{"tools": [{"name": "z", "inputSchema": {"required": ["first", "second"]}}], "nextCursor": ""},
                 {"tools": [{"name": "a", "description": "complete field"}], "nextCursor": "opaque-next"},
                 {"tools": [{"name": "m", "annotations": {"readOnlyHint": True}}]}]
        def call(method, params):
            calls.append((method, copy.deepcopy(params)))
            return pages.pop(0)
        result, count = discovery.collect_pages(call, "tools/list", "tools", "name")
        self.assertEqual(3, count)
        self.assertEqual([{}, {"cursor": ""}, {"cursor": "opaque-next"}], [params for _, params in calls])
        self.assertEqual(["a", "m", "z"], [item["name"] for item in result])
        self.assertEqual(["first", "second"], result[-1]["inputSchema"]["required"])

    def test_repeated_cursor_duplicate_item_and_missing_array_fail(self):
        cases = [
            [{"tools": [], "nextCursor": "same"}, {"tools": [], "nextCursor": "same"}],
            [{"tools": [{"name": "x"}], "nextCursor": "next"}, {"tools": [{"name": "x"}]}],
            [{"wrong": []}],
        ]
        for responses in cases:
            with self.subTest(responses=responses), self.assertRaises(discovery.DiscoveryFailure):
                discovery.collect_pages(lambda method, params: responses.pop(0), "tools/list", "tools", "name")

    def test_unique_cursors_still_have_a_page_limit(self):
        count = 0
        def call(method, params):
            nonlocal count
            count += 1
            return {"tools": [], "nextCursor": str(count)}
        with self.assertRaisesRegex(discovery.DiscoveryFailure, "exceeded"):
            discovery.collect_pages(call, "tools/list", "tools", "name", max_pages=3)
        self.assertEqual(3, count)

    def test_missing_or_empty_snapshots_cannot_pass_parity(self):
        self.assertEqual("FAIL", discovery.compare_snapshots({}, {})["status"])

    def test_parity_detects_schema_annotations_and_nested_array_changes(self):
        before = {"initialize": {"protocolVersion": "2025-03-26", "capabilities": {}},
                  "tools": [{"name": "one", "inputSchema": {"required": ["a", "b"]}, "annotations": {"readOnlyHint": True}}],
                  "resources": [{"uri": "helpdesk://status"}], "resourceTemplates": [], "prompts": [{"name": "review"}]}
        self.assertEqual("PASS", discovery.compare_snapshots(before, copy.deepcopy(before))["status"])
        after = copy.deepcopy(before)
        after["tools"][0]["inputSchema"]["required"].reverse()
        self.assertEqual(["tools"], discovery.compare_snapshots(before, after)["differentSections"])
        after = copy.deepcopy(before)
        after["tools"][0]["annotations"]["readOnlyHint"] = False
        self.assertEqual("FAIL", discovery.compare_snapshots(before, after)["status"])


class ProtocolProcessTests(unittest.TestCase):
    def session(self, directory, program, timeout=2):
        script = directory / "server.py"
        script.write_text(program, encoding="utf-8")
        return discovery.Session([sys.executable, "-u", str(script)], directory, os.environ.copy(), timeout=timeout)

    def test_notifications_and_response_have_clean_natural_eof(self):
        program = '''import json,sys
request=json.loads(sys.stdin.readline())
print(json.dumps({"jsonrpc":"2.0","method":"notifications/message","params":{"data":"synthetic"}}),flush=True)
print(json.dumps({"jsonrpc":"2.0","id":request["id"],"result":{"tools":[]}}),flush=True)
sys.stdin.read()
'''
        with tempfile.TemporaryDirectory() as temp:
            session = self.session(Path(temp), program)
            try:
                self.assertEqual({"tools": []}, session.call("tools/list", {}))
                self.assertEqual(0, session.finish())
            finally:
                session.close()

    def test_wrong_response_id_and_non_json_stdout_fail(self):
        programs = [
            'import sys; sys.stdin.readline(); print("banner",flush=True); sys.stdin.read()',
            'import json,sys; sys.stdin.readline(); print(json.dumps({"jsonrpc":"2.0","id":True,"result":{}}),flush=True); sys.stdin.read()',
            'import json,sys; sys.stdin.readline(); print(json.dumps({"jsonrpc":"2.0","id":99,"result":{}}),flush=True); sys.stdin.read()',
        ]
        for program in programs:
            with self.subTest(program=program), tempfile.TemporaryDirectory() as temp:
                session = self.session(Path(temp), program)
                try:
                    with self.assertRaises(discovery.DiscoveryFailure):
                        session.call("tools/list", {})
                finally:
                    session.close()

    def test_nonresponding_process_has_a_bounded_request_deadline(self):
        # Waiting for EOF simulates a server that never answers; no timing delay
        # is used to make the test pass. The request's own deadline must fire.
        with tempfile.TemporaryDirectory() as temp:
            session = self.session(Path(temp), 'import sys; sys.stdin.read()', timeout=0.2)
            try:
                with self.assertRaisesRegex(discovery.DiscoveryFailure, "Timed out"):
                    session.call("tools/list", {})
            finally:
                session.close()

    def test_server_that_stops_reading_cannot_block_a_large_cursor_write(self):
        program = 'import json,sys,threading; r=json.loads(sys.stdin.readline()); print(json.dumps({"jsonrpc":"2.0","id":r["id"],"result":{"tools":[],"nextCursor":"x"*2000000}}),flush=True); threading.Event().wait()'
        with tempfile.TemporaryDirectory() as temp:
            session = self.session(Path(temp), program, timeout=5)
            try:
                result = session.call("tools/list", {})
                session.timeout = 0.2
                with self.assertRaisesRegex(discovery.DiscoveryFailure, "Timed out writing"):
                    session.call("tools/list", {"cursor": result["nextCursor"]})
            finally:
                session.close()

    def test_stdout_and_stderr_have_byte_limits(self):
        for stream, bound in (("stdout", "MAX_FRAME_BYTES"), ("stderr", "MAX_STDERR_BYTES")):
            with self.subTest(stream=stream), tempfile.TemporaryDirectory() as temp, mock.patch.object(discovery, bound, 1024):
                program = f'import sys; sys.{stream}.write("x"*70000+"\\n"); sys.{stream}.flush(); sys.stdin.read()'
                session = self.session(Path(temp), program)
                try:
                    with self.assertRaisesRegex(discovery.DiscoveryFailure, "byte bound"):
                        session.call("tools/list", {})
                finally:
                    session.close()

    def test_error_response_and_nonzero_exit_are_not_success(self):
        programs = [
            'import json,sys; r=json.loads(sys.stdin.readline()); print(json.dumps({"jsonrpc":"2.0","id":r["id"],"error":{"code":-32601,"message":"missing"}}),flush=True); sys.stdin.read()',
            'import json,sys; r=json.loads(sys.stdin.readline()); print(json.dumps({"jsonrpc":"2.0","id":r["id"],"result":{}}),flush=True); sys.stdin.read(); sys.exit(3)',
        ]
        for program in programs:
            with self.subTest(program=program), tempfile.TemporaryDirectory() as temp:
                session = self.session(Path(temp), program)
                try:
                    with self.assertRaises(discovery.DiscoveryFailure):
                        session.call("tools/list", {})
                        session.finish()
                finally:
                    session.close()


if __name__ == "__main__":
    unittest.main()
