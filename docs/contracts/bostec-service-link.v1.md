APPENDIX B — SHARED RECIPROCAL M2M CONTRACT
# Shared implementation contract: bostec.service-link.v1

This appendix is normative for the paired NetRatel and RatelDesk implementation prompts. It is a proposed product contract to implement and freeze with shared fixtures, not a claim that the repositories already support it or that reciprocal registration is an OAuth standard. Pin the same contract document revision in both repositories. If a genuine incompatibility requires a change, update the common contract and both implementations together before release; do not silently diverge.

## Purpose and current interoperability baseline

An administrator starts from either product, enters the other product's Web base URL, approves one explicit local grant and one explicit remote grant within one guided flow, and obtains two independently revocable service identities. Runtime calls then use OAuth 2.0 client_credentials. Browser administrator approval is separate from runtime credentials. Typing a URL, displaying metadata, or possessing an ordinary account token does not authorize provisioning.

The required beta baseline is client_secret_post because reviewed NetRatel /connect/token implements that method. Preserve its existing form-encoded client_credentials request. Add client_secret_basic only as tested additive support; do not negotiate it unless implemented. private_key_jwt is an extension capability, not a prerequisite or an excuse to replace the authentication stack. Advertise only actually supported methods. Do not invent a full general OAuth authorization-code server or open dynamic-client-registration service to implement this product pairing flow.

RatelDesk needs a real confidential-service-client registry and constrained client_credentials issuer for this work. Existing rdk_ account credentials and legacy verification of NetRatel-signed callbacks are separate mechanisms. Use maintained cryptographic libraries and durable asymmetric signing material/JWKS for newly issued service JWTs, with explicit issuer, audience, algorithm and token-purpose validation. Never publish an existing HMAC login/SystemToken key in JWKS or reuse a Web administrative/system credential for the link. Preserve existing login and legacy integration authentication compatibility.

No standalone OAuth refresh token is needed for client_credentials. Cache short-lived access tokens, obtain replacements using the active service credential, and bind cache entries to the current tenant, peer, issuer, audience, scope set, connection revision and credential revision.

## Discovery, origin proof and network rules

Use exactly GET /api/integrations/service-link/metadata as the product discovery entry accessible at the configured Web base URL; expose/proxy it at the API origin where appropriate. Real OAuth metadata remains separate at its actual supported location. Do not name a custom pairing endpoint an OAuth authorization endpoint.

Metadata JSON field names:

{
  "contract": "bostec.service-link.v1",
  "product": "netratel",
  "product_version": "actual-version",
  "instance_id": "stable-installation-id",
  "source_instance_id": "stable-authorized-producer-id-or-null",
  "web_base_url": "https://configured-web.example",
  "api_base_url": "https://configured-api.example",
  "gateway_base_url": "https://configured-gateway.example-or-null",
  "oauth_issuer": "https://configured-issuer.example",
  "oauth_metadata_url": "https://configured-issuer.example/actual-metadata-path",
  "token_endpoint": "https://configured-api.example/connect/token",
  "jwks_uri": "https://configured-issuer.example/actual-jwks-path",
  "audience": "actual-api-audience",
  "token_endpoint_auth_methods_supported": ["client_secret_post"],
  "service_link_endpoint": "https://configured-api.example/api/integrations/service-link/v1",
  "approval_endpoint": "https://configured-web.example/account/integration-credentials/link/approve",
  "callback_endpoint": "https://configured-web.example/account/integration-credentials/link/callback",
  "permission_profiles": [],
  "supported_contracts": ["bostec.service-link.v1"]
}

product is netratel or rateldesk. product_version is informative, never proof of contract behavior. Nullable fields are JSON null; do not advertise an unsupported OAuth metadata/JWKS URL. Bind real supported OAuth metadata before release when the new issuer exposes it. Explicitly advertise client_secret_post: absence of token_endpoint_auth_methods_supported implies Basic in RFC 8414. Never advertise authorization_code or private_key_jwt merely to satisfy a metadata shape. Each profile lists a versioned capability, concrete scopes and the supported resource operations; the final exact profiles and fixtures must match the product's enforcement.

The Web, API, issuer and gateway addresses are distinct. Never infer the API or token endpoint by appending a path to the entered Web URL, and never send OAuth tokens/client secrets to the NetRatel Akka gateway. Preserve the v0.1.0 distinction between public Web/API and gateway addresses. Discovery does not change existing native enrollment, gateway, OIDC or branding settings.

Canonical URLs come from validated deployment/product settings, not an untrusted Host header. Establish the entered Web origin through normal TLS validation, retrieve its fixed metadata route, validate all advertised endpoints, and pin the approved descriptor, exact issuer string, instance identity, API origin and endpoint set for the attempt. The metadata may legitimately describe different configured Web/API/issuer origins; show the approved identity and origins in the consent details and validate each. An endpoint/issuer/origin change after approval requires a new approved connection revision.

Use the existing bounded safe HTTP/URI policy for discovery, descriptor retrieval, token, verification, callback, capability, receipt and delivery calls. Validate schemes, ports, userinfo/query/fragment rules, redirect policy, IPv4 and IPv6 addresses, and DNS resolution at connection time. Disable automatic HTTP redirects and do not forward credentials to redirect destinations. Prevent a subsequent DNS answer from bypassing the approved destination policy. No universal public-address-only restriction: retain explicit deployment-authorized private networks, private CA trust and existing private-HTTP opt-ins where supported. Remote metadata and browser input cannot enable private HTTP, relax TLS verification or grant arbitrary network access. Do not hardwire production hostnames or require a new public-HTTPS CI environment.

No anonymous API may fetch an arbitrary supplied callback or request URI. Public metadata/request-descriptor routes are read-only. The receiver's own authenticated administrator workflow retrieves the initiating origin's fixed metadata and fixed request-descriptor route through the safe client. A descriptor must actually be served by that HTTPS origin and target the receiving instance. Callback endpoints must equal the canonical endpoint from this verified descriptor and the stored request; no wildcard return URL or client-selected arbitrary redirect. A hostile site can describe itself, but it cannot impersonate another origin's stored request or bypass the receiving administrator's consent.

## Identity, grant and field conventions

Use A for initiator and B for responder. Either product can occupy either role. A single guided link creates both directions. Keep these identifiers distinct:

- attempt_id: unpredictable, expiring bootstrap attempt identifier.
- link_id: stable established relationship identifier, unchanged on rotation.
- link_revision: monotonic approved configuration/grant revision.
- commit_id: unique durable activation decision for one attempt/revision.
- instance_id: persistent installation identity, never regenerated on process/container restart.
- source_instance_id: exact established producer identity used by the incident receiver contract.
- source_namespace_id: receiver-generated durable deduplication namespace from RatelDesk #116; independent of credential IDs and link rotation.
- credential_revision: monotonic version within one directional service-client registration.
- lifecycle_state: persisted link/attempt state.
- browser_state: unpredictable correlation value bound to the initiating authenticated browser session; never a lifecycle label or audit field.

Wire IDs are strings in their documented canonical representation. Do not silently coerce NetRatel tenant integers, RatelDesk organization GUIDs, source IDs and local user IDs into one identity. The explicit map is authoritative, not matching display names, email domains, or coincidentally equal tenant IDs.

The request descriptor includes contract, attempt_id, expires_at, initiator_instance_id, initiator_tenant_id, expected_responder_instance_id, requested_responder_tenant_id (nullable until selected), initiator_endpoint_snapshot, responder_endpoint_snapshot, descriptor_hash, code_challenge, code_challenge_method (S256), requested_grants, and initiator_callback_endpoint. It contains no secret, verifier, pairing_code or browser_state. Public retrieval is a bounded expiring descriptor operation; it grants no rights and must not enumerate unrelated attempts or tenants.

initiator_endpoint_snapshot and responder_endpoint_snapshot are required typed objects containing the full accepted metadata field set shown in the discovery section: contract, product, product_version, instance_id, source_instance_id, web_base_url, api_base_url, gateway_base_url, oauth_issuer, oauth_metadata_url, token_endpoint, jwks_uri, audience, token_endpoint_auth_methods_supported, service_link_endpoint, approval_endpoint, callback_endpoint, permission_profiles and supported_contracts. They are the validated pinned snapshots, not URI references to mutable future documents. All those snapshot fields remain inside descriptor_hash and grant_hash. Optional metadata values use the prescribed null values; no endpoint is omitted and then inferred later. The duplicated top-level instance IDs and initiator_callback_endpoint must equal their snapshot values. Current metadata can be inspected for diagnostics, but cannot silently replace an approved snapshot or change a retry destination.

The immutable grant_summary returned after B's approval contains contract, attempt_id, link_id, proposed_link_revision, descriptor_hash, expires_at, both instance identities, those same complete initiator_endpoint_snapshot and responder_endpoint_snapshot objects, and exactly two grants. Every grant contains:

{
  "direction_id": "initiator_to_responder",
  "caller_snapshot": "initiator",
  "target_snapshot": "responder",
  "caller_product": "rateldesk",
  "caller_instance_id": "A",
  "caller_tenant_id": "A-tenant",
  "target_product": "netratel",
  "target_instance_id": "B",
  "target_tenant_id": "B-selected-tenant",
  "issuer": "B-approved-issuer",
  "audience": "B-approved-audience",
  "capabilities": ["concrete-versioned-capability"],
  "scopes": ["concrete-approved-scope"],
  "resource_constraints": {},
  "source_instance_id": "exact-source-id-where-applicable",
  "source_namespace_id": "receiver-generated-namespace-where-applicable"
}

The reverse grant uses direction_id responder_to_initiator, caller_snapshot=responder, target_snapshot=initiator and reversed caller/target identities. caller_snapshot and target_snapshot are fixed enum references to the full typed objects inside this same immutable grant_summary, never external URLs. Each grant's instance/product/issuer/audience must match its referenced snapshot; its concrete scopes/operations must be supported by that snapshot. Exchange/verification/commit and runtime resolution take the token_endpoint, API base, issuer and lifecycle endpoints from these approved objects. Never hash only issuer while accepting an unbound token/API URL from a later handoff. resource_constraints is a typed, documented per-capability schema, not arbitrary claims or a permissive JSON authorization bag. For RatelDesk it binds the approved organization and enabled customer boundaries and any permitted callback/task boundaries. No omitted/null constraint means global access. Reject unsupported capability/constraint fields before approval.

Freeze the hash routine as bostec.service-link.hash.v1: SHA-256 over the UTF-8 bytes produced by RFC 8785 JSON Canonicalization Scheme (JCS), encoded as 64 lowercase hexadecimal characters. Build the explicit unsigned descriptor payload first and EXCLUDE its own top-level descriptor_hash and any outer hash/signature envelope; then canonicalize/hash it. For grant_hash, build the explicit grant_summary payload EXCLUDING its own top-level grant_hash and any outer hash/signature envelope. A grant's reference to descriptor_hash remains inside the grant hash; never recursively remove all hash-named fields. Every security-relevant endpoint, source, tenant, resource boundary, operation, scope and revision stays in the respective payload. Only these explicit self/envelope fields are excluded; unknown semantic fields are rejected until a coordinated contract revision defines them.

JCS controls object-property ordering, escaping, Unicode preservation and UTF-8 output; arbitrary JsonSerializer property order/default escaping is not a canonicalization algorithm. Reject duplicate JSON property names, invalid Unicode and unsupported numeric forms. This schema uses booleans/null/strings plus nonnegative integer revisions/counts no larger than 9007199254740991; represent larger identifiers as strings. Before JCS, the typed payload builder puts set-valued scopes/capabilities in ordinal order and rejects duplicates; it orders the two grants by direction_id. Meaningful ordered arrays retain order. Contract timestamps use UTC RFC3339 at whole-second precision, YYYY-MM-DDTHH:mm:ssZ, and are not silently renormalized on the receiving side. The semantic exchange fingerprint and exchange_response_hash use the same typed/JCS routine over the full corresponding payload, including secret values when present but excluding only their own optional hash wrapper. Never log fingerprint inputs.

Required canonicalizer known vectors (canonical text is UTF-8 with no trailing newline):

- Object input with fields z=null, n=1, a=[true,"x"] in any order canonicalizes to {"a":[true,"x"],"n":1,"z":null}; SHA-256 bd92bf6940da187209c3f1092f1934110a77a5308ad639fc23cb110fd621c4ee.
- Canonical text {"attempt_id":"test-attempt","contract":"bostec.service-link.v1","expires_at":"2026-10-04T15:00:00Z"} hashes to 85c791bf3a6272a64c8a86e9108cb8f92878db92296f789682466524155f595b. The hash-projection fixture adding/replacing a top-level descriptor_hash around this payload must yield that same digest; changing expires_at must not. This is a hashing fixture, not a complete valid production descriptor.
- Include RFC 8785 escaping/Unicode/order conformance vectors and full valid product-descriptor/grant/exchange vectors in both repositories; duplicate-key inputs fail before hashing. Changing any approved issuer/endpoint/tenant/source/scope/constraint must change its protected digest. Scope/claim semantics cannot change without a shared profile/contract revision.

The A start request is discovery and a proposed permission ceiling, not an inbound grant to an unknown B tenant. B may select its authorized tenant and narrow the proposal; it may not expand A's requested limits. A learns B's concrete selection through the protected review response described below. A then approves the exact final local grant. Once either final grant is approved, silently changing its peer/tenant/scope/constraint is prohibited. An incompatible selection or requested expansion starts a new approved revision. If A already specified the exact peer tenant and grant, it may confirm the same summary in the existing final step; do not add repetitive approvals for unchanged data.

For NetRatel incident delivery, source_instance_id must exactly match the current persisted FlowRuntimeIdentity.SourceInstanceId (or its actual successor in inspected code), and therefore the value used by stable action identity and X-NetRatel-Source-Instance. Do not create a second unrelated source GUID during pairing. If installation identity and flow source identity differ, retain both fields and explicitly register that mapping under administrator authority. Reuse RatelDesk's stable #116 source namespace across credential rotation, relink and receipt reconciliation; clients cannot claim another namespace by copying a header. Do not auto-create organizations/customers or globally authorize all sources.

The receiver chooses source_namespace_id. If RatelDesk is A, its authorized start operation may reserve/reuse a pending namespace for the discovered exact NetRatel source and A's local resource boundary before B builds grant_summary; this reservation confers no principal membership or business permission. Include that fixed reservation in the proposed grant so the final grant_hash does not change later. If RatelDesk is B, it reserves/reuses the namespace as part of B's explicit approval. Activate membership for the actual directional service principal only under the corresponding final local grant. A caller never supplies its own authoritative namespace, and a reservation may not replace an existing approved mapping.

The service principal authenticates transport. It does not replace NetRatel FlowExecutionAuthority, publish/run actor checks, connector ownership, current tenant/resource permission checks, or RatelDesk customer/organization checks with an issuer identity or global administrator. Preserve existing per-operation authority checks when adding the new authentication path.

## Concrete reciprocal bootstrap

Stage 1: A starts discovery.

A's authenticated administrator selects A's tenant and enters B's Web URL. An anti-forgery-protected local action checks integration-management rights, fetches/pins B's metadata and creates a durable attempt with a cryptographically random verifier/challenge, a session-bound browser_state, proposed grant ceilings and finite expiry. There is no business-capable service client yet. Store the verifier protected server-side. A serves the fixed descriptor at GET {A.service_link_endpoint}/requests/{attempt_id}.

A returns a browser navigation to B's configured approval_endpoint with initiator_web_base_url, attempt_id and browser_state. browser_state is an opaque short-lived correlation value, not a bearer credential for any API. It is omitted from the public descriptor, telemetry and logs. No runtime secret, access token or code_verifier appears in the URL or browser storage.

Stage 2: B approves its explicit grant.

B authenticates its own administrator, authorizes the selected local tenant and fetches A's fixed descriptor as above. B confirms the descriptor targets B and shows the real initiating origin, both tenant identities and both requested directions. The administrator selects an allowed B tenant/resource mapping, can narrow requested capabilities and approves the resulting exact grant_summary using an anti-forgery-protected action. The response cannot silently broaden what A requested.

In one B transaction, persist B's consent actor/audit, the immutable grant_summary and grant_hash, create B's pending inbound service registration for A to B, generate a strong random directional secret, store only its verifier/hash in the inbound registration, and store a short-lived encrypted handoff escrow. Record a random pairing_code proof bound to the attempt, S256 challenge, exact callback, browser_state roundtrip, grant_hash, descriptor_hash and expiry. No business scopes are available to this pending client.

B returns the browser to A's exact callback with attempt_id, pairing_code, browser_state, responder_instance_id and oauth_issuer. The pairing_code is a constrained custom product provisioning code, not an OAuth authorization code, login token, administrative bearer or runtime credential. Store callback proof server-side immediately, clear the browser address through a safe redirect, and apply no-store/referrer controls to these temporary pages.

Stage 3: A reviews B's selected tenant/grant and approves its own grant.

A validates its session-bound browser_state, expected responder, exact issuer and callback binding. It performs POST {B.service_link_endpoint}/attempts/{attempt_id}/review using contract, attempt_id, pairing_code, code_verifier and descriptor_hash over the backchannel. B returns only grant_summary, grant_hash and lifecycle_state=approved. This response contains no runtime credentials. Repeated identical proof may retrieve the same immutable summary within the expiry; it cannot create new rights.

A displays the actual B-selected tenant/resource mapping and both grants. Its authenticated administrator authorizes A's exact local inbound grant and confirms that summary. Recheck current permission and anti-forgery protection at this action. If the summary exceeds proposed limits or differs from what was displayed, fail; do not silently accept, combine tenants or issue a wildcard grant. If a change is required, narrow/restart through a new mutually approved revision.

In one A transaction, persist A's immutable consent to grant_hash, create A's pending inbound service registration for B to A, generate its independent secret, store the inbound secret verifier/hash and protected escrow, and record a durable exchange work item. This is A's only actual grant approval; the initial Continue was discovery. Both local and remote approval occur within the same guided flow.

Stage 4: exchange both directional credentials over the backchannel.

A sends POST {B.service_link_endpoint}/attempts/{attempt_id}/exchange:

{
  "contract": "bostec.service-link.v1",
  "attempt_id": "attempt-id",
  "pairing_code": "temporary-code",
  "code_verifier": "server-held-verifier",
  "descriptor_hash": "approved-descriptor-hash",
  "grant_hash": "approved-grant-hash",
  "initiator_consent_id": "A-durable-consent-id",
  "credential_for_responder": {
    "client_id": "B-to-A-service-client",
    "client_secret": "strong-random-secret",
    "token_endpoint_auth_method": "client_secret_post",
    "issuer": "A-approved-issuer",
    "token_endpoint": "A-approved-token-endpoint",
    "audience": "A-approved-audience",
    "scopes": ["approved-B-to-A-business-scopes"],
    "credential_revision": 1,
    "caller_instance_id": "B",
    "caller_tenant_id": "B-selected-tenant",
    "target_instance_id": "A",
    "target_tenant_id": "A-selected-tenant"
  }
}

B validates the proof, expiry and every immutable descriptor/grant binding before accepting any credential. URL, tenant, scope or instance fields must equal the approved summary; the handoff is not authority to change them. In one transaction, B stores credential_for_responder as its recoverably encrypted outbound B-to-A credential, records the canonical fingerprint of the accepted exchange body, records the at-most-once provisioning transition, and preserves the exact encrypted response handoff.

B returns:

{
  "contract": "bostec.service-link.v1",
  "attempt_id": "attempt-id",
  "link_id": "stable-link-id",
  "link_revision": 1,
  "grant_hash": "approved-grant-hash",
  "lifecycle_state": "prepared",
  "credential_for_initiator": {
    "client_id": "A-to-B-service-client",
    "client_secret": "different-strong-random-secret",
    "token_endpoint_auth_method": "client_secret_post",
    "issuer": "B-approved-issuer",
    "token_endpoint": "B-approved-token-endpoint",
    "audience": "B-approved-audience",
    "scopes": ["approved-A-to-B-business-scopes"],
    "credential_revision": 1,
    "caller_instance_id": "A",
    "caller_tenant_id": "A-selected-tenant",
    "target_instance_id": "B",
    "target_tenant_id": "B-selected-tenant"
  }
}

A validates all response bindings and atomically stores its recoverably encrypted outbound A-to-B credential plus a durable acknowledgement/probe work item before acknowledging preparation. Client IDs and secrets are distinct in each direction; never share one client/secret between directions or tenants.

Crash/replay semantics are mandatory. Identical exchange retries with the same valid attempt proof and canonical request fingerprint return the identical stored response and original credentials until completion or expiry. They never mint replacement clients. A changed body under the same attempt returns a deterministic 409 conflict after authorization, with no extra side effects. Wrong/cross-attempt/expired proof returns no handoff. Fingerprinting includes the entire semantic credential/grant request, with a documented canonical representation and shared test vectors; arbitrary JSON property order must not create spurious differences. Do not log the fingerprint input.

The first successful exchange consumes the approval's authority to create the pair. Re-delivery is recovery of that exact custom transaction result, not an OAuth authorization-code replay grant. Protect the pairing_code proof, verifier and replay escrow from plaintext storage, logs and unrelated users. Keep encrypted plaintext-equivalent handoff escrow only for a short finite configured bootstrap lifetime; pin the selected expires_at in both records, bound it by both local policies, and do not extend it on every retry. The default should be suitable for one interactive linking session. Purge escrow on acknowledged completion or its fixed expiry. Escrow expiry is not permission to delete a prepared/verified participant or an uncertain commit decision. Permanent inbound clients retain only secret verifiers/hashes; permanent outbound clients retain only purpose-protected recoverable material. After both sides have durably stored outbound credentials, recovery uses the link's constrained authenticated control path without retaining escrow indefinitely. A participant missing a handoff after escrow expiry requests a durable coordinator abort through its available control direction or the authenticated local administrator recovery flow; it cannot pretend the pair completed.

Stage 5: verify both directions before business activation.

Reserve bostec.service-link.verify for proof of the bound link and bostec.service-link.control for status/lifecycle operations on that same approved link. Neither scope may register other clients, discover unrelated tenants, administer a product or enable additional grants.

An approved pending/prepared service registration may obtain short-lived bostec.service-link.verify and bostec.service-link.control tokens, separately or together, never its eventual business scopes. Bind the tokens and API policies to the exact client, issuer, audience, tenant, attempt_id, link_id, grant_hash and credential/link revisions. The verify scope permits the bound verification operation; control permits only the status, acknowledgement and decision-recovery operations valid for that registration's current state. A pending client cannot rotate another credential, create another link, expand a grant or call a business endpoint. An ordinary business policy must reject it even if an old broad caller-ID allowlist would match. Use separate explicit policies and state guards; do not rely solely on a scope string when a broad legacy M2M policy could bypass it.

Reviewed NetRatel token handling currently couples a legacy audience name to requested scope. The new managed-client validation path must accept the dedicated verify/control/read/invoke scopes under the registration's fixed API audience without requiring the broad legacy netratel.api scope as an extra permission. Keep audience validation and operation-scope validation separate. Preserve existing deployment/environment-client behavior on its legacy path. Do not make verification work by adding an unrestricted API permission to pending clients.

Each side obtains a real client_credentials access token using its new outbound client and calls POST {peer.service_link_endpoint}/links/{link_id}/verify. Verification performs no business write. It returns a durable non-secret verification_receipt_id and the verified bindings/revisions. Each side records its successful outbound probe and the incoming peer probe against its own inbound client. A can obtain B's authenticated verification status through the same constrained control surface. Merely comparing saved settings is not a successful probe.

Stage 6: durable activation decision and acknowledgements.

lifecycle_state values are awaiting_approval, approved, prepared, verified, in_doubt, commit_decided, active, revocation_pending, revoked, expired and failed. Do not use state for this wire field because browser_state has a different meaning.

A records commit_decided only after both handoffs are durably persisted and both verification receipts exist. A sends an idempotent commit to POST {B.service_link_endpoint}/links/{link_id}/commit containing contract, attempt_id, link_id, link_revision, commit_id, descriptor_hash, grant_hash, initiator_verification_receipt_id and responder_verification_receipt_id. B checks these against its own stored evidence, records the same decision and acknowledges. Late retries with changed grants or revisions conflict; identical retries resume the decision.

Only after that shared decision may an inbound registration become active for its approved business scopes. Each business sender remains disabled until it has observed the peer's active acknowledgement for that same commit_id/revision. Completion acknowledgements and authenticated status queries converge after response loss. Until both directions confirm, show Completing setup with separate directional status. A cross-database status update cannot be atomic: guarantee no business scope during partial credential exchange/failed verification and no business delivery before both directions are prepared, verified, committed and active acknowledgement is observed. Do not claim stronger distributed atomicity.

A is the durable decision coordinator for this attempt. Its decision is initially undecided and can transition atomically once to commit or abort. A cannot abort after recording commit_decided; a later administrator cancellation uses revoke. A recorded commit must not be discarded as an unused expired attempt. Critically, B may reach its local expiry after A records commit but before B receives it: once B is prepared/verified, it must retain an in_doubt, control-only record and reconcile A's durable decision. It must not unilaterally revoke/delete the prepared registration merely because the bootstrap clock elapsed. A likewise keeps its durable decision and needed permanent inbound/outbound state. Finite escrow can still be purged on schedule because decision recovery uses permanent protected credentials/control authority, not the temporary secret response. A receiver can accept a late matching commit for its retained prepared/in_doubt record, with the original verified grants/receipts; this does not reopen an expired new-bootstrap attempt.

Before any prepared participant can exist, an unprepared/never-exchanged expired attempt may be atomically aborted and cleaned up. Once A has dispatched an exchange whose outcome is unknown, it must assume B might have prepared it; a missing response is not proof that no participant exists. For prepared/verified/in_doubt attempts, B requests A's decision or requests abort; A records abort only if its decision is still undecided and returns that durable result. Cleanup then follows the confirmed abort. If A already decided commit, B completes that decision; if A is unreachable, keep the control-only in_doubt record and show recovery pending. Bootstrap expiry does not disable the narrowly bound control-token recovery of a retained in_doubt/decided record. Never let a half-prepared pair start business work, and never silently switch a commit into abort. Use database uniqueness/concurrency constraints and the existing durable work mechanism for this narrow state machine; do not introduce a generic distributed-transaction platform.

## Frozen lifecycle operation matrix

All paths below are relative to the verified service_link_endpoint. After bootstrap review/exchange, use Authorization: Bearer with a locally issued, link-bound service token for the stated scope. Do not reuse browser_state, a human session token or an arbitrary account API token as control authentication. Local administrator actions keep their ordinary product authorization/anti-forgery boundary and enqueue these peer operations; they are not anonymously callable peer APIs.

Every lifecycle POST carries contract, operation_id, link_id, link_revision and grant_hash; initial activation posts also carry attempt_id. operation_id is an unpredictable stable retry identifier. The path/body link_id must agree. Persist the semantic request fingerprint and operation outcome: identical retries return the same logical result, changed bodies under the same operation_id return 409 after authorization, and unrelated/foreign authority learns no operation or receipt existence. Successful idempotent lifecycle responses use 200; an accepted queued rotation request may use 202 with the same rotation_id/status location. All responses contain contract, link_id, link_revision, grant_hash and lifecycle_state and use no-store. Mutations never redirect.

| Method and relative route | Required scope and binding | Additional request/response fields |
| --- | --- | --- |
| POST /links/{link_id}/verify | bostec.service-link.verify; exact approved pending/active principal and direction | Request: attempt_id, direction_id, credential_revision, rotation_id or null. Response: verification_receipt_id, caller_instance_id, caller_tenant_id, target_instance_id, target_tenant_id, credential_revision, rotation_id or null, verified_at. Receipt is durable and tied to the authenticated caller and, during rotation, the exact successor. |
| GET /links/{link_id}/status | bostec.service-link.control; same approved link, including in_doubt recovery | Response: coordinator_instance_id, attempt_id, decision (undecided/commit/abort), commit_id or null, abort_id or null, descriptor_hash, local_inbound_ready, local_outbound_persisted, local_inbound_active, local_business_sender_enabled, peer_active_acknowledged, initiator_verification_receipt_id or null, responder_verification_receipt_id or null, rotations (non-secret summaries). No credential material or unrelated tenant information. |
| POST /links/{link_id}/ack | bostec.service-link.control; same attempt/link and current phase | Request: ack_phase (prepared/active/revoked), peer_operation_id, commit_id or null, exchange_response_hash or null, revocation_id or null. prepared attests that the caller's outbound handoff was durably saved; active requires matching commit_id and observed peer active state; revoked confirms local business access and sender disabled for that revocation. Response: acknowledged_phase and durable acknowledgement_id. A sender cannot fabricate the peer's own local readiness/receipt. |
| POST /links/{link_id}/commit | bostec.service-link.control; A coordinator only; B must have matching prepared/verified evidence | Request: commit_id, descriptor_hash, initiator_verification_receipt_id, responder_verification_receipt_id. Response: decision=commit, commit_id and local_inbound_active. Repeat/status/active ack recovers response loss. No changed/foreign grant can be committed. |
| POST /links/{link_id}/abort | bostec.service-link.control; only this attempt, or an authorized local coordinator action | Request: abort_phase (request/decision), abort_id, reason_code. B sends request to A; A atomically returns/records decision=abort with abort_id only if undecided, otherwise returns its existing commit decision/commit_id. Only A may deliver abort_phase=decision to B. B never converts commit to abort. Response includes the durable decision identifiers. |
| POST /links/{link_id}/revoke | bostec.service-link.control; peer of this existing link, or authorized local action | Request: revocation_id, expected_link_revision, reason_code. Receiver atomically disables its bound inbound business access/outbound sender, preserves tombstones, records/acknowledges revocation and prevents new business tokens. Response: revocation_id, local_business_revoked=true, local_sender_disabled=true. No ability to revoke another link/tenant. |
| POST /links/{link_id}/rotate | bostec.service-link.control; active link and role/direction checks in the rotation section | Request: rotation_id, rotation_phase, direction_id, expected_current_credential_revision and phase-specific fields below. Response: rotation_id, rotation_state, current_credential_revision, successor_credential_revision or null; never echoes a secret except the authorized offer delivery body. |

The persisted status/decision record is authoritative; a 200, a token grant or an acknowledgement by itself does not imply both directions are active. The prepared acknowledgement and verification receipts must exist before coordinator commit. A control token cannot synthesize someone else's receipt or alter current local state beyond that endpoint's checked transition.

For lost abort/revoke responses, retain a minimal terminal acknowledgement record and a finite, explicitly configured control-only recovery permission for the bound principal if automated confirmation requires it. It may reveal/acknowledge only that link's terminal decision and cannot verify new credentials, rotate, reopen, receive business tokens or call business APIs. Do not renew this terminal permission forever. After it expires, finish ambiguous remote confirmation through authenticated administrator recovery; do not falsely infer peer deletion from 401/timeout. Business revocation is immediate independently of this narrowly documented confirmation channel.

## Business scopes, callback continuity and receiver capability

The two directional identities may each have multiple separately approved operations, but no generic global administrator grant. Keep incident creation/receipt access, orchestration operations and callback submission as distinct permissions.

For new RatelDesk managed clients, freeze and enforce these narrow scope names with the corresponding route contracts:

- rateldesk.incidents.create: approved-source incident creation on existing POST /api/v1/incidents/; current organization/customer and other business checks still apply.
- rateldesk.incident-receipts.read: source-bound capability/receipt reconciliation from RatelDesk #116, including GET /api/v1/integrations/netratel/capabilities and GET /api/v1/integrations/netratel/incident-receipts/{key} under the exact frozen receiver contract.
- rateldesk.incident-targets.read: the read-only POST /api/v1/integrations/netratel/targets/validate contract from #116, restricted to validating the caller's approved source/organization/customer/resource tuple; it creates no incident, source, organization or customer and discloses no foreign target data.
- rateldesk.orchestration.callback: POST /api/v1/orchestration/provider/callback and GET /api/v1/orchestration/provider/m2m/ping. Validate expected task/request correlation and the approved integration/tenant ownership; possession of this scope cannot update another tenant's arbitrary task.

The applicable receiver contract remains rateldesk.incident-create.v1 from the separate #116 work. Pairing creates or binds its stable approved source registry entry through authorized local operations. It must not invent a parallel receipt namespace or bypass the registry with a source header. Authentication/link success does not prove idempotency support. Automatic NetRatel incident dispatch stays disabled until the deployed receiver reports the exact verified contract, source identity, target validation and retention/reconciliation guarantees.

NetRatel-to-RatelDesk callbacks on a newly paired installation use a RatelDesk-issued service access token through the newly authorized callback policy. Preserve the legacy OrchestrationM2M acceptance path for configured existing integrations. A fresh paired link must not display fully working orchestration while the callback path still depends on undeclared legacy environment secrets. Test a real existing request/task callback with the new pair as well as the legacy upgrade case.

Freeze NetRatel's new managed scope names now so the RatelDesk-first implementation/release has a stable profile: netratel.orchestration.read permits the existing orchestration health/ping/catalog read operations; netratel.orchestration.invoke permits the existing orchestration ingest operation. Implement those exact scope strings and map them to the inspected current routes. Both scopes remain restricted by the approved tenant/resource/request tuples and current runtime authorization, with no cross-tenant catalog disclosure or unrestricted ingest. Publish the concrete route mapping in permission_profiles/shared fixtures before release; the names and semantics above are agreed, not deferred to a later NetRatel-only decision. Preserve existing netratel.api scope/audience behavior for legacy deployment-configured clients; do not silently give every new managed client its entire API. Never bypass current FlowExecutionAuthority or make a service-client sub/client_id into a local user foreign key. The issuer must grant only a requested subset of the registration's approved scopes; reject unsupported/escalating scope requests, including mixed valid+invalid requests.

The reviewed concrete NetRatel route map, relative to its canonical API base, is:
- netratel.orchestration.read: GET /internal/health, GET /api/v1/system/m2m/ping, GET /internal/catalog/jobs, GET /internal/catalog/tenants and GET /internal/catalog/request-definitions. Health is an inert observation; a public health response alone cannot prove managed authentication. Catalog results must be filtered to the approved tenant/resource boundary, with no unrelated tenant enumeration.
- netratel.orchestration.invoke: POST /internal/ingest, restricted to the approved existing request/task operations and current resource authority.
- POST /connect/token remains the OAuth token grant endpoint, not a business operation authorized by those scopes.
These paths are recorded in the reviewed RatelDesk docs/integrations/netratel-orchestrator.md. Confirm their current NetRatel endpoint bindings while implementing and preserve documented compatibility. If a genuine route conflict requires an adjustment, synchronize the common profile and both consumers before publication; do not publish incompatible guesses.

## Environment/UI configuration and effective authority

Keep typed Options binding for deployment values, including existing M2M/M2MClients keys and NetRatel's documented client-ID hyphen/underscore compatibility. Introduce a durable tenant-aware service registry and protected outbound connection profiles behind one common effective resolver. Both token issuance and API authorization must consult the same current grant authority.

Preserve each product's inspected existing environment/persisted-profile precedence and inheritance semantics; explicitly document and test them rather than imposing a different universal precedence. Show the effective source in the UI. Deployment-managed clients are identifiable/read-only; a deliberate separate managed profile can be selected where the product supports it. A managed client may not shadow a deployment client ID, including canonical aliases. Do not splice a client ID from one source with a secret, issuer or tenant from another. Do not write container environment files from the web UI, dump environment secrets, or require an API restart for an ordinary managed-client creation.

Keep global issuer, signing-key, allowed-network and deployment authority settings separate from account-level credential creation. Changes to a managed connection use optimistic revision checks and one validated immutable effective snapshot per operation. Cache keys/invalidation include profile/source, local/peer tenant, source identity, peer instance, issuer, audience, scope set, connection revision and credential revision. Replicas must observe create, update, disabled state and revocation consistently. IOptionsMonitor alone does not watch a database or refresh a captured authorization allowlist; implement actual invalidation/version checking through existing product mechanisms.

Managed clients are tenant-owned service identities with explicit lifecycle policy, not human-account access tokens. An approving actor is audit provenance, never automatic authority to act as that person or as global admin. Apply the product's documented live tenant/resource/service-principal rules without accidentally changing current account-owned bearer-token semantics. Service-client changes do not confer additional flow-owner authority.

Persist/reuse the product's protected-secret/key infrastructure and purpose-bind protected data to product, tenant, link/client, direction and revision. Persist encryption/signing keys across supported container and replica lifecycles. Prevent ciphertext substitution between profiles. Never display a saved outbound secret, browser-export tokens, log token endpoint bodies, or store runtime secrets in public metadata/audit. Manual service-client creation may show a newly generated secret once; list/read APIs return masked metadata and lifecycle information only.

## Rotation, disable, unlink and once-off operation

Once-off setup means ordinary runtime and policy-driven credential renewal do not ask administrators to repeat the linking ceremony. Implement manual Rotate and a configurable coordinated automatic rotation policy using existing scheduling/background infrastructure, with a safe default policy documented by the products. Expose the selected policy/status and preserve deployment-managed credential handling; do not invent an unbounded nightly release/test loop.

Rotate each direction independently under the existing approved link's control grant. Preserve link_id, logical client_id, scope/tenant/source mapping, source_namespace_id and receipt identity. The issuer for the direction being rotated is that rotation's coordinator. Either administrator can request rotation; automated policy uses the same operations. The other directional credential remains independent and authenticates issuer-to-caller control delivery. Route all peer phases through POST /links/{link_id}/rotate from the matrix; expose progress through GET /links/{link_id}/status.rotations. A manual one-direction credential without a reciprocal link uses the existing explicitly manual credential workflow, not an invented absent reverse channel.

A secret-only rotation changes credential_revision and token-cache identity, never semantic link_revision, grant_hash, connector target/source mapping, pinned flow version or queued incident action identity. Existing queued actions retain their stable idempotency key and can resolve the current authorized credential at dispatch/reconciliation without being invalidated by rotation. A change to endpoint/issuer, tenant/resource constraints or scope is a distinct approved semantic revision; do not disguise it as secret rotation.

Every rotate request has the common lifecycle fields plus rotation_id, rotation_phase, direction_id, expected_current_credential_revision and successor_credential_revision (null only for request). Rotation IDs and phase operation_ids are durable and stable on retries. The issuer assigns one unique strictly higher successor revision and prevents competing unfinished rotations of that same direction/current revision. The caller/issuer role and the token's bound peer/direction are checked at every phase; the reverse control identity cannot offer credentials for an unrelated tenant/link.

Freeze these rotation_phase values, fields and transitions:

| Phase and recipient | Additional fields | Required transition/result |
| --- | --- | --- |
| request, sent to the directional issuer | requested_by_instance_id; requested_policy_revision or null | Under the active link's existing control grant, ask for the same approved direction to rotate. The issuer persists one rotation or returns the existing one for an identical request. It does not disclose a secret in this response. Response rotation_state=requested or offered and the assigned revision when available. A local issuer administrator/policy enters the identical durable state directly. |
| offer, issuer sends to the caller | offer_expires_at, credential_for_caller | credential_for_caller has the same field schema as a bootstrap directional credential, the existing logical client_id, the successor credential_revision and exactly the already-approved endpoints/audience/scopes/tenant bindings. Issuer stores the successor secret verifier/hash, a finite protected offer escrow and pending successor status. Caller atomically saves the protected candidate and a probe work item and returns rotation_state=prepared. Offer retries with identical IDs/body reuse the same secret/revision; changed bodies conflict. Runtime secrets appear only in this authenticated backchannel offer, never status. |
| verified, caller sends to issuer | successor_verification_receipt_id | Caller obtained a verify/control-only token using the successor and called the common verify operation with this rotation_id. Issuer validates its own stored successor receipt, durably records activate_decision_id and makes the successor available for the unchanged approved business scopes. Response rotation_state=activated, activate_decision_id. Until activation is observed, caller keeps the working predecessor selected. Candidate control authority permits only completion/status/abort of its own pending rotation, not requesting another rotation or broadening grants. |
| switched, caller sends to issuer | activate_decision_id, successor_verification_receipt_id, caller_switch_revision | After observing the issuer's activated decision through response or status, caller atomically selects its already-persisted successor and invalidates token caches, then sends switched authenticated with the successor's control token. Issuer checks that exact revision/decision, records successor-in-use acknowledgement and fixes predecessor_retire_at within the agreed bounded overlap policy. Response rotation_state=retiring or completed and predecessor_retire_at. Lost responses do not undo caller's durable switch. |
| abort, sent to issuer before an activation decision | reason_code | Issuer may atomically abort an undecided/unactivated successor, revoke it, notify/reflect aborted state and purge the offer escrow while leaving predecessor configuration unchanged. After activate_decision_id exists, return the existing activated decision and recover it; never silently abort a successor the caller may already use. A subsequent change uses a fresh rotation or explicit link revocation. |

Rotation status entries contain rotation_id, direction_id, rotation_state (requested/offered/prepared/activated/retiring/completed/aborted/recovery_pending), expected_current_credential_revision, successor_credential_revision or null, offer_expires_at or null, successor_verification_receipt_id or null, activate_decision_id or null, caller_switch_revision or null and predecessor_retire_at or null. They contain no secret material. The issuer's decision and the caller's durable switch can be recovered from these records under the bounded control policy. The authenticated caller cannot overwrite issuer activation state merely by supplying a status value.

Use configurable credential maximum ages and overlap with validated bounds and deployment overrides, not production-specific calendars. Once caller switch is acknowledged, predecessor overlap has a fixed finite retirement deadline; never extend it on every retry. Before that acknowledgement, keep the working predecessor within its original configured lifetime and surface recovery_pending instead of revoking it merely because the peer is unavailable. Do not silently extend the predecessor's original hard expiry. If an offer expires before activation, abort the unused successor through the durable decision rule and retain the unchanged predecessor; after activation is decided, purge escrow on schedule but retain permanent credentials/decision state and reconcile the caller switch. Non-disruptive normal rotation must not rely on administrator action, a bootstrap secret that has already expired, or an unbounded overlap. Verify live traffic through manual and policy-driven rotation, including response loss and restart at offer, verification, activation, switch and retirement.

Unlink immediately disables local outbound workers and local inbound business authorization, invalidates caches, preserves durable source/receipt tombstones, and records a durable peer-revocation operation. Attempt the peer unlink under the existing bound control identity; show separate local and remote completion if the peer is unavailable. The peer operation is idempotent and cannot revoke another link. Never restore local access because a remote acknowledgement is missing. Handle lost successful unlink responses and revoked-credential retries explicitly; do not claim remote deletion merely from an ambiguous timeout/401. Allow recovery through the product's authenticated administrator interface.

Stopping token issuance alone does not invalidate previously issued JWTs. Managed-service business policies must observe current registration/link revocation state or an equivalent enforced token-revocation epoch; verify the selected behavior on cached tokens and replicas. Secret rotation may use the documented short-lived token/overlap policy, but link revocation must stop business access as specified. Reconnect cannot erase incident duplicate protection while the same source identity remains usable.

## Required shared acceptance evidence

1. Exact metadata/descriptor/grant/exchange/receipt/commit JSON fixtures interoperate in both initiator roles, including distinct Web/API/issuer/gateway configuration.
2. Discovery alone and initial Continue create no business authority. B-selected tenant/grant is shown to and approved by A before handoff; narrowing is accepted only through the same immutable summary and expansion is rejected.
3. Local and remote roles and tenant/resource limits are checked independently. Ordinary account credential permissions cannot create an administrative service client. Flow authority remains unchanged.
4. Wrong browser_state, verifier, pairing_code, issuer, callback, instance, tenant, source, descriptor_hash, grant_hash and expired/cross-attempt proof fail without disclosure or side effects.
5. No anonymous arbitrary-URI fetching; origin descriptor substitution, redirect, DNS-rebinding, IPv4/IPv6 and unsupported endpoint cases follow the existing safe-client policy. Supported private deployments still work.
6. Decline/unprepared expiry removes unused authority and escrow; prepared/verified expiry enters control-only in_doubt recovery instead of deleting a possible committed participant. No business scope can be obtained through a broad legacy caller policy while pending/in_doubt.
7. Both directions perform real OAuth token acquisition and authenticated read-only verification. A configuration-only mock is insufficient.
8. Identical review/exchange/commit retries preserve IDs, credentials and state; changed semantic bodies conflict. Canonical JSON-order equivalence and shared hash vectors are tested.
9. Crash/restart and response-loss injection after each database commit, handoff, probe and activation step recover the same attempt. Lost exchange response recovers the same escrowed secret while inbound storage remains hash-only. Exercise A committing durably immediately before B's setup expiry and withholding the commit message until afterward; B must retain/reconcile the decision, never discard it or claim a false completed/aborted pair.
10. Replicas/concurrent attempts cannot create duplicate active links, overwrite unrelated profiles, bypass consent or enable a half-prepared pair. Committed recovery works after escrow is no longer needed.
11. Runtime secrets/verifiers/codes/browser_state stay out of public descriptors, browser storage, URLs other than the explicitly bounded correlation/code roundtrip, normal read APIs, logs, audits and telemetry. Escrow lifetime and purge are proven.
12. UX-created service clients work immediately through both token validation and actual API authorization; disable/revoke is effective on cached tokens and replicas. Legacy env clients/aliases and account bearer credentials remain compatible.
13. Existing environment/persisted override precedence, explicit source selection, unset/empty values, alias collisions and complete-profile validation are covered. No mixed-source credential can be constructed accidentally.
14. The exact NetRatel flow source ID is registered at RatelDesk; foreign headers and changed credentials cannot hijack a source namespace. Rotation/relink preserves authorized receipt reconciliation.
15. The new RatelDesk-issued token works for incident create/capability/receipt/target-validation operations with current resource checks, and for the actual orchestration callback route with task correlation. Legacy callback setups remain functional. New NetRatel managed verify/control/read/invoke scopes work under the correct audience without adding unrestricted legacy netratel.api scope; both new narrow scopes and unchanged legacy environment behavior are covered.
16. Link readiness and rateldesk.incident-create.v1 delivery readiness are separate. No fake capability, version string or successful token endpoint call enables automatic delivery.
17. Manual and policy-driven automatic rotation maintain live traffic and stable identities, handle lost responses/restarts, and revoke the predecessor only after successor verification/acknowledgement within bounded policy.
18. Local disable/unlink stops work promptly, remote outage remains a truthful pending status, idempotent peer revocation cannot affect another link, and receipt/source tombstones survive.
19. Credential/control scopes cannot change the approved issuer, tenant, source, business scopes or resource boundaries. Issuer-signed service identity is never an automatic user/admin grant.
20. Relevant existing Local/OIDC/Hybrid login, current installers/native Akka gateway, rdk_ account credentials and orchestrator integration behavior remain covered without expanding to new unrelated CI platforms.

Use meaningful focused unit/integration/browser checks plus the repositories' established required CI. Capture cross-repository interoperability against actual compatible builds before NetRatel beta release. Do not claim deployed support from unmerged code or a passed configuration mock.

## Standards and framework references

- OAuth client_credentials: https://www.rfc-editor.org/rfc/rfc6749.html#section-4.4
- OAuth security BCP (redirect/PKCE/issuer/client-auth guidance): https://www.rfc-editor.org/rfc/rfc9700.html
- PKCE S256 construction: https://www.rfc-editor.org/rfc/rfc7636.html
- Issuer identification: https://www.rfc-editor.org/rfc/rfc9207.html
- Authorization server metadata: https://www.rfc-editor.org/rfc/rfc8414.html
- Protected resource metadata: https://www.rfc-editor.org/rfc/rfc9728.html
- Dynamic registration scope/initial authorization limits: https://www.rfc-editor.org/rfc/rfc7591.html
- Revocation implications for self-contained tokens: https://www.rfc-editor.org/rfc/rfc7009.html#section-3
- Options: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options?view=aspnetcore-10.0
- Configuration precedence: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-10.0
- Data Protection persistence: https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0
- Safe outbound URL handling: https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html
- JSON canonicalization used by the product hash routine: https://www.rfc-editor.org/rfc/rfc8785.html

The custom pairing/replay/reciprocal-commit contract above is product design. Do not label it a standardized OAuth reciprocal-registration grant or use the standards references to claim unimplemented features.

END APPENDIX B
