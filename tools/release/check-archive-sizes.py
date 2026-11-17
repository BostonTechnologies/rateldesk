#!/usr/bin/env python3
"""Measure the complete release payload and enforce its compressed archive budgets."""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path, PurePosixPath
import stat
import sys
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[2]
BUDGETS = Path(__file__).with_name("archive-size-budgets.json")
SUPPORT_FILES = {"LICENSE", "README.txt", "SECURITY.md", ".env.example", "setup-release.json"}
SUPPORT_DIRS = {"docker", "tools", "vendor"}
ARCHIVES = {(family, rid) for family in ("rateldesk-cli", "rateldesk-mcp-stdio")
            for rid in ("linux-x64", "linux-arm64", "win-x64")} | {("rateldesk-deployment", None)}


def archive_name(family, rid, version):
    identity = f"{family}-{version}"
    return identity + (f"-{rid}" if rid else "") + (".zip" if rid == "win-x64" else ".tar.gz")


def files(path):
    if path.suffix == ".zip":
        with zipfile.ZipFile(path) as archive:
            for entry in archive.infolist():
                if not entry.is_dir() and not stat.S_ISLNK(entry.external_attr >> 16):
                    yield entry.filename, entry.file_size
    else:
        with tarfile.open(path, "r|gz") as archive:
            for entry in archive:
                if entry.isfile():
                    yield entry.name, entry.size


def measure(path, budget):
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    archive_root = path.name.removesuffix(".tar.gz").removesuffix(".zip")
    helpers = {child.name for child in (ROOT / "tools/mcp").iterdir()}
    expanded = Counter({"runtime": 0, "documentation": 0, "support": 0})
    largest, seen = [], set()
    for name, size in files(path):
        parts = PurePosixPath(name).parts
        if len(parts) < 2 or parts[0] != archive_root or ".." in parts or parts in seen:
            raise ValueError(f"{path.name}: unsafe or duplicate archive entry {name}")
        seen.add(parts)
        top_level = parts[1]
        if top_level in {".agents", ".slopwatch"} or parts[1:3] == ("docs", "implementation"):
            raise ValueError(f"{path.name}: unexpected developer material {name}")
        if top_level in {"docs", "licenses"}:
            category = "documentation"
        elif budget["rid"] is None or top_level in SUPPORT_FILES | SUPPORT_DIRS | helpers:
            category = "support"
        else:
            category = "runtime"
        expanded[category] += size
        largest.append((size, "/".join(parts[1:])))
    if not seen:
        raise ValueError(f"{path.name}: empty archive")
    return {
        "file": path.name,
        "rid": budget["rid"],
        "compressedBytes": path.stat().st_size,
        "maximumCompressedBytes": budget["maximumCompressedBytes"],
        "referenceCompressedBytes": budget["referenceCompressedBytes"],
        "measuredCompressedBytes": budget["measuredCompressedBytes"],
        "expandedBytes": sum(expanded.values()),
        "expandedBytesByCategory": dict(expanded),
        "sha256": digest,
        "largestFiles": [{"path": name, "bytes": size} for size, name in sorted(largest, reverse=True)[:5]],
    }


def verify(directory, version, budgets, report):
    rows = budgets["archives"]
    if len(rows) != len(ARCHIVES) or {(row["family"], row["rid"]) for row in rows} != ARCHIVES:
        raise ValueError("archive budgets must contain exactly the seven supported identities")
    for row in rows:
        maximum, reference = row["maximumCompressedBytes"], row["referenceCompressedBytes"]
        if type(maximum) is not int or type(reference) is not int or not 0 < maximum <= reference:
            raise ValueError(f"{row['referenceAsset']}: budget must be positive and within the published recovery ceiling")
    expected = {archive_name(row["family"], row["rid"], version): row for row in rows}
    present = {path.name for path in directory.iterdir() if path.name.endswith((".zip", ".tar.gz"))}
    if expected.keys() != present:
        raise ValueError(f"release payload mismatch: missing {sorted(expected.keys() - present)}, unexpected {sorted(present - expected.keys())}")

    for name, budget in sorted(expected.items()):
        path = directory / name
        if path.is_symlink() or not path.is_file():
            raise ValueError(f"{name}: release archive must be a regular file")
        report["archives"].append(measure(path, budget))
    for archive in report["archives"]:
        if archive["compressedBytes"] > archive["maximumCompressedBytes"]:
            report["errors"].append(
                f"{archive['file']}: {archive['compressedBytes']} bytes exceeds {archive['maximumCompressedBytes']}")
    return not report["errors"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("version")
    parser.add_argument("source_revision")
    parser.add_argument("sdk_version")
    args = parser.parse_args()
    report = {
        "version": args.version, "sourceRevision": args.source_revision, "sdkVersion": args.sdk_version,
        "publish": {"configuration": "Release", "selfContained": True, "trimmed": False, "nativeAot": False},
        "archives": [], "errors": [],
    }
    try:
        verify(args.directory, args.version, json.loads(BUDGETS.read_text(encoding="utf-8")), report)
    except (OSError, ValueError, KeyError, tarfile.TarError, zipfile.BadZipFile) as error:
        report["errors"].append(str(error))
    (args.directory / "archive-sizes.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    for archive in report["archives"]:
        print(f"{archive['file']} {archive['compressedBytes']} / {archive['maximumCompressedBytes']} compressed bytes")
    for error in report["errors"]:
        print(error, file=sys.stderr)
    return int(bool(report["errors"]))


if __name__ == "__main__":
    sys.exit(main())
