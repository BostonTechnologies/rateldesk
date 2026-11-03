#!/usr/bin/env python3
"""Run isolated size experiments; retain failures and never publish a release."""

from __future__ import annotations

import argparse
from collections import Counter
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import tarfile
import time

from inventory import inventory_archive, inventory_directory


ROOT = Path(__file__).resolve().parents[2]
PROJECTS = {
    "cli": "src/Helpdesk.Cli/Helpdesk.Cli.csproj",
    "stdio": "src/Helpdesk.Mcp/Helpdesk.Mcp.csproj",
    "http": "src/Helpdesk.Mcp.Http/Helpdesk.Mcp.Http.csproj",
    "api": "src/Helpdesk.API/Helpdesk.API.csproj",
    "web": "src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj",
}
IL_CODE = re.compile(r"\bIL[23]\d{3}\b")


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def diagnostics(text: str) -> dict:
    messages = sorted({line.strip() for line in text.splitlines()
                       if IL_CODE.search(line) and ("warning" in line.lower() or "error" in line.lower())})
    counts = Counter(code for line in messages for code in set(IL_CODE.findall(line)))
    return {"uniqueDiagnosticCount": len(messages), "byCode": dict(sorted(counts.items())),
            "messages": messages}


def run(command: list[str], log: Path, timeout: int = 900) -> dict:
    """Keep the complete diagnostic log, including unsuccessful/timeout commands."""
    log.parent.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    status, code = "COMPLETED", None
    with log.open("w", encoding="utf-8") as stream:
        stream.write("Command: " + json.dumps(command) + "\n")
        stream.flush()
        try:
            process = subprocess.Popen(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT,
                                       start_new_session=os.name != "nt")
            try:
                code = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                status = "TIMED_OUT"
                if os.name == "nt":
                    process.kill()
                else:
                    os.killpg(process.pid, signal.SIGKILL)
                process.wait()
                stream.write(f"\nTimed out after {timeout} seconds.\n")
        except FileNotFoundError as error:
            status = "BLOCKED"
            stream.write(f"\nMissing executable: {error.filename}\n")
    content = log.read_text(encoding="utf-8", errors="replace")
    return {"command": command, "executionStatus": status, "exitCode": code,
            "elapsedSeconds": round(time.monotonic() - started, 3),
            "log": str(log.relative_to(log.parent)), "diagnostics": diagnostics(content)}


def completed_ok(result: dict) -> bool:
    return result["executionStatus"] == "COMPLETED" and result["exitCode"] == 0


def publish_status(result: dict) -> str:
    if result["executionStatus"] in ("BLOCKED", "TIMED_OUT"):
        return result["executionStatus"]
    return "PUBLISHED" if completed_ok(result) else "PUBLISH_FAILED"


def dependency_graph(target: str) -> dict:
    assets = (ROOT / PROJECTS[target]).parent / "obj/project.assets.json"
    if not assets.is_file():
        return {"status": "NOT_AVAILABLE"}
    data = json.loads(assets.read_text(encoding="utf-8"))
    libraries = data.get("libraries", {})
    frameworks = data.get("project", {}).get("frameworks", {})
    return {"status": "CAPTURED", "assetsSha256": hashlib.sha256(assets.read_bytes()).hexdigest(),
            "packages": sorted(key for key, value in libraries.items() if value.get("type") == "package"),
            "projects": sorted(key for key, value in libraries.items() if value.get("type") == "project"),
            "targetGraphs": sorted(data.get("targets", {})),
            "frameworkReferences": {key: sorted(value.get("frameworkReferences", {}))
                                    for key, value in frameworks.items()}}


def base_command(target: str, mode: str, directory: Path, metadata: dict) -> tuple[list[str], dict]:
    client = target in ("cli", "stdio")
    self_contained = client or mode in ("trimmed", "aot")
    rid = "linux-x64" if self_contained else None
    if mode == "rid-specific":
        rid = "linux-x64" if target == "api" else "linux-musl-x64"
    command = ["dotnet", "publish", PROJECTS[target], "--configuration", "Release",
               "--self-contained", str(self_contained).lower(), "--output", str(directory),
               "--verbosity", "minimal", "-p:Version=" + metadata["version"],
               "-p:SourceRevisionId=" + metadata["sourceRevision"],
               "-p:BuildTimestamp=" + metadata.get("buildTimestamp", metadata["startedAt"]),
               "-p:ContinuousIntegrationBuild=true", "-p:UseSharedCompilation=false", "-m:1", "-nr:false",
               "-p:PublishTrimmed=" + str(mode in ("trimmed", "aot")).lower(),
               "-p:PublishAot=" + str(mode == "aot").lower(),
               "-p:PublishSingleFile=false", "-p:PublishReadyToRun=false"]
    if rid:
        command += ["--runtime", rid]
    if not self_contained:
        command.append("-p:UseAppHost=false")
    if mode == "portable-symbols":
        command += ["-p:DebugType=portable", "-p:DebugSymbols=true"]
    else:
        command += ["-p:DebugType=embedded", "-p:DebugSymbols=true"]
    if mode in ("trimmed", "aot"):
        command += ["-p:TrimmerSingleWarn=false",
                    "-p:SuppressTrimAnalysisWarnings=false"]
    elif mode == "analysis":
        command += ["-p:EnableTrimAnalyzer=true", "-p:EnableAotAnalyzer=true",
                    "-p:EnableSingleFileAnalyzer=true", "-p:TrimmerSingleWarn=false",
                    "-p:SuppressTrimAnalysisWarnings=false"]
    deployment = "native-aot" if mode == "aot" else ("self-contained" if self_contained else "framework-dependent")
    return command, {"runtimeIdentifier": rid, "deploymentMode": deployment,
                     "shippingContainerEquivalent": False if self_contained and not client else None}


def capture_archived_catalog(archive: Path, evidence: Path) -> dict:
    spec = importlib.util.spec_from_file_location("publish_probe_discovery", Path(__file__).with_name("client-discovery.py"))
    discovery = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(discovery)
    directory = evidence / "discovery"
    directory.mkdir()
    result = {"status": "FAIL", "archiveSha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
              "scope": "Full backend-free archived stdio discovery only; not complete profile acceptance"}
    with discovery.backend_tripwire() as tripwire:
        try:
            extracted = evidence / "work" / "discovery-extracted"
            extracted.mkdir()
            with tarfile.open(archive, "r:gz") as stream:
                stream.extractall(extracted, filter="data")
            roots = list(extracted.iterdir())
            if len(roots) != 1 or not roots[0].is_dir():
                raise ValueError("Catalog archive must contain exactly one package root")
            files = {path.relative_to(roots[0]).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                     for path in sorted(roots[0].rglob("*")) if path.is_file()}
            write_json(directory / "executed-files.json", files)
            result["executedFileManifestSha256"] = hashlib.sha256((directory / "executed-files.json").read_bytes()).hexdigest()
            snapshot = discovery.capture_discovery(roots[0] / "rateldesk-mcp", directory, tripwire["origin"])
            if tripwire["requests"]:
                raise discovery.DiscoveryFailure("Catalog discovery contacted its backend")
            result.update(status="PASS", counts={key: len(snapshot[key]) for key in ("tools", "resources", "resourceTemplates", "prompts")},
                          snapshotSha256=hashlib.sha256((directory / "snapshot.json").read_bytes()).hexdigest())
        except Exception as error:
            result["error"] = str(error)
        result["backendRequests"] = tripwire["requests"]
    write_json(directory / "result.json", result)
    return result


def make_package(target: str, publish: Path, work: Path, evidence: Path, metadata: dict, mode: str) -> dict:
    tool, prefix = ("rateldesk", "rateldesk-cli") if target == "cli" else ("rateldesk-mcp", "rateldesk-mcp-stdio")
    name = f"{prefix}-{metadata['version']}-linux-x64"
    package = work / name
    shutil.copytree(publish, package)
    symbol_bytes = 0
    if mode == "portable-symbols":
        symbols = evidence / "symbols" / target
        for pdb in list(package.rglob("*.pdb")):
            relative = pdb.relative_to(package)
            destination = symbols / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            symbol_bytes += pdb.stat().st_size
            shutil.move(str(pdb), str(destination))
    shutil.copytree(ROOT / "tools/mcp", package, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    shutil.copy2(ROOT / "LICENSE", package / "LICENSE")
    documentation = run([sys.executable, "tools/release/package-documentation.py", str(package),
                         "--source-revision", metadata["sourceRevision"]],
                        evidence / f"{target}-documentation.log", timeout=120)
    if not completed_ok(documentation):
        return {"status": "FAILED", "documentation": documentation}
    (package / "README.txt").write_text(
        f"RatelDesk {tool} {metadata['version']}\n\n"
        f"Run ./{tool} --help or ./{tool} --version after extracting. Run ./{tool} setup explicitly from an interactive terminal for client onboarding. Linux archives require a glibc-based distribution; they are not Alpine/musl builds. Configure credentials in a protected file or environment variable, never on a command line.\n\n"
        "Canonical configuration/setup guidance is bundled in docs/integrations/cli-stdio.md and docs/mcp-http/. Third-party notices and licenses are included alongside LICENSE.\n",
        encoding="utf-8")
    archive = work / "archives" / (name + ".tar.gz")
    archive.parent.mkdir(parents=True, exist_ok=True)
    packed = run(["tar", "-C", str(work), "-czf", str(archive), name],
                 evidence / f"{target}-archive.log", timeout=180)
    if not completed_ok(packed):
        return {"status": "FAILED", "packaging": packed}
    package_inventory = inventory_archive(archive)
    return {"status": "PACKAGED", "name": archive.name,
            "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
            "inventory": package_inventory, "separateSymbolBytes": symbol_bytes,
            "symbolsRetainedInArchiveBytes": package_inventory["categories"].get("symbols", {}).get("bytes", 0),
            "symbolPolicy": "separate-portable-pdb" if mode == "portable-symbols" else "retain-all-publish-output"}


def probe(target: str, mode: str, work: Path, evidence: Path, metadata: dict) -> dict:
    directory = work / (target + "-publish")
    command, profile = base_command(target, mode, directory, metadata)
    result = run(command, evidence / (target + "-publish.log"), timeout=600)
    value = {"target": target, "mode": mode, **profile, "publish": result,
             "publishStatus": publish_status(result), "acceptance": "NOT_ACCEPTED",
             "dependencyGraph": dependency_graph(target)}
    if completed_ok(result):
        value["publishInventory"] = inventory_directory(directory)
        value["compatibilityStatus"] = ("UNRESOLVED_IL_DIAGNOSTICS"
            if result["diagnostics"]["uniqueDiagnosticCount"] else "PUBLISHED_NO_IL_DIAGNOSTICS")
        if target in ("cli", "stdio"):
            value["package"] = make_package(target, directory, work, evidence, metadata, mode)
            if target == "stdio" and value["package"]["status"] == "PACKAGED":
                value["catalogDiscovery"] = capture_archived_catalog(work / "archives" / value["package"]["name"], evidence)
        elif mode == "portable-symbols":
            # Host-side measurement only; these files are retained as diagnostics,
            # and no changed container is built or promoted by this probe.
            pdbs = list(directory.rglob("*.pdb"))
            value["separateSymbolBytes"] = sum(p.stat().st_size for p in pdbs)
            value["applicationBytesExcludingSeparateSymbols"] = (
                value["publishInventory"]["expandedBytes"] - value["separateSymbolBytes"])
            for pdb in pdbs:
                destination = evidence / "symbols" / target / pdb.relative_to(directory)
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(pdb, destination)
    else:
        value["compatibilityStatus"] = "NOT_ESTABLISHED"
    return value


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("target", choices=["clients", "api", "web", "http"])
    parser.add_argument("--mode", choices=["baseline", "portable-symbols", "trimmed", "aot", "analysis", "rid-specific"], required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.target == "clients" and args.mode not in ("baseline", "portable-symbols", "trimmed", "aot"):
        parser.error("The client campaign uses baseline, portable-symbols, trimmed or aot.")
    if args.target in ("web", "api") and args.mode in ("trimmed", "aot"):
        parser.error("Web/API use an analysis lane; full trim/AOT research has separate issue prerequisites.")
    evidence = args.output.resolve()
    evidence.mkdir(parents=True, exist_ok=True)
    work = evidence / "work"
    if work.exists():
        parser.error("Use a fresh output directory for each profile; refusing mixed candidate files.")
    work.mkdir()
    source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    baseline = json.loads((ROOT / "tools/size-campaign/baseline-0.1.3-beta.3.json").read_text())
    metadata = {"schemaVersion": 1, "campaignId": baseline["campaignId"],
                "sourceRevision": source, "releaseSourceRevision": baseline["release"]["sourceRevision"],
                "version": baseline["release"]["version"], "platform": platform.platform(),
                "machine": platform.machine(), "runId": os.environ.get("GITHUB_RUN_ID"),
                "startedAt": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")}
    epoch = int(subprocess.check_output(["git", "show", "-s", "--format=%ct", source], cwd=ROOT, text=True))
    metadata["buildTimestamp"] = dt.datetime.fromtimestamp(epoch, dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    sdk = run(["dotnet", "--info"], evidence / "sdk.log", timeout=30)
    report = {**metadata, "mode": args.mode, "sdk": sdk, "results": [],
              "runtimeAcceptance": "NOT_RUN", "releaseEligible": False}
    report_path = evidence / "publish-report.json"
    write_json(report_path, report)
    if not completed_ok(sdk):
        report["status"] = "BLOCKED"
        write_json(report_path, report)
        return 1
    targets = ["cli", "stdio"] if args.target == "clients" else [args.target]
    for target in targets:
        result = probe(target, args.mode, work, evidence, metadata)
        report["results"].append(result)
        write_json(report_path, report)
        print(json.dumps({"target": target, "mode": args.mode,
                          "publishStatus": result["publishStatus"],
                          "ILDiagnosticCount": result["publish"]["diagnostics"]["uniqueDiagnosticCount"],
                          "publishBytes": result.get("publishInventory", {}).get("expandedBytes"),
                          "archiveBytes": result.get("package", {}).get("inventory", {}).get("archiveBytes")}), flush=True)
    if args.target == "clients" and all(r.get("package", {}).get("status") == "PACKAGED" for r in report["results"]):
        assets = work / "archives"
        hashes = {r["package"]["name"]: r["package"]["sha256"] for r in report["results"]}
        write_json(assets / "release-manifest.json", {"version": metadata["version"],
                   "sourceRevision": source, "assets": hashes, "supportedRids": ["linux-x64"]})
        (assets / "SHA256SUMS").write_text("".join(f"{digest}  {name}\n" for name, digest in sorted(hashes.items())))
        smoke = run(["bash", "tools/release/test-executable-archives.sh", str(assets), "linux-x64", "--validate-docs"],
                    evidence / "archive-smoke.log", timeout=180)
        report["archiveSmoke"] = smoke
        report["runtimeAcceptance"] = "SMOKE_PASSED" if completed_ok(smoke) else "SMOKE_FAILED"
    report["status"] = "DISCOVERY_COMPLETE"
    # Successful discovery is distinct from candidate acceptance. Trim/AOT
    # failures remain explicit evidence and never become a runtime PASS.
    report["requiredFollowup"] = ["Full published-candidate functional suite", "Native ARM64 and Windows validation",
                                  "Warning resolution", "Measured deployment startup/memory/latency"]
    write_json(report_path, report)
    print("CAMPAIGN_RESULT " + json.dumps({"target": args.target, "mode": args.mode,
          "status": report["status"], "runtimeAcceptance": report["runtimeAcceptance"],
          "releaseEligible": False}), flush=True)
    baseline_failed = args.mode == "baseline" and (
        any(r["publishStatus"] != "PUBLISHED" for r in report["results"]) or
        (args.target == "clients" and (report["runtimeAcceptance"] != "SMOKE_PASSED" or
         any(r["target"] == "stdio" and r.get("catalogDiscovery", {}).get("status") != "PASS" for r in report["results"]))))
    return 1 if baseline_failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
