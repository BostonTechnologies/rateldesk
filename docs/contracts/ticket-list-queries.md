# Ticket list queries

The incident, request and change workspace lists show tickets independently of AI
provenance. Their normal navigation, refresh and filter reset never send an
AI-only predicate. The AI Assistant, stored provenance and audit history remain
available under their existing permissions.

Compatibility inventory: the public request list accepts `aiOnly=true` for
requests with a source ticket or automation binding. The CLI `--ai-involved`
option emits `aiInvolved`; the incident/change APIs historically ignore that
parameter. These public contracts are retained. The obsolete workspace checkboxes
are removed without deleting external options or provenance.

## Shared workspace filters

Customer means the ticket's organization (`Ticket.OrganizationId`), displayed as
`CustomerOrgName`. The incident/request list formerly derived its organization
label indirectly from the contact; the list now uses the ticket's authoritative
organization, including tickets without a contact. For means `CustomerId` for
incidents/requests and `RequestedForUserId` for changes. Assignee uses
`AssignedToId` for incidents/requests and `ImplementorUserId` for changes. IDs,
never labels or email addresses, are persisted in queries.

The additive GET parameters on `/api/v1/incidents`, `/api/v1/requests` and
`/api/v1/changes` are:

| Parameter | Meaning |
| --- | --- |
| `customerOrgId`, `forId`, `assigneeId` | Repeat to OR up to 20 stable IDs within the dimension |
| `stateValues` | Repeat ticket states; changes use lifecycle states |
| `priority` | Repeat Low, Medium, High or Critical |
| `unassigned=true` | Include null assignees, OR with selected assignee IDs |
| `view` | `active` (default), `historic`, or `all`; explicit states replace the default view restriction |
| `timeField` | `updated` (default, falling back to creation for never-updated tickets), or `created` |
| `from`, `to` | Offset-bearing ISO timestamps, converted to UTC; `from <= timestamp < to` |
| `sort` | `updated`, `created`, `trackingId`, `subject`, `priority`, or `state` |
| `descending` | `true` by default; stable ticket-ID tie breaker |

Dimensions combine with AND. Authorization and predicates run in SQL before
counting, sorting and paging on SQLite and PostgreSQL. Paged calls clamp page
sizes to 1–100; the workspace offers 25/50/100. Legacy unpaged request/change
consumers retain their response contract. `q` is bounded to 200 characters and
SQL wildcard characters are literal search text. Existing legacy state,
lifecycle, requester-email, organization, summary and total controls remain.

`/api/v1/ticketing/filter-options?module=incident&dimension=customer&q=example`
provides 20 suggestions by default, capped at 50, plus `hasMore`. Modules are
`incident`, `request`, `change`; dimensions are `customer`, `for`, `assignee`.
Optional repeated `customerOrgId` values narrow suggestions. Customer/requester
options come from the caller's authorized ticket dataset. Assignee suggestions
share the same eligible-user predicates as the assignment picker; lookup does
not grant write permissions. All lookup values are encoded, requests cancel on
query changes, and no complete user directory is downloaded.

Repeated `selectedId` values (maximum 20) resolve readable labels for restored
filter chips within the same authorization scope. Former assignees are available
only when an authorized ticket still references them. This does not change
normal assignment suggestions or assignment write validation.

The workspace keeps filters, view, sort, page and size in its URL. Back/Forward
restores that state; detail links carry a local `returnUrl`. Filter changes reset
to page one and clear bulk selections. A later response cannot overwrite a newer
query. Loading, no matches and query failures have distinct messages.

Time controls display the chosen IANA timezone (initially UTC). Enter a date or
local date and time. A date-only To expands to the next local midnight, which
can span 23 or 25 hours at daylight-saving transitions. Ambiguous and nonexistent
local times are rejected; choose UTC or another valid wall time. The URL stores
UTC instants plus the display timezone. A reversed or empty range is rejected.
No indexes or schema changes are required by this additive contract; existing
organization/ticket keys and provider timestamp mappings are retained.
