# NetRatel automation

Configure NetRatel through [System connections pairing](netratel-pairing.md).
After pairing, select the real NetRatel tenant and RatelDesk organization,
name the connection and explicitly enable **Run automation**, then **Save**.
The automation integration page uses these saved connections.

The scoped catalog, invocation and correlated callback/result paths require
current mapping authority and existing task/resource policy. A successful
token or read-only probe does not establish an accepted execution. A transport
failure after dispatch may leave an uncertain outcome; preserve its original
execution/correlation identity rather than inventing an acknowledgement or
silently submitting a second task.

An authenticated, correlated `unknown` callback means the execution outcome
requires reconciliation. RatelDesk puts the task and parent request on hold,
retains the provider request/run IDs, attempt history and previous result,
and disables automatic, single-task and bulk retries. The task shows
**Reconciliation required**; use **Recovery** and the recorded provider IDs
to investigate the original execution. A later `succeeded` or `failed`
callback for that same execution can resolve the uncertainty. Duplicate
`unknown` and late `running` callbacks cannot reset this hold. Do not submit a
replacement execution merely because no start acknowledgement was received.

A callback can arrive before RatelDesk saves the provider's ingest
acknowledgement. An authenticated callback for that exact current mapped attempt
receives HTTP 503 with `Retry-After: 1` while the acknowledgement is pending.
The provider can retry the same callback; RatelDesk does not learn remote IDs
from an early callback. Stale attempts, foreign mappings and conflicting or
partial identities remain denied.

When upgrading from retired manual/deployment NetRatel profiles, upgrade both
products and use system pairing. Existing saved paired connections retain their
IDs and mappings; the deadline and self-submission repairs do not require
re-pairing or another credential.
The independent Netclaw integration and API/MCP credentials are unchanged.

## Durable execution deadlines and recovery

An automation attempt persists its start, correlation identity and deadline
before contacting the saved connection. The positive snapshotted hard timeout
is the maximum local attempt window. If a legacy hard timeout is missing or
invalid, use its positive expected runtime plus nonnegative grace; missing or
invalid values fall back to the existing 30-minute runtime and 10-minute grace.
These limits are separate from task SLA, approval due dates and pending local
task wait time. An admitted provider operation still queued or running at the
local limit is **unknown**, not proof of remote cancellation or failure.

The existing Hangfire runtime checks indexed batches of at most 50 attempts on
startup and every minute, using a persisted replica lease and optimistic task
and parent versions. It checks the recorded organization, exact saved mapping,
peer and revision, current attempt, and external request/run through the
provider's authenticated read-only execution endpoint. It never replays ingest
to discover an outcome. Before the deadline it checks at one-minute intervals;
unresolved outcomes back off to five minutes. An older provider without the
execution-read contract remains safely reconcilable through its authenticated
callback path.

At the deadline, an unconfirmed outcome becomes **Timed out — outcome unknown**
with **Needs reconciliation** on the parent. The UI retains the original
attempt and provider identifiers, deadline, last actual provider update and
Recovery access. This hold blocks dependent work, ordinary failure/Continue
policy, failure incidents and automatic or manual retry. A later confirmed
result for the same attempt atomically records the real outcome, recomputes
parent/dependency state and removes only its uncertainty hold. Independent
operator/approval/failure holds remain. Historical timeout evidence remains
after reconciliation; an earlier attempt cannot update a later attempt.

Old Running rows enter the same bounded sweep without manual SQL. A trusted
stored start and runtime snapshot determine their deadline. An active legacy
row without a trusted start is routed immediately to visible reconciliation;
missing attempt or remote identity prevents provider lookup and resubmission.
Manual and approval tasks are never changed by this execution sweep. Callback
and sweep races commit state, audit and queued observable effects together;
duplicate terminal results cannot create another job, worklog or failure
incident. Interrupted post-commit workflow continuation resumes durably.

## Edit request workflows

Open **Administration → Automation → Service Explorer**, open a service, and edit
its request form. The editor starts on **Workflow**. Select a task in the ordered
navigator to edit its details, automation connection/target/mappings, or local
**Advanced workflow** settings. On a narrow screen, select a task and use
**Back to tasks** to return to the list. The task editor and navigator each own
one scroll area; Save and Cancel stay outside them.

Use the drag handle or **Move task up/down** buttons to reorder tasks. Their IDs,
dependency references, mappings and unsaved edits remain associated with the same
task. Add and Duplicate select the new task; Delete selects a neighboring task.
**Preview workflow** only renders the configured graph and does not run automation.

**Form fields** and **Form setup** edit the same draft. Switching tabs does not
save or reset it. Save validates every task and field, including inactive tabs,
and opens the relevant section/task with focus on the control that needs action.
Existing connection/organization eligibility and target validation still apply.
Cancel asks before discarding changed drafts; clicking the backdrop or pressing
Escape does not dismiss the editor.

If the form saves but automation binding synchronization fails, the editor remains
open with a stage-specific error. Review the bindings and choose Save again. The
retry updates the same saved form instead of creating another form or binding.
The earlier form save has already happened; canceling the remaining draft does
not roll that server-side save back.
