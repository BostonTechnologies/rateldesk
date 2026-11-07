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
