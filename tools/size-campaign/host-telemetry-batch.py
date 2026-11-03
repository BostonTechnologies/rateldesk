#!/usr/bin/env python3
"""SDK-generated telemetry ownership changes and native paired-image acceptance."""

from __future__ import annotations

import argparse
import collections
import copy
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import secrets
import shutil
import subprocess
import sys
import tarfile
import tempfile
from urllib.parse import parse_qs, urlsplit
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
TOOLS = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))
spec = importlib.util.spec_from_file_location("telemetry_api_image", TOOLS / "api-rid-acceptance.py")
api = importlib.util.module_from_spec(spec)
spec.loader.exec_module(api)
require, write_json, execute, successful = api.require, api.write_json, api.execute, api.successful
BASELINE = Path("tools/size-campaign/host-telemetry-baseline.json")
RECEIPT = Path("tools/size-campaign/host-telemetry-receipt.json")
GENERATOR = Path("tools/size-campaign/prepare-host-telemetry.sh")
SOURCE_PATHS = ("src/Helpdesk.API/Program.cs", "src/Helpdesk.ServiceDefaults/Extensions.cs")
PROJECT_PATHS = ("src/Helpdesk.API/Helpdesk.API.csproj", "src/Helpdesk.ServiceDefaults/Helpdesk.ServiceDefaults.csproj")
OWNERSHIP_PATHS = SOURCE_PATHS + PROJECT_PATHS
HOSTS = {
    "web": {"dockerfile": "docker/web/Dockerfile", "assembly": "HelpDesk.NewWeb", "appLayer": -1},
    "http": {"dockerfile": "docker/mcp-http/Dockerfile", "assembly": "Helpdesk.Mcp.Http", "appLayer": -2},
}


def git(root: Path, *arguments: str) -> bytes:
    return subprocess.check_output(["git", "-C", str(root), *arguments], timeout=60)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def inputs(root: Path) -> tuple[dict, dict[str, bytes]]:
    baseline = json.loads((root / BASELINE).read_text())
    require(baseline.get("schemaVersion") == 1 and re.fullmatch(r"[0-9a-f]{40}", baseline.get("originalSourceRevision", "")),
            "Baseline needs an immutable original source revision")
    require(re.fullmatch(r"\d+\.\d+\.\d+", baseline.get("sdk", "")), "Baseline needs an exact SDK")
    require(sorted(item["path"] for item in baseline["files"]) == sorted(OWNERSHIP_PATHS),
            "Baseline must cover exactly the four ownership files")
    originals = {}
    for item in baseline["files"]:
        data = git(root, "show", baseline["originalSourceRevision"] + ":" + item["path"])
        require(hashlib.sha256(data).hexdigest() == item["sha256"], "Original hash mismatch: " + item["path"])
        originals[item["path"]] = data
    return baseline, originals


def expected_source(path: str, original: bytes) -> bytes:
    if path == SOURCE_PATHS[0]:
        before = b"builder.AddServiceDefaults();\n"
        after = (before + b"// Keep database instrumentation with the host that owns Npgsql.\n"
                 b"builder.Services.AddOpenTelemetry()\n    .WithTracing(tracing => tracing.AddNpgsql());\n")
        require(original.count(before) == 1, "Unexpected API registration source")
        return original.replace(before, after)
    require(path == SOURCE_PATHS[1], "Unknown source ownership file")
    require(original.count(b"using Npgsql;\n") == 1 and
            original.count(b".AddHttpClientInstrumentation()\n                    .AddNpgsql();") == 1,
            "Unexpected shared registration source")
    return original.replace(b"using Npgsql;\n", b"").replace(
        b".AddHttpClientInstrumentation()\n                    .AddNpgsql();", b".AddHttpClientInstrumentation();")


def canonical_xml(element):
    return (element.tag, tuple(sorted(element.attrib.items())), (element.text or "").strip(),
            tuple(canonical_xml(child) for child in element))


def project_parts(content: bytes):
    tree = ET.fromstring(content)
    packages = {}
    for group in list(tree):
        if group.tag != "ItemGroup":
            continue
        for child in list(group):
            if child.tag != "PackageReference":
                continue
            name = child.attrib.get("Include")
            require(name and name not in packages and not group.attrib, "Unexpected package declaration")
            packages[name] = canonical_xml(child)
            group.remove(child)
        if not list(group) and not group.attrib and not (group.text or "").strip():
            tree.remove(group)
    return canonical_xml(tree), packages


def validate_project(original: bytes, candidate: bytes, path: str) -> None:
    before_tree, before = project_parts(original)
    after_tree, after = project_parts(candidate)
    expected = copy.deepcopy(before)
    package = "Npgsql.OpenTelemetry"
    declaration = canonical_xml(ET.Element("PackageReference", Include=package, Version="10.0.3"))
    if path == PROJECT_PATHS[0]:
        require(package not in expected and "Npgsql" in expected, "Original API must retain its direct client-batch driver")
        expected[package] = declaration
    else:
        require(path == PROJECT_PATHS[1] and expected.pop(package, None) == declaration,
                "Unexpected original database instrumentation package")
    require(before_tree == after_tree and expected == after, "Project changed beyond package ownership: " + path)


def validate_sources(root: Path, originals: dict[str, bytes]) -> None:
    for path in SOURCE_PATHS:
        require((root / path).read_bytes() == expected_source(path, originals[path]),
                "Source differs beyond the registration move: " + path)


def validate_receipt(root: Path, baseline: dict, originals: dict[str, bytes], receipt: dict, sdk: str) -> None:
    require(receipt.get("schemaVersion") == 1 and receipt.get("originalSourceRevision") == baseline["originalSourceRevision"],
            "Receipt original source differs from baseline")
    require(receipt.get("sdk") == baseline["sdk"] == sdk, "Receipt, baseline and current SDK differ")
    require(re.fullmatch(r"[0-9a-f]{40}", receipt.get("preparedFromSourceRevision", "")), "Receipt has no preparation source")
    require(receipt.get("preparationScriptSha256") == sha256(root / GENERATOR), "Generation script changed after preparation")
    require(sorted(item.get("path", "") for item in receipt.get("files", [])) == sorted(PROJECT_PATHS),
            "Receipt must identify exactly two generated projects")
    for item in receipt["files"]:
        path = item["path"]
        require(item.get("originalSha256") == hashlib.sha256(originals[path]).hexdigest(), "Receipt original hash differs")
        require(item.get("candidateSha256") == sha256(root / path), "Checked-in project differs from SDK receipt: " + path)
        validate_project(originals[path], (root / path).read_bytes(), path)
    validate_sources(root, originals)


def ancestry(root: Path, source: str) -> None:
    result = subprocess.run(["git", "-C", str(root), "merge-base", "--is-ancestor", source, "HEAD"],
                            capture_output=True, timeout=60, check=False)
    require(result.returncode == 0, "Preparation source is not an ancestor of the candidate")


def output_values(state: str, sdk: str) -> None:
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as stream:
            stream.write(f"state={state}\nsdk={sdk}\n")


def prepare(output: Path) -> int:
    output.mkdir(parents=True, exist_ok=True)
    report = {"status": "RUNNING", "releaseEligible": False}
    try:
        baseline, originals = inputs(ROOT)
        sdk = subprocess.check_output(["dotnet", "--version"], cwd=ROOT, timeout=60, text=True).strip()
        require(sdk == baseline["sdk"], "Active SDK differs from the campaign baseline")
        source = git(ROOT, "rev-parse", "HEAD").decode().strip()
        require(not git(ROOT, "status", "--porcelain", "--untracked-files=no").strip(), "Tracked source is dirty")
        validate_sources(ROOT, originals)
        receipt_path = ROOT / RECEIPT
        if receipt_path.exists():
            receipt = json.loads(receipt_path.read_text())
            validate_receipt(ROOT, baseline, originals, receipt, sdk)
            ancestry(ROOT, receipt["preparedFromSourceRevision"])
            state = "checked-in"
        else:
            for path in PROJECT_PATHS:
                require((ROOT / path).read_bytes() == originals[path], "Project changed without a generation receipt: " + path)
            report["generation"] = execute(["bash", str(ROOT / GENERATOR)], output / "generation.log", timeout=300)
            successful(report["generation"])
            changed = set(git(ROOT, "diff", "--name-only").decode().splitlines())
            require(changed == set(PROJECT_PATHS), "SDK generation modified files outside the two project files")
            receipt = {"schemaVersion": 1, "originalSourceRevision": baseline["originalSourceRevision"],
                       "preparedFromSourceRevision": source, "sdk": sdk,
                       "preparationScriptSha256": sha256(ROOT / GENERATOR),
                       "files": [{"path": path, "originalSha256": hashlib.sha256(originals[path]).hexdigest(),
                                  "candidateSha256": sha256(ROOT / path)} for path in PROJECT_PATHS]}
            validate_receipt(ROOT, baseline, originals, receipt, sdk)
            state = "generated"
        for path in PROJECT_PATHS:
            target = output / "files" / path
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / path, target)
        write_json(output / "files" / RECEIPT, receipt)
        report.update(status="PASS", state=state, sdk=sdk, sourceRevision=source,
                      nextGate="Commit exact generated projects and receipt before native acceptance" if state == "generated" else "Native matrix required")
        output_values(state, sdk)
    except (RuntimeError, OSError, ValueError, KeyError, TypeError, ET.ParseError, subprocess.SubprocessError) as error:
        report.update(status="FAIL", error=str(error))
    write_json(output / "preparation-report.json", report)
    return 0 if report["status"] == "PASS" else 1


def telemetry_tests(path: Path) -> dict:
    document = ET.parse(path)
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    definitions = {test.attrib["id"]: test.find("t:TestMethod", ns) for test in document.findall(".//t:UnitTest", ns)}
    results = document.findall(".//t:UnitTestResult", ns)
    require(len(results) == 3, "Exactly three focused telemetry cases must execute")
    counts = collections.Counter()
    parameters = set()
    for result in results:
        require(result.attrib.get("outcome") == "Passed", "A focused telemetry case failed or was skipped")
        method = definitions.get(result.attrib.get("testId"))
        require(method is not None and method.attrib.get("className", "").split(",")[0] == "Helpdesk.Tests.Api.HostTelemetryTests",
                "TRX contains the wrong test class")
        name = method.attrib["name"]
        counts[name] += 1
        if name == "Shared_host_paths_preserve_Http_traces_health_filter_and_resources":
            match = re.search(r"useServiceDefaults:\s*(true|false)", result.attrib.get("testName", ""), re.I)
            require(match is not None, "Shared telemetry theory must record the actual boolean case")
            parameters.add(match.group(1).lower())
    require(counts == {"Api_provider_collects_real_PostgreSql_query_while_shared_provider_does_not": 1,
                       "Shared_host_paths_preserve_Http_traces_health_filter_and_resources": 2} and parameters == {"true", "false"},
            "Focused telemetry method/parameter coverage is incomplete")
    return {"status": "PASS", "executed": 3, "passed": 3, "skipped": 0, "trxSha256": sha256(path)}


def payload(directory: Path, assembly: str, *, database: bool) -> dict:
    runtime = json.loads((directory / (assembly + ".runtimeconfig.json")).read_text())["runtimeOptions"]
    require("includedFrameworks" not in runtime and not (directory / "System.Private.CoreLib.dll").exists(),
            "Host unexpectedly became self-contained")
    frameworks = runtime.get("frameworks", [runtime.get("framework", {})])
    require({value.get("name") for value in frameworks} >= {"Microsoft.NETCore.App", "Microsoft.AspNetCore.App"},
            "Host must retain framework-dependent ASP.NET deployment")
    dependencies = json.loads((directory / (assembly + ".deps.json")).read_text())
    libraries = sorted(name for name in dependencies["libraries"] if name.split("/", 1)[0].lower().startswith("npgsql"))
    files = sorted(path.relative_to(directory).as_posix() for path in directory.rglob("*")
                   if path.is_file() and path.name.lower().startswith("npgsql"))
    expected = {"Npgsql.dll", "Npgsql.OpenTelemetry.dll"}
    if database:
        require(expected <= {Path(path).name for path in files} and
                {"Npgsql", "Npgsql.OpenTelemetry"} <= {name.split("/", 1)[0] for name in libraries},
                "Database-owning payload lost Npgsql or its instrumentation")
    else:
        require(not files and not libraries, "Web/HTTP payload still includes an Npgsql dependency")
    return {"status": "PASS", "databaseDependenciesExpected": database, "npgsqlFiles": files,
            "npgsqlLibraries": libraries, "deploymentMode": "framework-dependent"}


def measure(image: str, oci: Path, architecture: str, output: Path, name: str, assembly: str, database: bool) -> dict:
    inspected = json.loads(api.capture(["docker", "image", "inspect", image]))[0]
    result = api.oci_inventory(oci, architecture, inspected, expected_entrypoint=("dotnet", assembly + ".dll"))
    container = api.capture(["docker", "create", "--entrypoint", "/__inventory_never_started", image])
    try:
        with tempfile.TemporaryDirectory(prefix="rd-telemetry-payload-") as temporary:
            subprocess.run(["docker", "cp", container + ":/app/.", temporary], check=True, timeout=180)
            directory = Path(temporary)
            result["application"] = api.inventory_directory(directory)
            result["payloadContract"] = payload(directory, assembly, database=database)
    finally:
        subprocess.run(["docker", "rm", container], check=True, timeout=30, stdout=subprocess.DEVNULL)
    write_json(output / (name + "-image.json"), result)
    return result


def compare_images(before: dict, after: dict, app_layer: int) -> dict:
    old, new = before["rootfsDiffIds"], after["rootfsDiffIds"]
    require(len(old) == len(new) and len(old) > abs(app_layer), "Image layer counts changed")
    changed = {index for index, values in enumerate(zip(old, new)) if values[0] != values[1]}
    require(changed == {len(old) + app_layer}, "A runtime/base/license layer changed outside the application payload")
    require(len(before["layers"]) == len(after["layers"]) == len(old), "OCI layers do not match rootfs layer count")
    for index in range(len(old)):
        if index != len(old) + app_layer:
            require(before["layers"][index] == after["layers"][index],
                    "A non-application compressed layer descriptor changed")
    require(isinstance(before["config"].get("config"), dict) and
            before["config"]["config"] == after["config"].get("config"),
            "Runtime image configuration changed outside package ownership")
    deltas = {}
    for name, control, candidate in (
        ("compressedLayerBytes", before["compressedLayerBytes"], after["compressedLayerBytes"]),
        ("uncompressedImageBytes", before["uncompressedImageBytes"], after["uncompressedImageBytes"]),
        ("applicationBytes", before["application"]["expandedBytes"], after["application"]["expandedBytes"])):
        require(type(control) is int and type(candidate) is int and 0 < candidate < control, "No measured reduction in " + name)
        deltas[name] = {"baseline": control, "candidate": candidate, "savedBytes": control - candidate,
                        "savedPercent": 100 * (control - candidate) / control}
    return deltas


def image_materials(metadata: dict, architecture: str) -> dict:
    materials = metadata.get("buildx.build.provenance", {}).get("materials", [])
    result = {}
    for family in ("sdk", "aspnet"):
        selected = [item for item in materials if item.get("uri", "").startswith("pkg:docker/mcr.microsoft.com/dotnet/" + family + "@")]
        require(len(selected) == 1, "Expected one resolved " + family + " image material")
        item = selected[0]
        require(parse_qs(urlsplit(item["uri"]).query).get("platform") == ["linux/" + architecture],
                "Resolved image material has the wrong platform")
        digest = item.get("digest", {}).get("sha256", "")
        require(re.fullmatch(r"[0-9a-f]{64}", digest), "Resolved image material has no exact SHA256 digest")
        result[family] = {"uri": item["uri"], "digest": "sha256:" + digest}
    return result


def compare_builds(before: dict, after: dict) -> None:
    require(before["sdk"] == after["sdk"], "Matched images used different SDK versions")
    require(before["imageMaterials"] == after["imageMaterials"], "Matched images resolved different SDK/runtime image materials")


def web_case(provider: str, architecture: str, images: dict, output: Path) -> dict:
    name = f"rd-telemetry-{architecture}-{provider.lower()}"
    environment = os.environ.copy()
    environment.update(COMPOSE_PROJECT_NAME=name, RATELDESK_SETUP_PROVIDER=provider,
                       RATELDESK_API_PORT="8222", RATELDESK_WEB_PORT="8111",
                       RATELDESK_API_URL="http://127.0.0.1:8222", RATELDESK_WEB_URL="http://127.0.0.1:8111")
    for variable in ("RATELDESK_UPGRADE_API_IMAGE", "RATELDESK_EXPECT_STORAGE_MANAGED"):
        environment.pop(variable, None)
    if provider == "PostgreSql":
        password = secrets.token_urlsafe(32)
        environment.update(RATELDESK_POSTGRES_PASSWORD=password, RATELDESK_SETUP_POSTGRES_PASSWORD=password)
    result = {"provider": provider, "status": "NOT_RUN"}
    with tempfile.TemporaryDirectory(prefix="rd-telemetry-compose-") as temporary:
        override = Path(temporary) / "images.json"
        write_json(override, {"services": {name: {"image": images[name], "pull_policy": "never"} for name in ("api", "web")}})
        files = [str(ROOT / "docker/docker-compose.yml")]
        if provider == "PostgreSql":
            files.append(str(ROOT / "docker/docker-compose.postgres.yml"))
        files.append(str(override))
        compose = ["docker", "compose", *[value for path in files for value in ("-f", path)]]
        try:
            result["start"] = execute(compose + ["up", "-d", "--wait", "--wait-timeout", "180", "--no-build", "--pull", "never"],
                                      output / (name + "-start.log"), environment=environment, timeout=240)
            successful(result["start"])
            result["smoke"] = execute(["bash", "tools/ci/smoke-first-run-local.sh", *files], output / (name + "-smoke.log"),
                                      environment=environment, timeout=600)
            successful(result["smoke"])
            result["executedConfigDigests"] = {}
            for service in ("api", "web"):
                container = subprocess.check_output(compose + ["ps", "-q", service], env=environment, timeout=30, text=True).strip()
                observed = api.capture(["docker", "inspect", "--format", "{{.Image}}", container])
                expected = api.capture(["docker", "image", "inspect", "--format", "{{.Id}}", images[service]])
                require(observed == expected, "Compose did not execute measured image: " + service)
                result["executedConfigDigests"][service] = observed
            result["status"] = "PASS"
        except (RuntimeError, OSError, subprocess.SubprocessError) as error:
            result.update(status="FAIL", error=str(error))
        finally:
            result["logs"] = execute(compose + ["logs", "--no-color"], output / (name + "-compose.log"), environment=environment, timeout=60)
            result["cleanup"] = execute(compose + ["down", "--volumes", "--remove-orphans"], output / (name + "-cleanup.log"),
                                        environment=environment, timeout=120)
            if any(result[key]["status"] != "PASS" for key in ("logs", "cleanup")):
                result["status"] = "FAIL"
    return result


def normalize_checkout(root: Path, timestamp: int) -> None:
    for item in git(root, "ls-files", "-z").split(b"\0"):
        if item:
            path = root / os.fsdecode(item)
            os.utime(path, (timestamp, timestamp), follow_symlinks=False)


def run(architecture: str, sdk: str, output: Path) -> int:
    output.mkdir(parents=True, exist_ok=False)
    evidence = output / "evidence"
    evidence.mkdir()
    report = {"status": "RUNNING", "architecture": architecture, "releaseEligible": False, "builds": {}, "images": {},
              "cases": [], "trimAotAnalyzersEnabled": False, "startedAt": dt.datetime.now(dt.timezone.utc).isoformat()}
    report_path = evidence / "host-telemetry-acceptance.json"
    worktrees = []
    try:
        baseline, originals = inputs(ROOT)
        report["originalOwnershipRevision"] = baseline["originalSourceRevision"]
        active_sdk = subprocess.check_output(["dotnet", "--version"], cwd=ROOT, timeout=60, text=True).strip()
        require(active_sdk == sdk, "Current SDK differs from preparation job")
        receipt = json.loads((ROOT / RECEIPT).read_text())
        validate_receipt(ROOT, baseline, originals, receipt, sdk)
        ancestry(ROOT, receipt["preparedFromSourceRevision"])
        require(platform.system() == "Linux" and platform.machine() == api.ARCHITECTURES[architecture][0], "Native hardware required")
        require(not git(ROOT, "status", "--porcelain", "--untracked-files=no").strip(), "Tracked source is dirty")
        source = git(ROOT, "rev-parse", "HEAD").decode().strip()
        report.update(sourceRevision=source, testSdkVersion=active_sdk, receiptSha256=sha256(ROOT / RECEIPT))
        docker = json.loads(api.capture(["docker", "info", "--format", "{{json .}}"] ))
        require(docker["OSType"] == "linux" and docker["Architecture"] in (architecture, api.ARCHITECTURES[architecture][0]),
                "Docker daemon architecture differs")
        require(b'--arch "$TARGETARCH"' in (ROOT / "docker/api/Dockerfile").read_bytes(), "Merge the API RID batch before host acceptance")
        report["dockerfileSha256"] = {}
        for path in ("docker/api/Dockerfile", "docker/web/Dockerfile", "docker/mcp-http/Dockerfile"):
            require((ROOT / path).read_bytes() == git(ROOT, "show", baseline["originalSourceRevision"] + ":" + path),
                    "Runtime/SDK Dockerfile changed outside this ownership batch: " + path)
            report["dockerfileSha256"][path] = sha256(ROOT / path)
        report["testBuild"] = execute(["dotnet", "build", "tests/Helpdesk.Tests/Helpdesk.Tests.csproj", "-c", "Release"],
                                      evidence / "telemetry-build.log", timeout=1200)
        successful(report["testBuild"])
        test_directory = evidence / "telemetry-tests"
        report["testCommand"] = execute(["dotnet", "test", "tests/Helpdesk.Tests/Helpdesk.Tests.csproj", "-c", "Release", "--no-build",
                                        "--filter", "FullyQualifiedName~Helpdesk.Tests.Api.HostTelemetryTests",
                                        "--logger", "trx;LogFileName=host-telemetry.trx", "--results-directory", str(test_directory)],
                                       evidence / "telemetry-tests.log", timeout=360)
        successful(report["testCommand"])
        report["telemetryTests"] = telemetry_tests(test_directory / "host-telemetry.trx")
        timestamp = git(ROOT, "show", "-s", "--format=%cI", "HEAD").decode().strip()
        seconds = int(git(ROOT, "show", "-s", "--format=%ct", "HEAD"))
        props = ET.parse(ROOT / "Directory.Build.props").getroot()
        prefix, suffix = props.findtext(".//VersionPrefix"), props.findtext(".//VersionSuffix")
        require(prefix is not None and suffix is not None and "$" not in prefix + suffix, "Version must be literal")
        version = prefix + ("-" + suffix if suffix else "")
        checkouts = {}
        for variant in ("baseline", "candidate"):
            checkout = output / variant
            subprocess.run(["git", "worktree", "add", "--detach", str(checkout), source], cwd=ROOT, check=True, timeout=120)
            worktrees.append(checkout)
            if variant == "baseline":
                for path, content in originals.items():
                    (checkout / path).write_bytes(content)
                require(set(git(checkout, "diff", "--name-only").decode().splitlines()) == set(OWNERSHIP_PATHS),
                        "Control differs beyond the four ownership files")
            normalize_checkout(checkout, seconds)
            checkouts[variant] = checkout
        report["controlChangedPaths"] = list(OWNERSHIP_PATHS)
        image_names = {}
        for host in ("web", "http", "api"):
            for variant in (("candidate",) if host == "api" else ("baseline", "candidate")):
                name = host + "-" + variant
                context = checkouts[variant]
                definition = HOSTS.get(host, {"dockerfile": "docker/api/Dockerfile", "assembly": "Helpdesk.API"})
                image_name = f"rateldesk-{host}:telemetry-{variant}-{architecture}"
                image_names[name] = image_name
                oci = output / (name + ".oci.tar")
                command = ["docker", "buildx", "build", "--platform", "linux/" + architecture, "--file", str(context / definition["dockerfile"]),
                           "--tag", image_name, "--load", "--progress", "plain", "--provenance=false", "--sbom=false",
                           "--metadata-file", str(evidence / (name + "-build-metadata.json")),
                           "--output", "type=oci,dest=" + str(oci) + ",compression=gzip",
                           "--build-arg", "VERSION=" + version, "--build-arg", "SOURCE_REVISION_ID=" + source,
                           "--build-arg", "BUILD_TIMESTAMP=" + timestamp, str(context)]
                print("Build " + name + " on native " + architecture, flush=True)
                report["builds"][name] = execute(command, evidence / (name + "-build.log"), timeout=1800)
                write_json(report_path, report)
                successful(report["builds"][name])
                log = (evidence / (name + "-build.log")).read_text(errors="replace")
                report["builds"][name]["sdk"] = api.resolved_sdk(log)
                report["builds"][name]["imageMaterials"] = image_materials(
                    json.loads((evidence / (name + "-build-metadata.json")).read_text()), architecture)
                report["builds"][name]["warningOccurrences"] = dict(collections.Counter(re.findall(r"warning ((?:IL|NU|CS)\d+)", log)))
                report["images"][name] = measure(image_name, oci, architecture, evidence, name, definition["assembly"],
                                                database=variant == "baseline" or host == "api")
                oci.unlink()
                write_json(report_path, report)
        report["deltas"] = {}
        for host, definition in HOSTS.items():
            before, after = host + "-baseline", host + "-candidate"
            compare_builds(report["builds"][before], report["builds"][after])
            report["deltas"][host] = compare_images(report["images"][before], report["images"][after], definition["appLayer"])
        for host in ("web", "http"):
            for variant in ("baseline", "candidate"):
                script = "smoke-web-static-assets.sh" if host == "web" else "test-mcp-compose-config.sh"
                name = host + "-" + variant
                case = execute(["bash", "tools/ci/" + script, image_names[name]], evidence / (name + "-published-smoke.log"), timeout=360)
                case.update(host=host, variant=variant, kind="published image static/config smoke")
                case["cleanupEvidence"] = "NOT_INDEPENDENTLY_VERIFIED: existing script uses a best-effort EXIT trap; outer timeouts fail and kill its process group"
                report["cases"].append(case)
                write_json(report_path, report)
                successful(case)
        pull = execute(["docker", "pull", "postgres:16"], evidence / "postgres-pull.log", timeout=300)
        successful(pull)
        report["postgresPull"] = pull
        report["postgresImage"] = json.loads(api.capture(["docker", "image", "inspect", "postgres:16"]))[0]["Id"]
        images = {"api": image_names["api-candidate"], "web": image_names["web-candidate"]}
        for provider in ("Sqlite", "PostgreSql"):
            report["cases"].append(web_case(provider, architecture, images, evidence))
            write_json(report_path, report)
        for variant in ("baseline", "candidate"):
            result_path = evidence / ("http-" + variant + "-runtime.json")
            case = execute([sys.executable, "tools/size-campaign/host-telemetry-http.py", "--api-image", images["api"],
                            "--web-image", image_names["web-" + variant], "--mcp-image", image_names["http-" + variant],
                            "--output", str(result_path)], evidence / ("http-" + variant + "-runtime.log"), timeout=720)
            case.update(kind="authenticated HTTP MCP fixture", variant=variant, runtimeReceipt=result_path.name)
            report["cases"].append(case)
            successful(case)
            try:
                runtime = json.loads(result_path.read_text())
                require(runtime["status"] == "PASS", "HTTP runtime receipt did not pass")
                for service, host in (("web", "web"), ("mcp-http", "http"), ("api", "api")):
                    key = "api-candidate" if host == "api" else host + "-" + variant
                    require(runtime["executedConfigDigests"][service] == report["images"][key]["configDigest"],
                            "HTTP acceptance did not execute the measured image")
            except (RuntimeError, OSError, ValueError, KeyError, TypeError):
                case["status"] = "FAIL"
                raise
            write_json(report_path, report)
        require(len(report["cases"]) == 8 and all(case["status"] == "PASS" for case in report["cases"]), "Not all runtime cases passed")
        report["status"] = "PASS"
    except (RuntimeError, OSError, ValueError, KeyError, TypeError, ET.ParseError, tarfile.TarError, subprocess.SubprocessError) as error:
        report.update(status="FAIL", error=str(error))
        print("Host telemetry acceptance failed: " + str(error), file=sys.stderr)
    finally:
        report["worktreeCleanup"] = []
        for checkout in reversed(worktrees):
            cleanup = execute(["git", "worktree", "remove", "--force", str(checkout)],
                              evidence / (checkout.name + "-worktree-cleanup.log"), timeout=120)
            report["worktreeCleanup"].append(cleanup)
            if cleanup["status"] != "PASS":
                report["status"] = "FAIL"
        report["finishedAt"] = dt.datetime.now(dt.timezone.utc).isoformat()
        write_json(report_path, report)
    return 0 if report["status"] == "PASS" else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    preparing = commands.add_parser("prepare")
    preparing.add_argument("--output", type=Path, required=True)
    native = commands.add_parser("run")
    native.add_argument("--architecture", choices=api.ARCHITECTURES, required=True)
    native.add_argument("--sdk", required=True)
    native.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    if arguments.command == "prepare":
        return prepare(arguments.output.resolve())
    return run(arguments.architecture, arguments.sdk, arguments.output.resolve())


if __name__ == "__main__":
    raise SystemExit(main())
