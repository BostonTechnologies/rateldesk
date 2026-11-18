# Native chat acceptance

This existing smoke executable has an explicit `continuation` mode for the
connected child-follow-up path. It uses the production chat manager, PostgreSQL
store and SignalR client. Run it against disposable instances only: it creates
an organization, a ticket and two remote sessions. It does not erase them.

The checked-in model fixture makes OpenAI-compatible responses on loopback.
Netclaw itself performs `spawn_agent`, runs the child actor, queues its completion
notice and produces the follow-up parent turn. This proves the native transport
and orchestration path with a deterministic model; it is not external-LLM or
production acceptance. Run one smoke at a time because the fixture's release
gate is shared between its two sequential conversations.

## Prepare disposable native instances

Use the official Netclaw CLI and daemon 0.28.1 release for Linux x64. The exact
source and release checksums used are in the
[issue #355 receipt](../../docs/implementation/355-child-followups.md).

From the repository root, select an empty work directory and paths to the
downloaded `netclaw` and `netclawd` binaries:

```bash
acceptance_dir=$(mktemp -d)
export NETCLAW_HOME="$acceptance_dir/native"
install -D -m 600 tools/AiAssistantChatSmoke/fixtures/netclaw.json "$NETCLAW_HOME/config/netclaw.json"
install -D -m 600 tools/AiAssistantChatSmoke/fixtures/synthetic-reader.md "$NETCLAW_HOME/agents/synthetic-reader.md"
python3 tools/AiAssistantChatSmoke/fixtures/synthetic-model.py --events "$acceptance_dir/model-events.jsonl"
```

Run `netclawd` in a second terminal with that same explicit `NETCLAW_HOME`.
The fixture configuration binds the daemon to `127.0.0.1:19599`, disables shell
execution, memory downloads, skill synchronization and automatic updates, and
uses the synthetic model at `127.0.0.1:19598`. The `team` deployment posture is
needed for the supported child-spawn tool. No production MCP profiles are used.

Pair a disposable device through the supported `netclaw daemon pair` and
`netclaw pair http://127.0.0.1:19599` flow. Keep the pairing code, device token,
native home and database credentials outside the repository and logs. Supply
the paired token through protected environment injection. See the canonical
[Netclaw integration guide](../../docs/integrations/netclaw.md) for pairing.

## Run the receiver

Supply `AI_ASSISTANT_CHAT_POSTGRES` for an empty disposable PostgreSQL database
and `AI_ASSISTANT_CHAT_DEVICE_TOKEN` for that device, then run:

```bash
export AI_ASSISTANT_CHAT_ENDPOINT=http://127.0.0.1:19599/hub/session
export AI_ASSISTANT_CHAT_CHILD_RELEASE_URL=http://127.0.0.1:19598/fixture/release-child
dotnet run --project tools/AiAssistantChatSmoke -- continuation
```

The receiver admits one harmless arithmetic request in each of ticket and
organization chat. It verifies durable pending child IDs and a live owner before
releasing the model's child gate. Success prints one JSON receipt per scope:
one operator input, two native replies, advancing native turn ordinals, pending
children cleared, retained durable transcript and no automatic resend. A timeout,
lost subscription, missing pending state or missing final report fails the run.

Stop both fixture processes and remove only the disposable work directory and
database after retaining the safe receipts. The executable is separate from
normal CI; owning test suites use deterministic transport and browser fixtures.

## Scheduled report browser acceptance

Use a disposable PostgreSQL RatelDesk API/Web stack and a separate official
NetClaw daemon. The pinned Linux x64 test binary is [NetClaw 0.28.2](https://github.com/netclaw-dev/netclaw/releases/tag/0.28.2)
at commit `7419d84f7317dae42783d8a9ed6e353f78f961a8`. Use the same loopback
configuration above with `Logging.LogLevel` set to `Information`. Pair one
disposable RatelDesk API device, set its NetClaw instance to `dev`, and leave the
API as the session's sole SignalR recipient. Run the model before the daemon.
Use an empty organization called Fixture Organization, a synthetic local
administrator, and a fresh incident in that organization.

```bash
python3 tools/AiAssistantChatSmoke/fixtures/synthetic-model.py --scheduled-reports --events "$acceptance_dir/model-events.jsonl"
```

Supply `HELPDESK_E2E_BASE_URL` as the loopback Web origin and inject the local
operator credentials through `HELPDESK_E2E_LOCAL_EMAIL` and
`HELPDESK_E2E_LOCAL_PASSWORD`. Set `HELPDESK_E2E_SCHEDULED_TICKET` to the
disposable incident ID. Record the app build and native binary version and
revision in `RATELDESK_SMOKE_VERSION` and `NETCLAW_SMOKE_VERSION`.
`NETCLAW_SMOKE_SESSION_LOGS` is the native home containing the UTC session audit
folders. The model endpoint is the local control URL
`NETCLAW_SMOKE_MODEL_URL=http://127.0.0.1:19598`.

```bash
npx playwright test --config tools/AiAssistantChatSmoke/browser/playwright.config.ts --output "$acceptance_dir/browser"
```

The browser sends one 30-second marker request in each view. Organization Chat
exercises dispatch after the parent terminal. Ticket AI Assistant holds the
loopback model's parent response until native dispatch has been acknowledged,
then releases the parent and verifies the buffered report. The fixture
supplies model replies and leaves tool execution, scheduling, buffering,
routing and SignalR output to the actual daemon. The test checks native audit
lines, durable API events, the pending and Ready UI, and reload. It emits UTC
receipts and screenshots. The suite runs only with this explicit configuration.
