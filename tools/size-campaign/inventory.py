#!/usr/bin/env python3
"""Read-only release/image size inventory. A successful inventory is not a runtime test."""

import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import stat
import subprocess
import tarfile
import tempfile
import zipfile


class InventoryError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise InventoryError(message)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def read_json(value):
    return json.loads(value, object_pairs_hook=unique_object)


def write_json(path, value):
    path = Path(path)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    temporary.replace(path)


def digest_file(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def command(args, timeout=180, destination=None):
    """No shell, no credential arguments, bounded subprocess lifetime."""
    try:
        if destination is None:
            result = subprocess.run(args, capture_output=True, timeout=timeout, check=False)
        else:
            with Path(destination).open("wb") as stream:
                result = subprocess.run(args, stdout=stream, stderr=subprocess.PIPE,
                                        timeout=timeout, check=False)
        # Do not copy CLI stderr into evidence: auth failures can contain credentials/URLs.
        require(result.returncode == 0,
                f"Command failed (exit {result.returncode}): {' '.join(args)}")
        return result.stdout if destination is None else b""
    except subprocess.TimeoutExpired as error:
        raise InventoryError(f"Command timed out after {timeout}s: {' '.join(args)}") from error
    except OSError as error:
        raise InventoryError(f"Cannot execute {args[0]}: {error.strerror}") from error


def safe_member(name):
    require(isinstance(name, str) and name and "\x00" not in name and "\\" not in name,
            "Invalid archive member name")
    path = PurePosixPath(name)
    require(not path.is_absolute() and ".." not in path.parts
            and not re.match(r"^[A-Za-z]:", name), f"Unsafe archive member: {name}")
    return path.as_posix()


def category(name, executable=False):
    name = name.lower()
    parts = PurePosixPath(name).parts
    if "licenses" in parts or PurePosixPath(name).name.startswith(("license", "notice")):
        return "licenses"
    if "docs" in parts or name.endswith((".md", ".rst")):
        return "documentation"
    if "wwwroot" in parts or "_content" in parts:
        return "web-assets"
    if name.endswith((".pdb", ".dbg")):
        return "symbols"
    if "/native/" in name or re.search(r"\.so(?:\.[0-9]+)*$", name) or name.endswith((".dylib", ".a")):
        return "native-libraries"
    if name.endswith(".dll"):
        return "dll-files"  # Extension alone does not establish managed versus native code.
    if name.endswith((".sh", ".py", ".ps1", ".cmd", ".bat")):
        return "scripts"
    if executable or name.endswith(".exe"):
        return "executables"
    if name.endswith((".json", ".yml", ".yaml", ".config", ".xml")):
        return "configuration-and-metadata"
    return "other"


def summarize(entries, top_n):
    require(type(top_n) is int and top_n >= 0, "top_n must be a nonnegative integer")
    entries.sort(key=lambda item: item["path"])
    require(len({item["path"] for item in entries}) == len(entries), "Duplicate inventory path")
    files = [item for item in entries if item["kind"] == "file"]
    categories = {}
    for item in files:
        bucket = categories.setdefault(item["category"], {"fileCount": 0, "bytes": 0})
        bucket["fileCount"] += 1
        bucket["bytes"] += item["bytes"]
    return {"fileCount": len(files), "entryCount": len(entries),
            "expandedBytes": sum(item["bytes"] for item in files),
            "byteMeaning": "Sum of regular-file lengths; links are not followed; not filesystem allocation",
            "kindCounts": dict(Counter(item["kind"] for item in entries)),
            "categories": categories,
            "topFiles": sorted(files, key=lambda item: (-item["bytes"], item["path"]))[:top_n],
            "entries": entries}


def inventory_directory(path, top_n=20):
    root = Path(path)
    require(root.is_dir() and not root.is_symlink(), "Inventory root must be a real directory")
    entries = []

    def visit(directory):
        with os.scandir(directory) as children:
            for child in children:
                relative = Path(child.path).relative_to(root).as_posix()
                metadata = child.stat(follow_symlinks=False)
                mode = metadata.st_mode
                kind = ("symlink" if stat.S_ISLNK(mode) else "directory" if stat.S_ISDIR(mode)
                        else "file" if stat.S_ISREG(mode) else "special")
                item = {"path": relative, "kind": kind, "bytes": metadata.st_size if kind == "file" else 0}
                if kind == "file":
                    item["category"] = category(relative, bool(mode & 0o111))
                elif kind == "symlink":
                    item["target"] = os.readlink(child.path)
                entries.append(item)
                if kind == "directory":
                    visit(child.path)

    visit(root)
    return summarize(entries, top_n)


def inventory_archive(path, top_n=20):
    """List all members without extracting or following any archive link."""
    path = Path(path)
    entries = []
    if path.name.endswith(".zip"):
        with zipfile.ZipFile(path) as archive:
            for member in archive.infolist():
                name = safe_member(member.filename)
                mode = member.external_attr >> 16
                kind = "directory" if member.is_dir() else "symlink" if stat.S_ISLNK(mode) else "file"
                item = {"path": name, "kind": kind, "bytes": member.file_size if kind == "file" else 0}
                if kind == "file":
                    item["category"] = category(name, bool(mode & 0o111))
                entries.append(item)
    elif path.name.endswith(".tar.gz"):
        with tarfile.open(path, "r:gz") as archive:
            for member in archive:
                name = safe_member(member.name)
                kind = ("file" if member.isfile() else "directory" if member.isdir()
                        else "symlink" if member.issym() else "hardlink" if member.islnk() else "special")
                item = {"path": name, "kind": kind, "bytes": member.size if kind == "file" else 0}
                if kind == "file":
                    item["category"] = category(name, bool(member.mode & 0o111))
                elif kind in ("symlink", "hardlink"):
                    item["target"] = member.linkname
                entries.append(item)
    else:
        raise InventoryError(f"Unsupported archive type: {path.name}")
    return {"archiveBytes": path.stat().st_size, **summarize(entries, top_n)}


def expected_archives(version):
    return {f"rateldesk-{tool}-{version}-{rid}.{extension}"
            for tool in ("cli", "mcp-stdio")
            for rid, extension in (("linux-x64", "tar.gz"), ("linux-arm64", "tar.gz"), ("win-x64", "zip"))
            } | {f"rateldesk-deployment-{version}.tar.gz"}


def valid_digest(value):
    return isinstance(value, str) and re.fullmatch(r"sha256:[0-9a-f]{64}", value) is not None


def validate_baseline(baseline):
    require(baseline["schemaVersion"] == 1, "Unsupported baseline schema")
    require(re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", baseline["repository"]), "Invalid repository")
    release = baseline["release"]
    require(type(release["id"]) is int and release["id"] > 0, "Invalid release id")
    require(re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?", release["version"]), "Invalid version")
    require(release["tag"] == "v" + release["version"], "Baseline version/tag mismatch")
    require(re.fullmatch(r"[0-9a-f]{40}", release["sourceRevision"]), "Invalid source revision")
    assets = baseline["assets"]
    expected = expected_archives(release["version"]) | {"SHA256SUMS", "release-manifest.json", "release-manifest.json.sha256"}
    require(len(assets) == len(expected) and {a["name"] for a in assets} == expected, "Baseline asset set mismatch")
    require(len({a["id"] for a in assets}) == len(assets), "Duplicate baseline asset id")
    for asset in assets:
        require(type(asset["id"]) is int and asset["id"] > 0, "Invalid asset id")
        require(type(asset["sizeBytes"]) is int and asset["sizeBytes"] >= 0, "Invalid asset size")
        require(valid_digest("sha256:" + asset["sha256"]), "Invalid asset digest")
    images = baseline["images"]
    expected_images = {"ghcr.io/bostontechnologies/rateldesk-api",
                       "ghcr.io/bostontechnologies/rateldesk-web",
                       "ghcr.io/bostontechnologies/rateldesk-mcp-http"}
    require(len(images) == 3 and {i["name"] for i in images} == expected_images, "Baseline image set mismatch")
    require(all(valid_digest(i["digest"]) for i in images), "Invalid image digest")


def verify_file(path, asset):
    actual = {"sizeBytes": Path(path).stat().st_size, "sha256": digest_file(path)}
    require(actual["sizeBytes"] == asset["sizeBytes"], f"Size mismatch: {asset['name']}")
    require(actual["sha256"] == asset["sha256"], f"SHA256 mismatch: {asset['name']}")
    return actual


def checksum_lines(text):
    result = {}
    for line in text.splitlines():
        match = re.fullmatch(r"([0-9a-f]{64}) [ *]([^\r\n]+)", line)
        require(match is not None, "Malformed checksum line")
        digest, name = match.groups()
        require(safe_member(name) == name and "/" not in name, "Checksum must name a single release asset")
        require(name not in result, f"Duplicate checksum name: {name}")
        result[name] = digest
    return result


def verify_release_documents(directory, baseline):
    directory = Path(directory)
    release = baseline["release"]
    expected = {a["name"]: a["sha256"] for a in baseline["assets"]
                if a["name"] in expected_archives(release["version"])}
    require(checksum_lines((directory / "SHA256SUMS").read_text()) == expected, "SHA256SUMS does not match the exact seven archives")
    manifest_path = directory / "release-manifest.json"
    require(checksum_lines((directory / "release-manifest.json.sha256").read_text()) ==
            {"release-manifest.json": digest_file(manifest_path)}, "Detached manifest checksum mismatch")
    manifest = read_json(manifest_path.read_text(encoding="utf-8"))
    for field in ("version", "tag", "sourceRevision"):
        require(manifest[field] == release[field], f"Manifest identity mismatch: {field}")
    require(manifest["assets"] == expected, "Manifest archive set/digests mismatch")
    require(sorted(manifest["supportedRids"]) == ["linux-arm64", "linux-x64", "win-x64"], "Manifest RID set mismatch")
    containers = manifest["containers"]
    expected_images = {i["name"] + ":" + release["version"]: i["digest"] for i in baseline["images"]}
    require(len(containers) == 3 and {i["image"]: i["digest"] for i in containers} == expected_images,
            "Manifest image set/digests mismatch")
    return {"status": "PASS", "archiveCount": len(expected), "identity": release["sourceRevision"]}


def verify_release_metadata(metadata, baseline):
    release = baseline["release"]
    require(metadata["id"] == release["id"] and metadata["tag_name"] == release["tag"], "Release metadata identity mismatch")
    assets = metadata["assets"]
    require(len(assets) == len(baseline["assets"]), "Live release asset count mismatch")
    require(len({a["name"] for a in assets}) == len(assets), "Duplicate live release asset name")
    by_name = {a["name"]: a for a in assets}
    for asset in baseline["assets"]:
        actual = by_name[asset["name"]]
        require(actual["id"] == asset["id"] and actual["size"] == asset["sizeBytes"], f"Live asset metadata mismatch: {asset['name']}")
        require(actual["state"] == "uploaded", f"Incomplete release asset: {asset['name']}")
        if actual.get("digest") is not None:
            require(actual["digest"] == "sha256:" + asset["sha256"], f"Live asset digest mismatch: {asset['name']}")


def release_inventory(baseline, output, report):
    assets_dir = output / "assets"
    assets_dir.mkdir(exist_ok=True)
    repository = baseline["repository"]
    try:
        metadata = read_json(command(["gh", "api", f"repos/{repository}/releases/{baseline['release']['id']}"], timeout=60))
        # Keep bounded public metadata, not uploader identities or signed URLs.
        report["releaseMetadata"] = {key: metadata.get(key) for key in ("id", "tag_name", "draft", "prerelease", "published_at", "created_at", "updated_at", "html_url")}
        verify_release_metadata(metadata, baseline)
        report["metadataStatus"] = "PASS"
    except (ValueError, KeyError, TypeError) as error:
        report["errors"].append(f"Release metadata: {error}")
        report["metadataStatus"] = "FAIL"
    report["assets"] = []
    for asset in baseline["assets"]:
        print(f"Download and verify {asset['name']}", flush=True)
        record = {"name": asset["name"], "id": asset["id"], "status": "FAIL"}
        path = assets_dir / asset["name"]
        partial = assets_dir / (asset["name"] + ".part")
        try:
            command(["gh", "api", f"repos/{repository}/releases/assets/{asset['id']}",
                     "-H", "Accept:application/octet-stream"], timeout=300, destination=partial)
            record.update(verify_file(partial, asset))
            partial.replace(path)
            if asset["name"].endswith((".zip", ".tar.gz")):
                record["inventory"] = inventory_archive(path)
            record["status"] = "PASS"
        except (InventoryError, OSError, tarfile.TarError, zipfile.BadZipFile) as error:
            record["error"] = str(error)
            report["errors"].append(f"{asset['name']}: {error}")
        finally:
            partial.unlink(missing_ok=True)
        report["assets"].append(record)
        write_json(output / "release-inventory.json", report)
    try:
        require(all(item["status"] == "PASS" for item in report["assets"]), "Some pinned release assets were not verified")
        report["documentVerification"] = verify_release_documents(assets_dir, baseline)
    except (ValueError, OSError, KeyError, TypeError) as error:
        report["documentVerification"] = {"status": "FAIL", "error": str(error)}
        report["errors"].append(f"Release documents: {error}")


def verify_raw_manifest(raw, digest, expected_size=None):
    require(valid_digest(digest), "Invalid manifest digest")
    variants = [raw] + ([raw[:-1]] if raw.endswith(b"\n") else [])
    for value in variants:
        if "sha256:" + hashlib.sha256(value).hexdigest() == digest:
            require(expected_size is None or len(value) == expected_size, "Manifest descriptor size mismatch")
            return read_json(value)
    raise InventoryError(f"Manifest bytes did not match requested digest {digest}")


def platform_manifests(index):
    require(index["schemaVersion"] == 2 and isinstance(index.get("manifests"), list), "Expected a multi-platform image index")
    selected = {}
    for descriptor in index["manifests"]:
        item = descriptor.get("platform", {})
        if item.get("os") == "unknown" and item.get("architecture") == "unknown":
            continue  # BuildKit provenance/SBOM descriptors are not runnable platforms.
        platform_name = item.get("os", "") + "/" + item.get("architecture", "")
        require(platform_name in ("linux/amd64", "linux/arm64"), f"Unexpected runnable platform: {platform_name}")
        require(platform_name not in selected, f"Duplicate platform: {platform_name}")
        require(valid_digest(descriptor["digest"]) and type(descriptor["size"]) is int and descriptor["size"] > 0, "Invalid platform descriptor")
        selected[platform_name] = descriptor
    require(set(selected) == {"linux/amd64", "linux/arm64"}, "Missing required image platform")
    return selected


def add_layers(manifest, layers):
    require(manifest["schemaVersion"] == 2 and isinstance(manifest["layers"], list), "Invalid image manifest")
    total = 0
    for layer in manifest["layers"]:
        digest, size = layer["digest"], layer["size"]
        require(valid_digest(digest) and type(size) is int and size >= 0, "Invalid layer descriptor")
        require(digest not in layers or layers[digest] == size, f"Conflicting size for layer {digest}")
        layers[digest] = size
        total += size
    return total


def local_image_inventory(reference, platform_name, manifest, baseline):
    command(["docker", "pull", "--platform", platform_name, reference], timeout=600)
    inspected = read_json(command(["docker", "image", "inspect", reference], timeout=30))
    require(len(inspected) == 1, "Image inspection returned an unexpected count")
    image = inspected[0]
    require(image["Os"] + "/" + image["Architecture"] == platform_name, "Pulled image platform mismatch")
    require(image["Id"] == manifest["config"]["digest"], "Pulled image config digest mismatch")
    require(type(image["Size"]) is int and image["Size"] >= 0, "Invalid unpacked image size")
    labels = image["Config"].get("Labels") or {}
    for field, expected in (("version", baseline["release"]["version"]), ("revision", baseline["release"]["sourceRevision"])):
        require(labels.get("org.opencontainers.image." + field) == expected, f"OCI {field} label mismatch")
    result = {"uncompressedImageBytes": image["Size"], "configDigest": image["Id"],
              "applicationStarted": False, "ociVersion": labels["org.opencontainers.image.version"],
              "ociRevision": labels["org.opencontainers.image.revision"]}
    container_id = command(["docker", "create", "--platform", platform_name,
                            "--entrypoint", "/__inventory_never_started", reference], timeout=30).decode().strip()
    require(re.fullmatch(r"[0-9a-f]{12,64}", container_id), "Invalid created-container identifier")
    failure = None
    try:
        with tempfile.TemporaryDirectory(prefix="rd-size-app-") as temporary:
            command(["docker", "cp", f"{container_id}:/app/.", temporary], timeout=180)
            result["application"] = inventory_directory(temporary)
    except (InventoryError, OSError) as error:
        failure = str(error)
    finally:
        try:
            command(["docker", "rm", container_id], timeout=30)
        except InventoryError as error:
            failure = (failure + "; " if failure else "") + f"Stopped-container cleanup failed: {error}"
    require(failure is None, failure)
    return result


def image_inventory(baseline, output, report):
    manifests_dir = output / "manifests"
    manifests_dir.mkdir(exist_ok=True)
    report["images"] = []
    all_layers, platform_layers = {}, {"linux/amd64": {}, "linux/arm64": {}}
    for image in baseline["images"]:
        reference = image["name"] + "@" + image["digest"]
        family = image["name"].rsplit("/", 1)[1]
        print(f"Inspect fixed image {reference}", flush=True)
        record = {"reference": reference, "status": "FAIL", "platforms": []}
        try:
            raw = command(["docker", "buildx", "imagetools", "inspect", "--raw", reference])
            index = verify_raw_manifest(raw, image["digest"])
            (manifests_dir / (family + "-index.json")).write_bytes(raw)
            descriptors = platform_manifests(index)
            for platform_name, descriptor in sorted(descriptors.items()):
                platform_record = {"platform": platform_name, "digest": descriptor["digest"], "status": "FAIL"}
                try:
                    ref = image["name"] + "@" + descriptor["digest"]
                    raw = command(["docker", "buildx", "imagetools", "inspect", "--raw", ref])
                    manifest = verify_raw_manifest(raw, descriptor["digest"], descriptor["size"])
                    (manifests_dir / (family + "-" + platform_name.rsplit("/", 1)[1] + ".json")).write_bytes(raw)
                    platform_record["compressedLayerBytes"] = add_layers(manifest, all_layers)
                    add_layers(manifest, platform_layers[platform_name])
                    platform_record["layers"] = manifest["layers"]
                    platform_record["registryInventoryStatus"] = "PASS"
                    platform_record.update(local_image_inventory(ref, platform_name, manifest, baseline))
                    platform_record["status"] = "PASS"
                except (ValueError, OSError, KeyError, TypeError) as error:
                    platform_record["error"] = str(error)
                    report["errors"].append(f"{family} {platform_name}: {error}")
                record["platforms"].append(platform_record)
            if all(item["status"] == "PASS" for item in record["platforms"]):
                record["status"] = "PASS"
        except (ValueError, OSError, KeyError, TypeError) as error:
            record["error"] = str(error)
            report["errors"].append(f"{family}: {error}")
        report["images"].append(record)
        write_json(output / "image-inventory.json", report)
    report["uniqueLayers"] = {"allPlatformsBytes": sum(all_layers.values()),
                              "allPlatformsCount": len(all_layers),
                              "perPlatformBytes": {key: sum(value.values()) for key, value in platform_layers.items()},
                              "complete": len(report["images"]) == 3 and all(len(item["platforms"]) == 2 and all(p.get("registryInventoryStatus") == "PASS" for p in item["platforms"]) for item in report["images"]),
                              "meaning": "Unique compressed runnable-image layers across families; excludes config, index, SBOM and provenance blobs"}


def provenance():
    value = {"python": platform.python_version(), "system": platform.system(), "machine": platform.machine()}
    for name in ("GITHUB_SHA", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_REPOSITORY", "RUNNER_OS", "RUNNER_ARCH", "GITHUB_WORKFLOW"):
        if name in os.environ:
            value[name] = os.environ[name]
    try:
        value["gitSourceRevision"] = command(["git", "rev-parse", "HEAD"], timeout=10).decode().strip()
    except InventoryError as error:
        value["gitSourceRevisionError"] = str(error)
    return value


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("release", "images"))
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)
    args.output.mkdir(parents=True, exist_ok=True)
    report = {"schemaVersion": 1, "status": "RUNNING", "operation": args.operation,
              "scope": "Read-only byte/content inventory; no runtime acceptance or optimization approval",
              "startedAt": datetime.now(timezone.utc).isoformat(), "provenance": provenance(), "errors": []}
    filename = "release-inventory.json" if args.operation == "release" else "image-inventory.json"
    write_json(args.output / filename, report)
    try:
        baseline = read_json(args.baseline.read_text(encoding="utf-8"))
        validate_baseline(baseline)
        report.update({"campaignId": baseline["campaignId"], "baselineSha256": digest_file(args.baseline),
                       "baselineRelease": baseline["release"]})
        (release_inventory if args.operation == "release" else image_inventory)(baseline, args.output, report)
    except (ValueError, OSError, KeyError, TypeError) as error:
        report["errors"].append(str(error))
    report["status"] = "FAIL" if report["errors"] else "PASS"
    report["completedAt"] = datetime.now(timezone.utc).isoformat()
    write_json(args.output / filename, report)
    print(json.dumps({"status": report["status"], "report": str(args.output / filename), "errorCount": len(report["errors"])}), flush=True)
    return 1 if report["errors"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
