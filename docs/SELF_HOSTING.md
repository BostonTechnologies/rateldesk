# Self-hosting RatelDesk

## Architecture

RatelDesk has an ASP.NET Core API and a Blazor web application. The default installation uses embedded SQLite and local accounts; PostgreSQL, OIDC, and MCP hosting are optional. The Web application proxies `/api` traffic to the API. Run both application services behind a TLS-terminating reverse proxy in production.

For built-in roles, scoped custom roles, and the bounded tenant-member delegation workflow, see [Roles and tenant-scoped access](rbac.md).

## First use and Docker

Use [docker/docker-compose.yml](../docker/docker-compose.yml) as the default starting point. It starts only Web and API in Production mode and persists four distinct concerns: bootstrap state, data-protection keys, SQLite data, and attachments. Run repository-relative Compose commands from a checked-out repository's root directory. After the first API start, open `http://localhost:8111/`, or your public application URL. Fresh root and login visits automatically open `/setup`.

Retrieve the operator-only setup code using the running API's configuration:

```sh
docker compose -f docker/docker-compose.yml exec api dotnet /app/Helpdesk.API.dll --show-setup-code
```

For an existing managed deployment, use the actual container name instead. No Compose file or interactive shell is needed. Run the first command on the Docker host, find the API container, then replace `rateldesk-api-1` below with its name:

```sh
docker ps --format 'table {{.Names}}\t{{.Image}}'
docker exec rateldesk-api-1 dotnet /app/Helpdesk.API.dll --show-setup-code
```

If already inside the API container, run `dotnet /app/Helpdesk.API.dll --show-setup-code`. Choose `sh` when opening a container terminal: the Alpine image has `sh`, and does not require Bash. The command reads the current code without rotating it. It supports custom `Bootstrap__StateDirectory` values and deployment-supplied `Bootstrap__SetupCode` values, so no hard-coded file path is needed. `--show-setup-code` and `--setup-status` are available from rc.5 onward.

For a native .NET deployment, run the same switches with the published API assembly path and its deployment environment. Use absolute bootstrap, data, and key-ring paths if the service normally runs from a different working directory than its assembly. Operator commands resolve relative file paths from the API assembly directory and honor `DOTNET_CONTENTROOT` / `ASPNETCORE_CONTENTROOT` for mounted appsettings.

Paste the printed code into **Unlock setup**. The seven visible stages guide you through storage, instance details, the first administrator, optional branding, review, and completion. The code is consumed at completion and is never returned by HTTP APIs or application logs. The API exits its restricted setup host after completion; configure your container manager to restart it, as the bundled Compose files do. The wizard waits for the normal application host to become available.

Sign in with the administrator email and password created in the wizard. The setup code only unlocks the one-time wizard; it is not a login credential or an authenticator code. Initial login has no MFA step. An account that later enables two-factor authentication is asked for its authenticator or saved recovery code only after a correct password. Account activation links are issued by administrators for invited accounts and are not part of first-admin login.

When `StorageOptions__ImageSigningSecret` is unset, a bootstrap-managed installation generates a cryptographically random image and public-ticket link signing key on its first normal runtime start. The value is protected with the same durable Data Protection key ring and stored on the bootstrap volume; it is not written to appsettings or returned by an API. Keep that volume and key ring in the recovery set so existing signed links remain valid. Set `StorageOptions__ImageSigningSecret` only when a deployment deliberately owns and rotates that secret; a deployment-provided value takes precedence over the generated one.

An operator can prefill and lock non-secret interactive values with
`Bootstrap__Interactive__OrganizationName`,
`Bootstrap__Interactive__ApplicationName`,
`Bootstrap__Interactive__ApplicationUrl`, and
`Bootstrap__Interactive__TimeZoneId`. The setup page labels these as
deployment-managed and renders them read-only; the API applies the same values
at completion, so modifying the browser request cannot override them. Keep
database passwords and administrator passwords out of these values and use the
operator-controlled setup flow or unattended secret inputs instead.

Before setup completes, an operator can replace a lost or exposed setup code without reopening a completed instance:

```bash
docker exec rateldesk-api-1 dotnet /app/Helpdesk.API.dll --rotate-setup-code
```

Replace the example container name with your API container. The replacement is printed to the operator's terminal, written to the protected `setup-code` file, and invalidates existing setup sessions. This is an explicit reset of the setup code; use `--show-setup-code` for normal retrieval. Rotation refuses to run after setup is ready, when recovery is required, or when no valid bootstrap descriptor exists.

### Setup code and container troubleshooting

From the Docker host, inspect the existing state without displaying secrets:

```sh
docker exec rateldesk-api-1 dotnet /app/Helpdesk.API.dll --setup-status
```

This reports the API version/build, effective bootstrap directory, recorded setup state, and whether a valid code is available. It does not start another API host, initialize state, contact the database, or check HTTP readiness.

| Result | Next action |
| --- | --- |
| `Unconfigured` or `Configuring`, code available | Run `--show-setup-code` and paste the result into the wizard. |
| Current code missing or stale, valid unfinished state | Run `--rotate-setup-code` and use the replacement; earlier unlocked setup sessions become invalid. |
| `Ready` | Setup is complete. Sign in with the administrator created during setup. If Web still shows setup, verify its API destination and matching release versions. |
| `Missing` or `Invalid` | Check the configured bootstrap directory, volume mount, API startup logs, and image version. The diagnostic command deliberately leaves state untouched. |
| `RecoveryRequired` | Restore the matching database, bootstrap descriptor, and key ring; see [initialization recovery](#initialization-and-storage-recovery). |
| Startup disabled | Remove the test-only `Helpdesk__SkipDatabaseStartup=true` setting and restart the API. |

The default generated file is `/var/lib/rateldesk/bootstrap/setup-code`. A custom state directory changes that location; a deployment-supplied `Bootstrap__SetupCode` means a generated file is unnecessary. Data Protection XML files in the key-ring directory are unrelated to the setup code. Use the retrieval command rather than inspecting those files.

If a command is unavailable or the Web cannot read setup status, check the running image labels. Substitute both actual container names:

```sh
docker inspect --format '{{.Name}} image={{.Config.Image}} version={{index .Config.Labels "org.opencontainers.image.version"}} revision={{index .Config.Labels "org.opencontainers.image.revision"}}' rateldesk-api-1 rateldesk-web-1
```

Deploy matching API and Web release tags. An image reference without a tag uses `latest`; release candidates do not advance that tag. Explicitly select the intended RC, pull both images, and recreate both application containers while retaining their existing volumes. The rc.5 wizard shows connection guidance and a retry action if setup status is unavailable or unsupported.

For rc.4, which predates the two read commands, `dotnet /app/Helpdesk.API.dll --rotate-setup-code` is available inside the API container. It creates a replacement code for unfinished setup using the effective configuration and invalidates earlier setup sessions. Confirm the API is rc.4 or later before using this command; older versions may treat an unrecognized argument as a normal application launch.

### Operator-invoked unattended setup

An operator can initialize a new instance without rendering the wizard by
supplying the same first-run values through protected deployment configuration,
then invoking the explicit command once. It is never performed automatically
at API startup. For SQLite, set `Bootstrap__Unattended__Provider=Sqlite`,
`Bootstrap__Unattended__Email`, `Bootstrap__Unattended__DisplayName`,
`Bootstrap__Unattended__Password`, and
`Bootstrap__Unattended__OrganizationName`, and
`Bootstrap__Unattended__ApplicationUrl`. The canonical application URL must be
HTTPS (localhost HTTP is accepted) with no user information, query or fragment.
`Bootstrap__Unattended__ApplicationName` is optional and uses the same branding
behavior as the wizard. `Bootstrap__Unattended__TimeZoneId` accepts an installed IANA time-zone
ID and defaults to `UTC`. The password belongs in an operator-controlled secret
input, not a committed Compose file.

Run the command in the API container:

```bash
docker compose -f docker/docker-compose.yml exec api \
  dotnet Helpdesk.API.dll --initialize-unattended
```

For PostgreSQL, set `Bootstrap__Unattended__Provider=PostgreSql` and
`Bootstrap__Unattended__PostgreSqlConnectionString` instead. The same bounded
preflight accepts only an empty target with the required database connectivity and permissions.
Completion writes the normal durable marker and descriptor. A restricted API
host observes that descriptor, exits, and Compose restarts it into the normal
runtime; it does not retain the unattended password.

For the documented localhost HTTP profile, both services set `Authentication__AllowInsecureLocalhost=true`; this intentionally uses the `RatelDesk.Local` cookie rather than a `__Host-` cookie. Public deployments must use HTTPS, set a durable shared `DataProtection__KeyRingPath`, and remove that localhost-only setting.

Back up the SQLite data directory, including both `rateldesk.db` and the durable `rateldesk.hangfire.db` scheduler database, together with the bootstrap state directory, shared key ring, and attachment directory as one recovery set. Restoring a SQLite file without its initialization descriptor or data-protection key material can require operator recovery.

SQLite uses its own EF Core migration assembly; setup and every normal API startup apply that migration history rather than using `EnsureCreated`. To upgrade, take a verified backup, deploy one versioned image release, and allow the API to start. If migration fails, stop the new image and restore the complete backup set before retrying. Changing a running instance from SQLite to PostgreSQL (or the reverse) is not an in-place upgrade: provision the target database and migrate data through an explicit export/import plan.

## PostgreSQL

An existing PostgreSQL deployment continues to use `ConnectionStrings__HelpdeskDb`; select `Database__Provider=PostgreSql` explicitly for new deployment-managed PostgreSQL configuration. Preserve the database and shared data-protection keys before an upgrade. PostgreSQL uses the existing provider-specific migration chain and does not require non-standard extensions; do not point RatelDesk at an unrelated or non-empty database.

### Bundled PostgreSQL

For a fresh local or single-host deployment, combine the default stack with [docker/docker-compose.postgres.yml](../docker/docker-compose.postgres.yml). It runs the standard `postgres:16` image and keeps database data in a separate named volume:

```bash
RATELDESK_POSTGRES_PASSWORD='replace-with-a-secret' \
  docker compose -f docker/docker-compose.yml -f docker/docker-compose.postgres.yml up --build
```

Complete `/setup` with host `postgres`, port `5432`, database/user `rateldesk`, and the supplied password. The bundled database uses a local password for this recipe; use an externally managed PostgreSQL service with backups, restricted credentials, TLS, and managed extension lifecycle for public or multi-host deployments. The sidecar is not an automatic SQLite-to-PostgreSQL migration path.

### External PostgreSQL

For an interactive first run, start the default source or release Compose stack and select PostgreSQL at `/setup`. The API remains in the restricted setup host until it has preflighted the external connection and committed the one-time initialization. The external database must therefore be reachable from the API container, but its credential is supplied only in the setup request; it is never placed in the normal runtime environment.

For deployment-managed unattended initialization, use [docker/docker-compose.external-postgres.yml](../docker/docker-compose.external-postgres.yml) with either base Compose file. It maps operator-supplied values only to the `Bootstrap__Unattended__*` settings; it does not need to set `ConnectionStrings__HelpdeskDb`. That ordinary connection setting is also supported for fresh databases: setup keeps deployment-owned storage read-only and still requires the first administrator. Established installations follow the conservative adoption path.

```bash
export RATELDESK_POSTGRES_CONNECTION_STRING='Host=db.example.test;Port=5432;Database=rateldesk;Username=rateldesk;Password=replace-with-a-secret;Ssl Mode=Require'
export RATELDESK_BOOTSTRAP_ADMIN_EMAIL='admin@example.test'
export RATELDESK_BOOTSTRAP_ADMIN_DISPLAY_NAME='Initial Administrator'
export RATELDESK_BOOTSTRAP_ADMIN_PASSWORD='replace-with-a-long-passphrase'
export RATELDESK_BOOTSTRAP_ORGANIZATION_NAME='Example Organization'
export RATELDESK_BOOTSTRAP_APPLICATION_URL='https://desk.example.test'
docker compose -f docker/docker-compose.release.yml -f docker/docker-compose.external-postgres.yml up -d

docker compose -f docker/docker-compose.release.yml -f docker/docker-compose.external-postgres.yml exec api \
  dotnet Helpdesk.API.dll --initialize-unattended
```

After successful initialization, remove the bootstrap credential variables from the operator environment or secret injection and redeploy with only the base Compose file. Do not remove volumes: the durable bootstrap descriptor, key ring, attachments, and database state remain required.

```bash
docker compose -f docker/docker-compose.release.yml -f docker/docker-compose.external-postgres.yml down
unset RATELDESK_POSTGRES_CONNECTION_STRING RATELDESK_BOOTSTRAP_ADMIN_EMAIL RATELDESK_BOOTSTRAP_ADMIN_DISPLAY_NAME RATELDESK_BOOTSTRAP_ADMIN_PASSWORD RATELDESK_BOOTSTRAP_ORGANIZATION_NAME
docker compose -f docker/docker-compose.release.yml up -d
```

For a new instance, the target must be empty. RatelDesk tests the connection with bounded timeouts before it creates schema. It identifies a historical RatelDesk schema separately from an unrelated non-empty database, but refuses setup for both so an existing installation cannot be modified by a first-run session. The setup principal must be able to apply the existing migrations but does not need superuser access. Use TLS, least-privilege credentials, and provider-managed backups. Deployment-managed `ConnectionStrings__HelpdeskDb` supports both fresh setup and established installations. Readiness is determined from installation evidence, not from the presence of a connection string.

Back up an external deployment as a complete recovery set: a consistent PostgreSQL backup and roles according to the database provider's procedure, the bootstrap state directory, the shared data-protection key ring, and attachments. Test restoring that set into an isolated target before relying on it. To upgrade, take and verify this backup, deploy one versioned image release, and let the API apply its existing migration chain. If the migration fails, stop the new image and restore the complete recovery set; never point the original initialized descriptor at a new empty database to recover it.

## Reverse proxy and Traefik

Any standards-compliant reverse proxy can front RatelDesk. A generic Traefik deployment should route the public host to the web service and preserve HTTPS, forwarded headers, and WebSocket/SSE support. Keep deployment-specific routers, DNS names, and certificates outside this repository.

## Identity

Local accounts are the first-run default and public self-registration is disabled. OIDC is supported with Authentik and Microsoft Entra ID through `Authentication__Mode=Oidc` or `Hybrid`. Configure each provider with an issuer under a domain you control, a client ID, a client secret supplied outside source control, and public callback URLs. Use `id.example.com` and `helpdesk.example.com` only as documentation examples.

### Local administrator recovery

When SMTP is unavailable, an operator with container access can issue a one-time local-account recovery token without resetting the instance or opening setup:

```bash
docker compose -f docker/docker-compose.yml exec api \
  dotnet Helpdesk.API.dll --recover-local-admin admin@example.com
```

The command only succeeds for an existing instance-administrator account in the selected identity store. It enables that account, invalidates old sessions, and writes a one-time token only to the command output. Give the administrator the `/activate` page and token through an approved secure channel; the token is consumed when a new passphrase is set.

### Local account security

Signed-in local users can change their passphrase at `/account/change-password` and set up a time-based authenticator at `/account/authenticator`. The authenticator page verifies the current passphrase before giving the user a shared key to enter or scan in a TOTP application, requires its current six-digit code to confirm setup, and displays ten recovery codes exactly once. Store recovery codes separately from the authenticator device. After TOTP is enabled, local sign-in requires either an authenticator code or an unused recovery code. An administrator can issue a fresh one-time activation token for a local account through the user administration UI; this is the no-SMTP password-reset path.

The browser login form includes an antiforgery token. Direct cookie-based API clients must send `X-Requested-With: XMLHttpRequest` for local login and authenticated mutations; same-origin browsers also supply Fetch Metadata. Cross-site cookie requests are rejected. Bearer-token integrations do not need this header. Create accounts from **Team → Create**. Reset and disable actions are available from **Team → Manage local account**; local accounts are disabled rather than deleting the application profile while leaving credentials active.

## Email

Configure mailbox receiving and sending separately in **Administration → Email Settings**. A fresh instance leaves its mailbox worker paused until an administrator deliberately starts it; `EmailIngestion:Enabled=false` is an operator hard stop that the UI cannot override. IMAP and POP3 need an explicit SMTP submission host, port, verified TLS mode and separate credential to send ticket mail. Graph sending uses the selected mailbox's Microsoft application identity and requires `Mail.Send` authorization separately from mailbox read access. A dedicated assignment determines both the tenant's incoming route and outgoing sender; a broken dedicated sender never falls back to the global mailbox. Use a mailbox dedicated to RatelDesk and configure sender-domain controls deliberately. See [Mailbox administration](mailbox-ingestion.md) for activation, routing and recovery behavior.

## AI, MCP, and orchestration

AI providers are configurable and should use provider-specific credentials from a secret store. MCP hosts use isolated `RATELDESK_MCP_CONFIG` configuration files and `RATELDESK_MCP_<INSTANCE>_API_BASE_URL` endpoint pinning. Netclaw uses the canonical `Netclaw__...` namespace, with `AiAssistantChat__...` retained as a migration alias. NetRatel uses `Orchestrator__...`; its setup, M2M contract, rotation, and callback boundary are documented in the [NetRatel orchestrator guide](integrations/netratel-orchestrator.md). Configure provider endpoints and credentials only when required by your deployment.

Native AI Assistant chat is an optional PostgreSQL capability in rc.4 and is disabled by default. Its durable transport ownership uses PostgreSQL advisory locks. SQLite supports webhook AI assistance and its investigation history; the ticket UI falls back to that workflow when native chat is unavailable. To enable native chat, use PostgreSQL and configure `Netclaw__Enabled=true` together with its endpoint and credential. Enabling it with SQLite is stored safely but remains runtime-disabled with an actionable capability message.

### AI Assistant chat recovery

An AI Assistant turn is `Processing` only while RatelDesk has recent, durable provider activity. Set `Netclaw__TurnInactivityTimeout` to the maximum acceptable silent interval (default `00:05:00`) and `Netclaw__ActivityHeartbeatInterval` to the bounded activity checkpoint period (default `00:00:15`). Both must be positive and the heartbeat must be shorter than the timeout. The former `AiAssistantChat__...` names remain readable during migration.

When no activity is checkpointed before the timeout, RatelDesk changes the turn to `DeliveryUnknown`. This is intentionally not a retry state: the provider could have admitted the message or already executed a tool, so RatelDesk never resends it automatically. Operators can choose **Stop waiting** while a turn is processing and confirm the same safe local transition. They can then check/reconcile the saved remote session, or acknowledge the uncertainty and archive the transcript to start a blank conversation.

An API restart is different from a silent live connection: RatelDesk immediately marks any persisted `Processing` turn as `DeliveryUnknown`, because the prior in-memory transport owner no longer exists. It records that restart boundary and never resends the turn.

## Observability

OpenTelemetry is enabled through standard `OTEL_*` settings. Export to an OTLP endpoint you operate and avoid placing user, ticket, or credential data in telemetry attributes.

## Troubleshooting

- Verify the selected database; PostgreSQL additionally needs its required extensions.
- Confirm the API is reachable from the web service's configured reverse-proxy destination.
- Check OIDC issuer, callback, audience, and client-secret configuration when login fails.
- Confirm `DataProtection__KeyRingPath` is writable and durable in production.
- Use health endpoints and structured logs before changing application state.

### Initialization and storage recovery

On startup, RatelDesk verifies the database instance and operation marker before applying migrations. A missing or mismatched initialized database enters `RecoveryRequired`; it does not recreate SQLite or reopen first-admin setup. Restore the matching database, bootstrap descriptor and keys, then restart. Startup also reconciles an initialization that committed its database marker before the descriptor was marked Ready. An established pre-rc.4 PostgreSQL installation is adopted only after finding historical migration and tenant/user evidence.

Private attachments live under `StorageOptions__RootPath/attachments` (the Compose `/app/storage` volume). At startup existing files under `wwwroot/attachments` are migrated into that directory. A different file with the same name is reported as a conflict and both copies are retained. Legacy `/attachments/...` URLs no longer serve private files anonymously; use the authorized attachment API. Keep the storage volume when recreating API containers.

Outside Compose, the default storage path is `storage` under the API content root. Relative overrides resolve against that same content root for attachments and all image stores. If the application directory is read-only, set `StorageOptions__RootPath` to an absolute, durable directory writable by the API service account, such as `/var/lib/rateldesk/storage`. Keep it outside `wwwroot`; startup fails if private attachment storage is unsafe or unavailable.

For the bundled PostgreSQL sidecar, use host `postgres`, port `5432`, and the configured database credentials in setup. Leave **Require TLS** unchecked for that private Compose network; the bundled server does not configure TLS. Use **Require TLS** for an external server with TLS configured.

Explicit `Authentication__Mode` values remain authoritative after setup. Without an explicit mode, fresh instances use Local and adopted legacy installations use OIDC. `Branding__ApplicationName` and `Branding__ApplicationUrl` prefill deployment-managed setup fields; optional visual branding does not discard the instance name.

Inbound provider configuration, tenant overrides and upgrade procedures are documented in [Inbound mailbox administration](mailbox-ingestion.md).
