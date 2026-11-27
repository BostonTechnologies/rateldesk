# Support group ticket queues acceptance register

RatelDesk adds a nullable support-group assignment to incidents, requests and
changes. The SQLite and PostgreSQL upgrades keep existing tickets in Main
Queue and retain their existing assignments and histories. The queue
assignment version changes with a named-group transition or a return to Main
Queue. Transfer and claim transactions validate current eligibility before
saving.

The draft implementation is tracked in
[RatelDesk PR #518](https://github.com/BostonTechnologies/RatelDesk/pull/518).
The receiver uses the paired service principal and the source-specific route
allowlist. The NetRatel writer depends on a published compatible receiver.
The companion work is tracked in NetRatel #862. [Receiver contract](../contracts/rateldesk-incident-create.v3.md)

## Authorization inventory

| Surface and source owner | Gate for restricted workforce identities | Evidence and open verification |
| --- | --- | --- |
| Incident, request and change list, filters, totals and detail in the ticket endpoint modules | Module and organization grants, current group membership, coverage and scope apply to the ticket query or record. | SQLite and PostgreSQL policy fixtures and real gateway incident tests cover hidden named queues, Main Queue, filtering before totals, delegated identity and live revocation. Equivalent endpoint runs for requests and changes remain open. |
| Ordinary create, transfer and claim in the ticket and queue endpoints | Create checks the authorized destination. Transfer checks source and destination scope and current assignment version for each record. Claim checks current ticket write authority and eligibility. | The gateway fixture covers incident transfer acceptance and denial. Real API multi-ticket transfers, creation, claim and concurrent membership changes across all modules remain open. |
| Individual assignment, update and state in the ticket endpoint modules | Named-queue assignee writes validate current group eligibility. Restricted state and supported change lifecycle updates use the ticket scope. Generic form updates and legacy individual and bulk routes remain subject to the restricted endpoint gate. | Existing lifecycle regression coverage and rendered state checks. Scoped command coverage remains open. |
| Worklogs and ticket timeline in the incident, request and change handlers | Record authorization applies before history and freshly before protected SSE messages. The scoped projection limits legacy mail and diagnostic metadata. | Independent live transfer and revocation tests of each stream remain open. |
| Ticket attachment routes and history/attachment counters | Resolve authorized parent before projecting its metadata or serving stored bytes. | Full direct-download, upload, count and legacy signed-content matrix remains open. |
| Neutral ticket relation reads | Both endpoints pass the same queue and organization scope before counts and display. Related request tasks inherit parent scope. | Gateway test covers hiding a same-tenant linked ticket and retaining a permitted linked ticket. Related task and mutation scenarios remain open. |
| Request child tasks | Both task lists and individual reads check the authorized parent. Restricted task responses use a bounded projection. | Task execution, approval and automation diagnostics remain behind the restricted endpoint gate. Live task state transitions remain open. |
| Global search in the Web and API | Scoped workspace search uses ticket module list endpoints. Directory, investigation and general search routes remain behind the restricted endpoint gate. | Rendered restricted search and cached session revocation remain open. |
| Reports, exports, notification feed and AI session/tool endpoints | Organization-wide routes remain behind the restricted endpoint gate. | Endpoint-specific denial and legacy regression acceptance remain open. |
| Mailbox assignment outbox and support subscriptions | The committed transition determines the recipient set. Delivery rechecks active membership, current group, ticket read, rule and preference. The template carries an authenticated URL. | Six Mailpit deliveries cover the three modules on both providers. Each mail went to the sole authorized address with its authenticated ticket URL. A second eligible recipient lost membership after enqueue and was suppressed. Provider failure replay, repeated transitions and browser sign-in remain open. |
| Public links, requester mails and self-service | Public views recheck the recipient binding. Known restricted workforce recipients need an independent requester or approval entitlement. The restricted endpoint gate covers unreviewed authenticated routes. | A full signed-link and dual-context customer acceptance matrix remains open. |
| CLI, stdio MCP and HTTP MCP | Requests reach the same API. Integration credentials and gateway execution constrain effective group grants to their source account and credential context. | The regenerated catalog and actual gateway/adapter incident tests cover discovery, permissions and revocation. Published executable and wire acceptance remain open. |
| NetRatel keyed create and receipts | V3 uses the approved pairing, source, organization, customer and currently enabled allowed queue. On retry the server uses the original committed receipt. | Both databases cover concurrent duplicate creates, durable assignment effect, restart readback, changed-queue conflict, legacy conflict and revocation. A running NetRatel writer remains dependent on receiver publication. |

## Evidence

- `TicketQueueMigrationTests` upgrades populated legacy SQLite and PostgreSQL
  databases and checks rollback of the additive model.
- `TicketWorkforceScopeTests` covers MSP, customer and foreign organizations,
  Desktop, Wintel and App Vendor, with overlapping role grants, selected
  groups, local account and OIDC resolution, credential narrowing, membership
  removal and coverage revocation.
- `McpGatewayRealAccessPipelineTests` checks the real credential-to-execution
  boundary and the serialized API responses.
- `AutomationTraceabilityTests` checks the paired receiver using its real token,
  source route and restart behavior.
- `ticket-filters.spec.ts` checks rendered queue selection, counts, URL restoration
  and a versioned move. Its synthetic transport exercises the browser behavior.
  The database-backed tests provide the authorization evidence.

Independent review, required CI, the remaining authorization matrix, and the
published native and deployment artifact checks gate the beta. The issue
acceptance criteria apply to the resulting reviewed revision. [Epic #511](https://github.com/BostonTechnologies/RatelDesk/issues/511)
