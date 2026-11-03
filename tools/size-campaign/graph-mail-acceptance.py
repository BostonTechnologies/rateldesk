#!/usr/bin/env python3
"""Native, matched-source API Graph payload comparison; no publishing or deployment."""

from __future__ import annotations

import argparse
import collections
import datetime as dt
import hashlib
import importlib.util
import json
import os
import platform
from pathlib import Path
import re
import shutil
import statistics
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
TOOLS = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))
spec = importlib.util.spec_from_file_location("graph_host_tools", TOOLS / "host-telemetry-batch.py")
host = importlib.util.module_from_spec(spec)
spec.loader.exec_module(host)
api = host.api
require, write_json, execute, successful = api.require, api.write_json, api.execute, api.successful
CONTROL = "96ada6885fd765166d68e6020ddd3322213aa33a"
PROJECTS = ("src/Helpdesk.API/Helpdesk.API.csproj", "src/Helpdesk.Infrastructure/Helpdesk.Infrastructure.csproj")
CLIENT = "src/Helpdesk.Infrastructure/Email/GraphClient"


def source_contract():
    receipt = json.loads((ROOT / CLIENT / "generation-receipt.json").read_text())
    actual = {p.relative_to(ROOT / CLIENT / "Generated").as_posix(): host.sha256(p)
              for p in sorted((ROOT / CLIENT / "Generated").rglob("*.cs"))}
    require(len(actual) == 100 and actual == receipt["generatedFiles"], "Generated Graph source receipt differs")
    require(host.sha256(ROOT / CLIENT / "LICENSE.txt") == receipt["licenseSha256"], "Graph license differs")
    require(all(host.sha256(ROOT / CLIENT / name) == digest for name, digest in receipt["wrapperSha256"].items()),
            "Graph wrapper source receipt differs")
    for path in PROJECTS:
        before = ET.fromstring(host.git(ROOT, "show", CONTROL + ":" + path))
        after = ET.parse(ROOT / path).getroot()
        refs = lambda xml: {p.attrib["Include"]: p.attrib.get("Version") for p in xml.iter("PackageReference")}
        old, new = refs(before), refs(after)
        require(old.pop("Microsoft.Graph") == "6.8.0", "Control Graph version differs")
        if "Infrastructure" in path:
            require(new.pop("Microsoft.Graph.Core") == "4.0.1", "Graph Core version differs")
        require(old == new, "Unrelated NuGet dependencies changed")
    return receipt


def normalize_checkout(checkout, seconds, *, control):
    # The bounded control deliberately deletes the generated client. Every other
    # tracked source must exist; do not silently skip unexpected missing input.
    for item in host.git(checkout, "ls-files", "-z").split(b"\0"):
        if not item:
            continue
        relative = os.fsdecode(item)
        path = checkout / relative
        if not path.exists() and not path.is_symlink():
            require(control and relative.startswith(CLIENT + "/"), "Unexpected missing build input: " + relative)
            continue
        os.utime(path, (seconds, seconds), follow_symlinks=False)


def measurements(container):
    latencies = []
    for _ in range(20):
        start = time.monotonic()
        with urllib.request.urlopen("http://127.0.0.1:8222/health/db", timeout=10) as response:
            require(response.status == 200, "Database read sample failed")
            response.read()
        latencies.append(round((time.monotonic() - start) * 1000, 3))
    # Docker's Linux stats value excludes cache; keep its definition and raw units.
    memory = json.loads(api.capture(["docker", "stats", "--no-stream", "--format", "{{json .}}", container]))
    return {"scope": "Descriptive candidate samples after the real CRUD/restart smoke; no performance improvement claim",
            "databaseRead": "GET /health/db executes SELECT 1 through the configured provider",
            "databaseReadMilliseconds": latencies, "databaseReadMedianMilliseconds": statistics.median(latencies),
            "memoryUsage": memory["MemUsage"], "memoryPercent": memory["MemPerc"],
            "memoryDefinition": "docker stats --no-stream on Linux, cache excluded; one post-workload sample"}


def compare_models(images, output):
    with tempfile.TemporaryDirectory(prefix="rd-graph-models-") as temporary:
        paths = {}
        for side in ("baseline", "candidate"):
            path = Path(temporary) / side
            path.mkdir()
            container = api.capture(["docker", "create", "--entrypoint", "/__inventory_never_started", images[side]])
            try:
                subprocess.run(["docker", "cp", container + ":/app/.", str(path)], check=True, timeout=180)
            finally:
                subprocess.run(["docker", "rm", container], check=True, timeout=30, stdout=subprocess.DEVNULL)
            paths[side] = path
        require((paths["baseline"] / "Microsoft.Graph.dll").is_file(), "Full SDK missing from control")
        require(not (paths["candidate"] / "Microsoft.Graph.dll").exists(), "Full SDK still ships in candidate")
        require((paths["candidate"] / "Email/GraphClient/LICENSE.txt").read_bytes() ==
                (ROOT / CLIENT / "LICENSE.txt").read_bytes(), "API image omitted Graph license")
        result = execute(["dotnet", "run", "--project", "tools/size-campaign/graph-contract-compare/GraphContractCompare.csproj",
                          "-c", "Release", "--", str(paths["candidate"]), str(paths["baseline"] / "Microsoft.Graph.dll"),
                          str(paths["candidate"] / "Helpdesk.Infrastructure.dll"), str(output / "model-comparison.json")],
                         output / "model-comparison.log", timeout=300)
        successful(result)
        comparison = json.loads((output / "model-comparison.json").read_text())
        require(comparison["status"] == "MATCHING_MODEL_SURFACES" and comparison["candidateModelCount"] == 87 and
                comparison["differences"] == [], "Graph model contracts changed")
        return comparison


def run(architecture, output):
    output.mkdir(parents=True, exist_ok=False)
    evidence = output / "evidence"
    evidence.mkdir()
    report_path = evidence / "graph-mail-acceptance.json"
    report = {"status": "RUNNING", "releaseEligible": False, "architecture": architecture,
              "controlRevision": CONTROL, "builds": {}, "images": {}, "cases": [], "worktreeCleanup": [],
              "deploymentMode": "untrimmed framework-dependent JIT; embedded symbols retained",
              "trimAotAnalyzersEnabled": False, "startedAt": dt.datetime.now(dt.timezone.utc).isoformat()}
    checkouts = []
    write_json(report_path, report)
    try:
        require(platform.system() == "Linux" and platform.machine() == api.ARCHITECTURES[architecture][0], "Native hardware required")
        require(not host.git(ROOT, "status", "--porcelain", "--untracked-files=no").strip(), "Tracked source dirty")
        docker = json.loads(api.capture(["docker", "info", "--format", "{{json .}}"] ))
        require(docker["OSType"] == "linux" and docker["Architecture"] in (architecture, api.ARCHITECTURES[architecture][0]), "Native Docker required")
        source = host.git(ROOT, "rev-parse", "HEAD").decode().strip()
        subprocess.run(["git", "merge-base", "--is-ancestor", CONTROL, source], cwd=ROOT, check=True)
        report.update(sourceRevision=source, sourceContract=source_contract(), testSdkVersion=api.capture(["dotnet", "--version"]))
        report["fixtureBuild"] = execute(["dotnet", "build", "tests/Helpdesk.Tests/Helpdesk.Tests.csproj", "-c", "Release"], evidence / "fixture-build.log", timeout=1200)
        successful(report["fixtureBuild"])
        result = execute(["dotnet", "test", "tests/Helpdesk.Tests/Helpdesk.Tests.csproj", "-c", "Release", "--no-build",
                          "--filter", "FullyQualifiedName~GraphMailbox|FullyQualifiedName~GraphEmail|FullyQualifiedName~GraphNotification",
                          "--logger", "trx;LogFileName=graph-mail.trx", "--results-directory", str(evidence)],
                         evidence / "graph-mail-tests.log", timeout=600)
        successful(result)
        counters = ET.parse(evidence / "graph-mail.trx").find(".//{*}Counters").attrib
        require(int(counters["total"]) == 57 and int(counters["passed"]) == 57, "Expected all 57 Graph mail cases with no skips")
        report["graphFixtures"] = {"scope": "Native test process with recorded Graph transport fixtures; no live external tenant", "counters": counters}
        seconds = int(host.git(ROOT, "show", "-s", "--format=%ct", "HEAD"))
        timestamp = host.git(ROOT, "show", "-s", "--format=%cI", "HEAD").decode().strip()
        props = ET.parse(ROOT / "Directory.Build.props").getroot()
        prefix, suffix = props.findtext(".//VersionPrefix"), props.findtext(".//VersionSuffix")
        require(prefix is not None and suffix is not None and "$" not in prefix + suffix, "Literal root version required")
        version = prefix + ("-" + suffix if suffix else "")
        report.update(version=version, timestampPolicy={"sourceDateEpoch": seconds, "buildTimestamp": timestamp, "rewriteTimestamp": True, "exporters": ["docker", "oci"]})
        images = {side: f"rateldesk-{'web' if side == 'web' else 'api'}:graph-{side}-{architecture}"
                  for side in ("baseline", "candidate", "web")}
        for side in ("baseline", "candidate"):
            checkout = output / side
            subprocess.run(["git", "worktree", "add", "--detach", str(checkout), source], cwd=ROOT, check=True)
            checkouts.append(checkout)
            if side == "baseline":
                for path in PROJECTS:
                    (checkout / path).write_bytes(host.git(ROOT, "show", CONTROL + ":" + path))
                shutil.rmtree(checkout / CLIENT)
                changed = host.git(checkout, "diff", "--name-only").decode().splitlines()
                require(set(PROJECTS) <= set(changed) and all(p in PROJECTS or p.startswith(CLIENT + "/") for p in changed), "Control projection changed unrelated source")
                report["controlChangedPaths"] = changed
            normalize_checkout(checkout, seconds, control=side == "baseline")
        for side in ("baseline", "candidate", "web"):
            checkout = output / ("candidate" if side == "web" else side)
            dockerfile = "docker/" + ("web" if side == "web" else "api") + "/Dockerfile"
            oci = output / (side + ".oci.tar")
            command = ["docker", "buildx", "build", "--platform", "linux/" + architecture, "--file", str(checkout / dockerfile),
                       "--tag", images[side], "--progress", "plain", "--provenance=false", "--sbom=false",
                       "--metadata-file", str(evidence / (side + "-build-metadata.json")),
                       "--output", "type=docker,rewrite-timestamp=true", "--output", "type=oci,dest=" + str(oci) + ",compression=gzip,rewrite-timestamp=true",
                       "--build-arg", "SOURCE_DATE_EPOCH=" + str(seconds), "--build-arg", "VERSION=" + version,
                       "--build-arg", "SOURCE_REVISION_ID=" + source, "--build-arg", "BUILD_TIMESTAMP=" + timestamp, str(checkout)]
            print("Build " + side + " on native " + architecture, flush=True)
            build = execute(command, evidence / (side + "-build.log"), timeout=1800)
            report["builds"][side] = build
            write_json(report_path, report)
            successful(build)
            log = (evidence / (side + "-build.log")).read_text(errors="replace")
            build["sdk"] = api.resolved_sdk(log)
            build["imageMaterials"] = host.image_materials(json.loads((evidence / (side + "-build-metadata.json")).read_text()), architecture)
            build["warningOccurrences"] = dict(collections.Counter(re.findall(r"warning ((?:IL|NU|CS)\d+)", log)))
            if side != "web":
                require(not build["warningOccurrences"], "API publish has warnings; inspect retained build log")
                report["images"][side] = api.measure_image(images[side], oci, architecture, evidence, side)
            oci.unlink()
            write_json(report_path, report)
        host.compare_builds(report["builds"]["baseline"], report["builds"]["candidate"])
        report["deltas"] = host.compare_images(report["images"]["baseline"], report["images"]["candidate"], -1)
        report["modelComparison"] = compare_models(images, evidence)
        pull = execute(["docker", "pull", "postgres:16"], evidence / "postgres-pull.log", timeout=300)
        successful(pull)
        report["postgresImage"] = json.loads(api.capture(["docker", "image", "inspect", "postgres:16"]))[0]["Id"]
        for provider in ("Sqlite", "PostgreSql"):
            for mode in ("fresh", "upgrade"):
                print("Exercise " + provider + " " + mode, flush=True)
                report["cases"].append(api.provider_case(provider, mode, architecture, images, evidence, measurement=measurements))
                write_json(report_path, report)
        require(len(report["cases"]) == 4 and all(c["status"] == "PASS" for c in report["cases"]), "Not all native provider cases passed")
        report["status"] = "PASS"
    except (RuntimeError, OSError, ValueError, KeyError, TypeError, ET.ParseError, tarfile.TarError, subprocess.SubprocessError) as error:
        report.update(status="FAIL", error=str(error))
        print("Graph mail acceptance failed: " + str(error), file=sys.stderr)
    finally:
        for checkout in reversed(checkouts):
            cleanup = execute(["git", "worktree", "remove", "--force", str(checkout)], evidence / (checkout.name + "-cleanup.log"), timeout=120)
            report["worktreeCleanup"].append(cleanup)
            if cleanup["status"] != "PASS":
                report["status"] = "FAIL"
        report["finishedAt"] = dt.datetime.now(dt.timezone.utc).isoformat()
        write_json(report_path, report)
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--architecture", choices=api.ARCHITECTURES, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    raise SystemExit(run(args.architecture, args.output.resolve()))
