# RatelDesk beta.12 laptop acceptance campaign

Run this campaign from the operator's laptop after the owner deploys
`0.1.2-beta.12`. Use its published release manifest, verified native archives and
an exact checkout at the manifest's source revision. The laptop executes the
campaign and records evidence; the deployment, implementation PR and release
remain the release owner's work.

The fixed scope is **37 tools, 390 operations, 103 domain cases, 86 historical
obligations and nine cleanup assertions**. The catalog revision intentionally
remains `2026-10-25.beta11-contracts-v25`: beta.12 changes transport behavior and
campaign preparation, with no operation contract change. Verify the current
catalog and schema hashes. The [beta.12 gate](../../tools/ci/validate-beta12-campaign.py)
retains the [immutable acceptance contract](../implementation/0.1.2-beta.8-campaign.md)
and beta.11 evidence checks.

The prior beta.11 campaign was `NOT_READY`: 324 operations passed, seven fixed
Local/HTTP exclusions applied and 59 operations remained unpassed. It mapped
16/103 domain cases, closed 0/86 historical obligations and had no qualifying
post-functional soak. Retain that history unchanged. Start a fresh beta.12
workspace; no previous result supplies a current positive or an inherited pass.

## 1. Create a private workspace and establish release identity

Use the existing explicitly selected owner HTTP MCP profile and protected
credential. Keep the ordinary, customer and approval actors distinct. Follow the
[profile and credential instructions](campaign-testing.md#run-selected-live-read-checks)
without copying credentials into captures or inventing another credential source.

From the exact release checkout in PowerShell:

```powershell
$Repo = (Resolve-Path .).Path
$Catalog = Join-Path $Repo "docs\contracts\mcp-operation-catalog.json"
$CampaignParent = Join-Path $env:USERPROFILE "Documents\RatelDesk-Campaigns"
New-Item -ItemType Directory -Force $CampaignParent | Out-Null
$Campaign = Join-Path $CampaignParent ("beta12-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
py -3 tools/ci/validate-beta12-campaign.py init --catalog $Catalog --output $Campaign
py -3 -m venv (Join-Path $Campaign "python")
$Python = Join-Path $Campaign "python\Scripts\python.exe"
& $Python -m pip install -r tests/http_mcp/requirements.txt
$Manifest = Join-Path $Campaign "release\release-manifest.json"
$Receipt = Join-Path $Campaign "receipt.json"
$Prerequisites = Join-Path $Campaign "prerequisites.json"
```

`init` refuses an existing directory and writes only fresh `NOT_RUN`/`NOT_CHECKED`
templates. Keep the workspace outside the checkout and apply a user-only Windows
ACL. Download the manifest and checksums into its `release` directory; verify the
Windows CLI and stdio archive checksums and their reported version/source. Record
all three deployed component identities and immutable image digests against the
manifest. An image label or public API version alone cannot establish all three.

Set a unique campaign ID, observed authentication mode and start time. Declare the
p95 latency budget before measurement. Capture owner and ordinary access, live
`tools/list`, capabilities and exact schemas. Resolve any deployment, native tool,
source, catalog or schema mismatch before collecting current case evidence.

## 2. Prepare exact fixture prerequisites

Complete `prerequisites.json` using the same campaign/source context as the case
receipts. A `READY` row requires every named assertion, a fresh observation time,
and an evidence reference with relative `path`, SHA-256, JSON pointer and
`expected: true`. The checker reads existing evidence; fixture preparation uses
supported product operations and the intended authority. A blocked row needs the
exact missing fixture or authority, affected cases and a concrete owner action.
Continue independent lanes while that prerequisite is resolved.

The context contains `campaignId`, `version`, `tag`, `sourceRevision`,
`deploymentSha256`, `catalogFileSha256`, `catalogHash` and `schemaHash`. Each
assertion's hashed capture carries that exact context and an `observedAt` no later
than the row's readiness observation. Keep original raw captures unchanged. If a
normalized observation document is needed for a Boolean pointer, retain its
independent supporting raw captures and explain how the fact was observed.

The [fixed residual fixture map](../../tools/ci/beta12-residual-fixtures.json)
assigns every one of the 59 prior unpassed operation IDs to one of these lanes:

| Prerequisite lane | Operations | Required preparation |
| --- | ---: | --- |
| `mail_recovery` | 15 | Selected mailbox/provider, owned eligible receipt and fence, historical source, controlled failure and uncertainty fixtures, isolated unrelated outbound work. |
| `pairing_lifecycle` | 11 | Disposable peer/source and approved pairing identity, mapping/source cleanup, recorded preservation of the working pair. |
| `native_ai_recovery` | 6 | Actual pending and uncertain native NetClaw sessions, an actual offered interaction and unbound session, recorded working pair. |
| `scheduler_sla` | 6 | Owned due/overdue/escalated/breached cases, eligible failure/retry records and authorized isolated background runtime. |
| `binding_drift` | 5 | Current exact-target profile revision, admitted operations, owned binding/schema drift and eligible input import/synchronization. |
| `events_investigation` | 4 | Intended Events actor authority, owned invocation/report, invocation-bound callback identity and known log/trace records. |
| `tenant_images` | 4 | Observed integer tenant identity, owned images, render/readback and exact asset removal/restoration. |
| `graph_dataset` | 3 | Approved Graph connection, required provider permissions and an owned nonempty dataset with provider readback. |
| `change_review` | 2 | Owned change in an eligible lifecycle state, distinct designated reviewers and independent readback. |
| `support_bootstrap` | 1 | Isolated owned target and an exact defaults baseline/disposition. |
| `online_presence` | 1 | Two actual scoped online actors and independent presence readback. |
| `ticket_customer` | 1 | Owned ticket/customer with a different current customer and operation-specific independent readback available. |

Retain the release identity, explicit owner/ordinary authority, baseline, customer
mode and basic direct-mail/NetClaw prerequisite rows as well. A basic mail send or
chat reply does not establish the advanced recovery, historical import, pairing
or callback fixtures. An empty collection is not an eligible positive fixture.

The `callbacks` row additionally requires owned correlation and phase controls,
authenticated 503 and 403 controls, and a bounded stream snapshot. The
`transport_configuration` row requires independently observed effective Web/MCP
limits and the public ingress route. Job permission does not establish Events
actor authority or fault-control access.

```powershell
& $Python tools/ci/validate-beta12-campaign.py check-prerequisites `
  --catalog $Catalog --manifest $Manifest --receipt $Receipt `
  --prerequisites $Prerequisites --evidence-root $Campaign
```

### Preserve working providers and shared state

Record NetRatel control-plane and agent versions separately. Discover the current
exact-target profile/revision through the authorized access, target, effective
profile and policy-target operations. Verify admission for the exact operations
at that revision. Renew an expired auxiliary grant through the reviewed flow with
its exact bounds, proposal hash and receipt. Authentication, tenant visibility,
policy admission and target online state are separate facts. The
[reviewed-admission procedure](beta11-laptop-campaign.md#resolve-exact-netratel-reviewed-admission)
remains useful; use current companion identities instead of old release pins.

Preserve the working NetClaw endpoint, saved pair and durable provider reply.
Native recovery requires real pending, uncertain, offered-interaction and unbound
session fixtures. Re-pairing and shared settings changes need a separate disposable
fixture. Preserve retained history and record the cleanup disposition.

The outgoing dispatcher is independent of the incoming worker pause. Establish
owner-approved isolation of due and in-flight unrelated mail before provider or
queue changes. Direct send uses the global sender; `send_test` is a separate
operation. Observe the exact outbox effect through independent storage and the
provider/recipient capture. Ticketless direct-send IDs do not become eligible
`pending_actions` fixtures. Retain held uncertainty and follow its supported
resolution; do not invent an eligible retry or erase evidence to clear the queue.

Keep a deliberately disabled shared scheduler disabled. Use authorized isolated
conditions on the intended deployment for background cases. Evidence from another
environment belongs to a separate campaign and cannot fill this receipt. Record
the precise missing owner resource and continue independent work.

## 3. Capture effective configuration and repeat the transport matrix

Before the boundary probes, capture independent effective deployment configuration
for both Web and MCP HTTP, including the default limit. Bind it to the current
source/deployment context and retain the raw supporting configuration. A 413 body
reporting a limit is outcome evidence, not independent configuration evidence.

Set `receipt.transportLimitConfiguration` to the hashed JSON document containing
`context`, `observedAt`, `maximumBodyBytes` and exactly two `components`: `web` and
`mcp-http`. Each component contains an `evidence` reference and a `pointer`, for
example `/effectiveMaximumBodyBytes`. The referenced capture has
`kind: "effective_deployment_configuration"`, the current context, a fresh
`observedAt` and the observed integer limit. The effective matrix limit is the
lower of the two component limits. Retain the external ingress route and any
additional proxy configuration as supporting evidence.

Select the existing approved profile explicitly:

```powershell
$Profile = "C:\private\profiles\owner-http-mcp.json"
& $Python tests/http_mcp/campaign_transport.py `
  --profile $Profile --manifest $Manifest --evidence-root $Campaign `
  --output (Join-Path $Campaign "transport-boundary")
```

If the observed effective limit is below 30,000,000 decoded bytes, supply that
exact `--limit`. Store the returned summary reference in `receipt.transportBoundary`.
The default matrix has 13 probes: `/mcp` and `/mcp/`, Content-Length and chunked
framing, limit minus one/exact/plus one, plus the historical 29,990,208-byte chunked
control. Each body is valid `initialize` JSON plus whitespace. The client sends
the complete body before reading a response, retaining the eager-upload behavior
that exposed the reset. In-limit probes must return the original RPC identity and
successful server information. Oversize probes must return the documented 413
`application/problem+json`, `code: request_too_large`, `limit: body` and the actual
maximum. A reset or unrelated proxy error fails the case.

Also retain fresh duplicate-member controls at outer and inner JSON levels on
both aliases and stdio, including an approximately 1.1 MB valid control and the
original RPC identity, without business mutations. If testing
`Expect: 100-continue`, record it separately from the eager-upload matrix. Source
socket tests support the change; the deployed public ingress needs its own fresh
observations.

## 4. Execute functional cases and map proved obligations

Register each owned fixture's marker, exact selector, organization/scope,
dependencies, creation/readback and eventual disposition. Establish current
bounded discovery before dependent reads. Exercise the repaired notification,
list and timeline paths, all residual operations and the complete catalog, domain
and historical scope with current fixtures.

For mutations capture the preview, one deliberate confirmed action, independent
readback and applicable ordinary/integration denial. Use an actual positive
fractional effort amount, an eligible failed automated retry, the actual designated
approver and a change in the required lifecycle state. See the
[read harness](campaign-testing.md#run-selected-live-read-checks) for comparable bounded
MCP/backend captures; read subsets alone do not close the campaign.

```powershell
& $Python tools/ci/validate-beta12-campaign.py status `
  --catalog $Catalog --receipt $Receipt
& $Python tools/ci/validate-beta12-campaign.py mapping-plan `
  --catalog $Catalog --manifest $Manifest --receipt $Receipt --evidence-root $Campaign
```

`status` is an inventory of recorded mappings. `mapping-plan` validates current
case evidence and identifies only candidates whose existing kind, assertions,
surfaces and operation match a required domain/history slot. Each slot also lists
its exact required assertion, kind, surfaces and operation IDs, including slots
without candidates. It never changes the receipt or awards a pass. Inspect each
candidate and add its explicit mapping only when the captured semantics prove that
obligation. Missing assertions need new observations; adding a label to old
evidence does not create proof. Reusing a valid case for several demonstrated
obligations avoids replaying destructive effects.

For direct mail prove exactly one new independently enumerated durable outbox
effect and delivery with the `direct_send_delivery_matches_effect` assertion on
`storage` and `transport_capture` surfaces. For concurrent approval prove durable
IDs/keys before execution, after the first action and after replay. Use the
[effect snapshot format](beta11-laptop-campaign.md#direct-mail-and-approval-effect-cardinality).
A foreign notification returning `updated: 0` can prove exact owner-readback
isolation; it cannot supply a positive own-notification mutation. Inbound rule
deletion needs its preview, owned confirmed deletion, ordinary denial and retained
history/readback.

Exercise real provider success, controlled failure and eligible single/bulk retry.
Callbacks need a real authenticated 503 followed by accepted recovery and a
terminal authenticated 403 at the correct owned phase. Observe durable callbacks,
RatelDesk attempts and idempotent business effects, including later reconciliation.
Use the intended Events actor and actual retry tokens. Keep stream snapshots
bounded. Retiring a definition must preserve readable execution history. If a
companion changes during the campaign, re-establish the affected identities and
observations.

The five fixed Local external-auth guards still need their designated positive
Local equivalents; the two HTTP set/unset operations retain their fixed exclusions.
Every other catalog operation needs its own positive evidence. Expected faults and
authority denials are separate cases and do not substitute for missing positives.

## 5. Finish functional coverage, soak and cleanup

Set `functionalCompletedAt` only after all functional requirements are demonstrated.
Then collect the canonical read-only soak for 900–3,600 seconds with 30–10,000
unique samples, gaps no greater than 60 seconds, no unexpected failures and p95
within the prospectively declared budget. Earlier functional traffic does not
count as a post-functional soak.

Prove all nine cleanup assertions and record every fixture's disposition. For
baseline comparisons use identical scope, tenant, query, filter, ordering and
normalization before and after. A whole-queue baseline and a campaign-marker-only
final export are different populations: keep and compare each population
separately. Preserve unrelated records, retained history and held uncertain effects.
Revoke temporary credentials, stop owned temporary controls and restore settings
whose approved campaign change requires restoration.

Set `receipt.prerequisites` to the hashed completed prerequisite document. A
candidate `PASS` label requires all current evidence and the complete validator:

```powershell
& $Python tools/ci/validate-beta12-campaign.py validate `
  --catalog $Catalog --manifest $Manifest --receipt $Receipt --evidence-root $Campaign
```

Return the validator output, complete operation/domain/history mappings, exact
remaining open gates and owner actions, prerequisite and cleanup dispositions,
and the measured verdict. Keep failed attempts and explain their repair/retest.
Prepare only a curated private evidence bundle, excluding credentials, profiles,
the Python environment and native archives. Publishing a release is not campaign
acceptance; report `NOT_READY` whenever the full gate remains incomplete.
