#!/usr/bin/env python3
"""Native final image inventories, Web alias/symbol experiments and archive lifecycle."""

import argparse
import collections
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import tarfile
import time
import urllib.request
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
TOOLS = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))
spec = importlib.util.spec_from_file_location("final_host_helpers", TOOLS / "host-telemetry-batch.py")
host = importlib.util.module_from_spec(spec)
spec.loader.exec_module(host)
api = host.api
require, write_json, execute, successful = api.require, api.write_json, api.execute, api.successful
WEB_CONTROL = "96ada6885fd765166d68e6020ddd3322213aa33a"


def comparison(before, after):
    require(len(before["layers"]) == len(after["layers"]), "Layer count differs")
    app = -2 if after["config"]["config"]["Entrypoint"] == ["dotnet", "Helpdesk.Mcp.Http.dll"] else -1
    selected = len(before["layers"]) + app
    for i in range(len(before["layers"])):
        if i != selected:
            require(before["layers"][i] == after["layers"][i] and before["rootfsDiffIds"][i] == after["rootfsDiffIds"][i], "Non-application layers differ")
    require(before["config"]["config"] == after["config"]["config"], "Image execution settings differ")
    return {name: {"baseline": old, "candidate": new, "savedBytes": old - new, "savedPercent": 100 * (old - new) / old}
            for name, old, new in (("compressedLayerBytes", before["compressedLayerBytes"], after["compressedLayerBytes"]),
                                   ("uncompressedImageBytes", before["uncompressedImageBytes"], after["uncompressedImageBytes"]),
                                   ("applicationRegularFileBytes", before["application"]["expandedBytes"], after["application"]["expandedBytes"]))}


def browser_case(image, output, name):
    container = "rd-static-" + name
    result = {"status": "FAIL", "kind": "Published Web static browser checks", "variant": name}
    started = time.monotonic()
    try:
        subprocess.run(["docker", "run", "--detach", "--name", container, "--publish", "127.0.0.1:18111:8111",
                        "--env", "ApiBaseUrl=http://127.0.0.1:8222", image], check=True, stdout=subprocess.DEVNULL)
        deadline = time.monotonic() + 90
        while True:
            try:
                with urllib.request.urlopen("http://127.0.0.1:18111/_framework/blazor.web.js", timeout=3) as response:
                    require(response.status == 200 and "javascript" in response.headers.get("Content-Type", ""), "Invalid Blazor asset")
                    response.read()
                break
            except OSError:
                require(time.monotonic() < deadline, "Published Web readiness timed out")
                time.sleep(0.5)
        result["firstAssetReadyMilliseconds"] = round((time.monotonic() - started) * 1000, 3)
        observed = api.capture(["docker", "inspect", "--format", "{{.Image}}", container])
        require(observed == api.capture(["docker", "image", "inspect", "--format", "{{.Id}}", image]), "Browser used wrong image")
        result["executedConfigDigest"] = observed
        result["command"] = execute(["node", "tools/size-campaign/web-static-acceptance.mjs", "http://127.0.0.1:18111", str(output / name)],
                                    output / (name + "-browser.log"), timeout=300)
        successful(result["command"])
        receipt = json.loads((output / name / "web-static.json").read_text())
        require(receipt["status"] == "PASS" and len(receipt["checks"]) == 20, "Expected 20 real browser static checks")
        result["browserReceipt"] = name + "/web-static.json"
        result["checks"] = len(receipt["checks"])
        stats = json.loads(api.capture(["docker", "stats", "--no-stream", "--format", "{{json .}}", container]))
        result["memorySample"] = {"usage": stats["MemUsage"], "definition": "docker stats Linux cache-excluded sample after browser work"}
        result["status"] = "PASS"
    finally:
        result["logs"] = execute(["docker", "logs", container], output / (name + "-container.log"), timeout=60)
        result["cleanup"] = execute(["docker", "rm", "--force", container], output / (name + "-cleanup.log"), timeout=60)
        require(result["cleanup"]["status"] == "PASS", "Browser container cleanup failed")
    return result


def run(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    evidence = output / "evidence"
    evidence.mkdir()
    report = {"status": "RUNNING", "sourceRevision": api.capture(["git", "rev-parse", "HEAD"]), "architecture": args.architecture,
              "releaseEligible": False, "builds": {}, "images": {}, "cases": [], "symbolResearch": {}, "trimAotAdopted": False}
    receipt_path = evidence / "host-final.json"
    write_json(receipt_path, report)
    control = output / "web-control"
    try:
        require(platform.system() == "Linux" and platform.machine() == api.ARCHITECTURES[args.architecture][0], "Native matching hardware required")
        require(not host.git(ROOT, "status", "--porcelain", "--untracked-files=no").strip(), "Tracked source dirty")
        manifest = json.loads((args.archives / "release-manifest.json").read_text())
        require(manifest["sourceRevision"] == report["sourceRevision"] and len(manifest["assets"]) == 7, "Final archives have stale source or wrong set")
        for name, digest in manifest["assets"].items():
            require(host.sha256(args.archives / name) == digest, "Final archive hash differs: " + name)
        report["archiveManifest"] = manifest
        seconds = int(host.git(ROOT, "show", "-s", "--format=%ct", "HEAD"))
        timestamp = host.git(ROOT, "show", "-s", "--format=%cI", "HEAD").decode().strip()
        report["timestampPolicy"] = {"sourceDateEpoch": seconds, "buildTimestamp": timestamp, "bothExportersRewriteTimestamp": True}
        props = ET.parse(ROOT / "Directory.Build.props").getroot()
        version = props.findtext(".//VersionPrefix") + "-" + props.findtext(".//VersionSuffix")
        subprocess.run(["git", "worktree", "add", "--detach", str(control), report["sourceRevision"]], cwd=ROOT, check=True)
        (control / "docker/web/Dockerfile").write_bytes(host.git(ROOT, "show", WEB_CONTROL + ":docker/web/Dockerfile"))
        require(host.git(control, "diff", "--name-only").decode().splitlines() == ["docker/web/Dockerfile"], "Web control projection changed other source")
        host.normalize_checkout(control, seconds)
        host.normalize_checkout(ROOT, seconds)
        names = {}
        definitions = [("api", "api", False), ("web-control", "web", False), ("web", "web", False), ("http", "mcp-http", False),
                       ("api-portable", "api", True), ("web-portable", "web", True), ("http-portable", "mcp-http", True)]
        for name, folder, portable in definitions:
            context = control if name == "web-control" else ROOT
            dockerfile = context / ("docker/" + folder + "/Dockerfile")
            if portable:
                text = dockerfile.read_text()
                require(text.count("p:UseAppHost=false") == 1, "Unexpected publish property boundary")
                dockerfile = evidence / (name + ".Dockerfile")
                dockerfile.write_text(text.replace("p:UseAppHost=false", "p:UseAppHost=false -p:DebugType=portable -p:DebugSymbols=true"))
            image = "rateldesk-final:" + name + "-" + args.architecture
            names[name] = image
            oci = output / (name + ".oci.tar")
            command = ["docker", "buildx", "build", "--platform", "linux/" + args.architecture, "--file", str(dockerfile), "--tag", image,
                       "--progress", "plain", "--provenance=false", "--sbom=false", "--metadata-file", str(evidence / (name + "-metadata.json")),
                       "--output", "type=docker,rewrite-timestamp=true", "--output", "type=oci,dest=" + str(oci) + ",compression=gzip,rewrite-timestamp=true",
                       "--build-arg", "SOURCE_DATE_EPOCH=" + str(seconds), "--build-arg", "VERSION=" + version,
                       "--build-arg", "SOURCE_REVISION_ID=" + report["sourceRevision"], "--build-arg", "BUILD_TIMESTAMP=" + timestamp, str(context)]
            print("Build " + name + " on native " + args.architecture, flush=True)
            build = execute(command, evidence / (name + "-build.log"), timeout=1800)
            report["builds"][name] = build
            write_json(receipt_path, report)
            successful(build)
            log = (evidence / (name + "-build.log")).read_text(errors="replace")
            build["sdk"] = api.resolved_sdk(log)
            build["imageMaterials"] = host.image_materials(json.loads((evidence / (name + "-metadata.json")).read_text()), args.architecture)
            build["warningOccurrences"] = dict(collections.Counter(re.findall(r"warning ((?:IL|NU|CS)\d+)", log)))
            require(not build["warningOccurrences"], "Unresolved image publish warnings")
            assembly = {"api": "Helpdesk.API", "web": "HelpDesk.NewWeb", "mcp-http": "Helpdesk.Mcp.Http"}[folder]
            report["images"][name] = host.measure(image, oci, args.architecture, evidence, name, assembly, database=folder == "api")
            oci.unlink()
            write_json(receipt_path, report)
        host.compare_builds(report["builds"]["web-control"], report["builds"]["web"])
        report["webAliasDeltas"] = comparison(report["images"]["web-control"], report["images"]["web"])
        require(all(metric["savedBytes"] > 0 for metric in report["webAliasDeltas"].values()), "PNG alias did not reduce complete image and regular-file bytes")
        for name in ("api", "web", "http"):
            portable = name + "-portable"
            host.compare_builds(report["builds"][name], report["builds"][portable])
            report["symbolResearch"][name] = {"status": "MEASURED_NOT_ADOPTED", "symbolPolicy": "Portable PDBs retained in image; no symbol deletion", "deltas": comparison(report["images"][name], report["images"][portable])}
        for name in ("web-control", "web"):
            case = browser_case(names[name], evidence, name + "-" + args.architecture)
            require(case["executedConfigDigest"] == report["images"][name]["configDigest"], "Browser did not execute measured Web config")
            report["cases"].append(case)
            write_json(receipt_path, report)
        # Execute the existing owning suites with their synthetic API fixtures,
        # replacing only each Web process with the measured image. Local/OIDC/
        # Hybrid presentation and role/circuit journeys retain their assertions.
        for suite in ("login", "ai", "assignee", "email", "filters", "details", "operations", "admin", "pending", "integrations"):
            environment = os.environ.copy()
            result_json = evidence / ("browser-" + suite + ".json")
            environment.update(HELPDESK_PUBLISHED_WEB_IMAGE=names["web"], HELPDESK_PUBLISHED_SUITE=suite,
                               HELPDESK_PUBLISHED_WEB_CONFIG_DIGEST=report["images"]["web"]["configDigest"],
                               HELPDESK_PUBLISHED_WEB_PROVENANCE=str(evidence / ("browser-" + suite + "-images")),
                               PLAYWRIGHT_JSON_OUTPUT_NAME=str(result_json), CI="true")
            case = execute(["npx", "playwright", "test", "--config", "playwright.published.config.ts", "--workers", "1",
                            "--reporter", "json", "--output", str(evidence / ("browser-" + suite))],
                           evidence / ("browser-" + suite + ".log"), environment=environment, timeout=1500)
            case.update(kind="Existing owning browser suite against measured Web image", suite=suite,
                        executedConfigDigest=report["images"]["web"]["configDigest"])
            report["cases"].append(case)
            write_json(receipt_path, report)
            successful(case)
            stats = json.loads(result_json.read_text())["stats"]
            require(stats["expected"] > 0 and stats["unexpected"] == 0, "Browser suite has no passing assertions or unexpected failures")
            case["browserStats"] = stats
            observed = [json.loads(p.read_text()) for p in (evidence / ("browser-" + suite + "-images")).glob("*.json")]
            require(observed and all(item["observedConfigDigest"] == report["images"]["web"]["configDigest"] for item in observed),
                    "Browser suite has missing or mismatched executed-image receipts")
            case["imageReceipts"] = observed
            remaining = api.capture(["docker", "ps", "--all", "--quiet", "--filter", "label=rateldesk.acceptance.published-web=true"])
            require(not remaining, "Published browser fixture left a container behind")
        successful(execute(["docker", "pull", "postgres:16"], evidence / "postgres-pull.log", timeout=300))
        for provider in ("Sqlite", "PostgreSql"):
            for mode in ("fresh", "upgrade"):
                images = {"baseline": names["api"], "candidate": names["api"], "web": names["web"]}
                case = api.provider_case(provider, mode, args.architecture, images, evidence)
                case["scope"] = "Final-source fresh setup / retained-data recreation on selected image; causal control-to-candidate upgrade was checked in owning optimization batches"
                require(case.get("executedCandidateConfigDigest") == report["images"]["api"]["configDigest"], "Provider smoke used wrong API image")
                report["cases"].append(case)
                write_json(receipt_path, report)
                require(case["status"] == "PASS", "Final native provider case failed")
        config = execute(["bash", "tools/ci/test-mcp-compose-config.sh", names["http"]], evidence / "http-config.log", timeout=360)
        successful(config)
        report["cases"].append(config)
        runtime_path = evidence / "http-runtime.json"
        gateway = execute([sys.executable, "tools/size-campaign/host-telemetry-http.py", "--api-image", names["api"], "--web-image", names["web"], "--mcp-image", names["http"], "--output", str(runtime_path)], evidence / "http-runtime.log", timeout=900)
        successful(gateway)
        runtime = json.loads(runtime_path.read_text())
        require(runtime["status"] == "PASS", "Final gateway fixture failed")
        for service, name in (("api", "api"), ("web", "web"), ("mcp-http", "http")):
            require(runtime["executedConfigDigests"][service] == report["images"][name]["configDigest"], "Gateway fixture used a different measured image")
        report["cases"].append(gateway)
        rid = api.ARCHITECTURES[args.architecture][1]
        archive = args.archives / f"rateldesk-mcp-stdio-{manifest['version']}-{rid}.tar.gz"
        extracted = output / "native"
        extracted.mkdir()
        subprocess.run(["tar", "-xzf", str(archive), "-C", str(extracted)], check=True)
        # Executable archives contain a single named root; locate only this RID's app.
        candidates = [p for p in extracted.rglob("rateldesk-mcp") if p.is_file() and p.stat().st_mode & 0o111]
        require(len(candidates) == 1, "Ambiguous extracted native stdio executable")
        deployment = args.archives / f"rateldesk-deployment-{manifest['version']}.tar.gz"
        for case in ("all", "legacy"):
            result = execute([sys.executable, "tools/ci/run-mcp-host-lifecycle-acceptance.py", "--api-image", names["api"], "--web-image", names["web"], "--mcp-image", names["http"], "--native-executable", str(candidates[0]), "--deployment-archive", str(deployment), "--case", case], evidence / ("extracted-lifecycle-" + case + ".log"), timeout=1200)
            report["cases"].append(result)
            write_json(receipt_path, report)
            successful(result)
        require(len(report["cases"]) == 20 and all(case["status"] == "PASS" for case in report["cases"]), "Not all owning final cases passed")
        report["status"] = "PASS"
    except Exception as error:
        report.update(status="FAIL", error=str(error))
        print("Final host acceptance failed: " + str(error), file=sys.stderr)
    finally:
        if control.exists():
            cleanup = execute(["git", "worktree", "remove", "--force", str(control)], evidence / "control-cleanup.log", timeout=120)
            report["controlCleanup"] = cleanup
            if cleanup["status"] != "PASS":
                report["status"] = "FAIL"
        report["finishedAt"] = dt.datetime.now(dt.timezone.utc).isoformat()
        write_json(receipt_path, report)
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--architecture", choices=api.ARCHITECTURES, required=True)
    parser.add_argument("--archives", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    raise SystemExit(run(parser.parse_args()))
