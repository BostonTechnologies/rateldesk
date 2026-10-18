#!/usr/bin/env bash

set -euo pipefail

for dependency in git rg dotnet python3 docker; do
  if ! command -v "$dependency" >/dev/null 2>&1; then
    printf 'layout validation: required tool is missing: %s\n' "$dependency" >&2
    exit 1
  fi
done

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

python3 tools/ci/validate-docs.py
python3 tools/ci/validate-mcp-acceptance.py --mode structure
python3 tools/ci/test-mcp-acceptance.py
python3 tools/ci/test-mcp-host-recipes.py
python3 tools/ci/test-mcp-launcher.py
python3 tools/ci/test-mcp-compose-toolchain.py
python3 tools/ci/test-mcp-native-launcher.py
python3 tools/ci/test-mcp-source-review.py
python3 tools/ci/test-mcp-legacy-recipe.py
python3 tools/ci/test-mcp-external-manager.py

runtime_projects=(
  Helpdesk.API
  HelpDesk.NewWeb
  Helpdesk.AgentClient
  Helpdesk.AppHost
  Helpdesk.Application
  Helpdesk.Cli
  Helpdesk.Infrastructure
  Helpdesk.Mcp
  Helpdesk.Mcp.Core
  Helpdesk.Mcp.Http
  Helpdesk.ServiceDefaults
  Helpdesk.Shared
  Helpdesk.Setup
)

failed=false
fail() {
  printf 'layout validation: %s\n' "$*" >&2
  failed=true
}

for project in "${runtime_projects[@]}"; do
  [[ -d "src/$project" ]] || fail "missing runtime project directory src/$project"
  [[ ! -e "$project" ]] || fail "runtime project directory reintroduced at repository root: $project"
done

[[ -d tests/Helpdesk.Tests ]] || fail 'missing tests/Helpdesk.Tests'
[[ ! -e Helpdesk.Tests ]] || fail 'Helpdesk.Tests reintroduced at repository root'
[[ ! -e HelpDesk.sln ]] || fail 'obsolete HelpDesk.sln must not coexist with Helpdesk.sln'
[[ -f docker/api/Dockerfile ]] || fail 'missing docker/api/Dockerfile'
[[ -f docker/web/Dockerfile ]] || fail 'missing docker/web/Dockerfile'
[[ -f docker/mcp-http/Dockerfile ]] || fail 'missing docker/mcp-http/Dockerfile'
[[ -f docker/docker-compose.yml ]] || fail 'missing docker/docker-compose.yml'
[[ ! -e stack-helpdesk.yml ]] || fail 'stack-helpdesk.yml must live under docker/'
[[ ! -e traefik-rp-dev.yml ]] || fail 'Traefik dev collateral must live under docker/traefik/'
[[ ! -e traefik-rp-prod.yml ]] || fail 'Traefik prod collateral must live under docker/traefik/'
[[ ! -e docs/docker-compose.yml ]] || fail 'Docker Compose collateral must live under docker/'

if find src -type f -name Dockerfile -print -quit | rg -q '.'; then
  fail 'project-local Dockerfiles must not exist below src/'
fi

active_paths=(.github .aspire .vscode docker tools package.json package-lock.json playwright.config.ts setup.sh Helpdesk.sln)
# A leading slash means the current docker/ paths are valid. Only a root-relative
# filename (or an old project-local Dockerfile) is stale.
stale_pattern='Helpdesk\.API/Dockerfile|HelpDesk\.NewWeb/Dockerfile|Helpdesk\.Mcp\.Http/Dockerfile|(^|[^A-Za-z0-9_/])stack-helpdesk\.yml|(^|[^A-Za-z0-9_/])traefik-rp-(dev|prod)\.yml'
if rg -n --glob '!docker/README.md' --glob '!tools/ci/validate-layout.sh' "$stale_pattern" "${active_paths[@]}"; then
  fail 'active build or deployment collateral still refers to a pre-migration Docker or stack path'
fi

while IFS= read -r project; do
  [[ -z "$project" ]] && continue
  [[ -f "$project" ]] || fail "solution references missing project: $project"
done < <(dotnet sln Helpdesk.sln list | tail -n +3)

if [[ "$failed" == true ]]; then
  exit 1
fi

printf 'layout validation passed\n'
