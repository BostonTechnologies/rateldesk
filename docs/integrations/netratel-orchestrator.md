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

Upgrade both products and pair afresh. Previous manual/deployment NetRatel
connection profiles are retired; the new pairing model is the sole setup path.
The independent Netclaw integration and API/MCP credentials are unchanged.

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
