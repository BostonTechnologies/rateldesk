# AI conversation lifecycle evidence

The 22 November 2026 Chromium run exercised the real NewWeb components with a synthetic API. Two Chat and Investigations checks verified exact argument and result copy after reload. Four viewport checks verified keyboard disclosure, three distinct runs, two same-name children, both original status polls, reported failure, fullscreen and zero prompt submissions. The screenshots use simulated viewports. Physical phone and native software keyboard testing remains an owner check.

| Viewport | Inline run list | Fullscreen detail |
| --- | --- | --- |
| 1366 × 768 | [Run list](assets/444-ai/1366x768-agent-cards.png) | [Child observations and tools](assets/444-ai/1366x768-agent-detail-fullscreen.png) |
| 390 × 844 | [Run list](assets/444-ai/390x844-agent-cards.png) | [Child observations and tools](assets/444-ai/390x844-agent-detail-fullscreen.png) |
| 360 × 640 | [Run list](assets/444-ai/360x640-agent-cards.png) | [Child observations and tools](assets/444-ai/360x640-agent-detail-fullscreen.png) |
| 360 × 480 | [Run list](assets/444-ai/360x480-agent-cards.png) | [Child observations and tools](assets/444-ai/360x480-agent-detail-fullscreen.png) |

| Issue | Source and regression coverage |
| --- | --- |
| [#443](https://github.com/BostonTechnologies/rateldesk/issues/443) | The [scheduled-report receiver](../../src/Helpdesk.Infrastructure/AiAssistant/Chat/AiAssistantChatSessionManager.ScheduledReports.cs) matches the original parent and occurrence key. [PostgreSQL lifecycle tests](../../tests/Helpdesk.Tests/Infrastructure/AiAssistant/ChatTransportLifecycleTests.ScheduledReports.cs) exercise the second operator admission, duplicate terminal, foreground reply and independent local stop. |
| [#446](https://github.com/BostonTechnologies/rateldesk/issues/446) | The [activity component](../../src/HelpDesk.NewWeb/Components/Shared/AiAssistantTurnActivity.razor) orders durable starts, results and approvals. [Rendered component tests](../../tests/Helpdesk.Tests/NewWeb/ChatParityRenderedTests.cs) cover overlapping calls, raw inert payloads and missing outcomes. |
| [#447](https://github.com/BostonTechnologies/rateldesk/issues/447) | The [NetClaw adapter](../../src/Helpdesk.Infrastructure/AiAssistant/Chat/NetclawOutput.cs) reads accepted child receipts, status results and unbound observations. [Projection tests](../../tests/Helpdesk.Tests/Infrastructure/AiAssistant/ChatPresentationTests.cs) and [browser checks](../../tests/ux/ai-ux-corrections.spec.ts) cover distinct identities, same-name children, reload and fullscreen. |

NetClaw 0.28.2 includes a run ID in accepted child starts and status results. Its standalone subagent progress frames contain the agent name, phase and optional result metadata. The panel preserves these observations separately when the wire omits run identity. [Accepted receipt](https://github.com/netclaw-dev/netclaw/blob/7419d84f7317dae42783d8a9ed6e353f78f961a8/src/Netclaw.Actors/SubAgents/ChildRunExecution.cs) · [Child status contract](https://github.com/netclaw-dev/netclaw/blob/7419d84f7317dae42783d8a9ed6e353f78f961a8/src/Netclaw.Actors/Sessions/LlmSessionActor.Children.cs) · [SignalR mapping](https://github.com/netclaw-dev/netclaw/blob/7419d84f7317dae42783d8a9ed6e353f78f961a8/src/Netclaw.Actors/Protocol/SessionOutputDtoMapper.cs)
