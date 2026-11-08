# Creating tickets and updating state

Use `/incidents`, `/requests` or `/changes` to search existing work and create a
ticket. Organization choices reflect your permissions for that module. Data
Management has its own organization authority and does not inherit ticket access.

## Searchable creation choices

Start a new incident, request or change and search **Organization** by any part
of its name. Initial suggestions are bounded; refine the search when more
choices are available. Searches cover the complete eligible population. The
selected organization label remains visible while you try another query.

Choose a real result before continuing. One matching result does not imply that
the organization is your only eligible organization. Loading, no matches, no
eligible choices and a failed lookup have distinct messages; use **Retry** after
a load failure.

For a change, search **Requested for** by name/email. Add approvers by searching
and choosing each person; selected approver chips remain visible across queries
and can be removed individually. Email or other available secondary information
helps distinguish people with the same name. Implementor eligibility remains
separate from requester/approver eligibility.

Changing organization clears dependent requester, approver, assignment and
category selections while retaining compatible title/description text. Choose
those values again in the new scope. Typing or selecting a suggestion creates
no customer or ticket. Authorized explicit customer creation remains a separate
action locked to the chosen organization.

The existing technician/implementor and service-category searches remain
available. Priority and Change Type remain short finite menus. Save validates
the selected IDs and organization again on the server; a stale or unauthorized
choice does not gain authority through the picker.

## State popups

On an editable row, activate the colored state chip using mouse, Enter or Space.
The anchored popup identifies the ticket and current state and offers the
permitted actions. Escape or outside dismissal makes no change. Choosing the
current state also makes no change. A read-only row displays a state indicator.

A normal permitted destination applies once. Wait for the pending indicator;
the chip shows the confirmed server result. If an update fails or its completion
is uncertain, follow the displayed retry/reload guidance before another attempt.
Reload verifies persisted state.

Incident and request state operations retain their existing permissions, SLA,
closure, timeline and notification behavior. Changes use lifecycle operations:
**Submit** is a command whose confirmed result can be Draft, Pending Approval or
Approved for Implementation. Approval authority and outstanding decisions remain
separate from write authority. An incomplete template or other prerequisite must
be completed through the required detail/approval action. Terminal lifecycle
states offer no quick transition.

After a successful update the list refreshes counts and membership while keeping
valid search, sort, filter and page context. A resolved ticket can leave the
Active view; use History to find it. Activating a state chip does not navigate to
the ticket. See the [ticket query contract](../contracts/ticket-list-queries.md)
for URL filters and paging behavior.

Use the keyboard to inspect focus and options, including at narrow widths and
200% zoom. Long labels remain human-readable; identifiers determine what is
saved.

## Guided change drafts

Create a change through **Overview → Plan → Schedule & people → Review**. The
steps share one draft. Back/Next and step navigation retain entered values and
type-specific plans; changing organization still clears people/categories that
require a new eligibility check. Dates and times in the guided editor are
explicitly UTC. End must follow start. Review checks the whole draft, and error
links open/focus the responsible section, including hidden steps.

**Create draft** saves in Draft. Missing plan fields are shown as a completeness
checklist and remain permitted for a draft; they do not become universal save
requirements. The additive API `CreateChangeDto.SaveAsDraft=true` requests this
behavior while existing consumers retain their creation semantics. Approval and
submission remain separate authorized lifecycle actions. A failed save retains
entered values; Cancel asks before discarding unsaved work.

Change details default to a readable plan summary. **Edit plan** uses the same
fields/help as creation. Save/Revert remain adjacent to the bounded metadata
panel. Outside Draft, the record and plan remain read-only and existing approval,
lifecycle, categories, activity, worklogs, attachments and return navigation
retain their authority and behavior.

## Task dashboard

A fresh `/tasks` visit starts with **Assigned to me** off and queries all tasks
the actor is already authorized to view. Opt in to restrict assignments. The
server-backed pager offers 25/50/100 rows. Filter changes reset page/selection;
exact request/service IDs remain under Advanced. Local task status, binding
health and external orchestration/reconciliation are distinct. Running is a
status. Start, Complete, Retry and Recovery icons retain eligibility and
permission checks; uncertain execution still requires reconciliation.
