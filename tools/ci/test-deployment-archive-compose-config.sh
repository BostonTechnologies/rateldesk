#!/usr/bin/env bash
set -Eeuo pipefail

archive=${1:?Usage: test-deployment-archive-compose-config.sh <deployment-archive>}
work_directory=$(mktemp -d)
extracted_directory="$work_directory/extracted"
config_file="$work_directory/config.json"

cleanup() {
  rm -rf "$work_directory"
}
trap cleanup EXIT

printf '%s\n' '{"apiBaseUrl":"https://api.example.test/","credentialMode":"gateway"}' > "$config_file"
chmod 0600 "$config_file"
mkdir -p "$extracted_directory"
tar -C "$extracted_directory" -xzf "$archive"
deployment_directory=$(find "$extracted_directory" -mindepth 1 -maxdepth 1 -type d -print -quit)
test -n "$deployment_directory"

test -x "$deployment_directory/setup-http-mcp.sh"
test -f "$deployment_directory/setup-http-mcp.py"
python3 - "$deployment_directory/setup-release.json" <<'PY'
import json, pathlib, re, sys
metadata = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
assert metadata["schemaVersion"] == 1
assert re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?", metadata["version"])
assert metadata["composeFile"] == "docker-compose.release.yml"
assert metadata["projectName"] == "rateldesk"
assert metadata["manager"] == "release-compose"
assert metadata["image"] == "ghcr.io/bostontechnologies/rateldesk-mcp-http:" + metadata["version"]
PY
test -s "$deployment_directory/docs/mcp-http/setup.md"
# Execute the extracted entrypoint with its adjacent helper. --help must not
# need a running deployment, source checkout, valid serving config or registry.
"$deployment_directory/setup-http-mcp.sh" --help > "$work_directory/launcher-help.txt"
grep -q -- '--verify' "$work_directory/launcher-help.txt"
grep -q -- '--apply' "$work_directory/launcher-help.txt"

test -f "$deployment_directory/examples/mcp-http/docker-compose.yml"
test -f "$deployment_directory/docker-compose.mcp.gateway.release.yml"
test -f "$deployment_directory/docker-compose.mcp.release.yml"
grep -Fqx 'RATELDESK_VERSION=' "$deployment_directory/examples/mcp-http/.env.example"
if grep -Eq '^RATELDESK_VERSION=[0-9]+\.[0-9]+' "$deployment_directory/examples/mcp-http/.env.example"; then
  echo "The extracted standalone MCP example must require an explicit release version." >&2
  exit 1
fi

compose_environment=(
  'RATELDESK_VERSION=0.0.0-pr'
  "RATELDESK_MCP_CONFIG_FILE=$config_file"
  'RATELDESK_API_BASE_URL=https://api.example.test/'
  'RATELDESK_MCP_PUBLIC_RESOURCE_URI=https://mcp.example.test/mcp'
  'RATELDESK_MCP_ALLOWED_ORIGIN=https://client.example.test'
  'RATELDESK_MCP_PORT=18223'
)
authentik_environment=(
  "${compose_environment[@]}"
  'RATELDESK_MCP_AUTHENTIK_AUTHORITY=https://auth.example.test/'
)

env "${compose_environment[@]}" docker compose \
  -f "$deployment_directory/examples/mcp-http/docker-compose.yml" config --quiet
env "${compose_environment[@]}" docker compose \
  -f "$deployment_directory/docker-compose.release.yml" \
  -f "$deployment_directory/docker-compose.mcp.gateway.release.yml" config --quiet
env "${authentik_environment[@]}" docker compose \
  -f "$deployment_directory/docker-compose.release.yml" \
  -f "$deployment_directory/docker-compose.mcp.release.yml" config --quiet
