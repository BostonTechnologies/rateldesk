#!/usr/bin/env python3
"""Explicit disposable Docker storage lane; pass an already built local image.

This exercises the real Docker boundary and persistence after tooling recreation.
It does not claim MCP/API, browser, release-integrity or owner deployment proof.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("mcp_launcher", ROOT / "tools/mcp/setup-http-mcp.py")
launcher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(launcher)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True, help="Existing local MCP image (never pulled); /bin/sh and stat must be present.")
    options = parser.parse_args()
    docker = launcher.Docker()
    docker.json(["image", "inspect", "--format", "{{json .}}", options.image])
    project = "rdk-storage-fixture-" + uuid.uuid4().hex[:12]
    created_volumes = [project + "-http-host-setup", project + "-http-client-data"]
    with tempfile.TemporaryDirectory(prefix="rateldesk-launcher-acceptance-") as temporary:
        directory = Path(temporary)
        source = directory / "compose.json"
        source.write_text(json.dumps({"services": {name: {"image": options.image} for name in launcher.SERVICE_NAMES}}))
        args = launcher.parser().parse_args(["--project", project, "--compose-file", str(source),
            "--manager", "external-compose", "--image", options.image])
        deployment = launcher.Deployment(args, docker)
        deployment.proposal = {"services": {"api": {}}, "x-rateldesk-source-files": [launcher.file_record(source)]}
        try:
            deployment.prepare_storage()
            launcher.write_private(deployment.output / "effective-deployment.json", {"syntheticFixture": "preserved"})
            launcher.stage_snapshot(deployment, "effective-deployment.json")
            command = launcher.tooling_arguments(deployment) + ["--rm", "--entrypoint", "/bin/sh", options.image,
                "-ec", 'umask 077; printf synthetic-private-profile > /var/lib/rateldesk/http-client/profile.secret; chmod 0600 /var/lib/rateldesk/http-client/profile.secret']
            docker.run(command)
            launcher.write_private(deployment.output / "launcher-state.json", {"storage": deployment.storage})
            # New deployment object and independent Docker invocations; no live
            # developer/session container carries the persisted profile.
            resumed = launcher.Deployment(args, docker)
            resumed.proposal = {"services": {"api": {}}, "x-rateldesk-source-files": [launcher.file_record(source)]}
            resumed.prepare_storage()
            command = launcher.tooling_arguments(resumed) + ["--rm", "--entrypoint", "/bin/sh", options.image,
                "-ec", 'test "$(cat /var/lib/rateldesk/http-client/profile.secret)" = synthetic-private-profile; test "$(stat -c %a /var/lib/rateldesk/http-client/profile.secret)" = 600; test "$(stat -c %u /var/lib/rateldesk/http-client/profile.secret)" = 10001; test -r /output/effective-deployment.json']
            docker.run(command)
            assert resumed.storage == deployment.storage
            invalid = directory / "not-owned-by-serving-user"
            invalid.mkdir(mode=0o700)
            resumed.metadata = {"Mounts": [{"Destination": "/output", "Type": "bind", "Source": str(invalid), "RW": True}]}
            try:
                resumed.prepare_storage()
            except launcher.Prerequisite as failure:
                assert str(failure).startswith("storage_preflight:"), failure
            else:
                raise AssertionError("A mount owned by the host user was accepted as UID 10001 storage")
            assert invalid.stat().st_uid != 10001
            assert invalid.stat().st_mode & 0o777 == 0o700
            print("Disposable Docker storage acceptance: automatic private stores, isolated personal Save/reload, fresh tooling recreation, and failed-mount preflight passed (4 checks).")
        finally:
            # Only volumes named and labelled by this unique fixture are removed.
            for name in created_volumes:
                data = docker.run(["volume", "inspect", "--format", "{{json .}}", name], optional=True)
                if data and (json.loads(data).get("Labels", {}) or {}).get("com.rateldesk.setup.project") == project:
                    docker.run(["volume", "rm", name])


if __name__ == "__main__":
    main()
