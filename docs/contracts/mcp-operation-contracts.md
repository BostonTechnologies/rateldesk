# MCP operation contracts

Use discovery from the selected server before constructing a call. The
[operation catalog](mcp-operation-catalog.json) records each operation's method,
route, request schema, effects, confirmation policy, supported transports,
credential requirements, authority, identifiers and prerequisites. The API
resolves live access; discovery does not grant permission. These MCP contracts
apply to stdio and HTTP transports against the same product version; the
[CLI guide](../integrations/cli-stdio.md) describes command-line syntax.
The [inventory delta](mcp-operation-inventory-delta.json) maps all 154 beta.3
operations to the retained current operations; no operation names were added or
removed. Updated routes/contracts and the new metadata remain distinct from
runtime acceptance evidence.

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

See [list search aliases](ticket-list-queries.md#mcp-search-aliases), the
[beta.4 regression manifest](../implementation/0.1.2-beta.4-mcp-regressions.md)
and [release engineering](../releases/README.md).
