#!/usr/bin/env python3
"""Prepare and validate the optional dependency-only native client campaign."""

from __future__ import annotations

import argparse
import copy
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import signal
import subprocess
import sys
import tarfile
import time
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[2]
TOOLS = Path(__file__).resolve().parent
ORIGINAL_REVISION = "59248cacce44ad33a1c3367f0eb000e8705e072b"
RECEIPT_PATH = Path("tools/size-campaign/client-dependency-receipt.json")
BASELINE_PATH = Path("tools/size-campaign/client-dependency-baseline.json")
PROJECT_PATHS = tuple(f"src/Helpdesk.{name}/Helpdesk.{name}.csproj" for name in
                      ("AgentClient", "Setup", "Mcp", "Shared", "Infrastructure", "API"))
ADDITIONS = {
    "AgentClient": ("Microsoft.Extensions.Configuration.Binder", "10.0.13"),
    "Setup": ("Microsoft.Extensions.Hosting", "10.0.13"),
    "Mcp": ("Microsoft.Extensions.Hosting", "10.0.13"),
    "Infrastructure": ("Npgsql", "10.0.3"),
    "API": ("Npgsql", "10.0.3"),
}


class BatchFailure(RuntimeError):
    pass


def load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def write_json(path: Path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def git(root: Path, *arguments: str) -> bytes:
    return subprocess.check_output(["git", "-C", str(root), *arguments])


def run(command: list[str], cwd: Path, log: Path, timeout: int = 900, environment=None) -> dict:
    log.parent.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    result = {"command": command, "executionStatus": "COMPLETED", "exitCode": None, "log": log.name}
    with log.open("w", encoding="utf-8") as output:
        output.write("Command: " + json.dumps(command) + "\n")
        output.flush()
        try:
            process = subprocess.Popen(command, cwd=cwd, stdout=output, stderr=subprocess.STDOUT,
                                       env=environment, start_new_session=os.name != "nt")
            try:
                result["exitCode"] = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                result["executionStatus"] = "TIMED_OUT"
                if os.name == "nt":
                    subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                                   stdout=output, stderr=subprocess.STDOUT, timeout=30, check=False)
                else:
                    os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=30)
                result["exitCode"] = process.returncode
                output.write(f"\nTimed out after {timeout} seconds.\n")
        except OSError as error:
            result["executionStatus"] = "BLOCKED"
            output.write(f"\nCould not execute command: {error}\n")
    result["elapsedSeconds"] = round(time.monotonic() - started, 3)
    return result


def require_success(result: dict):
    if result["executionStatus"] != "COMPLETED" or result["exitCode"] != 0:
        raise BatchFailure(f"{result['log']}: {result['executionStatus']} / exit {result['exitCode']}")


def baseline(root: Path) -> dict:
    value = json.loads((root / BASELINE_PATH).read_text(encoding="utf-8"))
    paths = [item["path"] for item in value["files"]]
    if value.get("schemaVersion") != 1 or value.get("originalSourceRevision") != ORIGINAL_REVISION or sorted(paths) != sorted(PROJECT_PATHS):
        raise BatchFailure("Original client project manifest does not match the pinned six-file scope")
    if not re.fullmatch(r"\d+\.\d+\.\d+", value.get("sdk", "")):
        raise BatchFailure("Original client project manifest has no exact SDK")
    return value


def original_projects(root: Path, original: dict) -> dict[str, bytes]:
    result = {}
    for item in original["files"]:
        content = git(root, "show", ORIGINAL_REVISION + ":" + item["path"])
        if hashlib.sha256(content).hexdigest() != item["sha256"]:
            raise BatchFailure("Pinned original project hash mismatch: " + item["path"])
        result[item["path"]] = content
    return result


def canonical_xml(element):
    return (element.tag, tuple(sorted(element.attrib.items())), (element.text or "").strip(),
            tuple(canonical_xml(child) for child in element))


def project_parts(content: bytes):
    tree = ET.fromstring(content)
    items = {"PackageReference": {}, "FrameworkReference": {}}
    for group in list(tree):
        if group.tag != "ItemGroup":
            continue
        for child in list(group):
            if child.tag in items:
                identity = child.attrib.get("Include")
                if not identity or identity in items[child.tag] or group.attrib:
                    raise BatchFailure("Unexpected duplicate or conditional dependency declaration")
                items[child.tag][identity] = canonical_xml(child)
                group.remove(child)
        if not list(group) and not group.attrib and not (group.text or "").strip():
            tree.remove(group)
    return canonical_xml(tree), items


def validate_project_delta(original: bytes, current: bytes, name: str):
    before_tree, before = project_parts(original)
    after_tree, after = project_parts(current)
    expected = copy.deepcopy(before)
    if name in ("AgentClient", "Setup", "Mcp"):
        if expected["FrameworkReference"].pop("Microsoft.AspNetCore.App", None) is None:
            raise BatchFailure("Expected original ASP.NET framework declaration")
    if name == "Shared":
        if expected["PackageReference"].pop("Npgsql", None) is None:
            raise BatchFailure("Expected original Shared Npgsql declaration")
    else:
        package, version = ADDITIONS[name]
        expected["PackageReference"][package] = canonical_xml(ET.Element("PackageReference", Include=package, Version=version))
    if before_tree != after_tree or expected != after:
        raise BatchFailure("Project differs beyond the declared dependency-only changes: " + name)


def validate_receipt(root: Path, original: dict, receipt: dict, sdk: str):
    if receipt.get("schemaVersion") != 1 or receipt.get("originalSourceRevision") != ORIGINAL_REVISION:
        raise BatchFailure("Invalid client preparation receipt identity")
    if receipt.get("sdk") != sdk or sdk != original["sdk"]:
        raise BatchFailure("Preparation receipt, campaign SDK and active SDK differ")
    if not re.fullmatch(r"[0-9a-f]{40}", receipt.get("preparedFromSourceRevision", "")):
        raise BatchFailure("Receipt has no exact preparation source revision")
    script = root / "tools/size-campaign/prepare-client-dependencies.sh"
    if receipt.get("preparationScriptSha256") != sha256(script):
        raise BatchFailure("Receipt preparation script hash differs from the checked-in generator")
    entries = receipt.get("files", [])
    if not isinstance(entries, list) or sorted(item.get("path", "") for item in entries) != sorted(PROJECT_PATHS):
        raise BatchFailure("Receipt must identify exactly the six generated project files")
    expected = {item["path"]: item["sha256"] for item in original["files"]}
    for item in entries:
        path = item["path"]
        if item.get("originalSha256") != expected[path] or not re.fullmatch(r"[0-9a-f]{64}", item.get("candidateSha256", "")):
            raise BatchFailure("Receipt project hash is invalid: " + path)
        if item["candidateSha256"] == expected[path] or sha256(root / path) != item["candidateSha256"]:
            raise BatchFailure("Checked-in project does not equal its hosted generation receipt: " + path)


def validate_preparation_ancestry(root: Path, receipt: dict):
    result = subprocess.run(["git", "-C", str(root), "merge-base", "--is-ancestor",
                             receipt["preparedFromSourceRevision"], "HEAD"], capture_output=True, check=False)
    if result.returncode != 0:
        raise BatchFailure("Receipt preparation source is not an ancestor of the checked-in candidate")


def output_values(values: dict):
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            for key, value in values.items():
                output.write(f"{key}={value}\n")


def prepare(output: Path) -> int:
    output.mkdir(parents=True, exist_ok=True)
    original = baseline(ROOT)
    originals = original_projects(ROOT, original)
    sdk = subprocess.check_output(["dotnet", "--version"], cwd=ROOT, text=True).strip()
    if sdk != original["sdk"]:
        raise BatchFailure(f"Expected SDK {original['sdk']}; active SDK is {sdk}")
    source = git(ROOT, "rev-parse", "HEAD").decode().strip()
    receipt_path = ROOT / RECEIPT_PATH
    if receipt_path.exists():
        receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
        validate_receipt(ROOT, original, receipt, sdk)
        validate_preparation_ancestry(ROOT, receipt)
        state = "checked-in"
    else:
        for path, content in originals.items():
            if (ROOT / path).read_bytes() != content:
                raise BatchFailure("Changed project has no committed generation receipt: " + path)
        generated = run(["bash", str(TOOLS / "prepare-client-dependencies.sh"), str(output), "--generate-only"],
                        ROOT, output / "generation-command.log", timeout=300)
        write_json(output / "generation-command.json", generated)
        require_success(generated)
        receipt = {"schemaVersion": 1, "originalSourceRevision": ORIGINAL_REVISION,
                   "preparedFromSourceRevision": source, "sdk": sdk,
                   "preparationScriptSha256": sha256(TOOLS / "prepare-client-dependencies.sh"),
                   "packageGeneration": "dotnet add/remove package; final restore and native acceptance are separate gates",
                   "files": [{"path": path, "originalSha256": hashlib.sha256(content).hexdigest(),
                              "candidateSha256": sha256(ROOT / path)} for path, content in originals.items()]}
        state = "generated"
    for path, content in originals.items():
        name = Path(path).stem.removeprefix("Helpdesk.")
        validate_project_delta(content, (ROOT / path).read_bytes(), name)
        destination = output / "files" / path
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / path, destination)
    validate_receipt(ROOT, original, receipt, sdk)
    write_json(output / "files" / RECEIPT_PATH, receipt)
    patch = git(ROOT, "diff", ORIGINAL_REVISION, "--", *PROJECT_PATHS)
    (output / "project-files.patch").write_bytes(patch)
    write_json(output / "preparation-state.json", {"status": "PASS", "state": state, "sourceRevision": source,
               "sdk": sdk, "nativeAcceptance": "READY_TO_RUN" if state == "checked-in" else "NOT_RUN_UNTIL_COMMITTED"})
    output_values({"state": state, "sdk": sdk})
    print(json.dumps({"state": state, "sdk": sdk, "generatedFiles": str(output / "files")}))
    return 0


def native_rid() -> str:
    system, machine = platform.system(), platform.machine().lower()
    if system == "Linux" and machine in ("x86_64", "amd64"):
        return "linux-x64"
    if system == "Linux" and machine in ("aarch64", "arm64"):
        return "linux-arm64"
    if system == "Windows" and machine in ("amd64", "x86_64"):
        return "win-x64"
    raise BatchFailure(f"Unsupported native host: {system}/{machine}")


def release_version(root: Path) -> str:
    properties = ET.parse(root / "Directory.Build.props").getroot()
    prefix, suffix = properties.findtext(".//VersionPrefix"), properties.findtext(".//VersionSuffix")
    value = prefix + ("-" + suffix if suffix else "")
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", value):
        raise BatchFailure("Root product version is not a supported SemVer")
    return value


def extract_archive(archive: Path, destination: Path):
    destination.mkdir(parents=True, exist_ok=True)
    def allowed(name: str):
        path = PurePosixPath(name)
        if not name or path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name:
            raise BatchFailure("Archive has an unsafe member path")
    if archive.suffix == ".zip":
        with zipfile.ZipFile(archive) as stream:
            for item in stream.infolist():
                allowed(item.filename)
                if (item.external_attr >> 16) & 0o170000 == 0o120000:
                    raise BatchFailure("Archive has a symbolic link")
            stream.extractall(destination)
    else:
        with tarfile.open(archive, "r:gz") as stream:
            for item in stream.getmembers():
                allowed(item.name)
                if not item.isfile() and not item.isdir():
                    raise BatchFailure("Archive has a non-regular member")
            stream.extractall(destination, filter="data")
    roots = list(destination.iterdir())
    if len(roots) != 1 or not roots[0].is_dir():
        raise BatchFailure("Archive must have exactly one package root")
    return roots[0]


def package(root: Path, publish: Path, work: Path, evidence: Path, target: str, rid: str, version: str, source: str, command):
    app, prefix = ("rateldesk", "rateldesk-cli") if target == "cli" else ("rateldesk-mcp", "rateldesk-mcp-stdio")
    name = f"{prefix}-{version}-{rid}"
    staged = work / name
    shutil.copytree(publish, staged)
    shutil.copytree(root / "tools/mcp", staged, dirs_exist_ok=True, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    shutil.copy2(root / "LICENSE", staged / "LICENSE")
    command([sys.executable, str(root / "tools/release/package-documentation.py"), str(staged), "--source-revision", source],
            evidence / f"{target}-documentation.log", 180)
    (staged / "README.txt").write_text(
        f"RatelDesk {app} {version}\n\n"
        f"Run ./{app} --help or ./{app} --version after extracting. Run ./{app} setup explicitly from an interactive terminal for client onboarding. Linux archives require a glibc-based distribution; they are not Alpine/musl builds. Configure credentials in a protected file or environment variable, never on a command line.\n\n"
        "Canonical configuration/setup guidance is bundled in docs/integrations/cli-stdio.md and docs/mcp-http/. Third-party notices and licenses are included alongside LICENSE.\n", encoding="utf-8")
    archives = evidence / "archives"
    archives.mkdir(parents=True, exist_ok=True)
    archive = archives / (name + (".zip" if rid == "win-x64" else ".tar.gz"))
    if rid == "win-x64":
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as stream:
            for path in sorted(staged.rglob("*")):
                if path.is_file():
                    stream.write(path, path.relative_to(work).as_posix())
    else:
        with tarfile.open(archive, "w:gz", compresslevel=6) as stream:
            stream.add(staged, arcname=name)
    return archive, app


def archive_smoke_command(archive_shell: Path, archives: Path, rid: str) -> list[str]:
    # On Windows, PATH can select System32's WSL bash instead of the Git Bash
    # already running the workflow. Require that exact shell's native path.
    if not archive_shell.is_absolute() or not archive_shell.is_file():
        raise BatchFailure("Archive acceptance requires an existing absolute Bash executable path")
    return [str(archive_shell), "tools/release/test-executable-archives.sh", archives.as_posix(), rid, "--validate-docs"]


def execute_batch(output: Path, rid: str, sdk: str, archive_shell: Path) -> int:
    evidence, work = output / "evidence", output / "work"
    if output.exists():
        raise BatchFailure("Use a fresh batch output directory")
    evidence.mkdir(parents=True)
    work.mkdir()
    source = git(ROOT, "rev-parse", "HEAD").decode().strip()
    report = {"schemaVersion": 1, "sourceRevision": source, "originalProjectRevision": ORIGINAL_REVISION,
              "sdk": sdk, "rid": rid, "platform": platform.platform(), "machine": platform.machine(),
              "runId": os.environ.get("GITHUB_RUN_ID"), "status": "RUNNING", "releaseEligible": False,
              "sides": {side: {"status": "NOT_RUN", "commands": [], "targets": {}} for side in ("control", "candidate")}}
    report_path = evidence / "batch-report.json"
    write_json(report_path, report)
    def save():
        write_json(report_path, report)
    try:
        archive_smoke_command(archive_shell, evidence / "control/archives", rid)
        report["archiveShell"] = str(archive_shell)
        if native_rid() != rid:
            raise BatchFailure("Selected RID does not match the native execution host")
        active_sdk = subprocess.check_output(["dotnet", "--version"], cwd=ROOT, text=True).strip()
        original = baseline(ROOT)
        originals = original_projects(ROOT, original)
        receipt = json.loads((ROOT / RECEIPT_PATH).read_text(encoding="utf-8"))
        validate_receipt(ROOT, original, receipt, active_sdk)
        validate_preparation_ancestry(ROOT, receipt)
        if active_sdk != sdk:
            raise BatchFailure("Native runner SDK differs from preparation")
        for path, content in originals.items():
            validate_project_delta(content, (ROOT / path).read_bytes(), Path(path).stem.removeprefix("Helpdesk."))
        write_json(evidence / "receipt-check.json", {"status": "PASS", "receipt": receipt})
        inventory = load_module("client_batch_inventory", ROOT / "tools/size-campaign/inventory.py")
        closure = load_module("client_batch_closure", TOOLS / "check-client-publish.py")
        discovery = load_module("client_batch_discovery", TOOLS / "client-discovery.py")
        version = release_version(ROOT)
        report["version"] = version
        timestamp = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        report["buildTimestamp"] = timestamp
        snapshots = {}
        with discovery.backend_tripwire() as tripwire:
            for side in ("control", "candidate"):
                side_report = report["sides"][side]
                side_evidence, side_work = evidence / side, work / side
                side_evidence.mkdir(); side_work.mkdir()
                checkout = side_work / "source"
                side_report["status"] = "RUNNING"
                save()
                def command(argv, log, timeout=900, environment=None):
                    result = run(argv, checkout, log, timeout, environment)
                    side_report["commands"].append(result)
                    save()
                    require_success(result)
                    return result
                try:
                    # Both worktrees are the same commit. Only the declared
                    # project files are changed in the control dependency graph.
                    added = run(["git", "-c", "core.longpaths=true", "-c", "core.autocrlf=false", "-c", "core.eol=lf", "worktree", "add", "--detach", str(checkout), source], ROOT,
                                side_evidence / "checkout.log", timeout=180)
                    side_report["commands"].append(added); save(); require_success(added)
                    if side == "control":
                        for path, content in originals.items():
                            (checkout / path).write_bytes(content)
                    changed = git(checkout, "diff", "--name-only").decode().splitlines()
                    expected_changes = sorted(PROJECT_PATHS) if side == "control" else []
                    if sorted(changed) != expected_changes:
                        raise BatchFailure("Control/candidate source differs beyond its declared six project files")
                    (side_evidence / "source-diff.patch").write_bytes(git(checkout, "diff"))
                    side_report["projectFiles"] = [{"path": path, "sha256": sha256(checkout / path)} for path in PROJECT_PATHS]
                    archives, extracted = [], {}
                    for target, project in (("cli", "Cli"), ("stdio", "Mcp")):
                        publish = side_work / (target + "-publish")
                        command(["dotnet", "publish", f"src/Helpdesk.{project}/Helpdesk.{project}.csproj", "-c", "Release",
                                 "-r", rid, "--self-contained", "true", "-o", str(publish),
                                 "-p:PublishTrimmed=false", "-p:PublishAot=false", "-p:PublishSingleFile=false",
                                 "-p:PublishReadyToRun=false", "-p:DebugType=embedded", "-p:DebugSymbols=true",
                                 "-p:ContinuousIntegrationBuild=true", "-p:Version=" + version,
                                 "-p:SourceRevisionId=" + source, "-p:BuildTimestamp=" + timestamp],
                                side_evidence / (target + "-publish.log"), 900)
                        assets = checkout / f"src/Helpdesk.{project}/obj/project.assets.json"
                        shutil.copy2(assets, side_evidence / (target + "-project.assets.json"))
                        archive, app = package(checkout, publish, side_work, side_evidence, target, rid, version, source, command)
                        archives.append(archive)
                        archive_inventory = inventory.inventory_archive(archive)
                        root = extract_archive(archive, side_work / (target + "-extracted"))
                        extracted[target] = root / (app + (".exe" if rid == "win-x64" else ""))
                        target_report = {"publishInventory": inventory.inventory_directory(publish),
                                         "archive": archive.name, "archiveSha256": sha256(archive),
                                         "archiveInventory": archive_inventory, "symbolPolicy": "embedded-debug-retained"}
                        if side == "candidate":
                            target_report["closure"] = closure.check_publish(root, target, rid)
                            write_json(side_evidence / (target + "-closure.json"), target_report["closure"])
                        side_report["targets"][target] = target_report
                        save()
                        if side == "candidate" and target_report["closure"]["status"] != "PASS":
                            raise BatchFailure("Candidate published closure failed for " + target)
                    hashes = {path.name: sha256(path) for path in archives}
                    write_json(side_evidence / "archives/release-manifest.json", {"version": version, "sourceRevision": source,
                               "supportedRids": [rid], "assets": hashes})
                    (side_evidence / "archives/SHA256SUMS").write_text("".join(f"{digest}  {name}\n" for name, digest in sorted(hashes.items())))
                    environment = os.environ.copy()
                    environment["RATELDESK_ARCHIVE_PTY_ACCEPTANCE"] = "true" if rid.startswith("linux-") else "false"
                    command(archive_smoke_command(archive_shell, side_evidence / "archives", rid),
                            side_evidence / "archive-smoke.log", 600, environment)
                    snapshots[side] = discovery.capture_discovery(extracted["stdio"], side_evidence / "discovery", tripwire["origin"])
                    if tripwire["requests"]:
                        raise BatchFailure("Discovery made an unexpected request to the synthetic API tripwire")
                    side_report["status"] = "PASS"
                except Exception as error:
                    side_report["status"] = "FAIL"
                    side_report["error"] = str(error)
                save()
            report["backendRequests"] = tripwire["requests"]
        if len(snapshots) == 2:
            report["discoveryParity"] = discovery.compare_snapshots(snapshots["control"], snapshots["candidate"])
        else:
            report["discoveryParity"] = {"status": "NOT_RUN", "reason": "Both complete discovery snapshots are required"}
        report["comparison"] = {}
        for target in ("cli", "stdio"):
            control = report["sides"]["control"]["targets"].get(target)
            candidate = report["sides"]["candidate"]["targets"].get(target)
            if control and candidate:
                before, after = control["archiveInventory"], candidate["archiveInventory"]
                report["comparison"][target] = {"controlArchiveBytes": before["archiveBytes"],
                    "candidateArchiveBytes": after["archiveBytes"], "downloadBytesSaved": before["archiveBytes"] - after["archiveBytes"],
                    "downloadPercentSaved": round(100 * (1 - after["archiveBytes"] / before["archiveBytes"]), 6),
                    "controlExpandedBytes": before["expandedBytes"], "candidateExpandedBytes": after["expandedBytes"]}
        report["sizeAcceptance"] = {"status": "PASS" if len(report["comparison"]) == 2 and all(
            item["downloadBytesSaved"] > 0 and item["candidateExpandedBytes"] < item["controlExpandedBytes"]
            for item in report["comparison"].values()) else "FAIL"}
        report["status"] = "PASS" if (all(side["status"] == "PASS" for side in report["sides"].values())
                and report["discoveryParity"]["status"] == "PASS" and report["sizeAcceptance"]["status"] == "PASS"
                and not report["backendRequests"]) else "FAIL"
    except Exception as error:
        report["status"] = "FAIL"
        report["error"] = str(error)
    finally:
        save()
    print(json.dumps({"status": report["status"], "rid": rid, "comparison": report.get("comparison", {}),
                      "discoveryParity": report.get("discoveryParity"), "report": str(report_path)}))
    return 0 if report["status"] == "PASS" else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    preparation = commands.add_parser("prepare")
    preparation.add_argument("--output", type=Path, required=True)
    native = commands.add_parser("run")
    native.add_argument("--output", type=Path, required=True)
    native.add_argument("--rid", choices=("linux-x64", "linux-arm64", "win-x64"), required=True)
    native.add_argument("--sdk", required=True)
    native.add_argument("--bash", type=Path, required=True, help="Absolute native path of the workflow's active Bash executable")
    args = parser.parse_args()
    try:
        if args.command == "prepare":
            return prepare(args.output.resolve())
        return execute_batch(args.output.resolve(), args.rid, args.sdk, args.bash)
    except Exception as error:
        print("Client batch failed: " + str(error), file=sys.stderr)
        if args.command == "prepare":
            write_json(args.output / "preparation-state.json", {"status": "FAIL", "error": str(error)})
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
