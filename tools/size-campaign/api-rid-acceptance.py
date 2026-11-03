#!/usr/bin/env python3
"""Build matched-source API images and exercise them on the native Linux architecture.

This optional campaign gate never pushes images or changes a release. The control
uses an immutable old Dockerfile with the candidate source, version and timestamp.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import secrets
import signal
import subprocess
import sys
import tarfile
import tempfile
import time
import xml.etree.ElementTree as ET

from inventory import inventory_directory


ROOT = Path(__file__).resolve().parents[2]
BASELINE_DOCKERFILE_REVISION = "59248cacce44ad33a1c3367f0eb000e8705e072b"
ARCHITECTURES = {"amd64": ("x86_64", "linux-x64", 62), "arm64": ("aarch64", "linux-arm64", 183)}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def capture(command: list[str]) -> str:
    return subprocess.check_output(command, cwd=ROOT, timeout=60, text=True).strip()


def execute(command: list[str], log: Path, *, environment: dict | None = None, timeout: int = 1800) -> dict:
    """Persist exit/timeout evidence; a partial log is never interpreted as a pass."""
    started = time.monotonic()
    result = {"command": command, "status": "RUNNING", "exitCode": None, "log": log.name}
    with log.open("w", encoding="utf-8") as stream:
        stream.write("Command: " + json.dumps(command) + "\n")
        stream.flush()
        try:
            process = subprocess.Popen(command, cwd=ROOT, env=environment, stdout=stream,
                                       stderr=subprocess.STDOUT, start_new_session=True)
            try:
                result["exitCode"] = process.wait(timeout=timeout)
                result["status"] = "PASS" if result["exitCode"] == 0 else "FAIL"
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
                result["status"] = "TIMED_OUT"
                stream.write(f"\nTimed out after {timeout} seconds.\n")
        except OSError as error:
            result["status"] = "BLOCKED"
            stream.write(f"\n{type(error).__name__}: {error}\n")
    result["elapsedSeconds"] = round(time.monotonic() - started, 3)
    return result


def successful(result: dict) -> None:
    require(result["status"] == "PASS", f"Command did not pass; see {result['log']}")


def resolved_sdk(log: str) -> dict:
    """Read the actual SDK compiler/targets paths emitted by verbose publish."""
    versions = sorted(set(re.findall(r"/usr/share/dotnet/sdk/([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)/", log)))
    require(len(versions) == 1,
            "Expected one resolved SDK version in verbose publish paths; missing or ambiguous SDK evidence")
    return {"version": versions[0], "evidencePath": "/usr/share/dotnet/sdk/" + versions[0] + "/",
            "source": "Verbose publish compiler/targets paths; not inferred from image tag"}


def oci_inventory(path: Path, architecture: str, inspected: dict) -> dict:
    """Verify every referenced OCI blob and tie it to the locally executed image."""
    with tarfile.open(path, "r:") as archive:
        members = archive.getmembers()
        require(len({member.name for member in members}) == len(members), "Duplicate OCI archive paths")
        require(all(member.isdir() or member.isfile() for member in members), "Unexpected OCI archive link")

        def read(name: str) -> bytes:
            member = archive.getmember(name)
            require(member.isfile(), "OCI member must be a regular file")
            return archive.extractfile(member).read()

        def blob(descriptor: dict) -> bytes:
            digest = descriptor["digest"]
            require(re.fullmatch(r"sha256:[0-9a-f]{64}", digest) is not None, "Unsupported OCI digest")
            data = read("blobs/sha256/" + digest.split(":", 1)[1])
            require(type(descriptor["size"]) is int and len(data) == descriptor["size"], "OCI blob size mismatch")
            require("sha256:" + hashlib.sha256(data).hexdigest() == digest, "OCI blob digest mismatch")
            return data

        index = json.loads(read("index.json"))
        require(index["schemaVersion"] == 2 and len(index["manifests"]) == 1,
                "Expected one runnable native image without attestations")
        descriptor = index["manifests"][0]
        manifest = json.loads(blob(descriptor))
        require(manifest["schemaVersion"] == 2 and len(manifest["layers"]) > 1, "Invalid OCI manifest")
        config = json.loads(blob(manifest["config"]))
        require(config["os"] == "linux" and config["architecture"] == architecture, "OCI architecture mismatch")
        require(inspected["Os"] == "linux" and inspected["Architecture"] == architecture,
                "Loaded Docker image architecture mismatch")
        require(inspected["Id"] == manifest["config"]["digest"], "OCI export and loaded image config differ")
        require(inspected["RootFS"]["Layers"] == config["rootfs"]["diff_ids"], "Loaded image layer identity mismatch")
        require(inspected["Config"]["Entrypoint"] == ["dotnet", "Helpdesk.API.dll"], "API entrypoint changed")
        for layer in manifest["layers"]:
            require(layer["mediaType"].endswith("tar+gzip"), "Expected gzip-compressed OCI layers")
            blob(layer)
    return {"manifestDigest": descriptor["digest"], "configDigest": manifest["config"]["digest"],
            "compressedLayerBytes": sum(layer["size"] for layer in manifest["layers"]),
            "uncompressedImageBytes": inspected["Size"], "rootfsDiffIds": config["rootfs"]["diff_ids"],
            "layers": manifest["layers"], "manifest": manifest, "config": config,
            "sizeDefinition": "All gzip layer descriptors in the verified OCI export; config/index and attestations excluded"}


def candidate_payload(directory: Path, architecture: str) -> dict:
    """Check the actual publish payload selects the correct SQLite native library."""
    _, rid, machine = ARCHITECTURES[architecture]
    runtime = json.loads((directory / "Helpdesk.API.runtimeconfig.json").read_text())["runtimeOptions"]
    require("includedFrameworks" not in runtime, "Candidate unexpectedly became self-contained")
    frameworks = runtime.get("frameworks", [runtime.get("framework", {})])
    require({item.get("name") for item in frameworks} >= {"Microsoft.NETCore.App", "Microsoft.AspNetCore.App"},
            "Candidate must use both installed .NET and ASP.NET frameworks")
    require(not (directory / "System.Private.CoreLib.dll").exists(), "Runtime pack was bundled into application")
    dependencies = json.loads((directory / "Helpdesk.API.deps.json").read_text())
    require(dependencies["runtimeTarget"]["name"].endswith("/" + rid), "Candidate dependency graph has the wrong RID")
    sqlite_names = {"libe_sqlite3.so", "e_sqlite3.dll", "libe_sqlite3.dylib", "e_sqlite3.a", "libe_sqlite3.a"}
    libraries = sorted(path for path in directory.rglob("*") if path.is_file()
                       and (path.name in sqlite_names or path.suffix == ".dylib"))
    require(libraries == [directory / "libe_sqlite3.so"], "Candidate retained missing or foreign SQLite native assets")
    header = libraries[0].read_bytes()[:20]
    require(header[:6] == b"\x7fELF\x02\x01" and int.from_bytes(header[18:20], "little") == machine,
            "SQLite shared library ELF architecture mismatch")
    return {"status": "PASS", "runtimeIdentifier": rid, "deploymentMode": "framework-dependent",
            "nativeLibrary": "libe_sqlite3.so", "elfMachine": machine}


def measure_image(image: str, oci_path: Path, architecture: str, output: Path, variant: str) -> dict:
    inspected = json.loads(capture(["docker", "image", "inspect", image]))[0]
    result = oci_inventory(oci_path, architecture, inspected)
    container = capture(["docker", "create", "--entrypoint", "/__inventory_never_started", image])
    try:
        with tempfile.TemporaryDirectory(prefix="rd-api-payload-") as temporary:
            directory = Path(temporary)
            subprocess.run(["docker", "cp", container + ":/app/.", temporary], check=True, timeout=180)
            result["application"] = inventory_directory(directory)
            if variant == "candidate":
                result["payloadContract"] = candidate_payload(directory, architecture)
    finally:
        subprocess.run(["docker", "rm", container], check=True, timeout=30, stdout=subprocess.DEVNULL)
    write_json(output / (variant + "-image.json"), result)
    return result


def provider_case(provider: str, mode: str, architecture: str, images: dict, output: Path) -> dict:
    """Exercise fresh candidate data and a control-to-candidate retained-data upgrade."""
    name = f"rd-size-api-{architecture}-{provider.lower()}-{mode}"
    environment = os.environ.copy()
    environment.update({"COMPOSE_PROJECT_NAME": name, "RATELDESK_SETUP_PROVIDER": provider,
                        "RATELDESK_API_PORT": "8222", "RATELDESK_WEB_PORT": "8111",
                        "RATELDESK_API_URL": "http://127.0.0.1:8222", "RATELDESK_WEB_URL": "http://127.0.0.1:8111"})
    for variable in ("RATELDESK_UPGRADE_API_IMAGE", "RATELDESK_EXPECT_STORAGE_MANAGED"):
        environment.pop(variable, None)
    if provider == "PostgreSql":
        password = secrets.token_urlsafe(32)
        environment.update({"RATELDESK_POSTGRES_PASSWORD": password, "RATELDESK_SETUP_POSTGRES_PASSWORD": password})
    if mode == "upgrade":
        environment["RATELDESK_UPGRADE_API_IMAGE"] = images["candidate"]
    with tempfile.TemporaryDirectory(prefix="rd-api-compose-") as temporary:
        override = Path(temporary) / "images.json"
        write_json(override, {"services": {
            "api": {"image": images["baseline" if mode == "upgrade" else "candidate"], "pull_policy": "never"},
            "web": {"image": images["web"], "pull_policy": "never"}}})
        files = [str(ROOT / "docker/docker-compose.yml")]
        if provider == "PostgreSql":
            files.append(str(ROOT / "docker/docker-compose.postgres.yml"))
        files.append(str(override))
        compose = ["docker", "compose"]
        for path in files:
            compose += ["-f", path]
        result = {"provider": provider, "mode": mode, "status": "NOT_RUN",
                  "initialApiImage": images["baseline" if mode == "upgrade" else "candidate"],
                  "finalApiImage": images["candidate"]}
        try:
            result["start"] = execute(compose + ["up", "--detach", "--wait", "--wait-timeout", "180", "--no-build", "--pull", "never"],
                                      output / (name + "-start.log"), environment=environment, timeout=240)
            successful(result["start"])
            result["smoke"] = execute(["bash", "tools/ci/smoke-first-run-local.sh", *files],
                                      output / (name + "-smoke.log"), environment=environment, timeout=600)
            successful(result["smoke"])
            container = subprocess.check_output(compose + ["ps", "-q", "api"], cwd=ROOT, env=environment,
                                                timeout=30, text=True).strip()
            actual_image = capture(["docker", "inspect", "--format", "{{.Image}}", container])
            expected_image = capture(["docker", "image", "inspect", "--format", "{{.Id}}", images["candidate"]])
            require(actual_image == expected_image, "Smoke did not finish on the measured candidate image")
            result["executedCandidateConfigDigest"] = actual_image
            result["status"] = "PASS"
        except (RuntimeError, subprocess.SubprocessError) as error:
            result["status"] = "FAIL"
            result["error"] = str(error)
        finally:
            result["logs"] = execute(compose + ["logs", "--no-color"], output / (name + "-compose.log"),
                                     environment=environment, timeout=60)
            result["cleanup"] = execute(compose + ["down", "--volumes", "--remove-orphans"],
                                        output / (name + "-cleanup.log"), environment=environment, timeout=120)
            if result["logs"]["status"] != "PASS" or result["cleanup"]["status"] != "PASS":
                result["status"] = "FAIL"
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--architecture", choices=ARCHITECTURES, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"status": "RUNNING", "architecture": args.architecture, "releaseEligible": False,
              "baselineDockerfileRevision": BASELINE_DOCKERFILE_REVISION,
              "startedAt": dt.datetime.now(dt.timezone.utc).isoformat(), "builds": {}, "images": {}, "cases": [],
              "adoptionRequires": ["Both native architecture jobs pass", "Required PR/main checks pass"],
              "trimAotAnalyzersEnabled": False}
    report_path = output / "api-rid-acceptance.json"
    write_json(report_path, report)
    try:
        require(platform.system() == "Linux" and platform.machine() == ARCHITECTURES[args.architecture][0],
                "This gate requires native matching Linux hardware; emulation is not acceptance")
        report["sourceRevision"] = capture(["git", "rev-parse", "HEAD"])
        require(not capture(["git", "status", "--porcelain", "--untracked-files=no"]), "Tracked source is dirty")
        report["dockerVersion"] = capture(["docker", "version", "--format", "{{json .}}"])
        report["buildxVersion"] = capture(["docker", "buildx", "version"])
        server = json.loads(capture(["docker", "info", "--format", "{{json .}}"] ))
        require(server["OSType"] == "linux" and server["Architecture"] in
                (args.architecture, ARCHITECTURES[args.architecture][0]), "Docker daemon is not on matching native architecture")
        baseline = output / "baseline.Dockerfile"
        baseline.write_bytes(subprocess.check_output(
            ["git", "show", BASELINE_DOCKERFILE_REVISION + ":docker/api/Dockerfile"], cwd=ROOT, timeout=60))
        candidate = ROOT / "docker/api/Dockerfile"
        report["dockerfiles"] = {name: hashlib.sha256(path.read_bytes()).hexdigest()
                                 for name, path in (("baseline", baseline), ("candidate", candidate))}
        require(report["dockerfiles"]["baseline"] != report["dockerfiles"]["candidate"], "Candidate Dockerfile equals control")
        props = ET.parse(ROOT / "Directory.Build.props").getroot()
        prefix, suffix = props.findtext(".//VersionPrefix"), props.findtext(".//VersionSuffix")
        require(prefix is not None and suffix is not None and "$" not in prefix + suffix, "Root version must be literal")
        report["version"] = prefix + ("-" + suffix if suffix else "")
        timestamp = capture(["git", "show", "-s", "--format=%cI", "HEAD"])
        images = {key: f"rateldesk-{('web' if key == 'web' else 'api')}:size-{key}-{args.architecture}"
                  for key in ("baseline", "candidate", "web")}
        for variant, dockerfile in (("baseline", baseline), ("candidate", candidate), ("web", ROOT / "docker/web/Dockerfile")):
            command = ["docker", "buildx", "build", "--platform", "linux/" + args.architecture,
                       "--file", str(dockerfile), "--tag", images[variant], "--load", "--progress", "plain",
                       "--provenance=false", "--sbom=false", "--metadata-file", str(output / (variant + "-build-metadata.json")),
                       "--build-arg", "VERSION=" + report["version"],
                       "--build-arg", "SOURCE_REVISION_ID=" + report["sourceRevision"],
                       "--build-arg", "BUILD_TIMESTAMP=" + timestamp]
            oci_path = output / (variant + ".oci.tar")
            if variant != "web":
                command += ["--output", "type=oci,dest=" + str(oci_path) + ",compression=gzip"]
            command.append(".")
            print(f"Build {variant} on native {args.architecture}", flush=True)
            report["builds"][variant] = execute(command, output / (variant + "-build.log"))
            write_json(report_path, report)
            successful(report["builds"][variant])
            if variant != "web":
                report["builds"][variant]["sdk"] = resolved_sdk(
                    (output / (variant + "-build.log")).read_text(encoding="utf-8", errors="replace"))
                report["images"][variant] = measure_image(images[variant], oci_path, args.architecture, output, variant)
                oci_path.unlink()
            write_json(report_path, report)
        baseline_image, candidate_image = report["images"]["baseline"], report["images"]["candidate"]
        require(report["builds"]["baseline"]["sdk"]["version"] == report["builds"]["candidate"]["sdk"]["version"],
                "Control and candidate used different SDK versions; comparison would be confounded")
        report["resolvedSdkVersion"] = report["builds"]["candidate"]["sdk"]["version"]
        require(baseline_image["rootfsDiffIds"][:-1] == candidate_image["rootfsDiffIds"][:-1],
                "Control and candidate have different runtime/base layers; comparison would be confounded")
        report["deltas"] = {}
        for metric in ("compressedLayerBytes", "uncompressedImageBytes"):
            control, value = baseline_image[metric], candidate_image[metric]
            require(value < control, "Candidate did not reduce " + metric)
            report["deltas"][metric] = {"baseline": control, "candidate": value,
                                         "savedBytes": control - value, "savedPercent": 100 * (control - value) / control}
        control = baseline_image["application"]["expandedBytes"]
        value = candidate_image["application"]["expandedBytes"]
        require(value < control, "Candidate did not reduce application bytes")
        report["deltas"]["applicationBytes"] = {"baseline": control, "candidate": value,
                                                 "savedBytes": control - value, "savedPercent": 100 * (control - value) / control}
        report["postgresPull"] = execute(["docker", "pull", "postgres:16"], output / "postgres-pull.log", timeout=300)
        successful(report["postgresPull"])
        report["postgresImage"] = json.loads(capture(["docker", "image", "inspect", "postgres:16"]))[0]["Id"]
        for provider in ("Sqlite", "PostgreSql"):
            for mode in ("upgrade", "fresh"):
                print(f"Exercise {provider} {mode} with native {args.architecture} images", flush=True)
                report["cases"].append(provider_case(provider, mode, args.architecture, images, output))
                write_json(report_path, report)
        require(len(report["cases"]) == 4 and all(case["status"] == "PASS" for case in report["cases"]),
                "Not all real SQLite/PostgreSQL fresh/upgrade cases passed")
        report["status"] = "PASS"
    except (RuntimeError, OSError, subprocess.SubprocessError, KeyError, ValueError, tarfile.TarError) as error:
        report["status"] = "FAIL"
        report["error"] = str(error)
        print(f"API RID acceptance failed: {error}", file=sys.stderr)
    finally:
        report["finishedAt"] = dt.datetime.now(dt.timezone.utc).isoformat()
        write_json(report_path, report)
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
