# NetRatel orchestrator integration

RatelDesk's NetRatel integration is an outbound, administrator-managed
provider connection. It is separate from account-issued `rdk_` credentials,
reverse HTTP MCP delegation, and inbound callback authentication.

## Contract and trust boundary

The beta.4 adapter targets the unchanged NetRatel upstream contract at pinned
source revision `cc58661bff496825d68de4db9ec53da92de45942`. The adapter uses
OAuth 2.0 client credentials with the `netratel.api` audience/scope and the
provider's M2M routes:

- `POST /connect/token` for client-credentials tokens;
- `GET /internal/health` for the basic health probe;
- `GET /api/v1/system/m2m/ping` for the authenticated identity probe;
- `GET /internal/catalog/jobs`, `/tenants`, and `/request-definitions` for
  catalogue metadata;
- `POST /internal/ingest` for bound task submission.

The ingest adapter sends the provider wire fields
`NetRatelRequestDefinitionId` and `NetRatelJobDefinitionId`, and accepts only
a real provider acknowledgement containing an execution identifier. A
transport timeout after the request may be an uncertain outcome; RatelDesk
does not invent a local execution id or silently retry it.

The callback URL in a submission is a RatelDesk callback boundary. It is not a
credential exchange and it does not make the provider's client credential
available to the callback caller. Keep callback validation and the legacy
`Orchestration:Provider` inbound settings separate from this outbound
provider profile.

## Configure through the Integration hub

An administrator can open **Administration → Integration hub → NetRatel
orchestrator**. The page stores the client secret protected at rest, displays
only redacted metadata, uses optimistic revision checks, and records the last
apply and test result without recording the secret. A successful test performs
health and authenticated identity probes.

Use a dedicated NetRatel M2M client for each deployment or trust boundary.
Never paste an account integration credential into this form. The credential
is not returned by the API or browser after save.

## Deployment configuration

Canonical deployment keys use the `Orchestrator__` namespace:

```text
Orchestrator__Enabled=true
Orchestrator__BaseUrl=https://netratel.example.invalid
Orchestrator__Authority=https://netratel.example.invalid
Orchestrator__TokenEndpoint=https://netratel.example.invalid/connect/token
Orchestrator__Audience=netratel.api
Orchestrator__Scope=netratel.api
Orchestrator__ClientId=<dedicated-client-id>
Orchestrator__ClientSecret=<secret-from-secret-store>
Orchestrator__HealthPath=/internal/health
Orchestrator__IngestPath=/internal/ingest
Orchestrator__CatalogPath=/internal/catalog
```

Deployment-managed settings are read-only in the UI. When no meaningful
deployment profile is supplied, the database-managed profile is used. The
legacy `Orchestration__Provider__...` profile remains readable during the
beta.4 migration. At the same configuration-provider priority, canonical
`Orchestrator__...` values win. A higher-priority legacy source can still
override a lower-priority canonical source, matching normal .NET configuration
precedence.

The old legacy outbound profile may use `M2M__ClientId` and
`M2M__ClientSecret` only when it has no provider-specific client credential.
The canonical `Orchestrator` profile never borrows the inbound M2M credential.
Do not leave both profiles enabled with different intended providers.

## Configure, test, and rotate

1. Create or select a dedicated NetRatel M2M client with only the required
   `netratel.api` access.
2. Enter the base URL, client id, and secret in the Integration hub, or inject
   the canonical deployment keys through the approved secret manager.
3. Confirm the default internal paths unless the pinned provider explicitly
   documents a compatible deployment prefix.
4. Run the protected connectivity test and confirm both health and identity
   probes succeed.
5. Bind only reviewed request definitions and job definitions to RatelDesk
   request forms. Review the returned execution id and callback state before
   treating a submission as accepted.
6. For rotation, create the replacement client first, apply it, run the test,
   then revoke the old client. A failed or timed-out submission must be
   investigated before retrying because its remote outcome may be uncertain.

Do not put client secrets, private endpoints, access tokens, or customer
payloads in appsettings files, source control, issues, pull requests, or
support logs.

## Troubleshooting

| Symptom | Check first |
| --- | --- |
| Token request fails | Authority/token endpoint, client credentials, TLS, audience/scope, and the provider's M2M policy. |
| Health succeeds but identity fails | The access token may lack the `netratel.api` M2M permission, or the base URL may point at a non-M2M edge. |
| Catalogue is empty | Confirm the exact internal catalogue routes and the provider-side definitions visible to the M2M client. |
| Submission is uncertain | Preserve the correlation/request task id, inspect NetRatel before retrying, and do not create a local success record without a real acknowledgement. |
| UI says deployment managed | Change the canonical deployment configuration and restart/reload; the database profile is intentionally read-only in this mode. |
