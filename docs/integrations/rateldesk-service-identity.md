# RatelDesk service credentials and deployment configuration

The RatelDesk service issuer is independent of local/OIDC login, `SystemToken`, human `rdk_` account credentials, legacy `OrchestrationM2M` callbacks, and RatelDesk's outbound Orchestrator credential. Its machine subject is `service:<stable-principal-id>`; approving administrators are recorded separately and confer no human roles or account identity.

An administrator can create a manual inbound NetRatel client from the **NetRatel M2M** create option at `/account/integration-credentials`. The client secret appears in the creation response once. Later list/read responses contain identity, scope, mapping, revision, lifetime, source and status metadata only. Manual rotation changes the credential revision while retaining the logical client ID, machine principal and receiver source namespace. Its predecessor has a fixed overlap deadline. Guided link credentials use the coordinated link rotation workflow instead of manual secret replacement.

The HTTP management API is `/api/v1/admin/service-clients`: `POST /` creates a client, `GET /` lists redacted metadata, `POST /{id}/rotate` accepts `expectedCredentialRevision`, and `POST /{id}/revoke` stops local business access immediately. These actions require a current administrator session. Account API/MCP credentials and service tokens cannot create administrative clients. List metadata distinguishes `source=database` from `source=deployment`, includes `readOnly`, and identifies guided clients through `linkId`/`directionId`.

## Issuer deployment

The new `ServiceIdentity` section is disabled by default. Configure the canonical installation identity and addresses explicitly; they never come from the incoming Host header. Web/API origins may differ. The issuer string remains exact and is not inferred from the Web URL.

```dotenv
ServiceIdentity__Enabled=true
ServiceIdentity__Issuer=https://rateldesk-api.example/services
ServiceIdentity__Audience=rateldesk.services
ServiceIdentity__ApiBaseUrl=https://rateldesk-api.example
ServiceIdentity__WebBaseUrl=https://rateldesk.example
ServiceIdentity__InstanceId=<stable-installation-id>
ServiceIdentity__AllowPrivateHttp=false
ServiceIdentity__AccessTokenLifetimeSeconds=300
ServiceIdentity__ClockSkewSeconds=15
ServiceIdentity__CredentialMaximumAgeDays=90
ServiceIdentity__CredentialOverlapSeconds=600
ServiceIdentity__TerminalControlRecoverySeconds=3600
DataProtection__KeyRingPath=/var/lib/rateldesk/keys
DataProtection__ApplicationName=Helpdesk-Keyring
```

Access-token lifetime accepts 60–900 seconds; skew accepts 0–60 seconds; credential maximum age accepts 1–365 days; manual overlap and terminal control recovery accept 60–86400 seconds. Persist the database and established Data Protection key ring across container replacements. Replicas must share the same ring/application name and database.

`POST {ApiBaseUrl}/connect/token` implements form-encoded `grant_type=client_credentials` with `client_id`, `client_secret` and an optional space-separated `scope`. The baseline authentication method is exactly `client_secret_post`. Basic, authorization-code grants, refresh tokens and `private_key_jwt` are not advertised. A request containing any unsupported/escalating scope fails as a whole. Responses use `Cache-Control: no-store`; issuance is rate limited and bounded to 8192 request bytes.

OAuth metadata is `{ApiBaseUrl}/.well-known/oauth-authorization-server`; public RSA JWKS is `{ApiBaseUrl}/.well-known/jwks.json`. Tokens use `RS256`, `typ=at+jwt`, a durable `kid`, exact issuer/audience, service purpose, client/principal identity, tenant/peer bindings and credential/grant/link revisions. No human or HMAC key enters JWKS. Private signing keys are protected in the database using the existing durable Data Protection ring, purpose bound to issuer and key ID. Missing/corrupt/unrecoverable signing material returns 503 at issuance; it never generates a replacement identity for an existing protected key. An issuer change requires an explicit migration of the signing identity.

An administrator can deliberately rotate signing keys through `POST /api/v1/admin/service-clients/signing-keys/rotate`. The prior public key remains published/usable for the configured maximum token lifetime plus skew plus 60 seconds. JWKS refresh is bounded by a 60-second cache lifetime. Validation and service grants read current durable authority on every authenticated request, including requests to another replica. Revocation therefore stops cached business JWTs before they expire.

## Deployment-managed inbound clients

`ServiceIdentity:Clients` provisions complete inbound registrations. IDs are unique ignoring case, and a deployment ID cannot shadow a database-managed ID. Collisions fail; neither source contributes missing pieces to the other. Deployment clients are read-only in the UI. Changing a complete deployment registration updates its effective durable authority and invalidates old credential/grant revisions. Removing or disabling it stops business authorization. Use a distinct logical client ID for a separate deployment profile; the UI does not edit container environment files or silently transfer ownership.

```dotenv
ServiceIdentity__Clients__0__ClientId=netratel-inbound-production
ServiceIdentity__Clients__0__ClientSecret=<independent-random-secret-at-least-32-characters>
ServiceIdentity__Clients__0__Name=NetRatel incident delivery
ServiceIdentity__Clients__0__OrganizationId=<approved-local-organization-id>
ServiceIdentity__Clients__0__PeerInstanceId=<approved-netratel-instance-id>
ServiceIdentity__Clients__0__PeerTenantId=<approved-netratel-tenant-id>
ServiceIdentity__Clients__0__CustomerIds__0=<approved-local-customer-id>
ServiceIdentity__Clients__0__SourceInstanceId=<exact-persisted-netratel-flow-source-guid>
ServiceIdentity__Clients__0__Scopes__0=rateldesk.incidents.create
ServiceIdentity__Clients__0__Scopes__1=rateldesk.incident-receipts.read
ServiceIdentity__Clients__0__Scopes__2=rateldesk.incident-targets.read
ServiceIdentity__Clients__0__Enabled=true
```

The #116 source registry supports one explicit organization/customer tuple per producer source in v1. Its receiver-generated namespace is reused across secret rotation and replay reconciliation. Operators may supply `SourceNamespaceId` only to identify an existing matching authorized namespace; a caller does not choose a new authoritative namespace. Inbound secrets are stored as salted constant-time verifiers, never recoverable inbound plaintext. Disabled organizations/customers, lost source membership and revoked grants stop business issuance or access according to the relevant current registry/resource checks.

The other narrow RatelDesk scope is `rateldesk.orchestration.callback`. A manual callback client must additionally provide `ResourceConstraintsJson` with explicit `organization_id`, `request_ids` and `task_ids` for recorded executions. For example, an environment value can contain the JSON object below. Local manual fields are normalized into the full frozen constraint shape; unknown/duplicate fields, null lists and duplicate/oversized identity sets are rejected.

```json
{
  "organization_id": "<approved-local-organization-id>",
  "customer_ids": [],
  "request_ids": ["<recorded-local-request-id>"],
  "task_ids": ["<recorded-local-task-id>"],
  "tenant_id": null,
  "resource_ids": [],
  "request_definition_ids": []
}
```

The callback must also match the current recorded external request and execution IDs. Guided callbacks use the approved link/tenant and the real task's stamped link/peer/revision tuple. Callback permission is independent of incident operations and cannot update unrelated tasks.

## Reciprocal-link settings and existing outbound precedence

The distinct `ServiceLinks` section controls the `bostec.service-link.v1` ceremony and durable recovery. These addresses must agree with the ServiceIdentity Web/API addresses; gateway is optional and is never a token destination.

```dotenv
ServiceLinks__Enabled=true
ServiceLinks__WebBaseUrl=https://rateldesk.example
ServiceLinks__ApiBaseUrl=https://rateldesk-api.example
# Omit GatewayBaseUrl when no gateway origin applies.
ServiceLinks__AllowPrivateHttp=false
ServiceLinks__BootstrapLifetimeSeconds=900
ServiceLinks__TerminalControlRecoverySeconds=3600
ServiceLinks__WorkerIntervalSeconds=15
ServiceLinks__MaximumPayloadBytes=131072
ServiceLinks__AutomaticRotationEnabled=true
ServiceLinks__RotationAgeDays=60
ServiceLinks__RotationOfferLifetimeSeconds=900
ServiceLinks__RotationOverlapSeconds=600
ServiceLinks__RotationPolicyRevision=1
```

Bootstrap/rotation-offer lifetime accepts 120–3600 seconds; worker interval accepts 1–60 seconds; payload limit accepts 4096–1048576 bytes; automatic rotation age accepts 1–365 days; coordinated overlap accepts 60–3600 seconds. The default policy requests rotation before a normally configured 90-day hard expiry, with a fixed bounded offer/overlap. Retries cannot extend predecessor hard expiry. Terminal control recovery is finite and cannot restore business access. Private HTTP remains an explicit deployment opt-in subject to the existing safe destination policy.

Pending/prepared/in-doubt clients can obtain only `bostec.service-link.verify` and `bostec.service-link.control`. Business issuance and resource validation additionally check the matching durable participant, exact approved grant/revision, recorded commit decision and local inbound activation. Public discovery, browser state, a token grant, or a public health response does not establish a connected pair or incident-delivery capability. See the [shared link contract](../contracts/bostec-service-link.v1.md) and [receiver contract](../contracts/rateldesk-incident-create.v1.md) for their separate readiness requirements.

Existing outbound settings remain on `/admin/automation/integration/orchestrator` behind the existing protected provider-settings service. `Orchestrator__...` is canonical; `Orchestration__Provider__...` remains the legacy namespace. Canonical aliases win at the same configuration-provider priority; a higher-priority provider retains normal .NET precedence. Explicit empty/false operator values remain meaningful and deployment-owned settings are read-only. A canonical Orchestrator profile never borrows new inbound ServiceIdentity credentials. Legacy `M2M__ClientId`/`M2M__ClientSecret` fallback keeps its inspected historical meaning only when the old outbound provider has no provider-specific credential fields. Existing inbound `OrchestrationM2M` callbacks remain separately configured and functional. RatelDesk does not reinterpret NetRatel's `M2MClients` namespace as its new issuer registry.

Managed outbound credentials remain protected in `M2MConnectivitySettings`; they are bound to the destination, issuer/token endpoint, audience/scopes/client ID and the approved local/peer tenant, instance, source, link/grant and credential revisions. Profile updates use optimistic revisions and apply at runtime. Token/cache identity includes that complete effective snapshot. A deployment-owned outbound profile prevents guided automatic replacement and remains a concrete manual configuration responsibility.
