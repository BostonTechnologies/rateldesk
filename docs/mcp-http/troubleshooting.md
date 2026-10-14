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
| Permitted tool result | One approved read-only call, such as `helpdesk_auth` operation `status`, returns a successful upstream MCP result |
| Streaming/cancellation | Incremental events arrive before completion and a cancelled client request ends upstream work; ingress does not buffer the stream |

Use the client's configured MCP session to send `initialize`, then
`notifications/initialized`, then the permitted tool call. Keep Authorization,
Accept (`application/json, text/event-stream`), Content-Type, supported
`MCP-Protocol-Version` and returned `Mcp-Session-Id` where applicable. Use secret
references in the client's protected config; never paste bearer arguments into
shell history. Setup's HTTP client check performs initialize and the read-only
upstream auth-status call. Local CLI doctor does not imply MCP initialization;
stdio setup reports skipped transport checks unless its executable is selected.
External OAuth client setup verifies protected-resource metadata and saves only
public client settings. Its authentication/initialize/tool steps stay unverified
until the selected OAuth-capable MCP client signs in and connects.

## Common failures

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
