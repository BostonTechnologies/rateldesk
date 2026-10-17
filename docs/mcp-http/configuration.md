# HTTP MCP configuration and authentication

## Existing local accounts: start with the Web URL

Use the [short HTTP MCP setup guide](setup.md). On the Docker host, run
`./setup-http-mcp.sh` from the extracted deployment tools directory, enter
`https://rateldesk.example.test` (or its `/mcp` URL), then review and explicitly
approve the host proposal. Apply through the existing deployment owner and run
a fresh `./setup-http-mcp.sh --verify`. Approve its separate, short-lived
status-only verifier. Optionally connect an ordinary client with
`./setup-http-mcp.sh --target http-client -- --profile incident-reader` and
approve its organization, exact permissions and expiry.

Local browser sign-in, gateway hosting and an ordinary MCP-purpose client
credential are separate decisions. Gateway hosting needs no outbound
administrator credential or shared signing key, and host approval grants no
incident access. Linked-OIDC account users can also use their own gateway
credentials. Existing authentication selections remain intact; explicit external
Authentik/OAuth setup is covered below. See the [deployment reference](deployment.md)
for Compose prerequisites, deliberately selected Docker configuration and recovery.

## Architecture and authentication reference

The HTTP host is stateless protocol infrastructure. API owns account grants,
credential verifiers, execution-token protection and database state. Web owns
public gateway routing. HTTP MCP stores its isolated outbound configuration,
validates ingress according to an explicit mode and calls the exact reviewed API
target. Its default transport is HTTP serving, not interactive setup.

## Mode matrix

| Ingress mode | Client credential | Outbound `credentialMode` | Persistent downstream secret | Metadata |
| --- | --- | --- | --- | --- |
| `gateway` | Account-owned MCP-purpose `rdk_`, exact resource | `gateway` | None | No fabricated OAuth metadata |
| `authentik` | JWT from explicit external authority/audience, required scopes/groups | Explicit `authentik` or API-purpose `integration` identity | Separate downstream API identity | Exact protected-resource metadata endpoint |

Gateway exchanges the ingress credential at `/api/v1/mcp/execution-token` and
uses only the returned short-lived request-scoped execution token for API
operations. Never paste the MCP bearer into an outbound profile or configure a
gateway service account/shared delegation key. API enforces the owner's current
organization, permissions, expiry and revocation on later requests.

External Authentik validates ingress JWTs but calls API as its separately
configured downstream identity. It is not automatic per-user delegated RBAC.
Authority/audience/scopes/groups do not replace downstream token credentials.
Gateway rejection never falls back to Authentik. Linked-OIDC application users
can instead obtain their own MCP-purpose credential after normal account linking
and use gateway mode.

## HTTP host settings

`Helpdesk:Mcp` and `Authentication:AuthentikMcp` are retained typed sections.
For environment binding replace each colon with `__`; array entries use numeric
indices, for example `Helpdesk__Mcp__AllowedOrigins__0`.

| Key | Default | Required condition / meaning |
| --- | --- | --- |
| `Helpdesk:Mcp:ConfigurationPath` | Empty | Optional isolated JSON path; preferred to legacy `RATELDESK_MCP_CONFIG` when set |
| `Helpdesk:Mcp:Instance` | Empty | Required label; lowercase letters/digits/hyphens, preserving existing named instances |
| `Helpdesk:Mcp:ExpectedApiBaseUrl` | Empty | Explicit target pin, or retained target environment aliases below |
| `Helpdesk:Mcp:PublicResourceUri` | Empty | Required canonical public HTTPS `/mcp` resource |
| `Helpdesk:Mcp:AllowedOrigins` | Empty array | One or more HTTP(S) browser origins without a path, query or fragment |
| `Helpdesk:Mcp:AuthenticationMode` | `authentik` | Select `gateway` deliberately for account-owned MCP credentials; only these two modes |
| `Helpdesk:Mcp:DelegationTimeoutSeconds` | `20` | Gateway exchange deadline, including response body; 1–60 seconds |
| `Authentication:AuthentikMcp:Authority` | Empty | External mode: explicit HTTPS issuer |
| `Authentication:AuthentikMcp:Audience` | Empty | External mode: same canonical public resource as PublicResourceUri |
| `Authentication:AuthentikMcp:RequiredScopes` | Empty array | External mode: non-empty unique scope values |
| `Authentication:AuthentikMcp:RequiredGroups` | Empty array | External mode: non-empty unique group values |
| `ASPNETCORE_URLS` | Framework listener default | Image entrypoint defaults to `http://+:8223`; set explicitly for custom listening |

Allow origins exactly as sent by authorized browser clients; they are origins,
not MCP paths. Non-browser clients may omit Origin. A mismatching Origin on MCP
returns 403. Configure the complete effective list when combining JSON and .NET
providers; numeric .NET array overrides can retain lower-provider indices.
The coordinated export emits the reviewed list. Gateway-origin settings and
trust/reload rules are in the [Web gateway guide](../deployment/web-gateway.md).

Public resource and approval URLs remain HTTPS. Private API/MCP backends on the
shared container network may use the existing pinned HTTP topology. An arbitrary
instance label does not mean target discovery. Outbound `apiBaseUrl` must exactly
match ExpectedApiBaseUrl after canonical URI normalization; use secured explicit
destinations for split/untrusted networks.

Target compatibility variables are `RATELDESK_MCP_INSTANCE`,
`RATELDESK_MCP_EXPECTED_API_BASE_URL` (shell-safe for every label), and
`RATELDESK_MCP_<INSTANCE>_API_BASE_URL` (retained legacy alias). Prefer typed
Instance/ExpectedApiBaseUrl in HTTP deployments. A hyphenated label's legacy
variable may not be a valid shell assignment; use the typed key or shell-safe
expected-target key rather than changing the instance identity.

## Dedicated outbound settings and aliases

The canonical section is `Helpdesk:Mcp:Outbound`. The MCP surface reads this
section and its own dedicated `RATELDESK_MCP_*` aliases, plus a deliberately
selected isolated JSON file. It does not import CLI `RATELDESK_*`, the default
CLI file or a profile from another product/deployment.

Every field below supports environment-only configuration. To bind the
canonical key, use `Helpdesk__Mcp__Outbound__` followed by the listed field;
the alias is `RATELDESK_MCP_` followed by the suffix.

| Field / JSON file key | Alias suffix | Default / condition |
| --- | --- | --- |
| `ApiBaseUrl` / `apiBaseUrl` | `API_BASE_URL` | Required explicit HTTP(S) API target |
| `CredentialMode` / `credentialMode` | `CREDENTIAL_MODE` | `authentik`; `integration` or `gateway` also supported |
| `IntegrationCredential` / `integrationCredential` | `INTEGRATION_CREDENTIAL` | Integration mode: inline alternative to file |
| `IntegrationCredentialFile` / `integrationCredentialFile` | `INTEGRATION_CREDENTIAL_FILE` | Integration mode: private secret-file reference |
| `AuthentikTokenUrl` / `authentikTokenUrl` | `AUTHENTIK_TOKEN_URL` | Authentik downstream mode: token endpoint |
| `AuthentikClientId` / `authentikClientId` | `AUTHENTIK_CLIENT_ID` | Authentik downstream mode: client identifier |
| `AuthentikUsername` / `authentikUsername` | `AUTHENTIK_USERNAME` | Authentik downstream mode: service identity |
| `AuthentikAppPassword` / `authentikAppPassword` | `AUTHENTIK_APP_PASSWORD` | Authentik downstream mode: inline alternative to file |
| `AuthentikAppPasswordFile` / `authentikAppPasswordFile` | `AUTHENTIK_APP_PASSWORD_FILE` | Authentik downstream mode: private secret-file reference |
| `AuthentikScope` / `authentikScope` | `AUTHENTIK_SCOPE` | `openid profile email` |
| `AgentUserEmail` / `agentUserEmail` | `AGENT_USER_EMAIL` | Optional downstream actor configuration |
| `CredentialTarget` / `credentialTarget` | `CREDENTIAL_TARGET` | Saved approved API target; rejects credential rebinding |

The two `_FILE` aliases above are explicitly supported; arbitrary `_FILE`
suffixes are not a generic framework feature. Choose one inline/file source at
the controlling level. Reads are bounded to 16 KiB, trim trailing newlines,
reject symbolic-link/reparse-point paths, and require files owned/readable only
by the current Unix user. For the non-root image that owner is UID 10001.
Private JSON reads are bounded to 64 KiB. Windows creation uses protected
current-user ACLs, including temporary/lock files and new directories. Existing
private secret files require the current-user owner and access restricted to
that user, SYSTEM and Administrators; broad ACLs are rejected unchanged. Atomic
replacement preserves/checks restricted access.

## Precedence, managed values and errors

Outbound effective precedence is:

1. Built-in mode/scope defaults (MCP has no default API target).
2. Explicit isolated file/profile.
3. Canonical host-file `Helpdesk:Mcp:Outbound` settings.
4. Dedicated MCP environment aliases, then canonical Outbound environment keys.
5. Explicit non-secret command options, where supported by the command.

Canonical environment keys beat aliases within the environment layer; dedicated
environment aliases override lower-priority host-file settings. Missing options
do not override a lower layer;
explicit empty environment values clear the selected setting and validation
then decides whether it is required. A higher-layer mode change replaces the
lower layer's credential identity rather than assembling mixed authentication.
A higher-layer inline/file choice replaces the lower secret alternative; two
non-empty alternatives at one level fail before network I/O. Unknown outbound
keys, unknown JSON fields, selected missing files and invalid JSON fail clearly.
No target/profile/example-URL fallback is used to recover invalid MCP settings.

Profile metadata such as approved permissions is retained from the selected
file; replacing its list replaces that metadata list. CLI/stdio profile details
and supported commands are in [CLI/stdio](../integrations/cli-stdio.md).
Effective inspection redacts secret values/references and reports setting
source. Environment/canonical host settings are externally managed: setup
exports a reviewed change but cannot alter parent-shell/running-container env
or immutable secrets. Outbound and host options require restart/recreation to
apply; do not advertise live environment reload.

## Environment-only gateway example

This contains no persistent downstream secret. A private API target is distinct
from the public resource and allowed browser origin.

```sh
Helpdesk__Mcp__Instance=local \
Helpdesk__Mcp__ExpectedApiBaseUrl=http://api:8222/ \
Helpdesk__Mcp__PublicResourceUri=https://rateldesk.example.test/mcp \
Helpdesk__Mcp__AllowedOrigins__0=https://rateldesk.example.test \
Helpdesk__Mcp__AuthenticationMode=gateway \
Helpdesk__Mcp__Outbound__ApiBaseUrl=http://api:8222/ \
Helpdesk__Mcp__Outbound__CredentialMode=gateway \
ASPNETCORE_URLS=http://+:8223 \
dotnet /app/Helpdesk.Mcp.Http.dll
```

Configure Web/API too through the [deployment guide](deployment.md); this
standalone host invocation does not automatically enable Web's route.

## Compose configuration layers

Compose `.env`/`--env-file` provides interpolation inputs for YAML `${NAME}`.
It does not inject every value into containers. A service `env_file` injects
its file into that container. The service `environment` map supplies the actual
.NET `__` variables and takes precedence over `env_file` for the same key.
Review the selected source/release overlay's interpolation names separately
from the runtime tables above. Recreate services after changes.

Preserve the effective settings of existing `RATELDESK_MCP_CONFIG` JSON inputs
when migrating. The reviewed proposal migrates the recognized legacy config-file
mount to the protected runtime volume and removes its conflicting selected
mapping from the complete deployment document; do not hand-edit the export.
A gateway file needs only explicit `apiBaseUrl` and `credentialMode=gateway`;
an external file retains its separate identity. Do not replace working API state
or reset account/credential identities. See [setup, persistence and migration](deployment.md).
