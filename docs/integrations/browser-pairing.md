# Browser-approved integration credential pairing

Client setup accepts the intended instance's HTTPS URL, validates its RatelDesk
capability response, and displays a trusted approval URL plus a short human
code. Open the browser explicitly, sign in through the normal account flow,
enter the code and review the requested connection. Approval is a deliberate
action; opening the link or signing in alone grants nothing. No account password
is entered in the terminal.

The browser approval page is `/account/integration-credentials/approve`.
It shows the name, API/MCP purpose and resource, then requires selection of one
currently enabled organization, currently assignable permissions and lifetime
(30-day default, 1–90 days). It does not preselect broad grants. The terminal
shows the actual approved grants, stages a private candidate, acknowledges that
durable candidate, then atomically activates the working profile and performs
bounded read-only checks. The created ordinary `rdk_`
credential appears at `/account/integration-credentials` and can be revoked
there normally.

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

SSH/container users can open the same trusted URL on another device without a
local callback. Browser launch failure leaves the URL/code usable. Denial,
expiry, bad URL and cancellation preserve previous profiles. Older deployments
can use manual existing-credential/import or explicit external authentication;
see [CLI/stdio profiles](cli-stdio.md) and [HTTP MCP setup](../mcp-http/deployment.md).
