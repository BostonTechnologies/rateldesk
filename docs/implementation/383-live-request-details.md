# Live request details (#383)

This register covers the isolated #383 implementation and its synthetic local
evidence. The baseline Web source is
`11c8997816833d753caf8df912a8279490da34fb`; the frozen candidate Web/API source is
`816046ba8c762ca5b5c09c254b1edc77ebf94afb`. Later owning test changes are recorded
separately in the source receipt. Campaign integration, native NetRatel execution,
hosted checks and release acceptance remain in the campaign register.

The operator detail keeps one compact header and workflow summary. Task rows
show local state and valid icon actions; provider outcome, binding health,
timing and identifiers live in a keyboard-accessible disclosure. A request-scoped
subscription remains mounted outside Activity. Committed journal invalidations
coalesce canonical reads, retain drafts and expanded rows, and resynchronize
after reconnect or browser resume. The own-request detail uses its stricter
ownership endpoint and exposes no operator recovery actions or provider IDs.

## Requirement register

| Requirement | Owning implementation | Local evidence |
| --- | --- | --- |
| Calm default rows; readable local state; technical disclosure; valid actions | `RequestDetail`, `RequestProgressLabels`, scoped styles and shared `WorkspaceAction` | Frozen three-task comparisons and thirty-task keyboard disclosure; owning layout suite |
| Persisted task/header/workflow convergence with Activity absent | Request journal/SSE, Web gateway, `TimelineStreamService`, `RequestLiveUpdates` | Real migrated-provider lifecycle → gateway SSE → rendered component; strict two-second bound |
| No snapshot/subscription gap; reconnect and stale-scope safety | Ready-triggered resync, bounded/coalesced queue, ticket/load versions and disposable leases | Actual reconnect with terminal commit during stream absence; held old snapshot while navigating between requests |
| No stale progress after an overlapping rejected save; preserve unsaved input | Canonical resync after the command and draft-field merge | Failing pre-fix and passing fixed save-overlap controls; unsaved High priority remains editable |
| Authorized streams throughout their lifetime | Existing scope/ownership checks plus local session validation before every delivery/tick | Foreign own request denied; revoked stream closes; reopening returns empty 403; shared stream denial pauses without a retry loop |
| Duplicates/foreign frames cannot assign row state | Canonical snapshots and scoped subscribers | Subscription lease, foreign-frame and duplicate/out-of-order signal cases |
| Narrow layouts and truthful unknown state | Responsive table tracks, bounded text and expanded detail surface | Light/dark desktop/mobile matrix, 0/3/30 tasks, no unsafe Retry, actual content containment |

## Frozen comparisons

Each before/after pair uses the same three synthetic running tasks. The baseline
shows its existing disabled Running controls and primary technical fields; the
candidate keeps those values in the disclosure. Values, account names and
addresses are disposable fixtures. Both builds retain the existing beta.9
version because this evidence does not allocate or publish a release.

| Viewport/theme | Baseline | Candidate, collapsed |
| --- | --- | --- |
| 1440 × 900, light | [Before](assets/383-live-request-details/before-light-1440.png) | [After](assets/383-live-request-details/after-light-1440.png) |
| 1440 × 900, dark | [Before](assets/383-live-request-details/before-dark-1440.png) | [After](assets/383-live-request-details/after-dark-1440.png) |
| 390 × 844, light | [Before](assets/383-live-request-details/before-light-390.png) | [After](assets/383-live-request-details/after-light-390.png) |
| 390 × 844, dark | [Before](assets/383-live-request-details/before-dark-390.png) | [After](assets/383-live-request-details/after-dark-390.png) |

The [thirty-task disclosure](assets/383-live-request-details/after-light-1440-disclosure30.png)
shows the complete task name, provider state and identifiers after Enter opens
the details. The [unknown-outcome mobile view](assets/383-live-request-details/after-light-390-unknown3.png)
retains Recovery and offers no Retry.

## Receipts and limits

The [source receipt](assets/383-live-request-details/source-receipt.json) records
source pins, build commands and assembly hashes. The
[capture geometry](assets/383-live-request-details/capture-geometry.json) records
capture hashes and checks the actual task/detail bounds as well
as document overflow; an earlier document-only check missed a horizontally
scrolled mobile table, which was repaired before the frozen captures.
The frozen candidate's [owning layout run](assets/383-live-request-details/layout-results.xml)
passed **11/11** without retries or skipped cases. Its
[matrix receipt](assets/383-live-request-details/layout-receipt.json) records
five viewport sizes in both themes, 0/3/30 tasks, keyboard disclosure and retained
expansion after changing tabs. Ten bounded screenshots are retained above.

Gateway tests use migrated SQLite/PostgreSQL fixtures, the real task lifecycle
and parent evaluator, a loopback Kestrel API and the actual YARP Web gateway.
The final normal owning project run passed **11/11**, with zero skipped cases.
The [gateway receipt](assets/383-live-request-details/gateway-receipt.json) and
[raw results](assets/383-live-request-details/gateway-results.trx) retain actual
ready/request-state frames and require matching rendered
task, header and workflow state within two seconds after the terminal commit.
Observed convergence was **935 ms on SQLite** and **1,658 ms on PostgreSQL**.
Activity is deliberately absent. They perform controlled lifecycle completion;
they do not execute a native provider job. Native paired execution and actual
200% browser zoom are separate campaign acceptance receipts.

The save-overlap negative control deliberately uses the earlier Web assembly
and fails because all three completed task rows remain stale after the rejected
save. Its matching fixed-assembly run passes while preserving unsaved priority.
The [expected failing control](assets/383-live-request-details/save-negative-control.trx)
and [passing fixed control](assets/383-live-request-details/save-positive.trx)
are separate from the final positive suite; an
expected negative-control failure is not counted as passing acceptance.

An earlier [owning batch](assets/383-live-request-details/gateway-before-instrumentation-fix.trx)
passed ten cases and missed the PostgreSQL deadline. Its HTTP test decorator
captured the renderer context and added a synthetic completion queue behind a
stale render. Noncapturing network awaits repaired that instrumentation; the
product source and two-second criterion stayed unchanged. Streamed response
traces are retained in the final passing results. Temporary linked-file runs
accelerated diagnosis; they are not the final normal-project receipt.

The final gateway and layout result files, source/capture receipts and precise
local outcomes are included beside the screenshots. No production deployment,
customer data, real mail or customer automation is represented by these assets.
