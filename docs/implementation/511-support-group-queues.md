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
| Incident, request and change list, filters, totals and detail in the ticket endpoint modules | Module and organization grants, current group membership, coverage and scope apply to the ticket query or record. | Endpoint fixtures cover all three modules and both databases cover the persisted policy. The real gateway fixture proves incident restrictions, delegated scope and revocation. The equivalent delegated request and change journeys remain open. |
| Ordinary create, transfer and claim in the ticket and queue endpoints | Create checks the authorized destination and requester identity. Transfer checks source and destination scope and current assignment version for each record. Claim checks current ticket write authority and eligibility. | Both database fixtures cover incident transfer and claim, mixed and permitted batches, concurrent writers, and repeated visits to a queue. Full module creation and concurrent membership acceptance remain open. |
| Individual assignment, update and state in the ticket endpoint modules | Named-queue assignee writes validate current group eligibility. Restricted state and supported change lifecycle updates use the ticket scope. Generic form updates and legacy individual and bulk routes remain subject to the restricted endpoint gate. | Existing lifecycle regression coverage and rendered state checks. Scoped command coverage remains open. |
| Worklogs and ticket timeline in the incident, request and change handlers | Record authorization applies before history and freshly before each protected SSE frame. The scoped projection limits legacy mail and diagnostic metadata. | The gateway request stream tests recheck live membership removal before delivering a committed workflow event on SQLite and PostgreSQL. Live incident and change stream acceptance remains open. |
| Ticket attachment routes and history/attachment counters | Resolve authorized parent before projecting its metadata or serving stored bytes. | Full direct-download, upload, count and legacy signed-content matrix remains open. |
| Neutral ticket relations | Both endpoints pass the same queue and organization scope. Relation choices are restricted to writable records before filtering, counts and paging. Both endpoints require write eligibility for mutations. Related request tasks inherit parent scope. | Gateway fixtures cover permitted choices, link and unlink, and a hidden same-tenant target. The cross-module task relation matrix remains open. |
| Request child tasks | Both task lists and individual reads check the authorized parent. Restricted task responses use a bounded projection. | Task execution, approval and automation diagnostics remain behind the restricted endpoint gate. Live task state transitions remain open. |
| Global search in the Web and API | Scoped workspace search uses ticket module list endpoints. Directory, investigation and general search routes remain behind the restricted endpoint gate. | The browser detects the loss of one named queue while the visible module permissions stay unchanged. The rendered global search matrix remains open. |
| Reports, exports, notification feed and AI session/tool endpoints | Organization-wide routes remain behind the restricted endpoint gate. | Endpoint-specific denial and legacy regression acceptance remain open. |
| Mailbox assignment outbox and support subscriptions | The committed transition determines the recipient set. Delivery rechecks active membership, current group, ticket read, rule and preference. The template carries an authenticated URL. | Six Mailpit deliveries cover the three modules on both providers. Each mail went to the sole authorized address with its authenticated ticket URL. A second eligible recipient lost membership after enqueue and was suppressed. Provider failure replay, repeated transitions and browser sign-in remain open. |
| Public links, requester mails and self-service | Public views recheck the recipient binding. Known restricted workforce recipients need an independent requester or approval entitlement. The interactive request history uses the currently linked customer record. | Both databases cover linked customer history after workforce revocation. The signed-link and submission matrix remains open. |
| CLI, stdio MCP and HTTP MCP | Requests reach the same API. Integration credentials and gateway execution constrain effective group grants to their source account and credential context. | The regenerated catalog and actual gateway/adapter incident tests cover discovery, permissions and revocation. Published executable and wire acceptance remain open. |
| NetRatel keyed create and receipts | V3 uses the approved pairing, source, organization, customer and currently enabled allowed queue. On retry the server uses the original committed receipt. | Both databases cover concurrent duplicate creates, durable assignment effect, restart readback, changed-queue conflict, legacy conflict and revocation. A running NetRatel writer remains dependent on receiver publication. |
| Support groups, memberships and coverage administration | Tenant settings authority is scoped to the group owner and both coverage organizations. Membership changes check the target user's tenant relationship. Self-enablement requires another administrator. | `SupportQueueAdministrationTests` checks actual tenant permission resolution, own group operations and foreign and self-assignment denial on both databases. The complete rendered administrator and role setup matrix remains open. |

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
- The rendered workspace run passed 97 tests across three ticket modules. Its
  synthetic fixture exercised selection, filter totals, URL restoration,
  versioned moves and a vendor claim. The session access suite passed four
  tests including partial queue revocation. Three administration browser cases
  passed, including selecting two group IDs and reopening their labels in the
  role editor. The database fixtures exercise API authorization.
- The first passing hosted run covers all six .NET regression shards, incident
  receiver acceptance, documentation and layout, the disclosure gate and
  PostgreSQL Compose configuration. It ran at `8cf1d289`, before the latest
  related-resource and administration changes. [GitHub Actions](https://github.com/BostonTechnologies/RatelDesk/actions/runs/46064236635)
- At `227e1e82`, the hosted rehearsal passed the seven archive size and checksum
  checks, native execution on Linux x64, Linux ARM64 and Windows x64, and
  candidate API, Web and HTTP MCP images for amd64 and arm64. [Artifact rehearsal](https://github.com/BostonTechnologies/RatelDesk/actions/runs/46066593965)

### Rendered screenshots

- [Incident queue filter](assets/511-queues/incident-filter.png)
- [Vendor claim in the allowed incident queue](assets/511-queues/vendor-claim.png)
- [Request queue transfer](assets/511-queues/request-transfer.png)
- [Change queue transfer](assets/511-queues/change-transfer.png)
- [Custom role with selected support groups](assets/511-queues/selected-support-groups.png)
- [Incident filters on mobile in dark mode](assets/511-queues/incident-filters-mobile-dark.png)
- [Change workflow at native 200% zoom](assets/511-queues/change-native-200-percent.png)

Independent review, required CI, the remaining authorization matrix, and the
published native and deployment artifact checks gate the beta. The issue
acceptance criteria apply to the resulting reviewed revision. [Epic #511](https://github.com/BostonTechnologies/RatelDesk/issues/511)
