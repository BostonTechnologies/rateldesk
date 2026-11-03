#!/usr/bin/env python3
"""Offline regressions for inventory integrity and non-executing image inspection."""

import copy
from contextlib import nullcontext
import hashlib
import io
import json
from pathlib import Path
import stat
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import inventory


BASELINE = json.loads(Path(__file__).with_name("baseline-0.1.3-beta.3.json").read_text())
DIGEST_A = "sha256:" + "a" * 64
DIGEST_B = "sha256:" + "b" * 64


class InventoryTests(unittest.TestCase):
    def test_directory_measures_files_without_following_links(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "output"
            (root / "docs").mkdir(parents=True)
            (root / "docs" / "guide.md").write_bytes(b"guide")
            (root / "libnative.so").write_bytes(b"123")
            outside = Path(temporary) / "outside"
            outside.mkdir()
            (outside / "large").write_bytes(b"x" * 100)
            (root / "external").symlink_to(outside, target_is_directory=True)
            (root / "file-link").symlink_to(outside / "large")
            result = inventory.inventory_directory(root)
            self.assertEqual(8, result["expandedBytes"])
            self.assertEqual(2, result["fileCount"])
            self.assertEqual(2, result["kindCounts"]["symlink"])
            self.assertEqual(5, result["categories"]["documentation"]["bytes"])
            self.assertNotIn("external/large", {item["path"] for item in result["entries"]})

    def test_tar_inventory_does_not_extract_or_follow_links(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "payload.tar.gz"
            with tarfile.open(path, "w:gz") as archive:
                member = tarfile.TarInfo("root/hello.dll")
                member.size = 3
                archive.addfile(member, io.BytesIO(b"abc"))
                link = tarfile.TarInfo("root/external")
                link.type = tarfile.SYMTYPE
                link.linkname = "/does-not-exist"
                archive.addfile(link)
            result = inventory.inventory_archive(path)
            self.assertEqual(3, result["expandedBytes"])
            self.assertEqual(path.stat().st_size, result["archiveBytes"])
            self.assertEqual(1, result["kindCounts"]["symlink"])
            self.assertFalse((Path(temporary) / "root").exists())

    def test_zip_symlink_is_not_counted_as_a_regular_file(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "payload.zip"
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("root/docs/guide.md", b"abc")
                link = zipfile.ZipInfo("root/link")
                link.create_system = 3
                link.external_attr = (stat.S_IFLNK | 0o777) << 16
                archive.writestr(link, "/outside")
            result = inventory.inventory_archive(path)
            self.assertEqual(3, result["expandedBytes"])
            self.assertEqual(1, result["fileCount"])
            self.assertEqual(1, result["kindCounts"]["symlink"])

    def test_archive_traversal_and_duplicate_normalized_paths_fail(self):
        for paths in (("../outside",), ("/absolute",), ("C:/absolute",), ("root/a", "root/./a")):
            with self.subTest(paths=paths), tempfile.TemporaryDirectory() as temporary:
                path = Path(temporary) / "payload.tar.gz"
                with tarfile.open(path, "w:gz") as archive:
                    for name in paths:
                        archive.addfile(tarfile.TarInfo(name))
                with self.assertRaises(inventory.InventoryError):
                    inventory.inventory_archive(path)

    def test_file_hash_and_size_are_both_required(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "asset"
            path.write_bytes(b"abc")
            asset = {"name": "asset", "sizeBytes": 3, "sha256": hashlib.sha256(b"abc").hexdigest()}
            self.assertEqual(asset["sha256"], inventory.verify_file(path, asset)["sha256"])
            for changes in ({"sizeBytes": 2}, {"sha256": "0" * 64}):
                with self.subTest(changes=changes), self.assertRaises(inventory.InventoryError):
                    inventory.verify_file(path, asset | changes)

    def test_checksum_parser_rejects_duplicates_paths_and_invalid_lines(self):
        valid = "a" * 64 + "  archive.tar.gz\n"
        self.assertEqual({"archive.tar.gz": "a" * 64}, inventory.checksum_lines(valid))
        for text in (valid + valid, "a" * 64 + "  ../outside\n", "a" * 64 + "  /outside\n", "invalid\n"):
            with self.subTest(text=text), self.assertRaises(inventory.InventoryError):
                inventory.checksum_lines(text)

    def test_json_duplicate_fields_fail_instead_of_replacing_identity(self):
        with self.assertRaises(inventory.InventoryError):
            inventory.read_json('{"version":"one","version":"two"}')

    def test_baseline_requires_every_asset_and_three_images(self):
        inventory.validate_baseline(BASELINE)
        for field in ("assets", "images"):
            baseline = copy.deepcopy(BASELINE)
            baseline[field].pop()
            with self.subTest(field=field), self.assertRaises(inventory.InventoryError):
                inventory.validate_baseline(baseline)

    def test_release_metadata_reconciles_asset_id_size_digest_and_count(self):
        metadata = {"id": BASELINE["release"]["id"], "tag_name": BASELINE["release"]["tag"],
                    "assets": [{"name": item["name"], "id": item["id"], "size": item["sizeBytes"],
                                "digest": "sha256:" + item["sha256"], "state": "uploaded"}
                               for item in BASELINE["assets"]]}
        inventory.verify_release_metadata(metadata, BASELINE)
        for field, value in (("id", 1), ("size", 1), ("digest", DIGEST_A), ("state", "new")):
            changed = copy.deepcopy(metadata)
            changed["assets"][0][field] = value
            with self.subTest(field=field), self.assertRaises(inventory.InventoryError):
                inventory.verify_release_metadata(changed, BASELINE)

    def write_release_documents(self, root, source_revision=None):
        release = BASELINE["release"]
        assets = {item["name"]: item["sha256"] for item in BASELINE["assets"]
                  if item["name"] in inventory.expected_archives(release["version"])}
        (root / "SHA256SUMS").write_text("".join(f"{digest}  {name}\n" for name, digest in assets.items()))
        manifest = {key: release[key] for key in ("version", "tag", "sourceRevision")}
        if source_revision:
            manifest["sourceRevision"] = source_revision
        manifest.update({"assets": assets, "supportedRids": ["linux-x64", "linux-arm64", "win-x64"],
                         "containers": [{"image": item["name"] + ":" + release["version"], "digest": item["digest"]}
                                        for item in BASELINE["images"]]})
        inventory.write_json(root / "release-manifest.json", manifest)
        (root / "release-manifest.json.sha256").write_text(inventory.digest_file(root / "release-manifest.json") + "  release-manifest.json\n")

    def test_release_documents_require_identity_and_exact_archive_set(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.write_release_documents(root)
            self.assertEqual(7, inventory.verify_release_documents(root, BASELINE)["archiveCount"])
            (root / "SHA256SUMS").write_text((root / "SHA256SUMS").read_text() + "a" * 64 + "  extra.tar.gz\n")
            with self.assertRaises(inventory.InventoryError):
                inventory.verify_release_documents(root, BASELINE)
            self.write_release_documents(root, source_revision="b" * 40)
            with self.assertRaises(inventory.InventoryError):
                inventory.verify_release_documents(root, BASELINE)

    def test_manifest_raw_digest_accepts_only_original_or_one_transport_newline(self):
        raw = b'{"schemaVersion":2}'
        digest = "sha256:" + hashlib.sha256(raw).hexdigest()
        self.assertEqual(2, inventory.verify_raw_manifest(raw, digest, len(raw))["schemaVersion"])
        self.assertEqual(2, inventory.verify_raw_manifest(raw + b"\n", digest, len(raw))["schemaVersion"])
        for content, size in ((raw + b" \n", len(raw)), (raw, len(raw) + 1), (raw + b"\n\n", len(raw))):
            with self.subTest(content=content, size=size), self.assertRaises(inventory.InventoryError):
                inventory.verify_raw_manifest(content, digest, size)

    def test_platforms_require_both_architectures_and_skip_attestations(self):
        descriptors = [{"platform": {"os": "linux", "architecture": arch}, "digest": DIGEST_A, "size": 10}
                       for arch in ("amd64", "arm64")]
        index = {"schemaVersion": 2, "manifests": descriptors + [{"platform": {"os": "unknown", "architecture": "unknown"}}]}
        self.assertEqual({"linux/amd64", "linux/arm64"}, set(inventory.platform_manifests(index)))
        for items in (descriptors[:1], descriptors + descriptors[:1]):
            with self.subTest(items=items), self.assertRaises(inventory.InventoryError):
                inventory.platform_manifests({"schemaVersion": 2, "manifests": items})

    def test_layer_totals_deduplicate_across_images_and_reject_conflicts(self):
        unique = {}
        manifest = {"schemaVersion": 2, "layers": [{"digest": DIGEST_A, "size": 10}, {"digest": DIGEST_B, "size": 20}]}
        self.assertEqual(30, inventory.add_layers(manifest, unique))
        self.assertEqual(30, inventory.add_layers(manifest, unique))
        self.assertEqual(30, sum(unique.values()))
        with self.assertRaises(inventory.InventoryError):
            inventory.add_layers({"schemaVersion": 2, "layers": [{"digest": DIGEST_A, "size": 11}]}, unique)

    def test_failed_cli_never_copies_credential_bearing_stderr(self):
        failure = subprocess.CompletedProcess(["gh", "api"], 1, b"", b"secret-token-do-not-record")
        with patch.object(inventory.subprocess, "run", return_value=failure):
            with self.assertRaises(inventory.InventoryError) as raised:
                inventory.command(["gh", "api", "repos/example/repo/releases/1"])
        self.assertIn("exit 1", str(raised.exception))
        self.assertNotIn("secret-token", str(raised.exception))

    def test_stopped_container_cleanup_runs_after_copy_failure_and_never_starts(self):
        calls = []
        image = {"Os": "linux", "Architecture": "amd64", "Id": DIGEST_A, "Size": 20,
                 "Config": {"Labels": {"org.opencontainers.image.version": BASELINE["release"]["version"],
                                       "org.opencontainers.image.revision": BASELINE["release"]["sourceRevision"]}}}

        def fake_command(args, **kwargs):
            calls.append(args)
            if args[1:3] == ["image", "inspect"]:
                return json.dumps([image]).encode()
            if args[1] == "create":
                return b"c" * 64 + b"\n"
            if args[1] == "cp":
                raise inventory.InventoryError("Synthetic copy failure")
            return b""

        with patch.object(inventory, "command", side_effect=fake_command):
            with self.assertRaisesRegex(inventory.InventoryError, "Synthetic copy failure"):
                inventory.local_image_inventory("example@" + DIGEST_B, "linux/amd64", {"config": {"digest": DIGEST_A}}, BASELINE)
        self.assertEqual(["docker", "rm", "c" * 64], calls[-1])
        self.assertFalse(any(args[1] in ("run", "start", "exec", "push") for args in calls))

    def test_invalid_baseline_writes_failed_receipt(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            baseline_path = root / "invalid.json"
            baseline_path.write_text('{"schemaVersion":0}')
            with patch.object(inventory, "provenance", return_value={}):
                self.assertEqual(1, inventory.main(["release", "--baseline", str(baseline_path), "--output", str(root / "evidence")]))
            report = json.loads((root / "evidence" / "release-inventory.json").read_text())
            self.assertEqual("FAIL", report["status"])
            self.assertTrue(report["errors"])

    def provisional_manifest(self):
        release = BASELINE["release"]
        return {"version": release["version"], "sourceRevision": release["sourceRevision"],
                "supportedRids": ["linux-x64", "linux-arm64", "win-x64"],
                "assets": {a["name"]: a["sha256"] for a in BASELINE["assets"]
                           if a["name"] in inventory.expected_archives(release["version"])},
                "containers": [f"ghcr.io/bostontechnologies/rateldesk-{name}:{release['version']}"
                               for name in ("web", "api", "mcp-http")]}

    def test_finalizer_reconstruction_matches_independently_pinned_release_hashes(self):
        final = inventory.reconstruct_release_metadata(self.provisional_manifest(), BASELINE)
        pinned = {a["name"]: a for a in BASELINE["assets"]}
        self.assertEqual(pinned["release-manifest.json"]["sizeBytes"], len(final))
        digest = hashlib.sha256(final).hexdigest()
        self.assertEqual(pinned["release-manifest.json"]["sha256"], digest)
        detached = (digest + "  release-manifest.json\n").encode("utf-8")
        self.assertEqual(pinned["release-manifest.json.sha256"]["sha256"], hashlib.sha256(detached).hexdigest())

    def test_reconstruction_rejects_unexpected_provisional_content(self):
        for field, value in (("sourceRevision", "b" * 40), ("version", "0.0.0"),
                             ("assets", {}), ("supportedRids", ["linux-x64"]),
                             ("containers", ["untrusted"] * 3), ("unexpected", "value")):
            with self.subTest(field=field), self.assertRaises(inventory.InventoryError):
                manifest = self.provisional_manifest()
                manifest[field] = value
                inventory.reconstruct_release_metadata(manifest, BASELINE)

    def artifact_metadata(self):
        expected = BASELINE["workflowArtifact"]
        return {"id": expected["id"], "name": expected["name"], "size_in_bytes": expected["sizeBytes"],
                "digest": "sha256:" + expected["sha256"], "expired": False,
                "workflow_run": {"id": expected["runId"], "head_sha": BASELINE["release"]["sourceRevision"],
                                 "repository_id": 1, "head_repository_id": 1}}

    def test_workflow_artifact_requires_pinned_identity_source_digest_and_live_bytes(self):
        metadata = self.artifact_metadata()
        inventory.verify_workflow_artifact_metadata(metadata, BASELINE)
        changes = [("id", 1), ("name", "other"), ("size_in_bytes", 1), ("digest", DIGEST_A), ("expired", True)]
        for field, value in changes:
            with self.subTest(field=field), self.assertRaises(inventory.InventoryError):
                changed = copy.deepcopy(metadata)
                changed[field] = value
                inventory.verify_workflow_artifact_metadata(changed, BASELINE)
        for field, value in (("id", 1), ("head_sha", "b" * 40), ("head_repository_id", 2)):
            with self.subTest(field=field), self.assertRaises(inventory.InventoryError):
                changed = copy.deepcopy(metadata)
                changed["workflow_run"][field] = value
                inventory.verify_workflow_artifact_metadata(changed, BASELINE)

    def test_workflow_zip_is_hash_verified_before_any_extraction(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "artifact.zip"
            archive.write_bytes(b"unexpected content")
            with self.assertRaises(inventory.InventoryError):
                inventory.extract_workflow_assets(archive, BASELINE, root / "assets")
            self.assertFalse((root / "assets").exists())

    def test_workflow_zip_rejects_traversal_unexpected_members_and_symlinks(self):
        for kind in ("traversal", "extra", "symlink", "duplicate"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                baseline = copy.deepcopy(BASELINE)
                members = {a["name"]: b"" for a in baseline["assets"] if a["name"] != "release-manifest.json.sha256"}
                for asset in baseline["assets"]:
                    if asset["name"] in members:
                        asset.update(sizeBytes=0, sha256=hashlib.sha256(b"").hexdigest())
                members["release-manifest.json"] = json.dumps(self.provisional_manifest()).encode("utf-8")
                archive_path = root / "artifact.zip"
                with zipfile.ZipFile(archive_path, "w") as archive:
                    for name, payload in members.items():
                        member = zipfile.ZipInfo(name)
                        if kind == "symlink" and name == "SHA256SUMS":
                            member.create_system = 3
                            member.external_attr = (stat.S_IFLNK | 0o777) << 16
                        archive.writestr(member, payload)
                    if kind != "symlink":
                        name = "../outside" if kind == "traversal" else "SHA256SUMS" if kind == "duplicate" else "extra"
                        with self.assertWarns(UserWarning) if kind == "duplicate" else nullcontext():
                            archive.writestr(name, b"extra")
                baseline["workflowArtifact"].update(sizeBytes=archive_path.stat().st_size, sha256=inventory.digest_file(archive_path))
                with self.assertRaises(inventory.InventoryError):
                    inventory.extract_workflow_assets(archive_path, baseline, root / "assets")
                self.assertFalse((root / "outside").exists())

    def test_failed_artifact_access_does_not_claim_reconstructed_metadata_passed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            baseline_path = root / "baseline.json"
            baseline_path.write_text(json.dumps(BASELINE))
            with patch.object(inventory, "provenance", return_value={}), \
                 patch.object(inventory, "command", side_effect=inventory.InventoryError("Read unavailable")):
                self.assertEqual(1, inventory.main(["release", "--release-source", "workflow-artifact",
                                                  "--baseline", str(baseline_path), "--output", str(root / "evidence")]))
            report = json.loads((root / "evidence" / "release-inventory.json").read_text())
            self.assertEqual("FAIL", report["status"])
            self.assertEqual("NOT_VERIFIED", report["acquisition"]["finalMetadata"])
            self.assertEqual([], report["assets"])


if __name__ == "__main__":
    unittest.main()
