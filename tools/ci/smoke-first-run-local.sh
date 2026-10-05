#!/usr/bin/env bash
set -Eeuo pipefail

if [[ "$#" -eq 0 ]]; then
  compose_files=(docker/docker-compose.yml)
else
  compose_files=("$@")
fi
compose_arguments=()
for compose_file in "${compose_files[@]}"; do
  compose_arguments+=(-f "$compose_file")
done

api_base_url="${RATELDESK_API_URL:-http://127.0.0.1:8222}"
web_base_url="${RATELDESK_WEB_URL:-http://127.0.0.1:8111}"
setup_provider="${RATELDESK_SETUP_PROVIDER:-Sqlite}"
work_directory="$(mktemp -d)"
cookie_jar="$work_directory/cookies.txt"
web_cookie_jar="$work_directory/web-cookies.txt"
self_service_cookie_jar="$work_directory/self-service-cookies.txt"

cleanup() {
  rm -rf "$work_directory"
}
trap cleanup EXIT
trap 'printf "First-run smoke failed at line %s.\n" "$LINENO" >&2' ERR

curl --retry 6 --retry-all-errors --retry-delay 2 --fail --silent --show-error \
  "$web_base_url/setup" > /dev/null

# Normal entry points must discover setup before offering a credentials form.
for entry_path in / /login; do
  entry_status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
    --dump-header "$work_directory/entry.headers" "$web_base_url$entry_path")"
  [[ "$entry_status" == "302" ]]
  grep -Eiq '^location: /setup[[:space:]]*$' "$work_directory/entry.headers"
done
premature_login_status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
  --dump-header "$work_directory/entry.headers" --request POST "$web_base_url/local-login")"
[[ "$premature_login_status" == "303" ]]
grep -Eiq '^location: /setup[[:space:]]*$' "$work_directory/entry.headers"

setup_code="$(docker compose "${compose_arguments[@]}" exec -T api dotnet /app/Helpdesk.API.dll --show-setup-code)"
[[ -n "$setup_code" ]]
[[ "$setup_code" == "$(docker compose "${compose_arguments[@]}" exec -T api dotnet /app/Helpdesk.API.dll --show-setup-code)" ]]
docker compose "${compose_arguments[@]}" exec -T api dotnet /app/Helpdesk.API.dll --setup-status > "$work_directory/setup-status.txt"
if [[ "${RATELDESK_EXPECT_STORAGE_MANAGED:-false}" == "true" ]]; then
  grep -q 'Setup state: Configuring' "$work_directory/setup-status.txt"
  curl --fail --silent --show-error "$api_base_url/api/v1/setup/status" | jq -e '.storageManaged == true and .provider == "PostgreSql"' > /dev/null
else
  grep -q 'Setup state: Unconfigured' "$work_directory/setup-status.txt"
fi
if grep -Fq "$setup_code" "$work_directory/setup-status.txt"; then
  echo "Setup diagnostics unexpectedly disclosed the setup code." >&2
  exit 1
fi
# Check the shipped runtime, not only the SDK/test-runner environment.
docker compose "${compose_arguments[@]}" exec -T api sh -c '
  set -eu
  set -- /usr/lib/*-linux-gnu/libgssapi_krb5.so.2
  test "$#" -eq 1
  test -r "$1"
  gssapi_dependencies="$(LC_ALL=C ldd "$1")"
  case "$gssapi_dependencies" in
    *"not found"*) exit 1 ;;
  esac
  test -r /usr/share/zoneinfo/Africa/Johannesburg
'
password="Smoke-$(openssl rand -hex 24)"

session_payload="$(jq -nc --arg setupCode "$setup_code" '{setupCode: $setupCode}')"
session="$(curl --fail --silent --show-error \
  --header 'Content-Type: application/json' \
  --data "$session_payload" \
  "$api_base_url/api/v1/setup/session" | jq -er '.session')"

case "$setup_provider" in
  Sqlite)
    storage_payload='{"provider":"Sqlite"}'
    ;;
  PostgreSql)
    postgre_sql_password="${RATELDESK_SETUP_POSTGRES_PASSWORD:?Set RATELDESK_SETUP_POSTGRES_PASSWORD for PostgreSQL setup validation}"
    storage_payload="$(jq -nc \
      --arg host "${RATELDESK_SETUP_POSTGRES_HOST:-postgres}" \
      --arg database "${RATELDESK_SETUP_POSTGRES_DATABASE:-rateldesk}" \
      --arg username "${RATELDESK_SETUP_POSTGRES_USERNAME:-rateldesk}" \
      --arg password "$postgre_sql_password" \
      '{provider: "PostgreSql", postgreSqlHost: $host, postgreSqlPort: 5432, postgreSqlDatabase: $database, postgreSqlUsername: $username, postgreSqlPassword: $password, postgreSqlUseTls: false}')"
    ;;
  *)
    echo "Unsupported setup provider: $setup_provider" >&2
    exit 2
    ;;
esac
curl --fail --silent --show-error \
  --header 'Content-Type: application/json' \
  --header "X-RatelDesk-Setup-Session: $session" \
  --data "$storage_payload" \
  "$api_base_url/api/v1/setup/storage" | jq -e '.state == "Configuring"' > /dev/null

# Exercise a real IANA zone in the shipped runtime, not only hosted browser tests or UTC.
initialize_payload="$(jq -nc --arg password "$password" '{email: "admin@example.test", displayName: "RC4 Test Administrator", password: $password, organizationName: "RC4 Test Organization", applicationName: "RatelDesk RC4", applicationUrl: "http://127.0.0.1:8111", timeZoneId: "Africa/Johannesburg"}')"
# A rejected field must be actionable, leave setup incomplete, and allow correction with the same session.
invalid_payload="$(jq -c '.timeZoneId = "Invalid/TimeZone"' <<< "$initialize_payload")"
invalid_status="$(curl --silent --show-error --output "$work_directory/invalid-initialize.json" --write-out '%{http_code}' \
  --header 'Content-Type: application/json' --header "X-RatelDesk-Setup-Session: $session" \
  --data "$invalid_payload" "$api_base_url/api/v1/setup/initialize")"
[[ "$invalid_status" == "400" ]]
jq -e '.code == "setup_validation_failed" and (.errors.timeZoneId | length > 0) and (.errors.password == null) and (.traceId | length > 0)' "$work_directory/invalid-initialize.json" > /dev/null
if grep -Fq "$password" "$work_directory/invalid-initialize.json" || grep -Fq "$session" "$work_directory/invalid-initialize.json"; then
  echo "Initialization validation unexpectedly disclosed submitted credentials." >&2
  exit 1
fi
curl --fail --silent --show-error "$api_base_url/api/v1/setup/status" | jq -e '.state == "Configuring"' > /dev/null
initialize_status="$(curl --silent --show-error --output "$work_directory/initialize.json" --write-out '%{http_code}' \
  --header 'Content-Type: application/json' \
  --header "X-RatelDesk-Setup-Session: $session" \
  --data "$initialize_payload" \
  "$api_base_url/api/v1/setup/initialize")"
if [[ "$initialize_status" != "200" ]]; then
  echo "Initialization failed with HTTP $initialize_status." >&2
  jq '{code, title, errors, traceId}' "$work_directory/initialize.json" >&2
  exit 1
fi
jq -e '.state == "Restarting"' "$work_directory/initialize.json" > /dev/null

login_payload="$(jq -nc --arg password "$password" '{email: "admin@example.test", password: $password, rememberMe: false}')"
for attempt in $(seq 1 30); do
  login_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    --cookie-jar "$cookie_jar" \
    --header 'Content-Type: application/json' \
    --header 'X-Requested-With: XMLHttpRequest' --data "$login_payload" \
    "$api_base_url/api/v1/local-auth/login" || true)"
  if [[ "$login_status" == "204" ]]; then
    break
  fi

  if [[ "$attempt" == "30" ]]; then
    echo "API did not restart into the normal application host after setup." >&2
    exit 1
  fi

  sleep 2
done

# A fresh Web-only jar proves the browser login establishes its own session.
curl --fail --silent --show-error --cookie-jar "$web_cookie_jar" \
  "$web_base_url/login" > "$work_directory/login.html"
if grep -Eiq 'name="(twoFactorCode|code)"|href="/activate"' "$work_directory/login.html"; then
  echo "The initial login form contains an unexpected activation or verification control."
  exit 1
fi
antiforgery_token="$(python3 - "$work_directory/login.html" <<'TOKEN'
from html.parser import HTMLParser
import sys
class TokenParser(HTMLParser):
    token = None
    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if tag == 'input' and values.get('name') == '__RequestVerificationToken':
            self.token = values.get('value')
parser = TokenParser()
parser.feed(open(sys.argv[1]).read())
assert parser.token, 'Login form must contain an antiforgery token'
print(parser.token)
TOKEN
)"
web_login_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
  --dump-header "$work_directory/web-login.headers" \
  --cookie-jar "$web_cookie_jar" --cookie "$web_cookie_jar" \
  --data-urlencode "__RequestVerificationToken=$antiforgery_token" \
  --data-urlencode 'email=admin@example.test' \
  --data-urlencode "password=$password" \
  "$web_base_url/local-login")"
[[ "$web_login_status" == "302" ]]
grep -Eiq '^location: /home[[:space:]]*$' "$work_directory/web-login.headers"
curl --fail --silent --show-error --cookie "$web_cookie_jar" \
  "$web_base_url/api/v1/auth/me" | jq -e '.isAuthenticated == true and .isHelpdeskAdmin == true' > /dev/null

access_payload="$(curl --fail --silent --show-error \
  --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  "$api_base_url/api/v1/auth/me")"
organization_id="$(jq -er '.primaryOrganizationId' <<< "$access_payload")"
administrator_id="$(curl --fail --silent --show-error \
  --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  "$api_base_url/api/v1/users/by-email/admin%40example.test" | jq -er '.id')"

customer_payload="$(jq -nc --arg organizationId "$organization_id" '{name: "RC4 Smoke Customer", email: "rc4-smoke-customer@example.test", organizationId: $organizationId}')"
customer_id="$(curl --fail --silent --show-error \
  --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  --header 'Content-Type: application/json' \
  --data "$customer_payload" \
  "$api_base_url/api/v1/customers" | jq -er '.id')"

incident_payload="$(jq -nc --arg customerId "$customer_id" --arg organizationId "$organization_id" '{title: "RC4 smoke incident", description: "Production first-run incident verification.", priority: 0, customerId: $customerId, organizationId: $organizationId}')"
incident_id="$(curl --fail --silent --show-error \
  --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  --header 'Content-Type: application/json' \
  --data "$incident_payload" \
  "$api_base_url/api/v1/incidents" | jq -er '.id')"
curl --fail --silent --show-error --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  "$api_base_url/api/v1/incidents/$incident_id" | jq -e --arg id "$incident_id" '.id == $id' > /dev/null
curl --fail --silent --show-error --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  --request PUT --header 'Content-Type: application/json' \
  --data '{"state":3,"priority":1}' \
  "$api_base_url/api/v1/incidents/$incident_id" > /dev/null

printf 'attachment durability fixture\n' > "$work_directory/attachment.txt"
attachment_id="$(curl --fail --silent --show-error \
  --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  --form "files=@$work_directory/attachment.txt;filename=rc4-smoke.txt;type=text/plain" \
  "$api_base_url/api/v1/tickets/$incident_id/attachments/" | jq -er '.[0].id')"
curl --fail --silent --show-error --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  "$api_base_url/api/v1/tickets/$incident_id/attachments/" | jq -e --arg id "$attachment_id" 'any(.[]; .id == $id)' > /dev/null
curl --fail --silent --show-error --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  "$api_base_url/api/v1/attachments/$attachment_id" > /dev/null

request_payload="$(jq -nc --arg customerId "$customer_id" --arg organizationId "$organization_id" '{title: "RC4 smoke request", description: "Production first-run request verification.", priority: 0, customerId: $customerId, organizationId: $organizationId}')"
request_id="$(curl --fail --silent --show-error \
  --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  --header 'Content-Type: application/json' \
  --data "$request_payload" \
  "$api_base_url/api/v1/requests" | jq -er '.id')"
curl --fail --silent --show-error --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  "$api_base_url/api/v1/requests/$request_id" | jq -e --arg id "$request_id" '.id == $id' > /dev/null
curl --fail --silent --show-error --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  --request PUT --header 'Content-Type: application/json' \
  --data '{"state":3,"priority":1}' \
  "$api_base_url/api/v1/requests/$request_id" > /dev/null

change_payload="$(jq -nc --arg organizationId "$organization_id" --arg administratorId "$administrator_id" '{title: "RC4 smoke change", description: "Production first-run change verification.", priority: 0, organizationId: $organizationId, requestedForUserId: $administratorId, implementorUserId: $administratorId, changeType: "Standard", implementationStartAt: "2030-01-02T10:00:00Z", implementationEndAt: "2030-01-02T11:00:00Z", changeTemplate: {isPreApproved: false, existingRunbookReference: "RC4-SMOKE", scopeOfChange: "Smoke validation", affectedSystems: ["RatelDesk"], implementationSteps: ["Verify startup"], validationSteps: ["Verify ticket creation"], rollbackPlan: "Revert the smoke change"}}')"
change_id="$(curl --fail --silent --show-error \
  --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  --header 'Content-Type: application/json' \
  --data "$change_payload" \
  "$api_base_url/api/v1/changes" | jq -er '.id')"
curl --fail --silent --show-error --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  "$api_base_url/api/v1/changes/$change_id" | jq -e --arg id "$change_id" '.id == $id' > /dev/null
curl --fail --silent --show-error --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  --request PUT --header 'Content-Type: application/json' \
  --data '{"state":3,"priority":1}' \
  "$api_base_url/api/v1/changes/$change_id" > /dev/null

self_service_email="rc4-self-service@example.test"
self_service_password="Rc4-$(openssl rand -hex 24)"
self_service_account_payload="$(jq -nc --arg email "$self_service_email" --arg organizationId "$organization_id" '{displayName: "RC4 Self-service User", email: $email, organizationId: $organizationId}')"
self_service_activation="$(curl --fail --silent --show-error \
  --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  --header 'Content-Type: application/json' \
  --data "$self_service_account_payload" \
  "$api_base_url/api/v1/local-auth/users")"
self_service_activation_token="$(jq -er '.activationToken' <<< "$self_service_activation")"
self_service_activate_payload="$(jq -nc --arg email "$self_service_email" --arg token "$self_service_activation_token" --arg password "$self_service_password" '{email: $email, activationToken: $token, newPassword: $password}')"
curl --fail --silent --show-error \
  --header 'Content-Type: application/json' \
  --data "$self_service_activate_payload" \
  "$api_base_url/api/v1/local-auth/activate" > /dev/null

self_service_login_payload="$(jq -nc --arg email "$self_service_email" --arg password "$self_service_password" '{email: $email, password: $password, rememberMe: false}')"
curl --fail --silent --show-error \
  --cookie-jar "$self_service_cookie_jar" \
  --header 'Content-Type: application/json' \
  --header 'X-Requested-With: XMLHttpRequest' --data "$self_service_login_payload" \
  "$api_base_url/api/v1/local-auth/login" > /dev/null

self_service_form_payload="$(jq -nc --arg organizationId "$organization_id" '{serviceId: "rc4-self-service", title: "RC4 self-service request", description: "Production local account validation.", jsonSchema: "{\"title\":\"RC4 self-service request\",\"description\":\"Production local account validation.\",\"fields\":[]}", organizationId: $organizationId, allowedOrganizationIds: [$organizationId], releaseStatus: 1}')"
self_service_form_id="$(curl --fail --silent --show-error \
  --cookie "$cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  --header 'Content-Type: application/json' \
  --data "$self_service_form_payload" \
  "$api_base_url/api/v1/request-forms" | jq -er '.id')"
self_service_request_payload="$(jq -nc --arg requestFormId "$self_service_form_id" '{requestFormId: $requestFormId, payloadJson: "{}"}')"
self_service_request_id="$(curl --fail --silent --show-error \
  --cookie "$self_service_cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  --header 'Content-Type: application/json' \
  --data "$self_service_request_payload" \
  "$api_base_url/api/v1/self-service/requests" | jq -er '.requestId')"
curl --fail --silent --show-error --cookie "$self_service_cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  "$api_base_url/api/v1/self-service/requests/$self_service_request_id" | jq -e --arg id "$self_service_request_id" '.id == $id' > /dev/null

self_service_attachment_id="$(curl --fail --silent --show-error \
  --cookie "$self_service_cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  --form 'files=@/dev/null;filename=rc4-self-service.txt;type=text/plain' \
  "$api_base_url/api/v1/tickets/$self_service_request_id/attachments/" | jq -er '.[0].id')"
curl --fail --silent --show-error --cookie "$self_service_cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  "$api_base_url/api/v1/tickets/$self_service_request_id/attachments/" | jq -e --arg id "$self_service_attachment_id" 'any(.[]; .id == $id)' > /dev/null
curl --fail --silent --show-error --cookie "$self_service_cookie_jar" --header 'X-Requested-With: XMLHttpRequest' \
  "$api_base_url/api/v1/attachments/$self_service_attachment_id" > /dev/null

# Exercise provider-specific timeline SQL, then verify durable files and cookies after recreation.
for ticket_route in "incidents/$incident_id" "requests/$request_id" "changes/$change_id"; do
  curl --fail --silent --show-error --cookie "$cookie_jar" \
    "$api_base_url/api/v1/$ticket_route/timeline" | jq -e 'type == "array"' > /dev/null
done
docker compose "${compose_arguments[@]}" logs --no-color api > "$work_directory/api.log"

# Only this disposable smoke enables service discovery, after real unattended setup.
# Keep Web and API identities explicit and distinct; public request Host is not configuration.
discovery_api_base_url="${api_base_url%/}"
discovery_web_base_url="${web_base_url%/}"
[[ "$discovery_api_base_url" != "$discovery_web_base_url" ]]
discovery_instance_id="$(python3 -c 'import uuid; print(uuid.uuid4())')"
discovery_issuer="$discovery_api_base_url/services"
discovery_audience="rateldesk.smoke.services"
discovery_override="$work_directory/service-link-discovery.compose.json"
jq -n \
  --arg api "$discovery_api_base_url" --arg web "$discovery_web_base_url" \
  --arg issuer "$discovery_issuer" --arg audience "$discovery_audience" \
  --arg instance "$discovery_instance_id" \
  '{services: {api: {environment: {
    ServiceIdentity__Enabled: "true",
    ServiceIdentity__Issuer: $issuer,
    ServiceIdentity__Audience: $audience,
    ServiceIdentity__ApiBaseUrl: $api,
    ServiceIdentity__WebBaseUrl: $web,
    ServiceIdentity__InstanceId: $instance,
    ServiceIdentity__AllowPrivateHttp: "true",
    ServiceLinks__Enabled: "true",
    ServiceLinks__ApiBaseUrl: $api,
    ServiceLinks__WebBaseUrl: $web,
    ServiceLinks__AllowPrivateHttp: "true"
  }}}}' > "$discovery_override"
compose_arguments+=(-f "$discovery_override")
docker compose "${compose_arguments[@]}" up --detach --no-deps --force-recreate api
curl --retry 30 --retry-all-errors --retry-delay 2 --fail --silent --show-error \
  --cookie "$cookie_jar" "$api_base_url/api/v1/attachments/$attachment_id" > "$work_directory/restored.txt"
cmp "$work_directory/attachment.txt" "$work_directory/restored.txt"
curl --fail --silent --show-error --cookie "$web_cookie_jar" \
  "$web_base_url/api/v1/auth/me" | jq -e '.isAuthenticated == true and .isHelpdeskAdmin == true' > /dev/null

curl --fail --silent --show-error "$api_base_url/api/v1/setup/status" | jq -e '.state == "Ready"' > /dev/null

# Exercise the real public Web pipeline, anonymously, against its configured API.
metadata_path="/api/integrations/service-link/metadata"
metadata_status="$(curl --fail --silent --show-error --dump-header "$work_directory/discovery.headers" \
  --output "$work_directory/web-metadata.json" --write-out '%{http_code}' \
  "$discovery_web_base_url$metadata_path")"
[[ "$metadata_status" == "200" ]]
grep -Eiq '^cache-control:.*no-store' "$work_directory/discovery.headers"
jq -e \
  --arg api "$discovery_api_base_url" --arg web "$discovery_web_base_url" \
  --arg issuer "$discovery_issuer" --arg audience "$discovery_audience" \
  --arg instance "$discovery_instance_id" \
  '.contract == "bostec.service-link.v1" and .product == "rateldesk" and
   (.product_version | type == "string" and length > 0) and
   .instance_id == $instance and .api_base_url == $api and .web_base_url == $web and
   .oauth_issuer == $issuer and .audience == $audience and
   .oauth_metadata_url == ($api + "/.well-known/oauth-authorization-server") and
   .token_endpoint == ($api + "/connect/token") and
   .jwks_uri == ($api + "/.well-known/jwks.json") and
   .service_link_endpoint == ($api + "/api/integrations/service-link/v1") and
   .approval_endpoint == ($web + "/account/integration-credentials/link/approve") and
   .callback_endpoint == ($web + "/account/integration-credentials/link/callback") and
   (.supported_contracts | index("bostec.service-link.v1") != null)' \
  "$work_directory/web-metadata.json" > /dev/null
curl --fail --silent --show-error "$discovery_api_base_url$metadata_path" \
  | jq -S . > "$work_directory/api-metadata.json"
jq -S . "$work_directory/web-metadata.json" > "$work_directory/web-metadata.sorted.json"
cmp "$work_directory/api-metadata.json" "$work_directory/web-metadata.sorted.json"

# Match ASP.NET's case-insensitive route semantics without admitting a path prefix.
curl --fail --silent --show-error "$discovery_web_base_url/API/INTEGRATIONS/SERVICE-LINK/METADATA" \
  | jq -S . > "$work_directory/web-metadata.uppercase.json"
cmp "$work_directory/api-metadata.json" "$work_directory/web-metadata.uppercase.json"
for blocked_path in /api/integrations/unrelated-discovery "$metadata_path/extra" "$metadata_path/"; do
  blocked_status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
    "$discovery_web_base_url$blocked_path")"
  [[ "$blocked_status" == "404" ]]
done

docker compose "${compose_arguments[@]}" exec -T api dotnet /app/Helpdesk.API.dll --setup-status > "$work_directory/setup-status.txt"
grep -q 'Setup state: Ready' "$work_directory/setup-status.txt"
for closed_command in --show-setup-code --rotate-setup-code; do
  if docker compose "${compose_arguments[@]}" exec -T api dotnet /app/Helpdesk.API.dll "$closed_command" > "$work_directory/closed-command.txt" 2>&1; then
    echo "Completed setup unexpectedly accepted $closed_command." >&2
    exit 1
  fi
done
docker compose "${compose_arguments[@]}" exec -T api sh -c 'test ! -f /var/lib/rateldesk/bootstrap/setup-code'
docker compose "${compose_arguments[@]}" logs --no-color api >> "$work_directory/api.log"
if grep -Fq "$setup_code" "$work_directory/api.log" || grep -Fq "$password" "$work_directory/api.log" || grep -Fq "$session" "$work_directory/api.log"; then
  echo "API logs unexpectedly disclosed setup credentials." >&2
  exit 1
fi
if grep -q 'Cannot load library libgssapi_krb5' "$work_directory/api.log"; then
  echo "API image is missing Npgsql native GSS support." >&2
  exit 1
fi

echo "First-run $setup_provider setup, local sign-in, core ticket CRUD, self-service request, attachment, and public Web discovery smoke test passed."
