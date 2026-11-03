"""Exercise the published-client gate's failure boundaries without a .NET SDK."""

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("client_publish", Path(__file__).with_name("check-client-publish.py"))
client_publish = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client_publish)


class ClientPublishBoundaryTests(unittest.TestCase):
    def fixture(self, directory, target="cli", rid="linux-x64"):
        app = client_publish.APP_NAMES[target]
        names = list(client_publish.SHARED_ASSEMBLIES) + [app + ".dll"]
        names += ([app + ".exe", "coreclr.dll", "clrjit.dll", "hostfxr.dll", "hostpolicy.dll"]
                  if rid == "win-x64" else [app, "libcoreclr.so", "libclrjit.so", "libhostfxr.so", "libhostpolicy.so"])
        for name in names:
            (directory / name).write_bytes(b"fixture")
        runtime = {"runtimeOptions": {"tfm": "net10.0", "includedFrameworks": [
            {"name": "Microsoft.NETCore.App", "version": "10.0.14"}]}}
        runtime_target = ".NETCoreApp,Version=v10.0/" + rid
        deps = {"runtimeTarget": {"name": runtime_target}, "targets": {runtime_target: {}},
                "libraries": {app + "/0.1.3-beta.3": {"type": "project"}, "Termina/0.17.0": {"type": "package"}}}
        (directory / (app + ".runtimeconfig.json")).write_text(json.dumps(runtime))
        (directory / (app + ".deps.json")).write_text(json.dumps(deps))
        return runtime, deps

    def test_all_six_standalone_jit_payload_shapes_are_supported(self):
        for target in ("cli", "stdio"):
            for rid in ("linux-x64", "linux-arm64", "win-x64"):
                with self.subTest(target=target, rid=rid), tempfile.TemporaryDirectory() as temp:
                    directory = Path(temp)
                    self.fixture(directory, target, rid)
                    self.assertEqual("PASS", client_publish.check_publish(directory, target, rid)["status"])

    def test_empty_publish_cannot_pass(self):
        with tempfile.TemporaryDirectory() as temp:
            self.assertEqual("FAIL", client_publish.check_publish(Path(temp), "cli", "linux-x64")["status"])

    def test_missing_setup_and_missing_jit_each_fail(self):
        for missing in ("Helpdesk.Setup.dll", "Termina.dll", "libclrjit.so"):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as temp:
                directory = Path(temp)
                self.fixture(directory)
                (directory / missing).unlink()
                result = client_publish.check_publish(directory, "cli", "linux-x64")
                self.assertEqual("FAIL", result["status"])
                self.assertTrue(any(missing in error for error in result["errors"]))

    def test_actual_aspnet_or_npgsql_dll_cannot_hide_outside_metadata(self):
        for name in ("Microsoft.AspNetCore.dll", "Microsoft.AspNetCore.Server.Kestrel.Core.dll", "Npgsql.dll"):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temp:
                directory = Path(temp)
                self.fixture(directory)
                nested = directory / "nested"
                nested.mkdir()
                (nested / name).write_bytes(b"unexpected payload")
                self.assertEqual("FAIL", client_publish.check_publish(directory, "cli", "linux-x64")["status"])

    def test_npgsql_dependency_is_rejected_even_without_the_dll(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            _, deps = self.fixture(directory)
            deps["libraries"]["Npgsql/10.0.3"] = {"type": "package"}
            (directory / "rateldesk.deps.json").write_text(json.dumps(deps))
            result = client_publish.check_publish(directory, "cli", "linux-x64")
            self.assertEqual("FAIL", result["status"])
            self.assertTrue(any("Npgsql/10.0.3" in error for error in result["errors"]))

    def test_framework_contract_cannot_be_disguised_as_missing_dlls(self):
        for dependent in (False, True):
            with self.subTest(dependent=dependent), tempfile.TemporaryDirectory() as temp:
                directory = Path(temp)
                runtime, _ = self.fixture(directory)
                if dependent:
                    runtime["runtimeOptions"]["framework"] = {"name": "Microsoft.NETCore.App", "version": "10.0.14"}
                else:
                    runtime["runtimeOptions"]["includedFrameworks"].append({"name": "Microsoft.AspNetCore.App", "version": "10.0.14"})
                (directory / "rateldesk.runtimeconfig.json").write_text(json.dumps(runtime))
                self.assertEqual("FAIL", client_publish.check_publish(directory, "cli", "linux-x64")["status"])

    def test_wrong_rid_does_not_count_as_native_evidence(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            self.fixture(directory, rid="linux-x64")
            result = client_publish.check_publish(directory, "cli", "linux-arm64")
            self.assertEqual("FAIL", result["status"])
            self.assertTrue(any("selected RID" in error for error in result["errors"]))

    def test_malformed_metadata_is_reported_as_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            self.fixture(directory)
            (directory / "rateldesk.deps.json").write_text('{"libraries":')
            result = client_publish.check_publish(directory, "cli", "linux-x64")
            self.assertEqual("FAIL", result["status"])
            self.assertTrue(any("invalid publish metadata" in error for error in result["errors"]))


if __name__ == "__main__":
    unittest.main()
