"""Evidence-integrity tests; actual product acceptance runs in native containers."""

import gzip
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("api_rid_acceptance", Path(__file__).with_name("api-rid-acceptance.py"))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


def fixture(path, *, bad_size=False, corrupt_layer=False):
    files = {}

    def descriptor(content, media_type):
        data = content if isinstance(content, bytes) else json.dumps(content).encode()
        digest = hashlib.sha256(data).hexdigest()
        files["blobs/sha256/" + digest] = data
        return {"digest": "sha256:" + digest, "size": len(data), "mediaType": media_type}

    raw_layers = [b"synthetic base layer", b"synthetic application layer"]
    layers = [descriptor(gzip.compress(data, mtime=0), "application/vnd.oci.image.layer.v1.tar+gzip") for data in raw_layers]
    rootfs = ["sha256:" + hashlib.sha256(data).hexdigest() for data in raw_layers]
    config = descriptor({"os": "linux", "architecture": "amd64", "rootfs": {"diff_ids": rootfs}},
                        "application/vnd.oci.image.config.v1+json")
    if bad_size:
        layers[0]["size"] += 1
    if corrupt_layer:
        name = "blobs/sha256/" + layers[0]["digest"].split(":")[1]
        files[name] = files[name][:-1] + bytes([files[name][-1] ^ 1])
    manifest = descriptor({"schemaVersion": 2, "config": config, "layers": layers},
                          "application/vnd.oci.image.manifest.v1+json")
    files["index.json"] = json.dumps({"schemaVersion": 2, "manifests": [manifest]}).encode()
    with tarfile.open(path, "w") as archive:
        for name, data in files.items():
            member = tarfile.TarInfo(name)
            member.size = len(data)
            archive.addfile(member, io.BytesIO(data))
    return {"Id": config["digest"], "Os": "linux", "Architecture": "amd64", "Size": 1234,
            "RootFS": {"Layers": rootfs}, "Config": {"Entrypoint": ["dotnet", "Helpdesk.API.dll"]}}, layers


class EvidenceTests(unittest.TestCase):
    def test_sdk_is_observed_from_publish_paths_and_repeated_paths_deduplicate(self):
        log = ('#24 /usr/share/dotnet/dotnet exec "/usr/share/dotnet/sdk/10.0.402/Roslyn/bincore/csc.dll"\n'
               '#24 /usr/share/dotnet/sdk/10.0.402/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.targets\n')
        self.assertEqual(gate.resolved_sdk(log)["version"], "10.0.402")

    def test_missing_or_conflicting_sdk_evidence_cannot_pass(self):
        for log in ("FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble; build succeeded",
                    "/usr/share/dotnet/sdk/10.0.402/Roslyn/csc.dll /usr/share/dotnet/sdk/10.0.403/Roslyn/csc.dll"):
            with self.subTest(log=log), self.assertRaisesRegex(RuntimeError, "SDK evidence"):
                gate.resolved_sdk(log)

    def test_sizes_are_verified_layer_descriptors_not_archive_or_log_size(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "image.tar"
            inspected, layers = fixture(path)
            result = gate.oci_inventory(path, "amd64", inspected)
            self.assertEqual(result["compressedLayerBytes"], sum(layer["size"] for layer in layers))
            self.assertEqual(result["uncompressedImageBytes"], 1234)
            self.assertNotEqual(result["compressedLayerBytes"], path.stat().st_size)

    def test_corrupt_layer_fails_even_when_descriptor_size_matches(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "image.tar"
            inspected, _ = fixture(path, corrupt_layer=True)
            with self.assertRaisesRegex(RuntimeError, "digest mismatch"):
                gate.oci_inventory(path, "amd64", inspected)

    def test_incorrect_layer_size_is_not_counted_as_savings(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "image.tar"
            inspected, _ = fixture(path, bad_size=True)
            with self.assertRaisesRegex(RuntimeError, "size mismatch"):
                gate.oci_inventory(path, "amd64", inspected)

    def test_image_under_test_must_match_measured_config(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "image.tar"
            inspected, _ = fixture(path)
            inspected["Id"] = "sha256:" + "0" * 64
            with self.assertRaisesRegex(RuntimeError, "config differ"):
                gate.oci_inventory(path, "amd64", inspected)

    def test_foreign_platform_cannot_be_accepted(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "image.tar"
            inspected, _ = fixture(path)
            with self.assertRaisesRegex(RuntimeError, "architecture mismatch"):
                gate.oci_inventory(path, "arm64", inspected)

    def test_failure_log_containing_pass_still_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            result = gate.execute([sys.executable, "-c", "print('PASS'); raise SystemExit(7)"],
                                  Path(temporary) / "failure.log", timeout=30)
            self.assertEqual(result["exitCode"], 7)
            self.assertEqual(result["status"], "FAIL")

    def test_native_payload_requires_correct_elf_and_rejects_foreign_libraries(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "Helpdesk.API.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
                "frameworks": [{"name": "Microsoft.NETCore.App"}, {"name": "Microsoft.AspNetCore.App"}]}}))
            (directory / "Helpdesk.API.deps.json").write_text(json.dumps({"runtimeTarget": {
                "name": ".NETCoreApp,Version=v10.0/linux-x64"}}))
            header = bytearray(20)
            header[:6] = b"\x7fELF\x02\x01"
            header[18:20] = (62).to_bytes(2, "little")
            (directory / "libe_sqlite3.so").write_bytes(header)
            (directory / "SQLitePCLRaw.provider.e_sqlite3.dll").write_bytes(b"managed provider")
            self.assertEqual(gate.candidate_payload(directory, "amd64")["status"], "PASS")
            header[18:20] = (183).to_bytes(2, "little")
            (directory / "libe_sqlite3.so").write_bytes(header)
            with self.assertRaisesRegex(RuntimeError, "ELF architecture"):
                gate.candidate_payload(directory, "amd64")
            header[18:20] = (62).to_bytes(2, "little")
            (directory / "libe_sqlite3.so").write_bytes(header)
            (directory / "e_sqlite3.dll").write_bytes(b"foreign library")
            with self.assertRaisesRegex(RuntimeError, "foreign SQLite"):
                gate.candidate_payload(directory, "amd64")


if __name__ == "__main__":
    unittest.main()
