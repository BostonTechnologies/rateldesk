#!/usr/bin/env bash

set -euo pipefail

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

api_port=${HELPDESK_E2E_API_PORT:-5158}
web_port=${HELPDESK_E2E_WEB_PORT:-5157}
database_port=${HELPDESK_E2E_DATABASE_PORT:-55432}
api_url="http://127.0.0.1:${api_port}"
web_url="https://127.0.0.1:${web_port}"
setup_api_port=${HELPDESK_E2E_SETUP_API_PORT:-5168}
setup_web_port=${HELPDESK_E2E_SETUP_WEB_PORT:-5167}
setup_api_url="http://127.0.0.1:${setup_api_port}"
setup_web_url="https://127.0.0.1:${setup_web_port}"
artifact_dir=${HELPDESK_E2E_ARTIFACT_DIR:-artifacts/e2e}
compose_project="helpdesk-e2e-${RANDOM}${RANDOM}"
compose_file=docker/docker-compose.e2e.yml
mkdir -p "$artifact_dir"

e2e_system_secret=$(openssl rand -hex 32)
api_log="$artifact_dir/api.log"
web_log="$artifact_dir/web.log"
database_log="$artifact_dir/postgres.log"
setup_api_log="$artifact_dir/setup-api.log"
setup_web_log="$artifact_dir/setup-web.log"
setup_state_dir=$(mktemp -d)

api_pid=''
web_pid=''
setup_api_pid=''
setup_web_pid=''
compose_started=false

cleanup() {
  local status=$?

  for pid in "$setup_web_pid" "$setup_api_pid" "$web_pid" "$api_pid"; do
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
      kill "$pid" 2>/dev/null || true
      wait "$pid" 2>/dev/null || true
    fi
  done

  if [[ "$compose_started" == true ]]; then
    RATELDESK_POSTGRES_PORT="$database_port" docker compose -p "$compose_project" -f "$compose_file" logs --no-color >"$database_log" 2>&1 || true
    RATELDESK_POSTGRES_PORT="$database_port" docker compose -p "$compose_project" -f "$compose_file" down --volumes --remove-orphans >/dev/null 2>&1 || true
  fi

  rm -rf "$setup_state_dir"

  exit "$status"
}
trap cleanup EXIT INT TERM

wait_for_health() {
  local name=$1
  local pid=$2
  local url=$3
  local readiness_path=$4
  local log=$5

  for _ in $(seq 1 90); do
    if curl --insecure --fail --silent --show-error --max-time 2 "$url$readiness_path" >/dev/null 2>&1; then
      return 0
    fi

    if ! kill -0 "$pid" 2>/dev/null; then
      echo "$name exited before becoming healthy."
      sed -n '1,240p' "$log"
      return 1
    fi

    sleep 1
  done

  echo "$name did not become ready at $url$readiness_path."
  sed -n '1,240p' "$log"
  return 1
}

# The image briefly starts a socket-only server during initialization. Wait for
# TCP before the host-side initializer connects to the published port.
wait_for_database() {
  for _ in $(seq 1 90); do
    if RATELDESK_POSTGRES_PORT="$database_port" docker compose -p "$compose_project" -f "$compose_file" exec -T postgres pg_isready --host 127.0.0.1 --username rateldesk --dbname rateldesk >/dev/null 2>&1; then
      return 0
    fi

    sleep 1
  done

  echo "PostgreSQL did not become ready."
  RATELDESK_POSTGRES_PORT="$database_port" docker compose -p "$compose_project" -f "$compose_file" logs --no-color || true
  return 1
}

stop_process() {
  local pid=$1

  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
}

run_setup_wizard_validation() {
  setup_web_publish="$setup_state_dir/published-web"
  dotnet publish src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj --configuration Release --no-build --no-restore --output "$setup_web_publish"
  # The bootstrap host exits after commit; supervise that transition just like Compose.
  (
    trap 'kill "$child_pid" 2>/dev/null || true; exit 0' TERM INT
    while true; do
      ASPNETCORE_ENVIRONMENT=Production \
      ASPNETCORE_URLS="$setup_api_url" \
      ConnectionStrings__HelpdeskDb= \
      Authentication__AllowInsecureLocalhost=true \
      DataProtection__KeyRingPath="$setup_state_dir/keys" \
      Bootstrap__StateDirectory="$setup_state_dir/state" \
      Bootstrap__DataDirectory="$setup_state_dir/data" \
      StorageOptions__RootPath="$setup_state_dir/storage" \
      dotnet run --project src/Helpdesk.API/Helpdesk.API.csproj --configuration Release --no-build --no-launch-profile >>"$setup_api_log" 2>&1 &
      child_pid=$!
      wait "$child_pid" || exit $?
    done
  ) &
  setup_api_pid=$!
  wait_for_health 'Bootstrap Helpdesk API' "$setup_api_pid" "$setup_api_url" '/health/ready' "$setup_api_log"

  # Exercise the operator command against the actual generated code and custom path.
  setup_code="$(ASPNETCORE_ENVIRONMENT=Production \
    Bootstrap__StateDirectory="$setup_state_dir/state" \
    dotnet src/Helpdesk.API/bin/Release/net10.0/Helpdesk.API.dll --show-setup-code)"
  [[ -n "$setup_code" ]]

  ASPNETCORE_ENVIRONMENT=Production \
  ASPNETCORE_CONTENTROOT="$setup_web_publish" \
  ASPNETCORE_URLS="$setup_web_url" \
  DataProtection__KeyRingPath="$setup_state_dir/keys" \
  ApiBaseUrl="${setup_api_url}/" \
  ReverseProxy__Clusters__apiCluster__Destinations__api1__Address="${setup_api_url}/" \
  Authentication__AllowInsecureLocalhost=true \
  dotnet "$setup_web_publish/HelpDesk.NewWeb.dll" >"$setup_web_log" 2>&1 &
  setup_web_pid=$!
  wait_for_health 'Bootstrap Helpdesk web' "$setup_web_pid" "$setup_web_url" '/' "$setup_web_log"

  PLAYWRIGHT_HTML_OUTPUT_DIR="$artifact_dir/setup-report" \
  HELPDESK_E2E_BASE_URL="$setup_web_url" \
  HELPDESK_E2E_IGNORE_HTTPS_ERRORS=true \
  HELPDESK_E2E_SETUP_CODE="$setup_code" \
  HELPDESK_E2E_RUN_SETUP_WIZARD=true \
  npm run test:ux -- tests/ux/setup.spec.ts --output="$artifact_dir/setup-test-results"

  stop_process "$setup_web_pid"
  setup_web_pid=''
  stop_process "$setup_api_pid"
  setup_api_pid=''
}

run_setup_wizard_validation

compose_started=true
RATELDESK_POSTGRES_PORT="$database_port" docker compose -p "$compose_project" -f "$compose_file" up --detach postgres
wait_for_database

Bootstrap__StateDirectory="$setup_state_dir/e2e-state" \
DataProtection__KeyRingPath="$setup_state_dir/e2e-keys" \
Bootstrap__Unattended__Provider=PostgreSql \
Bootstrap__Unattended__PostgreSqlConnectionString="Host=127.0.0.1;Port=${database_port};Database=rateldesk;Username=rateldesk;Password=rateldesk" \
Bootstrap__Unattended__Email=fixture.admin@example.test \
Bootstrap__Unattended__DisplayName='Fixture Administrator' \
Bootstrap__Unattended__Password="$e2e_system_secret" \
Bootstrap__Unattended__OrganizationName='Fixture Organization' \
Bootstrap__Unattended__ApplicationUrl="$web_url" \
dotnet run --project src/Helpdesk.API/Helpdesk.API.csproj --configuration Release --no-build --no-launch-profile -- --initialize-unattended

ASPNETCORE_ENVIRONMENT=Development \
Bootstrap__StateDirectory="$setup_state_dir/e2e-state" \
DataProtection__KeyRingPath="$setup_state_dir/e2e-keys" \
Authentication__Mode=Oidc \
ASPNETCORE_URLS="$api_url" \
ConnectionStrings__HelpdeskDb="Host=127.0.0.1;Port=${database_port};Database=rateldesk;Username=rateldesk;Password=rateldesk" \
EmailIngestion__Enabled=false \
Helpdesk__E2eSeedData=true \
StorageOptions__ImageSigningSecret="$e2e_system_secret" \
ExchangeEmail__TenantId=00000000-0000-0000-0000-000000000000 \
ExchangeEmail__ClientId=00000000-0000-0000-0000-000000000001 \
ExchangeEmail__ClientSecret="$e2e_system_secret" \
ExchangeEmail__MailboxAddress=helpdesk-e2e@example.invalid \
SYSTEM_TOKEN_SECRET="$e2e_system_secret" \
dotnet run --project src/Helpdesk.API/Helpdesk.API.csproj --configuration Release --no-build --no-launch-profile >"$api_log" 2>&1 &
api_pid=$!
wait_for_health 'Helpdesk API' "$api_pid" "$api_url" '/health/live' "$api_log"
# A bootstrap-only host is live too; browser authentication needs completed setup.
curl --fail --silent --show-error "$api_url/api/v1/setup/status" | python3 -c 'import json, sys; assert json.load(sys.stdin)["state"] == "Ready", "UX database initialization did not complete"'

ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS="$web_url" \
ApiBaseUrl="${api_url}/" \
ReverseProxy__Clusters__apiCluster__Destinations__api1__Address="${api_url}/" \
AUTHENTIK_CLIENT_SECRET="$e2e_system_secret" \
Authentication__Authentik__Authority=https://127.0.0.1:5999/application/o/helpdesk-e2e/ \
Authentication__Authentik__ClientId=helpdesk-e2e \
Authentication__Authentik__ApiScope=helpdesk-api \
SYSTEM_TOKEN_SECRET="$e2e_system_secret" \
dotnet run --project src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj --configuration Release --no-build --no-launch-profile >"$web_log" 2>&1 &
web_pid=$!
wait_for_health 'Helpdesk web' "$web_pid" "$web_url" '/' "$web_log"

HELPDESK_E2E_AUTH_MODE=development \
HELPDESK_E2E_BASE_URL="$web_url" \
HELPDESK_E2E_IGNORE_HTTPS_ERRORS=true \
npm run test:ux -- "$@"
