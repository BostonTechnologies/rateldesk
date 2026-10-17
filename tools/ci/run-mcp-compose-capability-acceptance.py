#!/usr/bin/env python3
"""Opt-in real Compose floor checks in isolated, checksum-pinned fixture stores.

This CI fixture deliberately downloads only with --download. It never installs a
system plugin or copies Docker credentials. Use a local Unix Docker daemon.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import urllib.request
import uuid
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("mcp_launcher", ROOT / "tools/mcp/setup-http-mcp.py")
launcher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(launcher)
TERMINAL_SPEC = importlib.util.spec_from_file_location("mcp_host_lifecycle", ROOT / "tools/ci/run-mcp-host-lifecycle-acceptance.py")
lifecycle = importlib.util.module_from_spec(TERMINAL_SPEC)
TERMINAL_SPEC.loader.exec_module(lifecycle)
FIXTURES = {
    "2.26.1": "2f61856d1b8c9de29ffdaedaa1c6d0a5fc5c79da45068f1f4310feed8d3a3f61",
    "2.34.0": "94a416c6f2836a0a1ba5eb3feb00f2e700a9d98311f062c4c61494ccbf3cd457",
    "2.35.0": "dba1915cf2f282527f5df0cd7a94b9503047ed200317801853abe8f22c8cd493",
    "2.39.4": "7af95166a730b87e172d4fc9aefea8725d3c6c7327d59149267b452114ddb7d4",
}


def plugin(version, destination, args):
    existing = (args.fixture_directory / version / "docker-compose-linux-x86_64") if args.fixture_directory else None
    if existing and existing.is_file():
        shutil.copyfile(existing, destination)
    elif args.download:
        request = urllib.request.Request(f"https://github.com/docker/compose/releases/download/v{version}/docker-compose-linux-x86_64",
            headers={"User-Agent": "RatelDesk-disposable-capability-acceptance"})
        with urllib.request.urlopen(request, timeout=45) as source, destination.open("wb") as target:
            total = 0
            while data := source.read(1024 * 1024):
                total += len(data)
                if total > 128 * 1024 * 1024:
                    raise RuntimeError("Compose fixture download exceeded bound")
                target.write(data)
    else:
        raise RuntimeError("Supply --fixture-directory with versioned fixtures or opt in with --download")
    assert hashlib.sha256(destination.read_bytes()).hexdigest() == FIXTURES[version], "Compose fixture integrity mismatch"
    destination.chmod(0o755)


def wizard(image, directory, target):
    project = "rdk-capability-" + uuid.uuid4().hex[:12]
    source = directory / "compose.json"
    source.write_text(json.dumps({"services": {name: {"image": image,
        "environment": {"Helpdesk__Mcp__AuthenticationMode": "gateway", "Helpdesk__Mcp__Outbound__CredentialMode": "gateway"}}
        for name in launcher.SERVICE_NAMES}}))
    arguments = ["--compose-file", str(source), "--project", project, "--manager", "external-compose", "--target", target]
    terminal = lifecycle.Terminal(arguments, directory)
    try:
        terminal.until("RatelDesk Web URL", seconds=60)
        terminal.send(b"\x03")
        terminal.finish(expected=2 if target == "http-host" else 0)
        assert "Short code:" not in terminal.text() and "Code:" not in terminal.text(), "Unexpected approval issued"
    finally:
        terminal.close()
        for suffix in ("http-host-setup", "http-client-data"):
            name = project + "-" + suffix
            metadata = launcher.Docker().run(["volume", "inspect", "--format", "{{json .}}", name], optional=True)
            if metadata and (json.loads(metadata).get("Labels", {}) or {}).get("com.rateldesk.setup.project") == project:
                launcher.Docker().run(["volume", "rm", name])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--download", action="store_true")
    parser.add_argument("--fixture-directory", type=Path)
    parser.add_argument("--image", help="Already built local MCP image; additionally prove host/client pristine wizard entry.")
    args = parser.parse_args()
    if os.uname().machine != "x86_64":
        parser.error("Pinned binary fixture acceptance runs on Linux x86_64")
    host = subprocess.run(["docker", "context", "inspect", "--format", "{{.Endpoints.docker.Host}}"],
        capture_output=True, timeout=20, check=True).stdout.decode().strip()
    if not host.startswith("unix://"):
        parser.error("Use a disposable local Unix Docker daemon for these isolated plugin fixtures")
    with tempfile.TemporaryDirectory(prefix="rateldesk compose capabilities ") as temporary:
        for version in FIXTURES:
            directory = Path(temporary) / version
            config = directory / "selected Docker config"
            plugins = config / "cli-plugins"
            plugins.mkdir(parents=True)
            plugin(version, plugins / "docker-compose", args)
            environment = os.environ.copy()
            for key in launcher.DOCKER_SELECTION_KEYS:
                environment.pop(key, None)
            environment.update(DOCKER_CONFIG=str(config), DOCKER_HOST=host)
            with patch.dict(os.environ, environment, clear=True):
                docker = launcher.Docker()
                if version in ("2.26.1", "2.34.0"):
                    completed = subprocess.run([str(ROOT / "tools/mcp/setup-http-mcp.sh"), "--compose-file", "/source-must-not-be-read", "--project", "fixture"],
                        capture_output=True, timeout=30)
                    assert completed.returncode == 78
                    assert b"compose_feature_unsupported:" in completed.stderr and b"config --no-env-resolution" in completed.stderr
                    assert b"source_unreadable" not in completed.stderr and b"approval" not in completed.stdout
                else:
                    docker.preflight()
                    assert docker.compose_version == version
                    if args.image:
                        wizard(args.image, directory, "http-host" if version == "2.35.0" else "http-client")
                detail = "unsupported flag rejected before sources/consent" if version in ("2.26.1", "2.34.0") else "source/order/interpolation/env-file/path capabilities and selected daemon"
                if args.image and version in ("2.35.0", "2.39.4"):
                    detail += "; pristine " + ("host" if version == "2.35.0" else "client") + " wizard Web URL reached and cancelled before approval"
                print(f"PASS Compose {version}: checksum verified; " + detail, flush=True)


if __name__ == "__main__":
    main()
