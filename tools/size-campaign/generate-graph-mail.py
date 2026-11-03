#!/usr/bin/env python3
"""Reproduce the bounded Graph mail client from pinned public metadata and Kiota."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
METADATA_COMMIT = "29d027ab701b8d16c5fafc8cee31bb22d29eecad"
METADATA_SHA256 = "69de68b2c895bc608a58229e662e2b3eeb23524385516afc250ff0b8f165ff29"
KIOTA_VERSION = "1.35.0"
OPERATIONS = [
    "/users/{user-id}/mailFolders#GET",
    "/users/{user-id}/mailFolders#POST",
    "/users/{user-id}/mailFolders/{mailFolder-id}#GET",
    "/users/{user-id}/mailFolders/{mailFolder-id}/messages/delta()#GET",
    "/users/{user-id}/messages#GET",
    "/users/{user-id}/messages/{message-id}#GET",
    "/users/{user-id}/messages/{message-id}#PATCH",
    "/users/{user-id}/messages/{message-id}/$value#GET",
    "/users/{user-id}/messages/{message-id}/attachments#GET",
    "/users/{user-id}/messages/{message-id}/move#POST",
    "/users/{user-id}/sendMail#POST",
]
DEFAULT_OUTPUT = ROOT / "src/Helpdesk.Infrastructure/Email/GraphClient/Generated"


def hashes(root):
    return {path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(root.rglob("*.cs"))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-directory", type=Path, required=True, help="Fresh temporary directory for metadata/tool/output")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--check", action="store_true", help="Compare generated C# bytes without editing the checked-in client")
    args = parser.parse_args()
    work = args.work_directory.resolve()
    work.mkdir(parents=True, exist_ok=False)
    description = work / "openapi.yaml"
    url = f"https://raw.githubusercontent.com/microsoftgraph/msgraph-metadata/{METADATA_COMMIT}/openapi/v1.0/openapi.yaml"
    with urllib.request.urlopen(url, timeout=120) as response, description.open("wb") as stream:
        shutil.copyfileobj(response, stream)
    if hashlib.sha256(description.read_bytes()).hexdigest() != METADATA_SHA256:
        raise ValueError("Pinned Graph metadata SHA256 does not match")
    tool = work / "tool"
    subprocess.run(["dotnet", "tool", "install", "Microsoft.OpenApi.Kiota", "--version", KIOTA_VERSION,
                    "--tool-path", str(tool), "--allow-roll-forward"], cwd=ROOT, check=True)
    executable = tool / ("kiota.exe" if os.name == "nt" else "kiota")
    version = subprocess.check_output([str(executable), "--version"], cwd=ROOT, text=True).strip()
    if not version.startswith(KIOTA_VERSION + "+"):
        raise ValueError("Unexpected Kiota executable version")
    generated = work / "Generated"
    command = [str(executable), "generate", "--openapi", str(description), "--output", str(generated),
               "--language", "CSharp", "--class-name", "BaseGraphServiceClient", "--namespace-name", "Microsoft.Graph",
               "--backing-store", "--additional-data"]
    for operation in OPERATIONS:
        command += ["--include-path", operation]
    subprocess.run(command, cwd=ROOT, check=True, timeout=900)
    expected = hashes(generated)
    if not expected:
        raise ValueError("Generator produced no C# source")
    if args.check:
        if expected != hashes(args.output):
            raise ValueError("Checked-in Graph client differs from pinned generation")
    else:
        for old in args.output.rglob("*.cs"):
            if old.relative_to(args.output).as_posix() not in expected:
                old.unlink()
        for name in expected:
            destination = args.output / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(generated / name, destination)
    (work / "generated-files.json").write_text(json.dumps(expected, indent=2) + "\n")
    print(f"Verified {len(expected)} pinned Graph C# files" if args.check else f"Wrote {len(expected)} pinned Graph C# files")


if __name__ == "__main__":
    main()
