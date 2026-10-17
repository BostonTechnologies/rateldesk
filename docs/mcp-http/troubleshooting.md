# HTTP MCP troubleshooting and verification

Record each layer independently. A reachable port, 401/403, login page or final
buffered response does not establish authenticated MCP setup. Diagnostics use
read-only operations and must not create tickets, send mail or run workflows.

| Layer | Evidence / next check |
| --- | --- |
| Configuration | Explicit mode, instance, exact expected API target, canonical HTTPS resource, allowed origins and required secret sources validate |
| DNS/TLS/reachability | Intended public host resolves and its certificate validates; private service DNS resolves within the container network |
| Route ownership | `/mcp` reaches MCP; no Blazor HTML/login fallback; selected backend outage remains a proxy/backend failure |
| Metadata | Gateway publishes no fabricated OAuth metadata; external mode publishes exactly `/.well-known/oauth-protected-resource/mcp` with matching resource/issuer |
| Authentication/exchange | Intended bearer accepted; gateway API exchange succeeds for current purpose/resource/grants; external issuer/audience/scopes/groups and separate downstream identity validate |
| MCP initialize | Valid protocol initialize response from the selected MCP host, with protocol/session/content headers retained |
| Tool catalog | The complete real RatelDesk catalog, with valid schema and pagination where applicable, matches the expected tools |
| Permitted tool result | One approved read-only call, such as `helpdesk_auth` operation `status`, returns a successful upstream MCP result |
| Streaming/cancellation | Incremental events arrive before completion and a cancelled client request ends upstream work; ingress does not buffer the stream |

Use the client's configured MCP session to send `initialize`, then
`notifications/initialized`, then the permitted tool call. Keep Authorization,
Accept (`application/json, text/event-stream`), Content-Type, supported
`MCP-Protocol-Version` and returned `Mcp-Session-Id` where applicable. Use secret
references in the client's protected config; never paste bearer arguments into
shell history. Setup's HTTP client check performs initialize, catalog validation and the read-only
upstream auth-status call. Local CLI doctor does not imply MCP initialization;
stdio setup reports skipped transport checks unless its executable is selected.
External OAuth client setup verifies protected-resource metadata and saves only
public client settings. Its authentication/initialize/tool steps stay unverified
until the selected OAuth-capable MCP client signs in and connects.

Host verification additionally compares the observed serving configuration with
the approved source generation. **Saved—apply pending** requires the deployment
owner's apply/recreation; **Applied—verify pending** requires the authenticated
live check. Re-run the deployment host's `./setup-http-mcp.sh --verify` from a
new invocation after recreation. Unchanged sources reuse approval; changed
sources stop for revalidation. A temporary verifier never becomes the host
downstream identity or an ordinary consumer profile. When cleanup cannot be
confirmed, keep its bounded cleanup-pending action and retry rather than
reporting completed cleanup.

Verification cleanup revokes its temporary credential and clears protected
delivery. Credential management retains the revoked record for inspection;
the owner can delete that specific revoked entry separately. Keep ordinary
client credentials and their private profiles while valid.

## Common failures

- **Compose prerequisite fails before the URL prompt:** use the detected
  version, safe diagnostic code and missing command/flag to resolve the selected
  implementation. Follow [Compose prerequisites and Docker selection](deployment.md#compose-prerequisites-and-docker-selection),
  including integrity verification for a deliberately selected isolated plugin.
  Keep that same Docker configuration/context/host/TLS selection on later
  client, apply and fresh verification commands.
- **Legacy config mount conflicts with runtime storage:** ordinary setup
  migrates the recognized selected file in the complete reviewed proposal.
  Resolve an unreadable protected source or unknown overlapping mount before
  approval, then review/apply a new proposal. Do not repair the approved export
  manually; see [migration](deployment.md#migration-persistence-and-rotation).
- **404 JSON on `/mcp`:** MCP generation is unselected or hard-disabled. Confirm
  the overlay configures both Web Enabled/Destination and the HTTP service.
  A Compose profile alone cannot change Web env. Manual routes survive ordinary
  disabled generation; HardDisable blocks even custom broad routes.
- **502/503 when selected:** check HTTP host startup validation, DNS, expected
  target and readiness. The gateway keeps the selected route during a backend
  outage; it does not switch instances.
- **Blazor HTML on `/mcp`:** inspect explicit advanced route overrides and outer
  ingress. Reserved MCP paths should not return the application fallback.
- **401/403:** inspect the exact credential purpose/resource, expiry/revocation,
  current account/organization grants, mode and browser Origin. In external
  mode also check ingress JWT requirements and the separate downstream identity.
  Never copy another profile's credential or switch authentication modes to
  conceal the failure.
- **Missing/invalid configuration:** selected file paths fail rather than load
  an example target. MCP has no ambient CLI fallback. Canonical Outbound keys
  beat dedicated aliases; explicitly empty env clears a value. Remove conflicting
  inline/file secrets or mixed-mode fields before retrying.
- **Permission/ownership error:** the serving process is UID 10001. The file-init
  helper copies the protected input to its volume as UID 10001 with mode 0400;
  the server mounts it read-only. Secret-file references must exist inside the
  container and be owned/readable by that UID. Host paths inside a JSON profile
  are not automatically container mounts. Reject symlink paths instead of
  relying on them for configuration.
- **Setup exits without a screen:** interactive setup needs a real TTY. Use
  `docker compose run` with its terminal allocation or `exec -it`, or supply the
  documented environment/private-file configuration. Normal stdio serving uses
  pipes and must not be given a setup prompt.
- **Changed settings have no effect:** exported files do not apply running
  environments. Review and recreate the intended services. Generated gateway
  options and HTTP settings are startup-only; only supported explicit YARP
  configuration file reload is live.
- **Pairing interrupted before completion:** normal runtime keeps the previous
  profile while the candidate/device-capability state stays in private sidecars.
  Re-enter explicit setup for the same profile/path to recover acknowledgement
  and activate, or clean expired/cancelled candidates. Do not expose sidecars in
  support output. Final activation never precedes confirmed acknowledgement.
- **Approval is denied/expired or needs editing:** use the visible URL/code,
  status and expiry in the main approval view; extended preview is in **Details**.
  Host **Back** returns to Hosting, ordinary-client **Back** returns to its
  authentication/profile context, and verifier **Back** returns to the
  applied-host verification checkpoint. Re-entering approval starts the
  appropriate current attempt; an old approval cannot approve changed sources.
- **Public URL advertises private DNS/HTTP:** align trusted API pairing origin,
  Web origin/resource and MCP PublicResourceUri. Public HTTPS identity can differ
  from private `http://api:8222/`; incoming Host headers are not deployment
  authority.
- **Delayed complete response:** check buffering and idle/activity limits in
  every proxy. The generated YARP MCP cluster disables response buffering, but
  an outer ingress can still buffer or terminate a valid stream.

## Recovery and rotation

Preserve API database, bootstrap state, data-protection keys and attachments.
HTTP MCP needs no API database/key mount. A read-only MCP configuration does
not require making the API key ring available to it.

For unchanged applied host settings, invoke `./setup-http-mcp.sh --verify` from
a new Docker-host process. For changed sources/proposal, invoke ordinary setup
with the same ordered Compose/env sources, storage and owner selectors; review
and approve the new proposal, apply through that owner, then verify. Preserve
the selected Docker environment throughout. Never edit protected receipts or
reuse a stale source/proposal hash.

Reload an explicitly selected ordinary profile while its credential is valid;
no new client approval is needed. Use the [saved-profile doctor template](setup.md#check-or-reconnect-a-saved-client)
with the actual container and private in-container path. Expired/revoked access
requires explicit client reauthorization and a newly selected grant. Temporary
verifier cleanup leaves the ordinary client profile/credential intact.

For gateway credentials, create/approve a replacement scoped client credential,
save it privately, verify initialize/read-only access, then revoke the retired
credential through account management. No downstream/shared gateway secret is
rotated. External mode rotates ingress provider configuration and downstream API
identity deliberately and separately. Restart/recreate affected services for
host/env changes, and retain a verified prior configuration for recovery.

Pairing delivery/restart/cancellation recovery is documented in
[browser pairing](../integrations/browser-pairing.md). Distinguish deleting a
local profile from server credential revocation; removing one profile must not
revoke an unrelated consumer.

## Check incident search

With an ordinary credential scoped to the intended organization and
`Incident.Read`, call `helpdesk_incidents` with synthetic fixture text:

```json
{"operation":"list","request":{"query":"synthetic-beta2-match","page":1,"pageSize":20,"includeTotal":true}}
```

For incident `list`, MCP maps `request.query` to the API's `q` parameter before
the API filters, counts and pages the authorized records. Existing `request.q`
callers work too. Aliases compare after trimming; blank/null values mean no text filter,
equal nonempty values produce one `q`, and conflicting nonempty values or
non-string values return `validation_failed` before a request is sent. The
adapter applies only to incident lists. Other tools retain their contracts.

Compare a fixture's known matching IDs/counts, then repeat with a unique phrase
absent from every fixture incident. With `includeTotal=true`, a genuine no-match
response has `items: []` and `totalCount: 0`; it must not return the baseline
incident. Blank/no-query requests retain the authorized baseline. A matching
foreign-organization record stays excluded, and an ungranted `Request.Read`
operation remains denied. An HTTP success or summary alone does not prove the
filter was sent or applied. Do not create live tickets for this check.
