"""Reject incomplete generation, runtime closure and test/size evidence offline."""

import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET


TOOLS = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))
spec = importlib.util.spec_from_file_location("host_telemetry_batch", TOOLS / "host-telemetry-batch.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


def generation_fixture(root):
    original = {
        gate.SOURCE_PATHS[0]: b"using Npgsql;\nbuilder.AddServiceDefaults();\n",
        gate.SOURCE_PATHS[1]: b"using Npgsql;\n.AddHttpClientInstrumentation()\n                    .AddNpgsql();\n",
        gate.PROJECT_PATHS[0]: b'<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Npgsql" Version="10.0.3" /></ItemGroup></Project>',
        gate.PROJECT_PATHS[1]: b'<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Npgsql.OpenTelemetry" Version="10.0.3" /><PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.20.0" /></ItemGroup></Project>',
    }
    for path, data in original.items():
        target = root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        if path in gate.SOURCE_PATHS:
            data = gate.expected_source(path, data)
        elif path == gate.PROJECT_PATHS[0]:
            data = data.replace(b'</ItemGroup>', b'<PackageReference Include="Npgsql.OpenTelemetry" Version="10.0.3" /></ItemGroup>')
        else:
            data = data.replace(b'<PackageReference Include="Npgsql.OpenTelemetry" Version="10.0.3" />', b'')
        target.write_bytes(data)
    script = root / gate.GENERATOR
    script.parent.mkdir(parents=True, exist_ok=True)
    script.write_bytes(b"synthetic exact generation script")
    baseline = {"schemaVersion": 1, "originalSourceRevision": "a" * 40, "sdk": "10.0.403"}
    receipt = {"schemaVersion": 1, "originalSourceRevision": baseline["originalSourceRevision"],
               "preparedFromSourceRevision": "b" * 40, "sdk": baseline["sdk"],
               "preparationScriptSha256": gate.sha256(script),
               "files": [{"path": path, "originalSha256": hashlib.sha256(original[path]).hexdigest(),
                          "candidateSha256": gate.sha256(root / path)} for path in gate.PROJECT_PATHS]}
    return baseline, original, receipt


def trx_fixture(path, *, outcomes=("Passed", "Passed", "Passed"), values=("True", "False")):
    namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
    def tag(name):
        return "{" + namespace + "}" + name
    document = ET.Element(tag("TestRun"))
    definitions, results = ET.SubElement(document, tag("TestDefinitions")), ET.SubElement(document, tag("Results"))
    methods = ["Api_provider_collects_real_PostgreSql_query_while_shared_provider_does_not",
               "Shared_host_paths_preserve_Http_traces_health_filter_and_resources",
               "Shared_host_paths_preserve_Http_traces_health_filter_and_resources"]
    for index, outcome in enumerate(outcomes):
        identifier = str(index)
        unit = ET.SubElement(definitions, tag("UnitTest"), id=identifier)
        ET.SubElement(unit, tag("TestMethod"), name=methods[index], className="Helpdesk.Tests.Api.HostTelemetryTests")
        suffix = "" if index == 0 else "(useServiceDefaults: " + values[index - 1] + ")"
        ET.SubElement(results, tag("UnitTestResult"), testId=identifier, testName=methods[index] + suffix, outcome=outcome)
    ET.ElementTree(document).write(path)


class GenerationTests(unittest.TestCase):
    def test_exact_sdk_generated_ownership_move_preserves_direct_driver(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            baseline, original, receipt = generation_fixture(root)
            gate.validate_receipt(root, baseline, original, receipt, "10.0.403")

    def test_driver_removal_or_runtime_flag_cannot_hide_in_project_change(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            _, original, _ = generation_fixture(root)
            path = gate.PROJECT_PATHS[0]
            candidate = (root / path).read_bytes()
            for changed in (candidate.replace(b'<PackageReference Include="Npgsql" Version="10.0.3" />', b''),
                            candidate.replace(b'</PropertyGroup>', b'<PublishTrimmed>true</PublishTrimmed></PropertyGroup>')):
                with self.subTest(changed=changed), self.assertRaisesRegex(RuntimeError, "beyond package ownership"):
                    gate.validate_project(original[path], changed, path)

    def test_receipt_rejects_missing_source_wrong_sdk_and_generator_hash(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            baseline, original, receipt = generation_fixture(root)
            for key, value in (("preparedFromSourceRevision", "main"), ("sdk", "10.0.402"),
                               ("preparationScriptSha256", "0" * 64), ("originalSourceRevision", "c" * 40)):
                changed = copy.deepcopy(receipt)
                changed[key] = value
                with self.subTest(key=key), self.assertRaises(RuntimeError):
                    gate.validate_receipt(root, baseline, original, changed, "10.0.403")

    def test_receipt_rejects_incomplete_extra_or_changed_generated_projects(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            baseline, original, receipt = generation_fixture(root)
            for entries in (receipt["files"][:1], receipt["files"] + receipt["files"][:1]):
                changed = copy.deepcopy(receipt)
                changed["files"] = entries
                with self.assertRaises(RuntimeError):
                    gate.validate_receipt(root, baseline, original, changed, "10.0.403")
            path = root / gate.PROJECT_PATHS[0]
            path.write_bytes(path.read_bytes() + b"\n")
            with self.assertRaisesRegex(RuntimeError, "differs from SDK receipt"):
                gate.validate_receipt(root, baseline, original, receipt, "10.0.403")

    def test_source_receipt_rejects_other_feature_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            baseline, original, receipt = generation_fixture(root)
            path = root / gate.SOURCE_PATHS[0]
            path.write_bytes(path.read_bytes() + b"builder.Services.Clear();\n")
            with self.assertRaisesRegex(RuntimeError, "beyond the registration move"):
                gate.validate_receipt(root, baseline, original, receipt, "10.0.403")


class TelemetryEvidenceTests(unittest.TestCase):
    def test_requires_real_three_case_trx_including_both_boolean_values(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "result.trx"
            trx_fixture(path)
            self.assertEqual(gate.telemetry_tests(path)["passed"], 3)

    def test_zero_missing_failed_and_skipped_cases_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "result.trx"
            for outcomes in ((), ("Passed",), ("Passed", "Failed", "Passed"), ("Passed", "Passed", "NotExecuted")):
                trx_fixture(path, outcomes=outcomes)
                with self.subTest(outcomes=outcomes), self.assertRaises(RuntimeError):
                    gate.telemetry_tests(path)

    def test_duplicate_theory_value_is_not_complete_coverage(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "result.trx"
            trx_fixture(path, values=("True", "True"))
            with self.assertRaisesRegex(RuntimeError, "coverage is incomplete"):
                gate.telemetry_tests(path)


class ImageEvidenceTests(unittest.TestCase):
    def test_exact_sdk_runtime_materials_are_required_for_the_native_platform(self):
        metadata = {"buildx.build.provenance": {"materials": [
            {"uri": "pkg:docker/mcr.microsoft.com/dotnet/" + family + "@10.0.401-alpine3.23?platform=linux%2Farm64",
             "digest": {"sha256": digest * 64}} for family, digest in (("sdk", "a"), ("aspnet", "b"))]}}
        materials = gate.image_materials(metadata, "arm64")
        self.assertEqual(materials["sdk"]["digest"], "sha256:" + "a" * 64)
        for changed in ({}, {"buildx.build.provenance": {"materials": []}}):
            with self.assertRaisesRegex(RuntimeError, "image material"):
                gate.image_materials(changed, "arm64")
        with self.assertRaisesRegex(RuntimeError, "wrong platform"):
            gate.image_materials(metadata, "amd64")

    def test_same_sdk_version_does_not_allow_changed_sdk_image(self):
        before = {"sdk": {"version": "10.0.401"}, "imageMaterials": {"sdk": {"digest": "sha256:" + "a" * 64},
                                                                    "aspnet": {"digest": "sha256:" + "b" * 64}}}
        after = copy.deepcopy(before)
        gate.compare_builds(before, after)
        after["imageMaterials"]["sdk"]["digest"] = "sha256:" + "c" * 64
        with self.assertRaisesRegex(RuntimeError, "different SDK/runtime image materials"):
            gate.compare_builds(before, after)

    def test_only_application_layer_and_measured_bytes_may_change(self):
        before = {"rootfsDiffIds": ["base", "user", "app-before", "licenses"],
                  "layers": [{"digest": name} for name in ("base", "user", "app-before", "licenses")],
                  "config": {"config": {"Env": ["MODE=kept"], "User": "helpdesk"}, "created": "first"},
                  "compressedLayerBytes": 1000, "uncompressedImageBytes": 2000, "application": {"expandedBytes": 500}}
        after = {"rootfsDiffIds": ["base", "user", "app-after", "licenses"],
                 "layers": [{"digest": name} for name in ("base", "user", "app-after", "licenses")],
                 "config": {"config": {"Env": ["MODE=kept"], "User": "helpdesk"}, "created": "second"},
                 "compressedLayerBytes": 900, "uncompressedImageBytes": 1800, "application": {"expandedBytes": 300}}
        self.assertEqual(gate.compare_images(before, after, -2)["applicationBytes"]["savedBytes"], 200)
        changed = copy.deepcopy(after)
        changed["rootfsDiffIds"][0] = "changed-base"
        with self.assertRaisesRegex(RuntimeError, "outside the application"):
            gate.compare_images(before, changed, -2)
        changed = copy.deepcopy(after)
        changed["compressedLayerBytes"] = 1001
        with self.assertRaisesRegex(RuntimeError, "No measured reduction"):
            gate.compare_images(before, changed, -2)

    def test_same_unpacked_base_cannot_hide_changed_compression_or_runtime_settings(self):
        before = {"rootfsDiffIds": ["base", "user", "app-before", "licenses"],
                  "layers": [{"digest": name, "size": 100} for name in ("base", "user", "app-before", "licenses")],
                  "config": {"config": {"Env": ["MODE=kept"], "User": "helpdesk"}},
                  "compressedLayerBytes": 1000, "uncompressedImageBytes": 2000, "application": {"expandedBytes": 500}}
        after = copy.deepcopy(before)
        after.update(compressedLayerBytes=900, uncompressedImageBytes=1800, application={"expandedBytes": 300})
        after["rootfsDiffIds"][2] = "app-after"
        after["layers"][2]["digest"] = "app-after"
        gate.compare_images(before, after, -2)
        changed = copy.deepcopy(after)
        changed["layers"][0]["digest"] = "same-content-different-compression"
        with self.assertRaisesRegex(RuntimeError, "compressed layer descriptor"):
            gate.compare_images(before, changed, -2)
        for key, value in (("Env", ["MODE=changed"]), ("User", "root"), ("Healthcheck", {"Test": ["NONE"]}),
                           ("Labels", {"base-image": "changed"})):
            changed = copy.deepcopy(after)
            changed["config"]["config"][key] = value
            with self.subTest(key=key), self.assertRaisesRegex(RuntimeError, "Runtime image configuration"):
                gate.compare_images(before, changed, -2)

    def test_host_payload_requires_no_driver_and_api_requires_driver_and_instrumentation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            assembly = "HelpDesk.NewWeb"
            runtime = {"runtimeOptions": {"frameworks": [{"name": "Microsoft.NETCore.App"}, {"name": "Microsoft.AspNetCore.App"}]}}
            (root / (assembly + ".runtimeconfig.json")).write_text(json.dumps(runtime))
            deps = root / (assembly + ".deps.json")
            deps.write_text(json.dumps({"libraries": {}}))
            self.assertEqual(gate.payload(root, assembly, database=False)["status"], "PASS")
            with self.assertRaisesRegex(RuntimeError, "lost Npgsql"):
                gate.payload(root, assembly, database=True)
            deps.write_text(json.dumps({"libraries": {"Npgsql/10.0.3": {}, "Npgsql.OpenTelemetry/10.0.3": {}}}))
            (root / "Npgsql.dll").write_bytes(b"driver")
            (root / "Npgsql.OpenTelemetry.dll").write_bytes(b"instrumentation")
            self.assertEqual(gate.payload(root, assembly, database=True)["status"], "PASS")
            with self.assertRaisesRegex(RuntimeError, "still includes"):
                gate.payload(root, assembly, database=False)

    def test_deps_only_residual_driver_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "Host.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {"frameworks": [
                {"name": "Microsoft.NETCore.App"}, {"name": "Microsoft.AspNetCore.App"}]}}))
            (root / "Host.deps.json").write_text(json.dumps({"libraries": {"Npgsql/10.0.3": {}}}))
            with self.assertRaisesRegex(RuntimeError, "still includes"):
                gate.payload(root, "Host", database=False)

    def test_reused_oci_validator_enforces_the_specific_host_entrypoint(self):
        from test_api_rid_acceptance import fixture
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "image.tar"
            inspected, _ = fixture(path)
            inspected["Config"]["Entrypoint"] = ["dotnet", "HelpDesk.NewWeb.dll"]
            gate.api.oci_inventory(path, "amd64", inspected, expected_entrypoint=("dotnet", "HelpDesk.NewWeb.dll"))
            with self.assertRaisesRegex(RuntimeError, "entrypoint changed"):
                gate.api.oci_inventory(path, "amd64", inspected, expected_entrypoint=("dotnet", "Helpdesk.Mcp.Http.dll"))


if __name__ == "__main__":
    unittest.main()
