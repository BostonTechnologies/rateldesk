#!/usr/bin/env bash
set -euo pipefail

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

artifact_dir=${HELPDESK_MAILBOX_ARTIFACT_DIR:-artifacts/e2e/mailbox-lifecycle/$(date -u +%Y%m%dT%H%M%SZ)}
compose_file=docker/docker-compose.mailbox-lifecycle.yml
compose_project="rateldesk-mailbox-${RANDOM}${RANDOM}"
api_port=${HELPDESK_MAILBOX_API_PORT:-5258}
web_port=${HELPDESK_MAILBOX_WEB_PORT:-5257}
database_port=${RATELDESK_MAILBOX_POSTGRES_PORT:-55442}
smtp_port=${RATELDESK_MAILBOX_SMTPS_PORT:-13465}
imap_port=${RATELDESK_MAILBOX_IMAPS_PORT:-13993}
pop_port=${RATELDESK_MAILBOX_POP3S_PORT:-13995}
export RATELDESK_MAILBOX_POSTGRES_PORT="$database_port"
export RATELDESK_MAILBOX_SMTPS_PORT="$smtp_port"
export RATELDESK_MAILBOX_IMAPS_PORT="$imap_port"
export RATELDESK_MAILBOX_POP3S_PORT="$pop_port"

mkdir -p "$artifact_dir"
fixture_dir=$(mktemp -d)
chmod 0755 "$fixture_dir"
export MAILBOX_CERT_DIR="$fixture_dir"
api_pid=''
web_pid=''
compose_started=false
api_publish=src/Helpdesk.API/bin/Release/net10.0/publish
web_publish=src/HelpDesk.NewWeb/bin/Release/net10.0/publish
api_publish_created=false
web_publish_created=false
[[ -d "$api_publish" ]] || api_publish_created=true
[[ -d "$web_publish" ]] || web_publish_created=true
greenmail_preexisting=false
greenmail_image='greenmail/standalone:2.1.14@sha256:1ef95a966418cd09b7ea91d504d8c0826bbe7a2f6e679a75c601a831587c1626'
docker image inspect "$greenmail_image" >/dev/null 2>&1 && greenmail_preexisting=true

cleanup() {
  local status=$?
  trap - EXIT INT TERM
  for pid in "$web_pid" "$api_pid"; do
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
      kill "$pid" 2>/dev/null || true
      wait "$pid" 2>/dev/null || true
    fi
  done
  if [[ "$compose_started" == true ]]; then
    docker compose -p "$compose_project" -f "$compose_file" logs --no-color >"$artifact_dir/fixtures.log" 2>&1 || true
    docker compose -p "$compose_project" -f "$compose_file" down --volumes --remove-orphans >/dev/null 2>&1 || true
  fi
  if [[ "$greenmail_preexisting" == false && "${HELPDESK_MAILBOX_KEEP_IMAGE:-false}" != true ]]; then
    docker image rm "$greenmail_image" >/dev/null 2>&1 || true
  fi
  [[ "$api_publish_created" == false ]] || rm -rf "$api_publish"
  [[ "$web_publish_created" == false ]] || rm -rf "$web_publish"
  rm -rf "$fixture_dir"
  exit "$status"
}
trap cleanup EXIT INT TERM

openssl req -x509 -newkey rsa:2048 -nodes -days 2 \
  -keyout "$fixture_dir/server.key" -out "$fixture_dir/ca.pem" \
  -subj '/CN=localhost' \
  -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1' \
  -addext 'basicConstraints=critical,CA:TRUE' \
  -addext 'keyUsage=critical,digitalSignature,keyEncipherment,keyCertSign' \
  >/dev/null 2>&1
openssl pkcs12 -export -inkey "$fixture_dir/server.key" -in "$fixture_dir/ca.pem" \
  -out "$fixture_dir/greenmail.p12" -passout pass:changeit >/dev/null 2>&1
chmod 0644 "$fixture_dir/greenmail.p12"

publish_flags=()
if [[ "${HELPDESK_MAILBOX_PUBLISH_NO_BUILD:-false}" == true ]]; then publish_flags+=(--no-build); fi
dotnet publish src/Helpdesk.API/Helpdesk.API.csproj --configuration Release --no-restore "${publish_flags[@]}" >"$artifact_dir/publish-api.log" 2>&1
dotnet publish src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj --configuration Release --no-restore "${publish_flags[@]}" >"$artifact_dir/publish-web.log" 2>&1

compose_started=true
docker compose -p "$compose_project" -f "$compose_file" up --detach postgres greenmail
for _ in $(seq 1 90); do
  if docker compose -p "$compose_project" -f "$compose_file" exec -T postgres \
    pg_isready -U rateldesk -d rateldesk >/dev/null 2>&1 && \
    MAILBOX_FIXTURE_CA="$fixture_dir/ca.pem" MAILBOX_FIXTURE_IMAPS_PORT="$imap_port" \
      python3 tools/ci/mailbox-lifecycle-fixture.py messages --account support >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
docker compose -p "$compose_project" -f "$compose_file" exec -T postgres \
  pg_isready -U rateldesk -d rateldesk >/dev/null

system_secret=$(openssl rand -hex 32)
api_url="http://127.0.0.1:${api_port}"
web_url="https://127.0.0.1:${web_port}"
connection="Host=127.0.0.1;Port=${database_port};Database=rateldesk;Username=rateldesk;Password=synthetic-postgres-password"

ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_CONTENTROOT="$repo_root/$api_publish" \
ConnectionStrings__HelpdeskDb="$connection" \
Bootstrap__StateDirectory="$fixture_dir/state" \
DataProtection__KeyRingPath="$fixture_dir/keys" \
Bootstrap__Unattended__Provider=PostgreSql \
Bootstrap__Unattended__PostgreSqlConnectionString="$connection" \
Bootstrap__Unattended__Email=admin@tenant-a.example.test \
Bootstrap__Unattended__DisplayName='Fixture Administrator' \
Bootstrap__Unattended__Password="$system_secret" \
Bootstrap__Unattended__OrganizationName='Tenant A' \
Bootstrap__Unattended__ApplicationUrl="$web_url" \
dotnet "$api_publish/Helpdesk.API.dll" --initialize-unattended >"$artifact_dir/initialize.log" 2>&1

ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_CONTENTROOT="$repo_root/$api_publish" \
ASPNETCORE_URLS="$api_url" \
Authentication__Mode=Local \
ConnectionStrings__HelpdeskDb="$connection" \
Bootstrap__StateDirectory="$fixture_dir/state" \
DataProtection__KeyRingPath="$fixture_dir/keys" \
StorageOptions__RootPath="$fixture_dir/storage" \
StorageOptions__ImageSigningSecret="$system_secret" \
SYSTEM_TOKEN_SECRET="$system_secret" \
SSL_CERT_FILE="$fixture_dir/ca.pem" \
EmailIngestion__AllowedPrivateHosts__0=localhost \
EmailSending__AllowedPrivateHosts__0=localhost \
dotnet "$api_publish/Helpdesk.API.dll" >"$artifact_dir/api.log" 2>&1 &
api_pid=$!

ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_CONTENTROOT="$repo_root/$web_publish" \
ASPNETCORE_URLS="$web_url" \
Authentication__Mode=Local \
DataProtection__KeyRingPath="$fixture_dir/keys" \
ApiBaseUrl="${api_url}/" \
ReverseProxy__Clusters__apiCluster__Destinations__api1__Address="${api_url}/" \
SYSTEM_TOKEN_SECRET="$system_secret" \
dotnet "$web_publish/HelpDesk.NewWeb.dll" >"$artifact_dir/web.log" 2>&1 &
web_pid=$!

for _ in $(seq 1 90); do
  if curl --fail --silent --max-time 2 "$api_url/health/ready" >/dev/null 2>&1 && \
    curl --insecure --fail --silent --max-time 2 "$web_url/" >/dev/null 2>&1; then
    break
  fi
  if ! kill -0 "$api_pid" 2>/dev/null || ! kill -0 "$web_pid" 2>/dev/null; then
    echo 'Published API or Web exited before readiness; see mailbox lifecycle logs.' >&2
    exit 1
  fi
  sleep 1
done
curl --fail --silent "$api_url/health/ready" >/dev/null
curl --insecure --fail --silent "$web_url/" >/dev/null

HELPDESK_E2E_AUTH_MODE=local \
HELPDESK_E2E_LOCAL_EMAIL=admin@tenant-a.example.test \
HELPDESK_E2E_LOCAL_PASSWORD="$system_secret" \
HELPDESK_E2E_BASE_URL="$web_url" \
HELPDESK_E2E_IGNORE_HTTPS_ERRORS=true \
HELPDESK_E2E_MAILBOX_LIFECYCLE=true \
MAILBOX_FIXTURE_CA="$fixture_dir/ca.pem" \
MAILBOX_FIXTURE_SMTPS_PORT="$smtp_port" \
MAILBOX_FIXTURE_IMAPS_PORT="$imap_port" \
MAILBOX_FIXTURE_POP3S_PORT="$pop_port" \
PLAYWRIGHT_HTML_OUTPUT_DIR="$artifact_dir/report" \
npx playwright test tests/ux/mailbox-lifecycle.spec.ts --output="$artifact_dir/results"
