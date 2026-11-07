#!/usr/bin/env python3
"""Small, local Compose contract checks; never starts a service or contacts an API."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

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
    retention_values = {
        "RATELDESK_VERSION": "0.0.0-test",
        "RATELDESK_PUBLIC_ORIGIN": "https://desk.example.test",
        "RATELDESK_API_BASE_URL": "https://api.example.test/",
        "RATELDESK_MCP_CONFIG_FILE": "/tmp/synthetic-mcp.json",
        "RATELDESK_MCP_PUBLIC_RESOURCE_URI": "https://desk.example.test/mcp",
        "RATELDESK_MCP_ALLOWED_ORIGIN": "https://client.example.test",
        "RATELDESK_MCP_AUTHENTIK_AUTHORITY": "https://auth.example.test/",
        "RATELDESK_POSTGRES_PASSWORD": "synthetic-password",
        "RATELDESK_SETUP_OUTPUT": "/tmp",
        "MAILBOX_CERT_DIR": "/tmp",
        "RATELDESK_POSTGRES_CONNECTION_STRING": "Host=postgres;Database=synthetic;Username=synthetic;Password=synthetic",
        "RATELDESK_BOOTSTRAP_ADMIN_EMAIL": "admin@example.test",
        "RATELDESK_BOOTSTRAP_ADMIN_DISPLAY_NAME": "Synthetic Administrator",
        "RATELDESK_BOOTSTRAP_ADMIN_PASSWORD": "synthetic-password",
        "RATELDESK_BOOTSTRAP_ORGANIZATION_NAME": "Synthetic Organization",
        "RATELDESK_BOOTSTRAP_APPLICATION_URL": "https://desk.example.test",
    }
    combinations = []
    for base, suffix in [("docker/docker-compose.yml", ""), ("docker/docker-compose.release.yml", ".release")]:
        combinations.append((base,))
        combinations.append((base, "docker/docker-compose.postgres.yml", "docker/docker-compose.external-postgres.yml"))
        for mode in ["mcp.env", "mcp.gateway", "mcp"]:
            combinations.append((base, f"docker/docker-compose.{mode}{suffix}.yml"))
    combinations.extend((file,) for file in [
        "docker/docker-compose.setup.yml", "docker/docker-compose.setup.source.yml",
        "docker/docker-compose.e2e.yml", "docker/docker-compose.mailbox-lifecycle.yml",
        "docker/examples/mcp-http/docker-compose.yml",
    ])
    for files in combinations:
        for size, count, minimum in [("200m", "5", "Warning"), ("8m", "2", "Debug")]:
            values = dict(retention_values)
            if size != "200m":
                values.update(RATELDESK_DOCKER_LOG_MAX_SIZE=size, RATELDESK_DOCKER_LOG_MAX_FILE=count,
                              RATELDESK_LOG_MINIMUM_LEVEL=minimum)
            services = render(*files, **values)
            for name, service in services.items():
                assert service["logging"] == {"driver": "json-file", "options": {"max-size": size, "max-file": count}}, (files, name)
                assert all(isinstance(value, str) for value in service["logging"]["options"].values())
                if name in {"api", "web", "mcp-http"}:
                    assert service["environment"]["RatelDesk__Logging__MinimumLevel"] == minimum

    # The operator's final owning overlay can explicitly replace the default
    # driver and its complete options instead of inheriting json-file options.
    with tempfile.TemporaryDirectory(prefix="rd-log-policy-") as directory:
        overlay = Path(directory) / "consumer.yml"
        overlay.write_text('services:\n  api:\n    logging: !override\n      driver: syslog\n      options:\n        syslog-address: "udp://127.0.0.1:514"\n')
        services = render("docker/docker-compose.release.yml", str(overlay), **retention_values)
        assert services["api"]["logging"] == {"driver": "syslog", "options": {"syslog-address": "udp://127.0.0.1:514"}}
    print(f"MCP host recipes: 6 host contracts and {len(combinations) * 2 + 1} effective logging contracts passed")


if __name__ == "__main__":
    main()
