#!/usr/bin/env python3
"""Small, local Compose contract checks; never starts a service or contacts an API."""
import json
import os
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]


def render(*files, **values):
    environment = {key: value for key, value in os.environ.items() if not key.startswith("RATELDESK_")}
    environment.update(values)
    command = ["docker", "compose"]
    for file in files:
        command.extend(["-f", str(root / file)])
    command.extend(["config", "--format", "json"])
    result = subprocess.run(command, cwd=root, env=environment, capture_output=True, text=True, timeout=30)
    if result.returncode:
        raise AssertionError(f"Compose recipe did not render: {', '.join(files)}")
    return json.loads(result.stdout)["services"]


def main():
    for base in ["docker/docker-compose.yml", "docker/docker-compose.release.yml"]:
        services = render(base, RATELDESK_VERSION="0.0.0-test")
        assert "mcp-http" not in services
        assert services["web"]["environment"]["ApiBaseUrl"] == "http://api:8222/"
        assert "ReverseProxy__Clusters__apiCluster__Destinations__api1__Address" not in services["web"]["environment"]

    for base, overlay in [
        ("docker/docker-compose.yml", "docker/docker-compose.mcp.env.yml"),
        ("docker/docker-compose.release.yml", "docker/docker-compose.mcp.env.release.yml"),
    ]:
        services = render(base, overlay, RATELDESK_VERSION="0.0.0-test", RATELDESK_PUBLIC_ORIGIN="https://desk.example.test")
        web, mcp, api = [services[name]["environment"] for name in ["web", "mcp-http", "api"]]
        assert web["WebGateway__Mcp__Enabled"] == "true"
        assert web["WebGateway__Mcp__Destination"] == "http://mcp-http:8223/"
        assert web["WebGateway__Mcp__AuthenticationMode"] == mcp["Helpdesk__Mcp__AuthenticationMode"] == "gateway"
        assert mcp["Helpdesk__Mcp__ExpectedApiBaseUrl"] == mcp["Helpdesk__Mcp__Outbound__ApiBaseUrl"] == web["ApiBaseUrl"]
        assert mcp["Helpdesk__Mcp__PublicResourceUri"] == api["IntegrationCredentials__Pairing__PublicOrigin"] + "/mcp"
        assert "RATELDESK_MCP_CONFIG" not in mcp
        assert not any("CREDENTIAL" in key.upper() and "MODE" not in key.upper() for key in mcp)
        assert not services["mcp-http"].get("ports")

    for file in ["docker/docker-compose.setup.yml", "docker/docker-compose.setup.source.yml"]:
        services = render(file, RATELDESK_VERSION="0.0.0-test", RATELDESK_SETUP_OUTPUT="/tmp")
        assert set(services) == {"mcp-http"}
        service = services["mcp-http"]
        assert service["command"] == ["setup", "--target", "http-host", "--export-directory", "/output"]
        assert service["user"] == "10001:10001"
        assert service["tty"] and service["stdin_open"]
        assert service["volumes"][0]["target"] == "/output"
        assert not service["volumes"][0].get("read_only", False)
        operator = render(file, RATELDESK_VERSION="0.0.0-test", RATELDESK_SETUP_OUTPUT="/tmp",
                          RATELDESK_SETUP_UID="1000", RATELDESK_SETUP_GID="1000")["mcp-http"]
        assert operator["user"] == "1000:1000"
    print("MCP host recipes: 6 local Compose contracts passed")


if __name__ == "__main__":
    main()
