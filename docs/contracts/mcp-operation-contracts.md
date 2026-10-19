# MCP operation contracts

Use discovery from the selected server before constructing a call. The
[operation catalog](mcp-operation-catalog.json) records each operation's method,
route, request schema, effects, confirmation policy, supported transports,
credential requirements, authority, identifiers and prerequisites. The API
resolves live access; discovery does not grant permission. These MCP contracts
apply to stdio and HTTP transports against the same product version; the
[CLI guide](../integrations/cli-stdio.md) describes command-line syntax.
The [inventory delta](mcp-operation-inventory-delta.json) compares the complete
[beta.7 source catalog](mcp-operation-catalog-beta7.json) with beta.8. It retains
all 155 historical operations, records every new named operator adapter and
identifies semantic changes separately from runtime acceptance evidence.

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

## Typed operator adapters

Beta.8 adds named adapters to canonical application endpoints. Discovery binds
each operation to one method, route and typed request; agents cannot provide an
arbitrary API method, host or path. Existing operation envelopes remain
compatible. For these new adapters, path identifiers and query selectors stay
directly under `request`, and API mutation fields go in `request.body`:

```json
{
  "operation": "update_binding",
  "request": {
    "bindingId": "authorized-binding-id",
    "body": { "enabled": false }
  },
  "confirm": true
}
```

| Tool | Operator capability |
| --- | --- |
| `helpdesk_automation` | Create/edit typed self-service form schemas and automation rules; create provider request definitions; create/edit/delete bindings; select exact connections; synchronize/import inputs and inspect drift; inspect workflow evidence; read and decide the caller's designated approval |
| `helpdesk_attachments` | List, download and upload ticket attachments through the ticket's normal permissions; one bounded file of at most 1 MiB per upload/download |
| `helpdesk_mail` | Read mail body/delivery evidence, reply/forward, review and apply selected retry/cancel/remove actions, and send through configured delivery |
| `helpdesk_email_settings` | Configure/test mailboxes and outgoing senders, manage worker lifecycle and import previews, edit templates/layouts, upload template images and manage mail rules |
| `helpdesk_support` | Manage groups, members, organization coverage, notification preferences and routing |
| `helpdesk_sla` | Manage policies, working calendars, organization SLA settings, subscriptions and reports |
| `helpdesk_datasets` | Manage datasets and Graph source settings, query rows/options, write bounded row payloads and create/revoke ingest credentials |
| `helpdesk_connections` | Generate an expiring pairing code, pair an explicitly selected peer, manage/test organization mappings, configure/test/pair Netclaw, confirm reviewed legacy session ownership, and administer incident receiver sources |
| `helpdesk_tenant_admin` | Manage organization settings/membership/role assignments, assets, instance/tenant branding and logos, background-worker settings, local-account activation and assignments, and the owner's credential metadata/revocation/removal |
| `helpdesk_ai_assistant` | Manage AI webhook configurations and state; dispatch a bound investigation; read its worklog; manage native chat, offered interactions and explicit recovery; retain the existing narrow autonomous report operation |
| `helpdesk_incidents` | Manage the ticket and its listener list, read/create/remove duplicate and related links, silently delete selected incident batches, and resolve selected marketing/spam incidents with outbound-email exclusion |
| `helpdesk_tickets`, `helpdesk_changes`, `helpdesk_notifications` | Resolve actual ticket organization/customer/assignee choices, read activity and owned tasks, set an eligible customer, decide the caller's designated change approval, and read authorized notification summaries/timeline |

`helpdesk_automation.create_form` and `update_form` accept a typed
`body.schema` with `fields` and `tasks`; the adapter serializes that object to
the canonical API `jsonSchema` field. Set task UUIDs, dependency UUIDs, task
types, approvers and `autoStart` deliberately. An explicit `autoStart:false`
survives the save and task creation. Omitted `allowedOrganizationIds` on an edit
preserves the current audience; an explicit empty list clears it. The API checks
dataset bindings and task dependency rules before saving. Read the form and
its binding independently after any edit.

The new request schemas reject unknown or duplicated fields, incorrect types,
undefined enums, out-of-range numbers and excessive payload nesting. Numeric
enums use the canonical API's defined numeric values. Mathematically integral
JSON numbers such as `1`, `1.0` and `1e0` are accepted consistently and normalized
before calling typed integer API parameters. `DateOnly` values use `YYYY-MM-DD`;
durations use the constant format such as `09:00:00`. A required field must be
present; its type determines whether null is allowed, and field-specific
length/format requirements determine whether an empty string is permitted.
Explicit nullable replacement fields can therefore reset instance branding
without an omitted field silently clearing it. For example, an empty required
`smtpPassword` preserves the saved sender credential under the canonical API
contract. Typed dataset content allows bounded user-defined JSON columns.

`helpdesk_datasets.upsert_rows` requires an explicit `deleteMissing`. Use `false`
for a batch that changes only the submitted case-insensitive external keys. Use
`true` only for a deliberately reviewed complete replacement: it removes saved
rows omitted from that batch. Requests contain at most 100 rows, 200 characters
per external key, 100 top-level data columns per row, 64 KiB per row's encoded
data and 1 MiB for the encoded batch. The operator, API-key ingestion and Graph
writers share a durable database fence. A competing write returns a real 409;
read the dataset before preparing a new update. Existing conflicting `A`/`a`
keys report a repair conflict instead of poisoning later writes. A reviewed
complete replacement can collapse that legacy collision; incremental updates
do not silently delete either row.

Use confirmed configuration operations for explicit secret replacement or
clearing. Secret inputs are marked `writeOnly` in discovery, pass only to the
selected API operation and are redacted from previews, returned diagnostics
and echoed upstream values. Ordinary reads return secret-state flags. Confirmed
account activation, linked-customer activation, ingest-credential creation and
pairing-code generation can return only that operation's newly created one-time
root token/key/code. These results carry `sensitiveResult:true`; deliver them
through the authorized private workflow and exclude values from campaign
ledgers, screenshots, logs and public issues. No operation reads an existing
account password or stored integration secret.

`my_task_approval`, `approve_task` and `reject_task` identify the caller from the
authenticated execution principal, its current email and the stored designated
user/customer. Instance ownership cannot substitute another approver identity.
The request must be in scope, the decision pending, and its deadline unexpired.
The shared approval lifecycle persists its timeline/audit and preserves a single
decision under concurrent signed-link and authenticated requests. Repeating an
accepted approval resumes the idempotent workflow continuation if its earlier
post-commit attempt failed, without recording another decision. Rejection requires
a reason and uses the canonical parent cancellation/notification lifecycle.
Changes have their corresponding `helpdesk_changes.my_approval` and
`review_approval` operations. These also bind to the stored designated caller;
the final required review advances the governed change lifecycle and queues its
durable effects. An owner grant does not grant permission to approve as someone
else.

### Recovering an interrupted investigation dispatch

An investigation queue record commits before the webhook request is submitted.
A process loss in that gap can leave the investigation at `Queued`; the same
status can also mean the provider accepted the webhook but has not reported yet.
The system has no automatic timeout sweep or remote-provider cancellation
protocol for these investigations. Repeating the original dispatch key reads its
bound invocation and does not resubmit it. A key cannot change its ticket,
configuration, operator or assistance instruction.

The owner-authorized recovery path is:

1. Read `helpdesk_ai_assistant.investigation_worklog` for the exact plural
   `ticketType` and `ticketId`. Retain the selected `invocationId` and
   `correlationId`, and inspect the provider's own delivery/run evidence.
2. To close local tracking, call `helpdesk_ai_assistant.report` with that exact
   binding, a new stable `eventId`, `status:"Cancelled"` or `status:"Failed"`,
   and a truthful explanation of the evidence and remaining uncertainty. Read
   the worklog again to verify the terminal entry. The same event is an
   idempotent retry; a new late callback cannot reopen a terminal invocation.
3. Start a new confirmed `dispatch_investigation` with a fresh idempotency key
   only after the provider evidence rules out duplicate execution or the owner
   explicitly resolves that uncertainty. Preserve the old invocation as history.

`Cancelled` closes RatelDesk's tracking. It does not assert that remote work was
cancelled or that the provider never received the original webhook. If delivery
is unknown, report that uncertainty and reconcile with the provider before a
replacement dispatch. The required provider regression simulates the committed
pre-submit checkpoint with zero captured webhook requests; it does not claim an
actual process-kill test or live provider acceptance.

Native AI chat requires the platform's PostgreSQL durable-session capability.
`chat_capabilities` reports the actual availability and reason. `chat_open`
requires confirmation because the canonical GET may create the first
conversation. `chat` requires an existing conversation ID and reads bounded
durable events after a cursor. Use stable message and investigation idempotency
identities, and read saved worklog/chat evidence after an uncertain send.
`chat_reconcile`, `chat_stop_waiting` and acknowledged `chat_abandon` expose the
same recovery decisions as the human interface; stopping local waiting does not
claim that a remote turn was cancelled.

Netclaw pairing requires a supplied short-lived pairing code and the currently
read settings revision. `netclaw_unbound_sessions` and
`confirm_netclaw_session_owner` let an owner review exact legacy conversation
IDs and bind them to their actual historical provider before pairing. A failed
verification can occur after pairing was saved; read `netclaw_settings` before
requesting another code. Deployment-managed settings remain read-only in both
the human interface and MCP.

`my_credentials`, `revoke_my_credential` and `delete_my_credential_record` act
only on the authenticated owner's metadata and records. They preserve the
separate interactive consent boundary for creating further account credentials.
`helpdesk_auth.revoke_self` also supports an ordinary MCP grant and invalidates
its next call. Incident-source administration can register, revise and disable
an existing API credential's organization/customer source binding, with current
credential validation and revision conflicts; it does not issue a credential.

Incident relationship creation uses an explicit `relationType` and
`closeSourceTicket`. `DuplicateOf` (`0`) always resolves the source; `RelatedTo`
(`1`) follows the submitted closure choice. Both copy source listeners to the
target through the canonical incident lifecycle. Removing a relationship does
not reopen the source or undo copied listeners. Read both tickets and their
relation list after an uncertain response. The distinct `bulk_delete` and
`bulk_marketing_spam` operations accept 1–100 explicit incident IDs: deletion
removes the selected records silently, while marketing/spam resolves them and
excludes them from requester outbound mail. Confirm the intended action and
inspect the returned updated/deleted/missing counts. Listener add/remove uses
the existing `update` operation with the complete desired `ccRecipients` array;
read the incident before replacing that array.

### Human operator parity and identity boundaries

The beta.8 source review compared the web interface's callable business actions
with the fixed operation catalog. The following source families are represented
by the operations above and the retained ticket, task, service, form, customer,
user, role and category operations:

| Web workflow | Canonical UI source | MCP coverage |
| --- | --- | --- |
| Incident duplicate/related links, silent bulk actions and listeners | [Incident pages](../../src/HelpDesk.NewWeb/Components/Pages/Incidents/), [relationships](../../src/HelpDesk.NewWeb/Components/Shared/TicketRelations.razor), [listeners](../../src/HelpDesk.NewWeb/Components/Shared/TicketListeners.razor) | Incident relationships, selected bulk actions and `update.ccRecipients` |
| Ticket creation/editing, assignee/customer choices, worklogs, attachments and approvals | [Shared ticket components](../../src/HelpDesk.NewWeb/Components/Shared/), [request pages](../../src/HelpDesk.NewWeb/Components/Pages/Requests/), [change pages](../../src/HelpDesk.NewWeb/Components/Pages/Changes/) | Existing ticket/task actions plus ticket selectors, attachment transport and designated approval decisions |
| Form/task design, bindings, provider targets and workflow recovery | [Administration pages](../../src/HelpDesk.NewWeb/Components/Pages/Admin/) | Typed form schemas, automation bindings/rules/targets, synchronization, drift and workflow evidence |
| Mail rules/settings/templates, support/SLA, datasets/Graph, account/organization administration, branding and worker settings | [Administration pages](../../src/HelpDesk.NewWeb/Components/Pages/Admin/) | Typed mail, support, SLA, dataset and tenant administration operations |
| AI webhook management, investigation dispatch and native chat recovery | [AI webhook management](../../src/HelpDesk.NewWeb/Components/Pages/Admin/AiAssistantWebhooks.razor), [shared ticket components](../../src/HelpDesk.NewWeb/Components/Shared/) | Confirmed AI configuration/dispatch/chat operations, worklog reads and the separately bound autonomous report |

This is a source coverage statement. Production acceptance still requires the
fresh campaign's real authority, persisted readback, provider, recovery and
tenant-isolation evidence for every operation. The catalog supplies that exact
denominator; successful schema construction alone cannot establish it.

An owner agent operates as its actual business owner and approved grant. The
initial owner grant and issuance of further account credentials retain their
interactive consent boundary; an existing agent can inspect, revoke and delete
its owner's credential records. An agent cannot review another person's
designated approval or enter another customer's self-service identity. Initial
account activation, anonymous ticket ingress and signed provider callbacks use
their own public protocols. Host-managed deployment settings and PostgreSQL-only
native chat availability retain the same restrictions reported by the human
interface. These boundaries must be reported separately from missing business
operations or unsuccessful campaign tests.

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
for a temporary setup verifier. The complete beta.7 catalog is retained as an
immutable baseline. Use the current generated catalog for beta.8 counts and
schemas. Follow the [owner-administration approval procedure](../mcp-http/configuration.md#approve-an-agent-to-administer-the-instance)
for caller-bound full HTTP authority; the [external provisioning procedure](../mcp-http/configuration.md#provision-an-explicit-external-authority)
remains available for independently configured external principals. Both require
their own expiry, revocation and isolation evidence.

`tenantId` for branding is a positive integer obtained from the tenant lookup;
an organization ID is a different identifier. Branding persistence looks up that
typed tenant key, so a fresh read and repeated save address the same row.
Customer authentication uses
`/api/v1/customers/{customerId}/auth/status`, `/invite`, `/resend-invite`,
`/disable-login` and `/sync-authentik` beneath that same `/auth` prefix. These
actions retain HelpdeskAdmin and External provider-mode rules: Local/ambiguous
identities and an unavailable provider have distinct outcomes. Local activation
uses `helpdesk_tenant_admin.reset_customer_login`, backed by
`/api/v1/customers/{customerId}/auth/local-activation-token`; disable/enable the
linked local account through the local-account lifecycle. Invitation acceptance
uses disposable customers and isolated capture. The SLA `tenantId` selector is
the string organization identifier and is separate from the integer branding
tenant key.

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
