# RatelDesk beta.11 laptop acceptance campaign

Run this campaign on the operator's laptop after the owner deploys
**0.1.2-beta.11**. Use the published release manifest and the checkout at its exact
source revision. This is an execution and evidence handoff for the released
product. Keep implementation work, pull requests and release publication outside
this laptop campaign.

The scope is **390 released operations, 103 domain cases, 86 historical obligations
and nine cleanup assertions**. Beta.11 retains every beta.10 operation and adds
`helpdesk_email_settings.delete_rule`. The authoritative schemas are the released
[catalog](../contracts/mcp-operation-catalog.json); the substantive case assertions
are the immutable [campaign contract](../../tools/ci/beta8-campaign-contract.json).
The [beta.11 validator](../../tools/ci/validate-beta11-campaign.py) reuses that
contract with current release identity, exact operation preservation, deliberate
HTTP body-limit responses and stronger mail/approval effect accounting.

## 1. Open a fresh private workspace and preserve the existing profile

Use the exact existing RatelDesk owner HTTP-MCP configuration. Resolve its explicit
resource URI and approved credential reference locally; do not discover credentials
from unrelated CLI, browser, NetRatel or deployment profiles. Keep the credential
in a separate protected file and use the [read-harness profile format](campaign-testing.md#run-selected-live-read-checks).
Use a separate ordinary scoped actor, designated approver, and customer actor as
required by each case. Preserve previous beta evidence as read-only history.

From PowerShell in the checkout at the released source revision, using Python
3.11 or later:

```powershell
$Repo = (Resolve-Path .).Path
$Catalog = Join-Path $Repo "docs\contracts\mcp-operation-catalog.json"
$CampaignParent = Join-Path $env:USERPROFILE "Documents\RatelDesk-Campaigns"
New-Item -ItemType Directory -Force $CampaignParent | Out-Null
$Campaign = Join-Path $CampaignParent ("beta11-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
py -3 tools/ci/validate-beta11-campaign.py init --catalog $Catalog --output $Campaign
py -3 -m venv (Join-Path $Campaign "python")
$Python = Join-Path $Campaign "python\Scripts\python.exe"
& $Python -m pip install -r tests/http_mcp/requirements.txt
```

`init` refuses an existing directory and creates `receipt.json`,
`prerequisites.json` and `fixture-registry.json`. All operations start `NOT_RUN`.
Set the new campaign ID and declared latency budget before execution. Copy the
independently downloaded manifest into `release/release-manifest.json` below the
campaign directory and retain its published checksum. Verify Windows x64 CLI and
stdio archives, their fresh-process version/source output, and all three deployed
image digests. Record the observed authentication mode and live `tools/list` and
capabilities against the exact released catalog. Stop a version mismatch lane
until the intended deployment is on the published identity; keep other completed
evidence intact.

The working schema revisions are `2026-10-25.beta11-contracts-v25` and 37 tools.
Use the actual released catalog and schema hashes; never copy beta.9/beta.10 hashes
into the beta.11 receipt.

## 2. Resolve prerequisites before the broad mutation campaign

Complete every row in `prerequisites.json` with fresh bounded observations, hashed
evidence references and typed assertions. The checklist does not issue credentials,
grant permissions, start a worker, send mail or create a provider job. Use the
supported product workflows to prepare the exact owned fixtures and then record
their verified state.

| Prerequisite | Required observation |
| --- | --- |
| Release and authority | Exact source/version/digests/catalog, explicit current owner grant, ordinary actor remains scoped, independent API/storage read access. |
| Baseline | Current settings, selected unrelated-record baseline, fixture registry, mail queue and provider job baseline. |
| Mail | Owner-reviewed isolation/disposition of unrelated due work before global-sender activation; a ready effective **global** sender, owned captured recipient distinct from the sender, and approved independent storage/provider readback. Pausing incoming mail does not pause outbound dispatch. A mailbox `send_test` receipt alone does not establish direct-send readiness. |
| NetRatel | Exact paired tenant and online target, independently read current target profile/revision, durable tenant visibility and admitted bounded policy, healthy protected M2M identity. Authentication alone is insufficient. Use current credentials and expiry observations. |
| NetClaw | Actual running daemon identity, saved pairing authenticates, real configured provider can produce a durable reply. Retain a corrected working endpoint instead of restoring an obsolete address. |
| Graph dataset | Approved configured Graph fixture with the permissions required for the actual dataset sync case. |
| Customer authentication | Local account/reset fixtures in Local mode; authorized external-provider fixtures in Authentik mode. Use the fixed Local guard/equivalent rules below. |
| Callback transport fixture | Owner-supplied receiver/ingress controls limited to the owned correlation and phase, preserving the approved paired origin, callback path and authentication. Current job authority does not supply a callback fault fixture. |

```powershell
& $Python tools/ci/validate-beta11-campaign.py check-prerequisites --catalog $Catalog --prerequisites (Join-Path $Campaign "prerequisites.json") --evidence-root $Campaign
```

Each `READY` prerequisite needs all its named assertions, real evidence and
`expected: true`. Keep missing resources as `BLOCKED_PREREQUISITE`, with exact
affected cases and the next action. Continue independent lanes while resolving
them. Do not manufacture an owner policy by broadening a credential or treating a
browser administrator session as native MCP delegation admission.

For **Local** authentication, the five external-identity operations retain their
documented guard plus positive local-equivalent requirements. They are not passed
by inventing an Authentik account:

| External operation | Required positive Local equivalent |
| --- | --- |
| `provision_user` | `create_local_account` |
| `customer_invite` | `create_local_account` or `reset_customer_login` |
| `customer_resend_invite` | `reset_local_account` or `reset_customer_login` |
| `customer_disable_login` | `disable_local_account` |
| `customer_sync_authentik` | `auth_status` or `reset_customer_login` |

The exact qualified IDs and historical mapping rules remain in the validator.
HTTP `helpdesk_config.set` and `.unset` are separately expected restrictions. The
other operations retain their positive requirements, including Graph and native
AI/provider effects. Preserve earlier blockers as historical observations.

### Prepare direct-mail delivery on the intended target

The outbound dispatcher runs independently of the incoming mailbox worker and
processes the shared due queue automatically. Incoming pause is not an outbound
hold, and there is no supported public "dispatch only this outbox ID" command.
Before enabling or changing the global sender, have the owner establish an
approved quiet window or isolation and disposition of unrelated due/in-flight
work. Preserve the intended target and sender baseline; do not create isolation
by cancelling unrelated deliveries or redirecting their sender. If these
conditions are unavailable, keep the delivery lane blocked and continue ready
independent work.

Send the one owned direct-mail fixture through the normal confirmed operation,
then observe ordinary dispatch. Its queued response does not contain the outbox
ID. Require approved read-only storage and provider capture access to find the
exact effect using its unique subject, recipient and sender, then prove its
delivery. [Pending Emails](../integrations/mailbox-ingestion.md#pending-delivery-queue)
actions address ticket/timeline deliveries; do not pass a ticketless direct
outbox ID to those actions. Preserve a historical `NeedsReview` direct effect
with its recorded uncertainty and operator disposition; do not invent a retry,
edit its database state or infer nondelivery from its continued presence.

### Resolve exact NetRatel reviewed admission

The bounded auxiliary credential retained by the preceding campaign expired on
**25 October 2026 at 08:00 UTC (10:00 SAST)**. Record its current lifecycle; prepare
an approved current auxiliary credential through the protected client flow. Keep
its selected grants, canonical resource, instance/environment, exact expiry and
consumer profile explicit. A replacement does not change the primary consumer.

Use the existing reviewed-access workflow in the pinned NetRatel
`v0.1.1-beta.48` source: `docs/mcp-http/setup.md` ("Reviewed client access and
exact expiry"), `docs/mcp-http/local-credential-mode.md` ("Reviewed admission")
and `docs/mcp-http/authentication.md`.
Beta.48 supplies the definition-retirement/history repair; it does not introduce
a bypass for these existing authority requirements.

1. Inspect the current caller-owned credential with `netratel_access/credential`.
   Record configured grants, exact UTC expiry, canonical resource and current
   durable owner permissions separately from the scope shown for a status call.
2. Resolve the exact persisted tenant/agent from the retained mapping and verify
   that agent is online. Read its current server-owned target profile using an
   already authorized owner: `netratel_access/target` and `/effective` accept
   `{tenantId, agentId}`. The authorized policy-administration account read
   `GET /api/v2/mcp-operator/targets/{tenantId}/{agentId}` also exposes the persisted
   profile and its `version`. Use that current version as `profileRevision`.
   Missing discovery authority remains a precise prerequisite; it is not a reason
   to add unrelated grants to the disposable job credential or omit the revision.
3. Inspect the exact desired operations through
   `POST /api/v2/account/integration-credentials/{credentialId}/admission` with
   `targets` containing `tenantId`, `tool`, `operation`, `agentId` and
   `profileRevision`. This owner-account call is read-only and does not install a
   policy. Do not infer jobs admission from the default flow-read samples.
4. When reconciliation is needed, prepare one durable request UUID with the exact
   selected grants, instance permissions, future reviewed expiry and operation
   targets. Preserve the returned request receipt and proposal hash. A new
   credential's creation starts with the protected setup client (`access prepare`
   or the validated Windows DPAPI wrapper) so its secret stays in protected
   one-time delivery, rather than model-visible tool output.
5. An existing authorized owner with current `mcp.policy.admin`, the required MCP
   administrator delegation and every selected durable permission may call
   `netratel_access` with `operation: owner_apply` and
   `request: {requestId, proposalHash, confirm: true}`. The hash is the exact
   64-character value in that pending receipt. This produces `owner_direct`;
   it does not add administrator authority to its caller. Otherwise use the
   supported authorized browser review of that same prepared request. Protected
   polling/resume completes creation and stores the new credential in its own
   profile. Reconcile an interrupted outcome using the same request ID before
   retrying; changed targets, bounds or expiry require a new proposal.
6. Refresh the exact admission, then perform permitted native reads on the
   intended tenant/agent and inspect runtime readiness before owned execution.
   Current authentication, tenant visibility, policy admission and online runtime
   are separate observations. Preserve unrelated-tenant denial.

The **Admin → MCP Policies** lifecycle UI is a different route: its operator needs
an independently assigned active bounded parent policy allowing
`PolicyAdministration` for the exact environment/tenant/target. It cannot create
its own authorizing parent. An absent parent in that lifecycle UI does not mean
the already-authorized owner's reviewed `owner_apply` path should be replaced by
repeated lifecycle previews. Use the applicable supported route with its actual
prerequisites and retain the receipt's approval mode.

## 3. Build current owned fixtures and acquire selectors independently

Record each fixture's campaign marker, type, exact ID, organization, creation
request/response, independent readback, dependencies and final disposition in
`fixture-registry.json`. Record baseline settings and exact restoration values
before changing them. A retained fixture is reusable only after current identity,
scope and lifecycle readback proves it is suitable for this case.

Use a unique marker and current list/search to acquire each selector. The
[discovery plan](../../tests/http_mcp/read-plan-discovery.example.json) resolves
`${ownedIncident.id}` only from same-run MCP and independent API reads after
count/marker assertions. Copy it into the private workspace, replace the unique
subject marker, then run:

```powershell
& $Python tests/http_mcp/campaign_reads.py --catalog $Catalog --plan (Join-Path $Campaign "read-plan.json") --profile (Join-Path $Campaign "profile.json") --output (Join-Path $Campaign "reads-001")
```

Keep the fixture conditions precise:

- Assignments need an enabled technician with exact support-group membership and
  customer-organization coverage. For caller-bound self-assignment, use a fixture
  in the authenticated actor's covered organization and test incident, request
  and change separately.
- Detail edits and assignment on changes need a Draft when that lifecycle requires
  it. A complete Normal change may already be approved and locked.
- Positive single and bulk retries need eligible **failed automation tasks**.
  Failed manual tasks are not retry fixtures. Preserve the first terminal attempt
  and require distinct execution IDs after retry.
- Designated approvers must be distinct real observed actors. Administrative
  ownership does not imply authority to impersonate an approver.
- Archive and soft-delete contracts need inactive/archived state readback. Where
  direct GET is unsupported, use the supported filtered list to prove absence.

For sequential incident reads, an explicit `slaCountdownToleranceSeconds` of at
most five permits only the two live SLA counters described in the canonical
guide. All raw captures and stable fields remain exact. Provider definitions use
the published `requestDefinitionName` field; inspect schemas before evaluating
an envelope or property name.

## 4. Execute and reconcile each lane as it completes

Run the historical repaired notification/list/timeline lanes first, then complete
the full catalog and domain workflows using the dependency registry. For every
applicable write capture unchanged preview, one confirmed MCP request, independent
durable effect and the ordinary-denial check where required. A preview, HTTP 200,
empty list, denial, `updated:0` or an unchanged row cannot replace a positive
business effect. API fixture setup does not count as MCP execution coverage.

Map each completed case into all operation, domain and historical requirements
that its actual assertions prove. Reuse one valid case across explicit mappings
instead of replaying the same destructive fixture to increase a count. References
in the final receipt are relative to the campaign root and include SHA-256 hashes;
rebase nested harness references when importing supporting read summaries.

```powershell
& $Python tools/ci/validate-beta11-campaign.py status --catalog $Catalog --receipt (Join-Path $Campaign "receipt.json")
```

`status` lists exact missing mappings and never validates recorded PASS labels.
Keep mapping while working; do not defer all 103 domain and 86 historical
reconciliations until the end.

### HTTP body boundary and duplicate validation

Run against the same released public ingress used by the owner MCP profile:

```powershell
& $Python tests/http_mcp/campaign_transport.py --profile (Join-Path $Campaign "profile.json") --manifest (Join-Path $Campaign "release\release-manifest.json") --evidence-root $Campaign --output (Join-Path $Campaign "transport-boundary")
```

Import the printed `transportBoundary` reference into `receipt.json`. The runner
checks 13 real requests at the default 30,000,000 decoded-byte cap: both routes,
both framing forms, one byte below/at/above, plus the reported chunked regression
size. Below/at must initialize successfully; above must return deliberate HTTP
413 with `code: request_too_large`, `limit: body` and the effective byte cap.
If the deployment intentionally uses a smaller cap, pass `--limit`, retain its
fresh configuration capture in `transportLimitConfiguration`, and test that cap.
Connection resets, proxy HTML and a smaller unexplained streamed boundary fail.

Retain the ~1.1 MB duplicate outer/inner member tests across both routes/framing
and Windows stdio, valid controls, original RPC IDs, structured validation errors
and no owned mutation. Separately check oversized declared-length
`Expect: 100-continue` behavior before upload. Source socket tests are supporting
implementation evidence; capture the deployed ingress results independently.

### Direct mail and approval effect cardinality

Capture before/first/replay effect snapshots from independent storage or the
supported effect API. Each normalized snapshot has this shape, with its actual
query and raw source receipt retained separately:

```json
{
  "observedAt": "2026-10-25T12:00:00Z",
  "scope": {"campaignId": "synthetic-campaign", "correlation": "unique-owned-effect-marker"},
  "effects": [{"id": "observed-effect-id", "effectKey": "observed-durable-effect-key"}]
}
```

Put `effectCardinality` in the corresponding case receipt:

```json
{
  "expectedNewEffects": 1,
  "before": {"path": "effects/before.json", "sha256": "actual-sha256"},
  "first": {"path": "effects/first.json", "sha256": "actual-sha256"},
  "replay": {"path": "effects/replay.json", "sha256": "actual-sha256"}
}
```

For direct `helpdesk_mail.send`, omit `replay`: require exactly one new selected
outbox effect, durable queue/history readback and independently captured provider
delivery to the exact owned recipient. Its postcondition must include
`direct_send_delivery_matches_effect` and both `storage` and `transport_capture`
surfaces. A successful mailbox `send_test` is a separate operation.

For `support.concurrent_approval_decisions`, predeclare the expected first-decision
effect count for the configured fixture. Capture winning decision state, effect
IDs/keys and the same set after replay/concurrency. The validator compares exact
effect sets, not only totals. Resolve every uncertain response by independent
readback before considering another external action. Record an explicit supported
disposition for retained uncertain mail without assuming nondelivery or flushing
unrelated work.

### Foreign notification no-op and new rule deletion

A foreign personal mark-read returning `success:true, updated:0` can prove expected
isolation when exact independent before/after records show no change. It cannot
prove a positive owner write. Such an expected-negative case must retain the actual
confirmed request/response and an `isolationReadback` containing `ownerActor`
(a different observed actor), `before` and `after` capture references. Those captures
contain ordered `observedAt` and the same full `body` notification with its selected
`id`. Require `other_actor_read_marker_unchanged` and both API/storage evidence.
Use a separate owned notification to prove positive mark-read.

For `helpdesk_email_settings.delete_rule`, create an owned narrow disabled rule.
Preview must leave it unchanged; owner confirmation must remove only that rule;
ordinary confirmation must be forbidden. Verify missing-by-ID/list absence and
preservation of existing processing audit logs referencing the deleted rule. This
operation uses `DELETE /api/v1/inbound-email-rules/{ruleId}` and does not require
starting the mail worker or sending a message.

### Paired transport, history and AI recovery

Prove provider success, controlled job failure, single/bulk eligible retry,
duplicate callback, incorrect binding and terminal ordering. Then test an owned
callback's transient HTTP 5xx retry and terminal rejection separately. Capture
exact request/task/binding/correlation, provider run and delivery-attempt state,
RatelDesk attempt state and cardinality. A failed job followed by successful retry
does not substitute for HTTP callback retry evidence.

The HTTP-failure cases require an owner-supplied controlled receiver/ingress
fixture, with documented activation, restoration and sanitized request capture.
It must select the exact owned correlation and callback phase at the approved
paired origin and `/api/v1/orchestration/provider/callback`, preserve normal
authentication/token exchange, and leave other traffic unchanged. The product
does not expose a production fault toggle or arbitrary callback-URL override.
Do not assume the laptop agent can create production ingress controls. Without
this fixture, record the precise prerequisite and keep these cases unpassed.

Use separate owned cases for a planned HTTP 503 followed by normal callback
acceptance and for a terminal HTTP 403. Capture the actual receiver HTTP attempts;
durable delivery-attempt counts are not counts of internal HTTP retries. For a
later-tick recovery assertion, the owner must hold the scoped transient response
through the initial retry window, then restore normal handling. After terminal
rejection, verify the retained rejected state and no further automatic delivery
across ordinary reconciliation. Repair the fixture before explicit replay. An
already admitted Events ControlPlane actor can locate the exact event using
`eventType` and `correlationId`, check `retryTarget: external-service-callback`,
and use `netratel_events/preview_retry` followed by `retry` with the unchanged
`eventId`, `planToken`, `idempotencyKey` and outer `confirm: true`. Job-execution
authority alone does not authorize these event controls. A published notification
is not a delivered callback; require the receiver effect and durable delivery
readback, including no duplicate effect after replay.

After deleting an owned completed provider definition, verify that terminal run
history remains readable through supported provider APIs/UI. Distinguish row
retention from a broken lookup through a deleted definition. Keep source repairs
and current live prerequisites separate. If the companion version changes,
revalidate affected lanes on the final version rather than pooling incompatible
segments into one pass.

For native AI, keep actual conversation, turn, message and interaction IDs; prove
reply, offered response, uncertainty reconcile, stop-waiting and acknowledged
abandon on eligible real states. An idle chat or synthetic webhook save cannot
substitute for provider dispatch and recovery effects.

## 5. Finish functional evidence, then soak, cleanup and validate

Set `functionalCompletedAt` only when applicable operation/domain/history mappings
and the required repair lanes are complete. Run the canonical read-only soak for
900–3,600 seconds after that point, with fresh RPC IDs, 30–10,000 unique captures,
no unobserved interval over 60 seconds, no unexpected failures and p95 within the
budget declared before campaign start. Preserve the exact final release identity.

Complete the nine canonical cleanup assertions after the soak: unrelated records,
temporary credential revocation, stopped temporary jobs, restored mailbox/worker/
sender, preserved terminal history, reconciled uncertain effects, every fixture's
disposition, excluded secrets and restored administration settings. Register
intentional permanent repairs and product soft-delete retention explicitly.

The final receipt's case assertions and evidence surfaces follow the existing
[acceptance contract](../implementation/0.1.2-beta.8-campaign.md). Set `readiness`
to `PASS` only as a candidate for complete validation; the command rejects missing
or inconsistent evidence:

```powershell
& $Python tools/ci/validate-beta11-campaign.py validate --catalog $Catalog --manifest (Join-Path $Campaign "release\release-manifest.json") --receipt (Join-Path $Campaign "receipt.json") --evidence-root $Campaign
```

Return the validator output, a human-readable report, exact residual IDs if any,
operation/domain/history mapping, prerequisite status, fixture disposition and a
curated private evidence archive. Keep the actual measured result. Source tests,
release publication and supporting read sweeps remain separate from deployed
production acceptance.
