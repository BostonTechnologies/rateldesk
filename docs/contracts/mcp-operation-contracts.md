# MCP operation contracts

Use discovery from the selected server before constructing a call. The
[operation catalog](mcp-operation-catalog.json) records each operation's method,
route, request schema, effects, confirmation policy, supported transports,
credential requirements, authority, identifiers and prerequisites. The API
resolves live access; discovery does not grant permission. These MCP contracts
apply to stdio and HTTP transports against the same product version; the
[CLI guide](../integrations/cli-stdio.md) describes command-line syntax.
The [inventory delta](mcp-operation-inventory-delta.json) retains all 154 beta.4
operations and records the additive beta.6 `helpdesk_auth.access` operation.
Updated routes/contracts and the new metadata remain distinct from runtime
acceptance evidence.

## Requests, previews and effects

Keep the `operation`, `request`, `confirm` envelope. Required fields belong inside
`request`; field names, enum values and nested extension objects come from the
operation's schema. For a confirmation-controlled operation, omitted or false
`confirm` is a valid preview and performs no mutation:

```json
{"operation":"update","request":{"taskId":"owned-task","priority":"High"},"confirm":false}
```

Review the preview and prerequisites, then submit the same operation with
`confirm:true` when authorized. Validation failures do not authorize a retry with
discarded fields. Unsupported ticket-update fields, including a `title` field
that the incident/request update contract does not support, reject the whole
request before upstream mutation. Explicitly open form/extension payloads retain
their documented fields, and arrays that support clearing accept `[]`.

| Contract | Discoverable request requirements |
| --- | --- |
| AI report | `invocationId`, `eventId`, `ticketId`, `ticketType`, `correlationId`, `status`, `message`; statuses Queued, Started, Progress, Finding, ActionAwaitingApproval, Completed, Failed, Cancelled |
| Change state/lifecycle/bulk | `newState` and `lifecycleState` have distinct enums; bulk operations require `ids` and their state/assignment fields |
| Task edit | At least one supported edit; `assignedToId` conflicts with `clearAssignment:true`, and `dueDate` conflicts with `clearDueDate:true`; supported attachment/asset arrays can clear with `[]` |
| Self-service create | `requestFormId`; `payloadJson` defaults to `{}` and must satisfy the selected form; requested-for selection has its documented source/authority prerequisites and does not require a pre-existing `requestId` |
| Scoped role create/update | Required name and permissions; create additionally requires `ownerOrganizationId`; permission dependencies and delegable ceilings apply |
| Service create | Use the API's `parentId` for nesting; the unused `parentServiceId` field is absent from the advertised contract |

An operation's effect and confirmation requirement are independent. Most writes
require confirmation. `helpdesk_ai_assistant.report` is an autonomous AI worklog
write under its existing authenticated invocation/event/ticket/correlation
binding; it is not a read, a general worklog writer or a ticket-state operation.
Its statuses and required fields come from discovery. Connectivity tests perform
external I/O and require confirmation even though they submit no automation job.
Local non-secret configuration writes also depend on the transport's persisted
configuration surface.

An AI callback event ID belongs to its stored invocation, organization, ticket
and correlation. Reusing it for a different binding or different recorded
status/severity/message/metadata/artifacts is rejected. A matching retry retains
the original event and timestamp without rewriting the invocation's current
run reference; run reference is not stored as an event snapshot. Completed, Failed and Cancelled
invocations reject new callback events; retries of already stored matching
events do not reopen them. A delayed dispatch failure also preserves the stored
terminal result while retaining delivery attempts and failure health. These
[source repairs](https://github.com/BostonTechnologies/RatelDesk/issues/234)
retain the exact AI authentication scheme and token expiry checks. Synthetic
provider regressions do not establish deployed provider acceptance.

## Persistence and uncertainty

Incident state updates reject undefined states before changing records,
timestamps or effects. Task priorities use Low, Medium, High and Critical in MCP
create/update payloads; supported names normalize to defined numeric API enum
values. Omitted priority retains the existing/default value. Explicit null,
numeric strings and numbers are invalid MCP priority edits. Direct API optional
null fields retain their documented omission/default behavior and undefined
numeric priorities are rejected atomically.

Task start/complete/fail return HTTP 200 with a flat transition DTO: task identity,
committed status and compatible scalar/list task fields, plus `correlationId`.
The parent `Request` navigation graph is excluded; workflow evaluation remains.
Correlation matches the response header and also survives bounded client errors
after an uncertain write. A correlation ID is a diagnostic trace, not an
idempotency key. Read the task, parent workflow and timeline before considering
another mutation after a lost response or 5xx. Repeated invalid transitions
remain rejected; the client does not automatically replay writes.

Change `state` and `lifecycle` remain separate operations. Ticket-state changes
are allowed in Draft, cannot directly resolve a change, and cannot bypass
approval or implementation. Outside Draft, use the governed lifecycle operation;
the existing approval authority, templates, implementation and closure rules
still apply. Bulk state retains its administrator policy and validates the
selected set before mutation. An unchanged state or `updated:0` establishes no
new transition.

Task retry requires an eligible failed automation task and configured peer,
tenant and binding. Manual-task retry is unsupported; a zero updated count is a
no-op. Bulk mutations with `updated:0` report `status:"no_op"` and no affected
IDs, with eligibility/readback guidance. Preserve these distinctions when
reporting campaign results.

An explicit single or bulk retry dispatches each selected eligible task even
when its template has `autoStart:false`. That setting suppresses automatic
workflow dispatch and is persisted on generated tasks; manual start remains
available. Each retry retains the previous execution identifiers and outcome
in task attempt history and persists a new provider attempt correlation before
submission. An uncertain submission must be reconciled before retry. Ordinary
MCP request correlation remains a diagnostic trace, not a write replay key.

Orchestration settings, jobs, tenants, request-definition reads and the confirmed
connectivity test accept optional `request.organizationId` and
`request.connectionId`. These select an existing eligible saved automation
mapping; the API validates its organization and connection capability. Omission
retains the existing unique-connection resolver. Invalid, ambiguous or mismatched
selection cannot authorize a different peer or tenant. The binding-inventory
operation retains its existing request contract. See
[issue #231](https://github.com/BostonTechnologies/RatelDesk/issues/231).

## Notification summaries and persisted effort

`helpdesk_notifications.summary` and CLI `notifications summary` use the general
`GET /api/v1/notifications/summary` endpoint. `totalCount`, `unreadCount` and
`unreadErrorCount` cover the actor's complete authorized notification population,
not a campaign search subset. The latter includes unread Error/Critical notices.
The separate error-only API endpoint and `unread_errors` operation retain their
existing meanings. Personal receipts, ticket/organization visibility and
`mark_read` confirmation remain enforced; previews change no receipts and
repeated confirmed marking is idempotent for that actor.

New worklog timeline events persist their exact decimal effort and `workLogId`
when written. Incident, request and change timeline refreshes and live updates
share that projection, including nonzero-hour internal notes. The additive
`recordedHours` is the authoritative nullable effort measurement: zero means a
measured zero; null means effort is unavailable (or not applicable for other
event types). The legacy numeric `hours`
field remains present and falls back to zero for clients that do not understand
the additive contract. New clients must use `recordedHours` to decide whether
to display effort. `workLogId` is null when no authoritative association exists.

Pre-upgrade timeline rows contain neither a worklog link nor an effort snapshot.
A retained transactional timeline-outbox payload can supply an authoritative
snapshot for a Worklog/InternalNote event only when its exact event ID, ticket
ID and event type agree. It must be a Timeline effect with a canonical ordinal
key `0000:Timeline` through `0255:Timeline`, linked to an existing succeeded
inbound receipt whose ticket and organization match the target. Original payload
identity fields must be unambiguous. Hours must be explicitly numeric and finite,
with no duplicate fields; every eligible exact candidate must contain the same
valid effort. Missing/invalid effort on an identifiable candidate blocks recovery
even if another candidate has a valid value. The migration may recover that effort snapshot; it does not
invent a canonical `workLogId`. Missing, malformed or conflicting evidence leaves
effort unavailable. Independently generated IDs, repeated text and nearby
timestamps never establish a worklog association. Canonical WorkLogs, outbox
records and effort totals remain unchanged; where a worklog is independently
addressable, read its own authoritative hours. The signed public timeline projects directly from visible canonical WorkLogs,
so those historical entries can expose known effort without joining ambiguous
staff timeline events. An old numeric timeline zero alone is not evidence of zero work. New
snapshots survive refresh without changing event identity/count/order, internal
visibility or inline-image signing.

## Transport, identity and authority

| Operation family | Required authority and prerequisites |
| --- | --- |
| Incident/request/task/change reads and writes | Applicable operation permission intersected with organization scope, ownership and action eligibility; support assignment also requires eligible membership |
| Instance administration, customer authentication, connectivity and privileged change bulk actions | Explicit supported HelpdeskAdmin identity; ordinary bounded integration credentials cannot acquire instance administrator authority through scoped grants |
| Scoped role management | Existing organization owner/role-management policy and delegable permission intersection |
| Self-service | Linked customer identity, ownership, eligible published request form and form-valid payload; create uses `requestFormId` |
| AI report | Real authenticated invocation/event/ticket/correlation binding and the existing reporting policy |
| Automation retry/connectivity | Correct saved integration, tenant and binding; test uses orchestration's own connectivity service |

Stdio uses an API-purpose account-bound credential. HTTP gateway ingress uses an
MCP-purpose/resource-bound credential and API-issued request-scoped execution
tokens. External Authentik uses an explicitly configured downstream identity.
Account state, expiry, revocation, resource binding, permissions and organization
scope remain enforced. An unavailable backend or denied request never selects a
different target or authentication mode. See [HTTP configuration](../mcp-http/configuration.md)
and [access control](../security/rbac.md).

Live capabilities, schema and status resources include `configuredIdentity`,
which identifies the active host modes and shared/request-bound execution
boundary without asserting verified authority. `helpdesk_auth.access` is an
additive read of the current API execution principal at `/api/v1/auth/me` in every
mode. In external HTTP mode it reports the separately configured downstream
principal, not the ingress OAuth caller. Existing `helpdesk_auth.status` keeps
its original mode-specific behavior and remains the only operation authorized
for a temporary setup verifier. The beta.6 catalog contains 28 tools and 155
operations, retaining the complete 154-operation historical corpus. Follow the
[external authority provisioning procedure](../mcp-http/configuration.md#provision-an-explicit-external-authority)
for actual administrator/AI identity prerequisites and their independent
expiry, revocation and isolation evidence.

`tenantId` for branding is a positive integer obtained from the tenant lookup;
an organization ID is a different identifier. Branding persistence looks up that
typed tenant key, so a fresh read and repeated save address the same row.
Customer authentication uses
`/api/v1/customers/{customerId}/auth/status`, `/invite`, `/resend-invite`,
`/disable-login` and `/sync-authentik` beneath that same `/auth` prefix. These
actions retain HelpdeskAdmin and provider-mode rules: Local/ambiguous identities
and an unavailable provider have distinct outcomes. Invitation acceptance uses
disposable customers and isolated capture.

## Fixed raw reads and result evidence

`rawEnabled:false` and `arbitraryRawAccessEnabled:false` disable arbitrary raw
access. `allowlistedRawReadsEnabled:true` advertises exactly `system_version`,
`get_incident`, `get_request` and `get_change`. Their method/route/target are fixed;
caller-supplied hosts, URLs, paths, methods, bodies and unknown operations are
rejected. The normal underlying permission and organization checks still apply.

Keep successful execution, expected denial, unsupported operation, missing
prerequisite, no-op and unobserved postcondition separate. Neither a preview,
HTTP 2xx nor `updated:0` proves a business mutation. Independently read ticket
seen-state after `helpdesk_tickets.mark_as_seen`; notification
`helpdesk_notifications.mark_read` is a separate operation.

See [list search and pagination](ticket-list-queries.md), the
[beta.5 regression manifest](../implementation/0.1.2-beta.5-mcp-regressions.md)
and [release engineering](../releases/README.md).
