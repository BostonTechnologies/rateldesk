# #355: connected native child follow-ups

Date: 7 November 2026. Implementation baseline:
`ee7067d12dafb22de81b70a58f61b211b9e37226` (`origin/main`), branch
`fix/355-child-followups`. Current behavior and limits belong in the canonical
[Netclaw guide](../integrations/netclaw.md#delegated-child-follow-ups).

## Native contract inspected

The supported Netclaw version is **0.28.1**, source
[`0d86cefc32a7c05ffd7b71f9e93f9a864eb1bae8`](https://github.com/netclaw-dev/netclaw/tree/0d86cefc32a7c05ffd7b71f9e93f9a864eb1bae8).
Inspection covered `src/Netclaw.Actors/Protocol/SessionOutputDto.cs`, its
`SessionOutputDtoMapper`, `Sessions/SessionState.cs` and
[`SPEC-004`, Waiting for child runs](https://github.com/netclaw-dev/netclaw/blob/0d86cefc32a7c05ffd7b71f9e93f9a864eb1bae8/docs/spec/SPEC-004-cli-contract.md#waiting-for-child-runs).

`openChildRunIds` belongs to the native session. A physically finished child
whose notice has not had its parent follow-up turn remains open. Missing/null
snapshots indicate an unknown capability, distinct from an explicit empty
snapshot. `turnOutcome` and `quietEnd` describe the parent turn; neither proves
that child work finished.

The actual native daemon reported version 0.28.1, commit `0d86cef`, built
`2026-10-26T03:38:06Z`. Official Linux x64 release bytes used:

| Release asset | SHA256 |
| --- | --- |
| [netclaw-0.28.1-linux-x64.tar.gz](https://github.com/netclaw-dev/netclaw/releases/download/0.28.1/netclaw-0.28.1-linux-x64.tar.gz) | `9b7a6fa6df5d6f1c3934cdbd775c06a6fb265c62015f70e33f041a3b0685a417` |
| [netclawd-0.28.1-linux-x64.tar.gz](https://github.com/netclaw-dev/netclaw/releases/download/0.28.1/netclawd-0.28.1-linux-x64.tar.gz) | `b49776e5551401bbde04e8b58a4dddbf461f8006b581db18aefd384ab369bdcc` |

Extracted CLI SHA256:
`258cbc1c17bb1a308d3cdf2a09e3d45c6428d32dd88d2bb4eb8a25094afb0810`.
Extracted daemon SHA256:
`796cde4ae4cb76feed938387115eb66a6d9b8f67535b4654497a2ab3916f6a7b`.

## Resulting behavior

The three original failures were terminal subscription disposal at the first
parent completion, persistence accepting only the original processing states,
and presentation ending the operator transcript at every completion boundary.
The receiving owner now remains attached while native children or approvals
remain unresolved, persistence accepts that bounded continuation, and the
projection retains its original operator transcript through later native turns.

`WaitingForChildren` is appended as state value 5; existing state values retain
their persisted/JSON identities. Versioned completion metadata uses existing
event storage and records the original operator admission, native ordinal,
outcome, raw child snapshot, effective unresolved children and fixed observation
deadline. Assistant replies also record their original admission association.
Continuation events leave `ClientMessageId` null, preserving its unique admission
index. No database migration is required.

An owner remains bound to its admitted message, remote session, provider profile
and conversation. New inputs and archive operations are rejected while pending.
Duplicate completion ordinals are ignored; equal text from distinct native turns
is preserved. Approvals retain their call IDs, choices and unresolved state.
Failed or unknown snapshots retain prior unresolved children. In particular, a
failed explicit-empty snapshot cannot clear an existing pending chain; a later
nonfailed explicit-empty completion can.

The native boundary validates field types, outcomes, ordinals, duplicate IDs,
128-child and 128-character ASCII identity bounds. ASCII matches the supported
native IDs and keeps escaped metadata below the reader's 64 KiB bound. The owner has a bounded 256-frame
queue, configured connection capacity and one-hour deadline from admission.
Waiting survives normal idle aging. Disconnect, restart, overflow, local stop
or expiry retains durable uncertainty without replaying work. An uncertain
reattached owner expires at the original deadline; expired chains cannot create
another receiver. Ordinary `Idle`/`DeliveryUnknown` output fences remain.

Ticket chat, organization chat and investigations show the pending status,
including fullscreen, history labels and investigation active counts/filtering.
SSE draft data must match the current original admission and durable sequence;
durable replay remains conversation/sequence based.

## Verification and evidence boundaries

The focused owning suites are `ChatContinuationSafetyTests`,
`ChatTransportLifecycleTests`, `ChatPostgresTests`, `ChatPresentationTests`,
`OrganizationChatTests`, `SignalRChatClientIntegrationTests` and
`AiAssistantChatEndpointsTests`. They cover multiple child turns, repeated text,
duplicate completion/SSE delivery, legacy/null/empty snapshots, failed parents,
approvals, isolation, restart, fixed deadlines and disconnect/reattach expiry.
PostgreSQL tests use disposable Testcontainers. SignalR fixtures exercise the
production client against a deterministic authenticated native-wire server.

Real NewWeb browser acceptance uses the shared loopback API fixture for ticket,
organization and investigation routes. It verifies one operator transcript,
live later replies, duplicate SSE, navigation, refresh, fullscreen, pending
composer restrictions and final clear state, plus organization history and
investigation active filtering. These are rendered presentation receipts.
Separate cases verify that failed and deliberate no-reply native boundaries
remain visible through reload without a tool/error frame or assistant reply.

Final focused result: **117 passed, zero failed, zero skipped**. Slopwatch found
zero issues, documentation validation passed (185 Markdown files and 849 local
links), and `tools/ci/validate-layout.sh` passed. The final native smoke build
passed with zero warnings/errors. These checks supplement the normal reviewed
PR/main build and test gates.

Final rendered browser result: **5 passed** (three complete continuation routes
and two failed/no-reply outcome cases), with zero retries.

OpenCover was collected for the changed safety, manager and projection classes,
without altering repository coverage exclusions. Combined line/branch coverage
was 85.5%/76.0%; safety 97.9%/94.2%, manager 84.2%/74.6%, and projection
97.5%/71.4%. CRAP review prompted small field/snapshot helpers: new parser helpers
have complexity at most 16, completion persistence 14, snapshot decisions 20 and
the pending-child sweep 28. Their corresponding new-code CRAP scores stay below
30. The pre-existing large receiver/projection methods remain complexity
hotspots; their behavior is exercised by the owning suites rather than excluded
from measurement.

The separate [existing smoke executable](../../tools/AiAssistantChatSmoke/README.md)
runs the production chat manager/store/client through the **official native
0.28.1 daemon** and a disposable PostgreSQL database. Its checked-in loopback
OpenAI-compatible model requests the supported `spawn_agent` tool and returns
deterministic arithmetic. The actual daemon runs the child actor and produces
the follow-up parent turn. The receiver releases the child only after observing
durable pending IDs and its live subscription. No external LLM, production data,
production endpoint or customer work is involved.

Both native scopes produced these safe receipt fields:

```json
{"Scope":"Ticket","Passed":true,"OperatorInputs":1,"Replies":2,"NativeTurns":[1,2],"ObservedPending":true,"Cleared":true,"DurableReload":13,"AutomaticResends":0}
{"Scope":"Organization","Passed":true,"OperatorInputs":1,"Replies":2,"NativeTurns":[1,2],"ObservedPending":true,"Cleared":true,"DurableReload":13,"AutomaticResends":0}
```

The native fixture, source pin and executable commands make this acceptance
repeatable without private model credentials. Pairing tokens, keys, database
credentials, generated native homes and raw sessions are excluded from Git.

Connected receive-path acceptance does not establish authoritative recovery for
outcomes missed during an actual interruption. Broader
[#308](https://github.com/BostonTechnologies/RatelDesk/issues/308) and
[#351](https://github.com/BostonTechnologies/RatelDesk/issues/351) replay/recovery
work remains separate; such interruptions stay truthfully unresolved here.
