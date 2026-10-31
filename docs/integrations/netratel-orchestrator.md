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
