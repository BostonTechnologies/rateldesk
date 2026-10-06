# NetRatel reciprocal service-link acceptance

The pinned product contract is [bostec.service-link.v1](../contracts/bostec-service-link.v1.md), supplied on 2026-10-04 with SHA256 `ab3a33e61a33cf86744a9cb1b58c24513ca5bb193bbc1566a2e16362866c7f77`. Its strict public schemas, concrete permission profiles and synthetic fixtures are [JSON contract](../contracts/bostec-service-link.v1.json) and [golden fixtures](../contracts/bostec-service-link.v1.fixtures.json). Their public fixture codes and credentials are synthetic values and cannot be used against an installation.

The incident receiver remains [rateldesk.incident-create.v1](../contracts/rateldesk-incident-create.v1.json). Its semantic document and existing fixture digest are preserved from #116. Adding the service authentication path does not change incident fingerprinting, headers, receipts, retention or replay ownership. Runtime capability metadata adds `oauth_client_credentials` only when that authenticated path and current source grants actually work.

## Current implementation — 6 October 2026

RatelDesk's reciprocal linking implementation is merged. The base is
[PR #120](https://github.com/BostonTechnologies/RatelDesk/pull/120)
(`006381c6f78ac1d37965f52ddc28bff002753055`), followed by discovery/sign-in
continuation (#123), cancellation/unlink convergence (#125), PostgreSQL
runtime and credential-mode ownership (#127), serialization recovery and
orchestration correlation (#129), current sender authority (#130), and
human-consent/bounded protocol-token recovery (#131). The reviewed source is
`af71b9f9ae47bf75ea44f9fb486bd5c812d34910`, with source version
`0.1.1-beta.13`. That exact source is now published as
[RatelDesk-0.1.1-beta.13](https://github.com/BostonTechnologies/RatelDesk/releases/tag/RatelDesk-0.1.1-beta.13):
the existing owner's [release run 37428393499](https://github.com/BostonTechnologies/RatelDesk/actions/runs/37428393499)
succeeded, and publication occurred on 6 October at 10:05:04 SAST
(08:05:04 UTC). Beta.13 includes #131 but predates the #122 dependency merge
and the focused #128 worker correction. Publication supplies no new
two-product acceptance result.

The interoperability receipts below used published
[RatelDesk-0.1.1-beta.12](https://github.com/BostonTechnologies/RatelDesk/releases/tag/RatelDesk-0.1.1-beta.12),
source `3cd63a776df67bd98d7efb81a330b2d0c57ad1f1`, which predates #131;
their tested build identities remain unchanged.

The existing continuation owner has advanced NetRatel PR #166 to
`d5bfa08550bd303e2d5d1388ed71336404c45fbc`, pushed on 6 October at 10:06:45
SAST (08:06:45 UTC). The owner started normal run `37433948570` against
published RatelDesk beta.13 source `af71b9f9ae47bf75ea44f9fb486bd5c812d34910`
and these intended peer images:

| Product | Beta.13 immutable image digest |
| --- | --- |
| RatelDesk API | `ghcr.io/bostontechnologies/rateldesk-api@sha256:659f72d941ccaa78131858b81cfcab9a7f34ba4136bc85159487278d63b16f5b` |
| RatelDesk Web | `ghcr.io/bostontechnologies/rateldesk-web@sha256:ecc57b4a4c2416713005a68c1a065a01b9ab2c1ed2c18aa8c3ebaa577a705354` |

At 10:18 SAST (08:18 UTC), source-Compose and both extracted local-first
bundle jobs had failed before preparing actual owner images or reaching
owner-browser acceptance; general tests were still running. This is not an
accepted final suite. The source smoke timed out waiting for its credential
selector option `credential-permission-1-telemetry.read`, before reciprocal
owner acceptance. Final results and the owner's next checkpoint are linked
from #89. Its test environment is not exposed in this workspace; reuse that
continuation's immutable results through
[RatelDesk #89](https://github.com/BostonTechnologies/RatelDesk/issues/89).
NetRatel beta.1 is not observed published.

At NetRatel PR #166
head `19f27f28bce127a2fdaa264ae28d570b94498d23`, the PR's reported real reciprocal
suite against that published beta.12 peer was **25 passed, 1 failed, 0 skipped**.
The remaining automatic-rotation case stopped at initial
remote approval with HTTP 409 `service-link-conflict`, before rotation began;
the reported full acceptance guards rejected the run. Separately, native
receipts in hosted run 37406559519
at candidate merge source `3cb76bf926973e1471a734158f39a8851abb5ee0` prove both
initiating roles' real execution, callback and replay against beta.12. A
NetRatel-issuer automatic-policy rotation also completed with four lost
responses/restarts and fixed predecessor retirement; its historical-age input
is a fixture, not an elapsed-time soak. That hosted run failed overall and
supplies no complete accepted suite; its Web
owner acceptance was skipped after an earlier Compose failure. There is no
accepted post-#131 pair in this receipt. Repeat that unchanged approval case
against the intended
corrected peer before treating rotation or the full sequence as accepted.

The partial receipts are retained in
artifact 11387929385.
They identify reviewed NetRatel source `19f27f28bce127a2fdaa264ae28d570b94498d23`,
test-merge source `3cb76bf926973e1471a734158f39a8851abb5ee0`, native client
assembly SHA256 `2364f5f09d23437c119cd1675a107973d4fe65ce46f82d3360678bc2a51ede6a`,
and published beta.12 images. External source, run and artifact links are
retained in [RatelDesk #89](https://github.com/BostonTechnologies/RatelDesk/issues/89):

| Product | Immutable image digest |
| --- | --- |
| RatelDesk API | `ghcr.io/bostontechnologies/rateldesk-api@sha256:c32f3537a5c3834224a9c07c47423335c1448cfc9607f2fcfd66924b17525e0b` |
| RatelDesk Web | `ghcr.io/bostontechnologies/rateldesk-web@sha256:baa433421994c6b739dba17aa887cc74a70828b0f9f91462d5ff9c50e22b2980` |
| RatelDesk HTTP MCP | `ghcr.io/bostontechnologies/rateldesk-mcp-http@sha256:44cd5142e711caf3ca2a35bba7cf45c5e8236e40d9e57e010d26b80693216958` |

This supersedes earlier descriptions of the RatelDesk implementation as
unmerged. It does not mark the sequence below as accepted against the final
published pair. Use the existing
NetRatel PR #166 continuation, coordinated through
[RatelDesk #89](https://github.com/BostonTechnologies/RatelDesk/issues/89)
for shared two-product acceptance; record the exact peer SHA, tag and image
digests for each result. The
[reconciliation ledger](../implementation/beta4-integration-progress.md)
maps that evidence to #89, #121, #124, #126 and #128 and records any remaining
publication or acceptance prerequisite. Synthetic fixtures and source-built
candidate runs do not establish published-peer acceptance. The supported
[legacy outbound M2M path](netratel-orchestrator.md) and legacy callback
upgrade case remain separate acceptance requirements.

This document records a reproducible acceptance sequence for the later NetRatel continuation. The fixtures establish serialization and hashing conformance. They do not establish that a deployed NetRatel build implements reciprocal linking or prove real two-product compatibility. Execute the following against the published RatelDesk release and the compatible updated NetRatel candidate; retain the actual immutable build identities and results in that continuation's evidence.

If peer approval reaches a signed-out RatelDesk browser, the Web endpoint removes ceremony correlation before showing ordinary sign-in. After signing in, return to the initiating product's integration credentials in the original browser session and select **Continue peer approval** (or **Continue NetRatel approval** when RatelDesk initiated). This explicit, antiforgery-protected continuation reopens the same live attempt and its pinned peer approval address; it does not create another attempt, approve a grant, or copy proof values into a login return URL or browser storage. Expired attempts or a different initiating actor/session require new setup. Once consent has completed, the separate **Resume** action reconciles the existing lifecycle decision.

## Issuer, linking and deployment configuration

`ServiceIdentity` is separate from browser login, SystemToken, personal `rdk_` credentials, existing inbound `OrchestrationM2M` settings and outbound `Orchestrator` profiles. It is disabled by default. Enable it with canonical configured `Issuer`, `Audience`, `ApiBaseUrl`, `WebBaseUrl` and a persistent `InstanceId`. The issuer string is exact; changing it requires deliberate signing-identity migration. OAuth metadata is served at API `/.well-known/oauth-authorization-server`, public RSA JWKS at API `/.well-known/jwks.json`, and the form-encoded `client_secret_post` grant endpoint at API `/connect/token`.

Approved links pin the local instance, issuer, audience and canonical Web/API addresses. Disabling the issuer or changing those identity settings immediately disables linked token issuance, resource authorization and cached outbound use, including control scopes. Restore the approved configuration to recover that link, or unlink and complete new consent for the replacement identity; an existing grant cannot silently adopt new settings. Product version updates alone do not change the approved identity.

| ServiceIdentity key | Default | Validated bounds |
| --- | --- | --- |
| `AccessTokenLifetimeSeconds` | 300 | 60–900 |
| `ClockSkewSeconds` | 15 | 0–60 |
| `CredentialMaximumAgeDays` | 90 | 1–365 |
| `CredentialOverlapSeconds` | 600 | 60–86400 |
| `TerminalControlRecoverySeconds` | 3600 | 60–86400 |
| `AllowPrivateHttp` | false | Explicit deployment opt-in; peer input cannot enable it |

`ServiceLinks` controls the reciprocal ceremony, durable worker and coordinated rotation policy. It is disabled by default and its Web/API addresses must agree with the issuer's canonical configuration. Optional `GatewayBaseUrl` is descriptive; OAuth secrets never go to it.

| ServiceLinks key | Default | Validated bounds |
| --- | --- | --- |
| `BootstrapLifetimeSeconds` | 900 | 120–3600 |
| `TerminalControlRecoverySeconds` | 3600 | 60–86400 |
| `WorkerIntervalSeconds` | 15 | 1–60 |
| `MaximumPayloadBytes` | 131072 | 4096–1048576 |
| `AutomaticRotationEnabled` | true | Applies to active managed reciprocal links |
| `RotationAgeDays` | 60 | 1–365; schedule before credential hard expiry |
| `RotationOfferLifetimeSeconds` | 900 | 120–3600 |
| `RotationOverlapSeconds` | 600 | 60–3600; within issuer overlap policy |
| `RotationPolicyRevision` | 1 | Positive safe integer ≤9007199254740991 |
| `AllowPrivateHttp` | false | Explicit deployment opt-in |

The default managed policy rotates each direction after 60 days within its 90-day maximum credential lifetime. Each issuer coordinates its own direction. Offer expiry and predecessor retirement are finite and never renewed by retries. Deployment-owned clients remain read-only in the web interface and are renewed through deployment configuration; ordinary managed client creation and DB profile updates do not require API restart.

Expired issuer rotation offers and secret-bearing offer journals are purged even when business authority or linking is disabled. Unlink purges transient rotation credentials transactionally. An ambiguous caller candidate remains protected while recovery must determine whether the issuer already activated it; expiry cannot undo a durable activation decision. Nonsecret lifecycle evidence and the existing finite terminal-control recovery survive cleanup.

Inbound signing private keys are protected in the shared relational key store with the existing durable Data Protection ring, bound to issuer and `kid`. Preserve that ring and database across replicas/restarts. The public JWKS never contains private/HMAC material. Unavailable signing material causes token issuance to return 503; it does not silently generate a replacement issuer. An authenticated instance administrator may deliberately rotate keys through `POST /api/v1/admin/service-clients/signing-keys/rotate`. The old public validation key overlaps for access-token lifetime plus clock skew plus 60 seconds.

For Docker-provisioned inbound service clients, `ServiceIdentity__Clients__{index}__...` is a complete deployment-owned identity. Its keys include `ClientId`, `ClientSecret`, `Name`, `OrganizationId`, `PeerInstanceId`, `PeerTenantId`, `Scopes`, `CustomerIds`, `ResourceConstraintsJson`, optional `SourceInstanceId`/`SourceNamespaceId`, and `Enabled`. A deployment client ID may not collide with a database client ID ignoring case. Neither source borrows the other's secret, issuer or tenant. An example with only the incident operations is:

```dotenv
ServiceIdentity__Enabled=true
ServiceIdentity__Issuer=https://configured-issuer.example
ServiceIdentity__Audience=rateldesk.services
ServiceIdentity__ApiBaseUrl=https://configured-api.example
ServiceIdentity__WebBaseUrl=https://configured-web.example
ServiceIdentity__InstanceId=<persistent-installation-id>
ServiceIdentity__Clients__0__ClientId=<distinct-deployment-client-id>
ServiceIdentity__Clients__0__ClientSecret=<independent-random-secret-at-least-32-characters>
ServiceIdentity__Clients__0__OrganizationId=<approved-local-organization-id>
ServiceIdentity__Clients__0__PeerInstanceId=<approved-netratel-instance-id>
ServiceIdentity__Clients__0__PeerTenantId=<approved-netratel-tenant-id>
ServiceIdentity__Clients__0__SourceInstanceId=<persisted-netratel-flow-source-guid>
ServiceIdentity__Clients__0__CustomerIds__0=<approved-local-customer-id>
ServiceIdentity__Clients__0__Scopes__0=rateldesk.incidents.create
ServiceIdentity__Clients__0__Scopes__1=rateldesk.incident-receipts.read
ServiceIdentity__Clients__0__Scopes__2=rateldesk.incident-targets.read
```

The local manual/deployment form normalizes its explicit organization/customer selections into the complete typed resource constraints. Adding `rateldesk.orchestration.callback` to a manual client additionally requires `ResourceConstraintsJson` with the matching `organization_id` and explicit recorded local `request_ids` and `task_ids`; this is distinct from granting arbitrary callback authority. Saved outbound secrets and existing inbound client secrets are never revealed on later reads. A newly created or manually rotated secret is revealed once.

For a guided link, callbacks may refer to newly created executions whose task rows were durably stamped with that exact `OrchestrationLinkId`, `OrchestrationPeerInstanceId` and `OrchestrationLinkRevision` at real outbound request submission. Empty `request_ids`/`task_ids` provide no standalone task grant; the checked stamped link ownership and recorded external request/execution acknowledgement supply the required explicit correlation. Nonempty lists narrow that already-bound set. Nonempty `customer_ids` additionally constrain the recorded task customer. A linkless manual client cannot use this dynamic path and must enumerate its recorded local request/task grants. Machine worklogs carry no human technician foreign key.

The wizard stages the existing protected outbound `Orchestrator` DB provider and enables its sender only after matching peer activation acknowledgement. Preserve `Orchestrator__...` and legacy `Orchestration__Provider__...` configuration: canonical keys win at the same provider priority, while higher-priority providers retain normal .NET precedence and select their complete profile. Explicit operator empty/false values remain intentional; empty authority/token endpoint/audience/scope values suppress inferred defaults and incomplete profiles cannot acquire tokens. The legacy provider uses its global `M2M` client-ID/secret pair only when neither provider-specific credential key exists. A partially configured provider credential never borrows the other field from global configuration. Meaningful deployment configuration makes that outbound profile read-only; the wizard reports the ownership conflict instead of replacing it. Existing `OrchestrationM2M` and `M2M__ClientId`/`M2M__ClientSecret` inbound callback meanings remain separate. A changed DB destination/issuer/tenant/client/grant cannot retain its previous secret. Managed revisions and current durable authorization checks invalidate stale settings/tokens across replicas.

## Build and identity record

Record the RatelDesk published version/tag, immutable source SHA, release artifact checksums and container digests from the completed release publication. Record the NetRatel candidate SHA and actual running build identities. Record each configured Web, API, issuer and gateway origin independently, without any secrets. Use the builds' actual canonical metadata; do not substitute the fixture hosts or synthesize an API URL from the Web URL.

Run the sequence in isolated installations with supported persistent SQLite or PostgreSQL databases, durable Data Protection/signing keys and no customer production data. Create one explicit test organization/customer in RatelDesk and one explicit test tenant/resource/request definition in NetRatel using the products' authenticated administration interfaces. Use NetRatel's persisted flow source instance identity for the source registration. Keep a second tenant/customer outside the selected grants to exercise denial.

## Reciprocal approval and orchestration

1. In RatelDesk, open `/account/integration-credentials`, choose **NetRatel M2M**, then **Link NetRatel**. Enter the NetRatel Web base URL and select the authorized local organization/customer and orchestration boundaries. Verify the displayed product, installation identity and separate canonical origins. At this point discovery/Continue grants no machine business authority.
2. Sign in to NetRatel as its authorized administrator at the advertised approval endpoint. Verify the real RatelDesk initiating origin, select the exact NetRatel tenant/resource/request constraints and approve both displayed directions. Narrowed grants must be displayed through the protected review response; an expansion must fail.
3. Return to RatelDesk's exact callback. Confirm the actual selected remote tenant and both final grants. Verify both directions acquire real OAuth `client_credentials` tokens and obtain durable authenticated verification receipts. Observe **Completing setup** until the coordinator's durable commit and both active acknowledgements converge.
4. Open `/admin/automation/integration/orchestrator`. Confirm the wizard populated the existing protected runtime provider profile. Run authenticated NetRatel ping and the scoped job/tenant/request-definition catalogs; only the approved boundary is visible. Public health success alone is insufficient.
5. Submit an approved existing request/task through `POST /internal/ingest`. Execute the recorded task and return its actual callback through `POST /api/v1/orchestration/provider/callback` with a RatelDesk-issued token scoped to `rateldesk.orchestration.callback`. Verify the approved tenant and real request/task/execution correlate and the acknowledgement updates that task. Attempt the same token against a foreign tenant/task and verify refusal. Independently retain a legacy `OrchestrationM2M` callback upgrade case.
6. Repeat the ceremony with NetRatel as initiator against fresh approved test boundaries. Verify the same JSON contract and exact final local/remote approval ordering; ordinary personal credentials and discovery remain unable to create administrative service clients.

## Incident creation and lost-response reconciliation

1. Using the NetRatel-to-RatelDesk directional credential, obtain a short-lived token at the pinned RatelDesk token endpoint with `client_secret_post`. Request only the three approved incident scopes. Obtain the approved source's authenticated capability response at `GET /api/v1/integrations/netratel/capabilities` with `X-NetRatel-Source-Instance`. Confirm the exact receiver contract, source namespace, target endpoint, retention guarantees and `oauth_client_credentials` support.
2. Call `POST /api/v1/integrations/netratel/targets/validate` using the approved organization/customer, optional assignee and categories. Verify the operation is read-only and returns the normalized permitted mapping. This observation must not bypass fresh create-time checks.
3. Persist one logical NetRatel flow action, its original target, source identity, stable idempotency key and first-attempt creation time. Deliver the existing `CreateIncidentDto` directly to `POST /api/v1/incidents/` with both `Idempotency-Key` and `X-NetRatel-Source-Instance`. Simulate losing the response after the RatelDesk database transaction commits.
4. Restart the relevant processes with the same databases and keys. Reconcile through `GET /api/v1/integrations/netratel/incident-receipts/{key}` and retry the identical action/body/key. Verify one incident, one accepted receipt, one durable logical creation-effect enqueue and the same captured `Location`. Lookup and replay return 200 and the original accepted body; first creation returns 201. Changing a material body field under that source/key returns 409 with `idempotency-payload-conflict` and creates no extra effects.
5. Attempt foreign source, organization, customer and receipt access, as well as generic incident/admin routes with the narrow service token. Verify refusal without disclosing foreign record existence. Disable a current service/source/customer/organization grant and prove a cached previously valid JWT loses business authorization on both replicas.

## Recovery, rotation and revocation

1. Inject response loss and process restart after each approval transaction, exchange acceptance, outbound handoff persistence, probe receipt and commit transaction. Repeated semantic requests preserve attempt/link/client IDs, credentials and decisions. Changed bodies under reused authorized operation IDs conflict. Escrow remains fixed-expiry and absent from status/read APIs.
2. Specifically commit durably at the initiator immediately before responder setup expiry, then withhold commit delivery until afterward. Verify the prepared responder retains control-only `in_doubt`, accepts the late matching coordinator decision and converges. It must neither delete the registration nor claim a completed/aborted disagreement. An unreachable coordinator remains accurately recovery pending with business senders disabled.
3. Rotate each direction manually and through the configured automatic policy while real approved traffic continues. Exercise response loss/restart at offer, successor verification, activation, durable caller switch and predecessor retirement. Preserve logical client ID, link/grant/source namespace and queued incident action/key. Verify successor verification/activation precedes switch acknowledgement and the predecessor retires within the fixed bounded policy.
4. Unlink while the peer is unavailable. Confirm local workers and cached-token business access stop immediately, local and remote completion are displayed separately, and source/receipt tombstones remain. Restore connectivity and reconcile the original durable revocation operation; retry must never affect another link. After finite terminal control recovery expiry, finish ambiguous remote confirmation through authenticated administrator recovery.

Retain actual test counts, skips, HTTP observations, database provider/migration evidence and immutable build identities. Do not label this sequence as executed until both real products complete it. RatelDesk-first conformance and publication are separate from the later NetRatel two-product release gate.
