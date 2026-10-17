#!/usr/bin/env python3
"""Exercise lossless candidate rendering with the actual owner's Compose CLI.

This lane uses only Compose config and Docker identity reads. It starts no
containers and makes no claim to exercise a deployment-manager UI or apply.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("source_review_acceptance_launcher", root / "tools/mcp/setup-http-mcp.py")
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)


def main():
    docker = launcher.Docker()
    docker.bind_daemon()
    assertions = 0
    with tempfile.TemporaryDirectory(prefix="rateldesk-source-review-owner-") as temporary:
        directory = Path(temporary)
        output = directory / "private"
        output.mkdir(mode=0o700)
        (directory / "owner.env").write_text("OWNER_VALUE=retained-interpolation\n")
        (directory / "private-outbound.json").write_text('{"apiBaseUrl":"http://api:8222/","credentialMode":"gateway"}\n')
        for list_environment in (False, True):
            source = directory / ("list.yml" if list_environment else "map.yml")
            environment = ('      - "Helpdesk__Mcp__DelegationTimeoutSeconds=20" # preserve inline comment\n'
                           '      - "UNCHANGED=${OWNER_VALUE}"\n') if list_environment else (
                           '      Helpdesk__Mcp__DelegationTimeoutSeconds: "20" # preserve inline comment\n'
                           '      UNCHANGED: "${OWNER_VALUE}"\n')
            text = ('# readable owner source; retain exactly\nservices:\n'
                    '  api:\n    image: synthetic-api:never-run\n'
                    '  web:\n    image: synthetic-web:never-run\n    labels:\n      synthetic-owner: preserved\n'
                    '  mcp-http:\n    image: synthetic-mcp:never-run\n    environment:\n' + environment +
                    '    configs:\n      - source: existing-outbound\n        target: /run/rateldesk-mcp/config.json\n'
                    '  db:\n    image: postgres:16-alpine\n'
                    'configs:\n  existing-outbound:\n    file: ./private-outbound.json\n')
            before = text.encode()
            source.write_bytes(before)
            command = ["compose", "--project-directory", str(directory), "--project-name", "rateldesk-source-review-acceptance",
                       "--env-file", str(directory / "owner.env"), "-f", str(source)]
            original = json.loads(docker.run(command + ["config", "--format", "json"], owner_tool=True))
            original["x-rateldesk-source-files"] = [{"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                                                   for path in (source, directory / "owner.env", directory / "private-outbound.json")]
            snapshot = output / "effective.json"
            launcher.write_private(snapshot, original)
            context = {"schemaVersion": 1, "compose": command, "sources": [str(source)], "projectDirectory": str(directory),
                       "outputDirectory": str(output), "dockerSelection": docker.selection, "daemonIdentity": docker.daemon_identity,
                       "effectiveSnapshot": str(snapshot), "manager": "external-compose", "ownerComposeRequired": True}
            context_path = output / "context.json"
            launcher.write_private(context_path, context)
            overlay = {"services": {"mcp-http": {"environment": {"Helpdesk__Mcp__DelegationTimeoutSeconds": "21"}}}}
            invoke = [sys.executable, str(root / "tools/mcp/source_review.py"), "--context", str(context_path)]
            completed = subprocess.run(invoke, input=json.dumps(overlay).encode(), capture_output=True, timeout=60)
            if completed.returncode:
                raise AssertionError("Owner Compose source review failed: " + completed.stderr.decode())
            result = json.loads(completed.stdout)
            candidate = Path(result["sourceCandidates"][0]["candidate"])
            expected = before.replace(b'DelegationTimeoutSeconds=20"', b'DelegationTimeoutSeconds=21"') if list_environment else before.replace(b'DelegationTimeoutSeconds: "20"', b'DelegationTimeoutSeconds: "21"')
            assert candidate.read_bytes() == expected
            assert source.read_bytes() == before
            assert set(result["completeDeployment"]["services"]) == {"api", "web", "mcp-http", "db"}
            assert "healthcheck" not in result["completeDeployment"]["services"]["web"]
            assert result["completeDeployment"]["services"]["mcp-http"]["environment"]["UNCHANGED"] == "retained-interpolation"
            assert result["completeDeployment"]["services"]["mcp-http"]["configs"] == original["services"]["mcp-http"]["configs"]
            again = subprocess.run(invoke, input=json.dumps(overlay).encode(), capture_output=True, timeout=60)
            assert again.returncode == 0 and json.loads(again.stdout) == result
            assertions += 7
    print(f"MCP source review: {assertions} actual owner-Compose assertions passed; 2 source styles; no containers/apply")


if __name__ == "__main__":
    main()
