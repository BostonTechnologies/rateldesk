# Browser-approved integration credential pairing

Client setup accepts the intended instance's HTTPS URL, validates its RatelDesk
capability response, and displays a trusted approval URL plus a short human
code. Open the browser explicitly, sign in through the normal account flow,
enter the code and review the requested connection. Approval is a deliberate
action; opening the link or signing in alone grants nothing. No account password
is entered in the terminal.

For local-account HTTP MCP, start with the [URL-first hosting/client guide](../mcp-http/setup.md).
Gateway hosting uses no outbound administrator credential/shared key. Linked-OIDC
accounts can retain their own gateway credentials; external Authentik/OAuth is
an explicit separate mode.

The browser approval page is `/account/integration-credentials/approve`.
Its compact **HTTP MCP host deployment** / **CLI / MCP client access** entry
panels use **Review host** and **Review client** respectively. Entering a code,
opening a deep link or signing in never approves it. A code for the wrong kind
does not switch the requested action. Only authorized administrators can review
host proposals. A successful lookup opens a bounded review with explicit
approve/deny actions; recoverable errors retain the entered code.

The terminal's compact approval view keeps purpose, URL/code, status/expiry and
actions ahead of extended **Details**. Host **Back** returns to Hosting, client
**Back** returns to client authentication/profile, and temporary-verifier **Back**
returns to its adopted/applied-host verification checkpoint. Pending old attempts cannot
advance a changed proposal.

Ordinary client review shows the name, API/MCP purpose and resource, then requires selection of one
currently enabled organization, currently assignable permissions and lifetime
(30-day default, 1–90 days). It does not preselect broad grants. The terminal
shows the actual approved grants, stages a private candidate, acknowledges that
durable candidate, then atomically activates the working profile and performs
bounded read-only checks. The created ordinary `rdk_`
credential appears at `/account/integration-credentials` and can be revoked or
deleted there normally. **Integration → API & MCP credentials** at
`/admin/integration?tab=credentials` is the same management surface. The grouped
permission selector is shared with manual creation and access editing: search
and group select/clear operate on exact eligible permission
IDs, and filtered select-all states its displayed scope. Existing selections
remain visible when filtered. Changing organization requires grant revalidation;
no broad grant is selected automatically.

## Host proposal and temporary verification consent

Host review identifies **Adopt existing baseline — no deployment changes** or an
explicit configuration-change proposal. The compact terminal shows purpose and
defined service count before/after; Details retains the redacted provider/source
review. Approval records current administrator review of that exact generation.
It issues no consumer credential or persistent downstream gateway identity.
Adoption has no fictitious apply stage. Only deliberate changes require the
declared deployment owner's save/deploy/recreation. Changed material sources
require explicit re-read/review; unchanged fresh verification does not repeat
provisioning consent.

The separately identified **Temporary verification client** shows its exact
status-check purpose, reviewed organization/resource and five-minute maximum
lifetime. Explicit consent creates a setup-only verifier through the existing
credential authority. It cannot select ordinary business grants or another
organization. Initialization/catalog and the exact authenticated
`helpdesk_auth/status` operation are its entire purpose. It is never saved as a
personal client profile and does not relax ordinary 1–90-day credential rules.
Setup revokes it after success, failure or cancellation; bounded server expiry
handles abandonment. Unconfirmed remote cleanup stays explicitly pending with
the displayed recovery action.

## Credential management

The compact list has search, **All**, **Active**, **Revoked** and **Expired**
filters/counts and bounded details. **Details** groups the connection resource,
status and purpose, lifecycle timestamps, exact UTC expiry, configured access
and current admission. The secret remains unrecoverable.

For an active ordinary credential, choose **Configured access → Edit access**,
change its same-organization permissions, then **Review changes → Apply access
changes** after explicitly confirming the additions, removals and resulting
grants. The current owner must still have every selected permission. This keeps
the credential ID, secret, owner, organization, purpose, resource and exact
expiry unchanged; the installed client continues using its existing secret.
Expired reviews and concurrent changes require fresh review. A failed attempt
retains the draft, and successful application refreshes list/Details counts and
admission. See [the complete editing and policy
workflow](../mcp-http/setup.md#edit-an-existing-credentials-access).

The editor does not change account roles or MCP policies. Additional
organizations need separate ordinary credentials. Owner-administration and
temporary-verification grants keep their dedicated consent workflows; inactive
credentials cannot be edited. Initial client-code approval issues a new
credential and is separate from this existing-credential review/apply workflow.

**Revoke** retains an inactive record.
**Revoke & delete** confirms the named active credential, stops subsequent
authorization and durably removes the authentication record and grants.
Inactive records offer **Delete**. Removing a local profile alone does neither.

**Delete selected inactive** sends only explicitly selected inactive IDs, in
bounded batches of at most 50. Hidden/unselected and active rows are not included
implicitly. Partial failures remain visible for retry; acknowledged changes
survive delayed stale list responses. Repeated deletion is idempotent and
owner-scoped. Non-secret actor/time audit remains; a stale pairing delivery
cannot restore deleted access. Already accepted operations cannot be undone.

An unauthenticated visit returns through the existing local password/MFA or
linked-OIDC sign-in using a validated relative return URL. Browser consent posts
through the antiforgery-protected Web endpoint
`/account/integration-credentials/pairing-consent`; signing in preserves the
approval destination without putting a code or device capability in the URL.

## Purpose and identity boundaries

- CLI and stdio profiles receive API-purpose credentials for account-bound API
  operations.
- An HTTP MCP client receives an MCP-purpose credential bound to the exact
  canonical public HTTPS `/mcp` resource. It is not a direct API credential or
  the HTTP host's downstream identity.
- Gateway HTTP hosting stores no downstream credential. It exchanges the
  incoming MCP credential at `/api/v1/mcp/execution-token` for an API-issued
  request-scoped token.
- External Authentik OAuth, NetClaw device pairing and NetRatel system
  connections remain separate integrations.

This is device-style browser approval of RatelDesk integration credentials. It
does not claim OAuth device-grant access/refresh-token compliance or create a
new OIDC issuer. Authority is always the intersection of the current account,
approved organization and selected permissions. Changed roles/grants, disabled
accounts/organizations, expiry and revocation continue to apply after pairing.
Use separate profiles for different organizations.

## Deployment prerequisite and protocol

API `IntegrationCredentials:Pairing:PublicOrigin` (environment
`IntegrationCredentials__Pairing__PublicOrigin`) must be the trusted instance
HTTPS origin, without credentials/path/query/fragment. Align it with Web's
`WebGateway:PublicOrigin`. Capability URLs come from this setting, never
incoming Host/forwarded headers. If unset, capabilities report pairing unsupported
and start returns 503; manual existing-credential setup remains available.

All protocol endpoints share `/api/v1/integration-credentials/pairing`:

| Path / method | Purpose |
| --- | --- |
| `/capabilities` GET | Secret-free product/pairing capability and canonical URLs |
| `/start` POST | Bounded initiation; human code and separate private device capability |
| `/poll` POST | Pending/slow-down/denied/expired or protected credential delivery |
| `/ack` POST | Confirm durable private candidate save; idempotent acknowledgement |
| `/cancel` POST | Cancel initiation and revoke an unacknowledged new credential |
| `/review`, `/approve`, `/deny` POST | Existing authenticated credential-management policy; consent protected by antiforgery |

Only the human code is displayed. The device capability and final bearer must
stay out of URLs, terminal output, logs, shell history and browser storage.
Clients validate TLS, RatelDesk identity, origin and response shape and reject
redirects/URLs to another authority. A failed target never selects another
instance or imports a different profile's identity.

## Delivery, cancellation and recovery

Pairing expires after ten minutes. Polling respects the supplied interval and
rate limits; visible states include pending, slow_down, denied, expired, issued,
consumed and cancelled. The durable identity transaction creates one ordinary
credential despite concurrent/replayed polling or API restart. Current owner
access is rechecked before issuance.
Human-code attempts have a durable limit of 30 per ten minutes per enabled
account. Initiation has a durable limit of 120 per ten minutes for the immediate
network peer, and transport requests share a process-local 3,000-per-minute
bucket for that peer. Behind Web/YARP, many clients can share the proxy address;
these limits do not provide separate end-user IP buckets. API does not trust raw
`X-Forwarded-For` for rate identity. Per-device polling persists a five-second
interval (at most 12 normal polls per minute), keeping 120 pending devices within
the shared transport allowance. Invalid code attempts reveal no other
users/organizations; respect slow-down/429 and retry deadlines.

Delivery is protected by the existing API data-protection keys for the smaller
of two minutes or the remaining pairing lifetime. A retry by the same private
device capability receives the same credential until acknowledgement. Permanent
credential storage remains one-way-verifier-only. Acknowledgement follows atomic
private candidate save; only confirmed acknowledgement permits atomic activation
of the working profile. Its consumed tombstone is retained for 24 hours for
idempotent recovery. Preserve the existing API key ring across restarts and API instances.
MCP does not mount that ring or the API database.

Cancelling or expiring an unacknowledged issuance revokes only that new
credential. It leaves older working profiles and acknowledged credentials
alone. Setup writes private `.pairing-candidate` and `.pairing-pending` files
beside the selected profile. The pending state contains the private device
capability; protect it like a secret. Normal runtime ignores these files and
continues using the prior working profile until activation.

After a crash or lost acknowledgement, re-enter explicit setup with the same
profile/path. It recovers the staged candidate, checks delivery/consumed state,
confirms acknowledgement and activates it with the original file revision
check. Expired/cancelled delivery cleans the candidate and preserves the working
profile; an orphan candidate without recoverable state is also cleaned on setup
re-entry. If no recoverable candidate exists, start again. Acknowledged credentials
remain ordinary revocable account credentials; aborting after acknowledgement
does not revoke an unrelated consumer. Never print the bearer/device capability
or copy sidecars into a public support report.

A new process can reload the same explicitly selected saved profile without
approval while its credential remains valid. After expiry/revocation, re-enter
explicit client setup for that profile and obtain a newly reviewed grant.
Temporary host-verifier cleanup does not revoke an ordinary client. Local file
deletion and server-side revocation remain separate operations.

SSH/container users can open the same trusted URL on another device without a
local callback. Browser launch failure leaves the URL/code usable. Denial,
expiry, bad URL and cancellation preserve previous profiles. Older deployments
can use manual existing-credential/import or explicit external authentication;
see [CLI/stdio profiles](cli-stdio.md) and [HTTP MCP setup](../mcp-http/deployment.md).
