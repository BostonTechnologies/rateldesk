# HTTP MCP configuration and authentication

## Existing local accounts: start with the Web URL

Use the [short HTTP MCP setup guide](setup.md). On the Docker host, run
`./setup-http-mcp.sh --native-executable "$NATIVE_SETUP"` from the extracted
deployment tools directory (`NATIVE_SETUP` is the absolute verified matching
native CLI/stdio apphost). Enter `https://rateldesk.example.test` (or `/mcp`).
Choose **Verify existing deployment** to adopt the current baseline without
source changes, new services, mounts or restarts. Choose **Prepare configuration
changes** only for deliberate minimal edits, review their readable diff/service
count, then save/deploy through the existing owner. Run the printed fresh
`./setup-http-mcp.sh --native-executable "$NATIVE_SETUP" --verify` command after
adoption or separately after owner apply. Approve its separate, short-lived
status-only verifier. Optionally connect an ordinary client with
`./setup-http-mcp.sh --target http-client -- --profile incident-reader` and
approve its organization, exact permissions and expiry.

Local browser sign-in, baseline/change review, temporary verification and an
ordinary MCP-purpose client credential are separate decisions. Gateway hosting needs no outbound
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

Authenticated JSON posts are checked for duplicate tool argument envelopes before
SDK binding, including streamed bodies without `Content-Length` and the trailing
slash route. The check accepts bodies above 1 MiB: that value is only its memory
buffering threshold, after which request-scoped temporary buffering is used.
The server's finite request-body limit applies; where the hosting server supplies
no finite limit, HTTP MCP uses 30,000,000 bytes. Larger bodies return HTTP `413`
before tool execution, whether or not the client supplies `Content-Length`.

## Mode matrix

| Ingress mode | Client credential | Outbound `credentialMode` | Persistent downstream secret | Metadata |
| --- | --- | --- | --- | --- |
| `gateway` | Account-owned scoped MCP or explicitly approved owner-administration `rdk_`, exact resource | `gateway` | None | No fabricated OAuth metadata |
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
and use gateway mode. Gateway ingress and `credentialMode=gateway` must be
selected together; either mixed configuration is rejected before serving.

## Discover the active execution identity

`helpdesk_capabilities.get`, `helpdesk_schema.list`, `helpdesk://server/status`
and `helpdesk://capabilities` include `configuredIdentity`. This describes the
serving host's actual ingress and downstream credential modes, whether callers
share a downstream principal, and which identity verification operation to use.
`authorityVerified:false` means discovery has not called API to resolve that
principal's authority. A listed operation or an external mode configuration
does not establish permission to execute it.

Call `helpdesk_auth.access` to read the existing `/api/v1/auth/me` projection:
current API permissions, `isHelpdeskAdmin`, organization scope and linked
customer. In external mode these fields describe the **downstream execution
principal**; the ingress OAuth caller is separate. The original
`helpdesk_auth.status` remains unchanged: gateway/integration credentials use
`/api/v1/auth/me`, while an Authentik downstream identity uses the exact
`AuthentikAiAgent` status policy. An ordinary external administrator can therefore
use `access` even when it does not possess the separate AI scheme. A temporary
setup verifier still permits only `helpdesk_auth.status`.

| Intended operation | Supported existing HTTP identity | Required positive evidence |
| --- | --- | --- |
| Tenant operations and owned self-service | Gateway, with current account grants intersected with the approved organization/permissions | `access` shows the actual bounded principal; each operation also satisfies ownership, support membership and form eligibility |
| Instance administration | Explicit owner-administration gateway grant from a current persisted instance administrator; an existing external administrator principal remains supported | `access.grantKind` is `owner-administration` and `access.isHelpdeskAdmin` is true for an owner grant; each administrative operation has its own verified postcondition |
| AI reporting and diagnostics | Explicit current owner-administration gateway grant, or an external token passing the exact `AuthentikAiAgent` scheme | `access` verifies owner authority or exact external `status` succeeds; a real bound invocation/report or bounded diagnostic is verified separately |

The 24 assignable tenant permissions retain their organization boundaries.
Existing ordinary credentials keep their original grants when the instance is
upgraded. An owner grant is a separate explicit consent decision. It uses the
API's owner-operation authorization path for AI diagnostics/reporting; it does
not impersonate the external `AuthentikAiAgent` identity. Invocation, ticket,
organization, correlation, event replay and terminal-state checks still apply.
An advertised operation or an expected denial does not establish a positive
campaign result.

## Approve an agent to administer the instance

An enabled persisted instance administrator can grant an HTTP MCP client owner
authority through **Integration Credentials** or the existing browser-approved
client pairing page. Select the HTTP MCP purpose, the exact canonical resource
such as `https://rateldesk.example.test/mcp`, and **Owner administration across
all organizations**. Review the agent, expiry and default organization, and
explicitly select **I authorize this agent to administer the instance and all
organizations on my behalf**. Ordinary scoped access remains the default.

The public creation/consent contract uses `purpose:"mcp"`,
`grantKind:"owner-administration"` and `ownerAdministrationConsent:true`.
The API persists the distinct `mcp-owner` purpose and revalidates the owning
account's current enabled instance-administrator authority on every exchange
and every execution request. An owner grant covers enabled organizations,
including ones created later. Its default organization is a selection aid;
the grant can administer the instance with no enabled default organization.
No request may change the reviewed MCP resource or select another API target.

Connect the approved credential through the agent's protected credential source
and verify `helpdesk_auth.access`. The response must show
`grantKind:"owner-administration"`, `isHelpdeskAdmin:true` and the actual
enabled organization set. Use that caller-bound gateway connection for account,
tenant, service, form, automation, support, mail, SLA and dataset administration.
Each API still checks its record-specific rules: support assignment needs an
eligible member; a designated approval can be decided only by that designated
user/customer; a report needs a real bound AI invocation.

Expiry, explicit revocation, disabling/deleting the owner account or removing
its instance-administrator authority invalidates subsequent requests. The agent
can revoke its own source credential with confirmed `helpdesk_auth.revoke_self`;
the next call must be unauthorized. New MCP/API credential consent remains an
interactive account-owner action. An existing agent cannot use its delegated
execution token to issue or broaden another integration credential. An owner
agent can list its owner's credential metadata with
`helpdesk_tenant_admin.my_credentials`, revoke an owned credential with
`revoke_my_credential`, or revoke and remove an owned record with
`delete_my_credential_record`. Another account's records remain inaccessible.
These management operations do not return credential secrets and retain
no-store response handling.

See the [typed operator contracts](../contracts/mcp-operation-contracts.md#typed-operator-adapters)
for request bodies, configuration secret writes, one-time activation/pairing
results and the readbacks required by the beta.8 campaign.

## Provision an explicit external authority

The instance owner and identity administrator must select the downstream
principal, its actual authority, the ingress group allowed to use it, the
approved task window and the expiry/revocation procedure. External mode admits
callers to one **shared downstream authority**. It does not delegate each
caller's tenant roles. Keep an external administrator host and an AI host
separate from the ordinary gateway and from each other when their admitted
populations or execution authority differ. They use the existing HTTP image
with independently reviewed configurations and canonical HTTPS `/mcp` resources.

1. Provision a real provider account and OAuth application for the intended API
   principal. Use Authentik's supported [service account](https://docs.goauthentik.io/users-sources/user/account-types/service-accounts)
   and [machine-to-machine token flow](https://docs.goauthentik.io/add-secure-apps/providers/oauth2/machine_to_machine).
   The existing downstream client requests `client_credentials` with the explicit
   application ID, account username and app password. Store the app password in
   the private `AuthentikAppPasswordFile` reference; never use an ordinary human
   account password or put a privileged secret into `credentialMode=gateway`.
2. For normal external administration, the API must support `Oidc` or `Hybrid`
   user authentication and validate the provider's exact issuer/audience/signing
   key. The principal must already have actual administrator authority through
   its persisted enabled application account or an explicitly authorized
   provider group and `Authentication:Authentik:RoleMappings`. Review the exact
   group membership and mapping. The MCP host does not create that role.
3. For the separate AI principal, match the API's
   `Authentication:AuthentikAiAgent:Authority`, `Audience` and `RequiredGroups`.
   Its API access token is separate from the ingress token and must pass the
   exact scheme. Configure distinct, unambiguous user/AI issuer settings for
   the intended API policy. Do not add `HelpdeskAdmin` to AI `SessionRoles` to
   simulate an administrator or to resolve a failed ordinary tenant grant.
4. Configure MCP ingress independently using
   `Authentication:AuthentikMcp:Authority`, the host's canonical `/mcp` audience,
   and non-empty required scopes/groups. Restrict that ingress population to
   callers authorized to exercise the entire downstream principal. Configure
   the private API target and the separately reviewed downstream token source.
   The MCP OAuth token is not forwarded to the API; audience blending and
   fallback do not replace separate tokens. This follows the
   [MCP authorization requirement for separate upstream tokens](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization).
5. Issue app passwords and ingress grants with explicit campaign expiry, and
   configure short API token lifetimes at the provider. Record issuer,
   audience, subject/account reference, groups, resource, approved authority
   and expiry in private evidence without recording secret values. Verify
   `configuredIdentity`, `access`, and the exact AI `status` where applicable;
   run each required operation and independently read its postcondition.
6. Revoke the provider app password and ingress grants at the agreed end,
   then prove rejected fresh requests. External JWT validation checks signed
   claims and lifetime; this host does not introspect every token. Revoking a
   password prevents new minting but does not itself invalidate every existing
   JWT. The API's existing lifetime clock skew is two minutes. Capture the
   provider's actual access-token expiry and any revocation latency. For linked
   application accounts the API also resolves current enabled account state
   and persisted roles; unlinked provider-group authority lasts for the token's
   validated claims/lifetime. Gateway expiry/revocation remains the distinct
   per-request database-backed path.

For `provision_user`, use the authenticated external caller's established
issuer/subject (or provider user identifier) and email. A non-administrator also
needs an existing login link to an enabled customer and organization. This
operation provisions that caller; request fields cannot select another user or
create an identity link by matching email. An explicitly authorized unlinked
external administrator receives its access projection without a fabricated
customer. A gateway credential intentionally does not impersonate an external
OIDC subject. Local-account creation and reset use the confirmed
`helpdesk_tenant_admin.create_local_account`, `reset_local_account` and
`reset_customer_login` operations, which return only a newly issued activation
token through the authorized private workflow. For customer list/detail positives, independently establish the
linked customer and owned requests. For incident/request self-assignment,
establish the caller's eligible support membership through supported account
and support administration, then verify each actual assignment separately.

Exact AI authentication is not a claim that every campaign scenario is covered.
The current diagnostic buffer is instance-level: the configured exact AI
principal and admitted ingress callers must be authorized for that scope.
Query filters bound the selected records, not the principal's authority.
AI reporting binds each event to its stored
invocation, organization, ticket and correlation. Conflicting event-ID reuse is
rejected; matching historical retries retain the original event. Completed,
Failed and Cancelled invocations reject new callbacks, including late progress.
Token issuer, audience, groups and expiry remain validated by the exact AI
scheme. The current invocation model has no separate report-expiry field;
webhook `TimeoutSeconds` bounds the outbound HTTP call only and does not define
an invocation-age deadline. Callback collision and terminal-ordering repairs are
tracked in [issue #234](https://github.com/BostonTechnologies/RatelDesk/issues/234).
Keep any unmet row open until its deployed positive and isolation checks pass.
A configured external host or a synthetic provider contract test alone is no
acceptance result.

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
The reviewed source candidate retains the intended list. Gateway-origin settings and
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
