# NetClaw scheduled-report acceptance, 18 November 2026

**GO for the connected disposable cases below. Production NO-GO.** The app
kept its original SignalR recipient through each parent terminal, persisted the
matching final and terminal, and displayed both replies under one operator
request. The shared production route and exact replay after an interrupted or
diverted stream remain acceptance gates.

## Exact builds

- Disposable RatelDesk Web and API `0.1.4-beta.1`, source
  `d1ca4028f2105e72e5b1bf992d84944c2e130e4c`, .NET SDK `10.0.404`.
  Both assembly informational versions carried that revision.
- Official NetClaw `0.28.2` Linux x64 daemon, source
  `7419d84f7317dae42783d8a9ed6e353f78f961a8`, release archive SHA-256
  `5b0c41919efed2c3cf91c7761024d8bb03a8ee81d1dd83b8b09a44e65285134c`.
  The deterministic loopback provider supplied model text. The official
  daemon executed `load_tool`, `set_reminder`, scheduling, buffering and the
  SignalR delivery. [NetClaw release](https://github.com/netclaw-dev/netclaw/releases/tag/0.28.2)
- The production failure was observed on RatelDesk `0.1.3` at
  `0d4f4e38f7af76714891aba8e396a56dd53a9901` and the shared native `0.28.2` OCI
  digest `sha256:59bb46d084e440793bf0f382de8be772ae063c18a7941cb89f74c970a634093d`.
  The receipts below use the standalone release binary in a separate environment.
  [Production reproduction](https://github.com/BostonTechnologies/RatelDesk/issues/351#issuecomment-6976118365)

## Native, durable, and browser evidence

Each case used one browser Send and one harmless `current_session` one-shot
scheduled after 30 seconds. The times below are UTC on 18 November 2026.

| Boundary | Organization Chat, post-parent | Ticket AI Assistant, buffered |
| --- | --- | --- |
| App admitted the operator request at sequence 2 | 16:15:06.107241 | 16:18:23.968835 |
| Native acknowledged dispatch | 16:15:36.432 | 16:18:54.462 |
| App saved parent Turn 1 at sequence 11 with one report pending | 16:15:06.619516 | 16:18:56.552943 |
| Native produced the final marker | 16:15:36.7344015 | 16:19:03.7375623 |
| Native completed Turn 2 | 16:15:36.7344332 | 16:19:03.7376456 |
| App saved the final at sequence 13 | 16:15:36.792460 | 16:19:03.812824 |
| App saved the matching Turn 2 terminal at sequence 14 | 16:15:36.795145 | 16:19:03.818658 |

The post-parent session audit shows the reminder admitted as
`channel=signalr`. The buffered audit shows dispatch during the parent, then
`turn_buffer_drain count=1` with the matching SignalR fire key. Independent
PostgreSQL readback matched the browser receipts for both conversations. Each
had one operator admission, one final marker, and one terminal with the same
operator message, native ordinal and fire key as the final. After reload, the
final appeared once, the pending count was zero, the waiting status cleared,
and the Ready composer was enabled. The ticket journey also navigated away
and back while the report was pending.

- Organization Chat [pending](assets/351-scheduled-reports/organization-pending.png) · [Ready after reload](assets/351-scheduled-reports/organization-ready.png)
- Ticket AI Assistant [pending](assets/351-scheduled-reports/ticket-pending.png) · [Ready after reload](assets/351-scheduled-reports/ticket-ready.png)
- [Machine-readable receipts](assets/351-scheduled-reports/receipts.json) · [Opt-in browser test and setup](../../tools/AiAssistantChatSmoke/README.md#scheduled-report-browser-acceptance)

The lifecycle tests cover multiple and recurring reminders, child work,
approval preservation, duplicate terminals and equal text across turns,
organization and session fences, newer turns and archive, unsupported
registration, disconnect, and bounded expiry. The transcript projection test
guards against closing the operator's displayed turn at the parent terminal.

## Native prerequisites for production

The live arguments, registration result, and `sourceReminderId` fire key
support the connected cases above. A storage state of **Delivered** acknowledges
native dispatch. It carries no exact final or terminal. On initialization
without an attached recipient, NetClaw can select its TUI default. The session
join DTO returns recent message history and a turn count.

The production gate requires an immutable release or daemon commit that
preserves the originating SignalR channel and recipient through
rematerialization, exposes pending occurrences tied to the admitted input,
and supports authorized replay of the exact final and terminal by session,
input, fire key, and turn ordinal. The inspected `0.28.2` and `0.28.3`
(`584023bda9ab4a30c7f3f6d8fbd0a7091b5a88ae`) code lacks that recovery
contract. A qualifying revision remains unverified.
[Operator guide](../integrations/netclaw.md#scheduled-reports)

The roughly 643-second provider/tool-call gap remains a separate upstream
dependency. Lifecycle diagnostics retain the route, pending counts, rejected
terminals, final persistence, and observation cutoff without using request
identifiers as metric dimensions. [NetClaw #2791](https://github.com/netclaw-dev/netclaw/issues/2791)
