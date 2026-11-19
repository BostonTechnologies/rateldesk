# OpenAI Agents API

RatelDesk uses the managed Codex harness for Chat, AI Ops and ticket assistants. Each OpenAI profile belongs to one RatelDesk organization. The browser uses the same authenticated chat and event stream as the [Netclaw integration](netclaw.md). Native conversations require PostgreSQL. SQLite installations can continue using ticket webhook assistance.

## Configure a profile

1. Create an application key in a designated OpenAI project. Grant `api.agents.read`, `api.agents.write` and `api.responses.write`. The project key also needs Models read access for discovery. [OpenAI Agents API quickstart](https://developers.openai.com/api/docs/guides/agents-api/quickstart)
2. Set up the paired HTTP MCP gateway on its canonical, publicly reachable HTTPS `/mcp` resource. OpenAI connects from its service network, including for the Chat and MCP execution profile. [HTTP MCP setup](../mcp-http/setup.md) · [OpenAI MCP connections](https://developers.openai.com/api/docs/guides/agents-api/tools/mcp)
3. Open **Administration → Integration → AI Providers → Use an API project**. Choose the organization, enter a profile name and supply the OpenAI project key in the password field. Enter optional OpenAI project and organization headers under **Advanced settings** if the project requires them.
4. Select **Refresh project models** and pick the model. The picker lists models visible to that project. A completed turn confirms whether the selected model works with Agents. Reasoning stays at the provider default when the project catalog supplies no compatible effort metadata.
5. Choose an HTTP MCP gateway and permitted read operations. Enable and save the profile. Open **Advanced settings** for hosted workspaces, versioned skills, instructions, approvals and other limits.
6. Select **Check saved project authentication** to verify Agents read permission. After selecting a funding source, run the bounded acceptance below to verify actual MCP use and a completed turn.

The WebUX encrypts project keys with ASP.NET Core Data Protection. Share the API key ring between replicas and protect its backups. The profile editor displays credential source and a deployment reference where applicable. **Keep saved key** preserves the credential on an ordinary edit. **Enter an API key** replaces a WebUX key on save. **Clear saved key and disable profile** removes the saved credential and disables admission. Revoke the corresponding key in the OpenAI project when you also need to stop its use outside RatelDesk.

For environment-managed secrets, put the key under `AiProviders:OpenAiKeys:<reference>` on the API service. For example, `AiProviders__OpenAiKeys__support` makes `support` selectable under **Use a deployment key reference**. Explicitly select the reference again when applying a rotated deployment value. The API snapshots the resolved key into the saved revision. Clearing the profile leaves its deployment secret in the deployment's secret store. Set `AiProviders__DefaultProfileId` to lock the default in the deployment. A configured pairing public origin contributes the canonical MCP gateway to the picker. Deployments can also supply registered HTTPS resources in `AiProviders:OpenAiMcpResources`. [RatelDesk self-hosting](../deployment/self-hosting.md)

### Funding

**Use an API project** uses API project billing. Eligible owners may activate the ChatGPT API-credit benefit for a selected API organization in their own account, then use one of that organization's project keys. Confirm the available credit and billing controls before a live test. OpenAI consumes the benefit before purchased credits and may continue into an existing paid balance. The benefit excludes API-key-authenticated Codex. **Use my ChatGPT plan** uses a separate customer-operated Codex login and its account's usage settings. [API-credit benefit](https://developers.openai.com/api/docs/guides/chatgpt-api-credits) · [API billing](https://platform.openai.com/settings/organization/billing/overview) · [ChatGPT Codex usage](https://chatgpt.com/codex/settings/usage)

**Chat and MCP** uses the `none` environment. It supports application questions, direct MCP reads and managed delegation. **Workspace and skills** uses a small OpenAI-hosted sandbox with outbound sandbox networking disabled. The MCP connection still originates from the OpenAI service. Use a trusted hosted template if configured and an optional versioned skill bundle for reproducible artifact work. [Hosted environments](https://developers.openai.com/api/docs/guides/agents-api/environments/openai-hosted)

An installation can run with OpenAI alone. Leave Netclaw disabled or unconfigured. In deployment-managed setups, set `Netclaw__Enabled=false` if a previous deployment supplied Netclaw settings. The providers can also be enabled together. Choose **Set default** on a profile or select a provider when starting new work in Chat or AI Ops. On a ticket, choose **Provider for next conversation** and start a new conversation. Existing conversations retain their provider and revision. The organization uses another eligible profile when the instance default is unavailable to it. A deployment can set `AiProviders__DefaultProfileId` to the desired profile ID.

## Tool authority and approvals

The API issues an expiring, ordinary MCP-purpose credential for each OpenAI session. It uses the original operator's current grants, the profile's organization and the exact canonical MCP resource. The gateway exchanges it for a short-lived execution token. OpenAI receives the MCP credential through its encrypted session transport configuration. The OpenAI project key stays in the RatelDesk API.

Direct MCP calls are restricted to the profile's selected operations. Grouped tools expose multiple operations, so the API enforces the operation policy on the route that actually runs. The OpenAI credential cannot perform writes through the direct MCP connection.

If **Allow approved internal incident notes from the main agent** is enabled, the root agent can request approval for an exact note on an incident. The original operator can review, allow once or deny the request in the existing chat. RatelDesk rechecks the account, grant, policy, incident and exact arguments before executing the note through its worklog service. Approval expires after ten minutes. After an interrupted write, check the incident before authorizing another attempt. OpenAI subagents can use the configured direct MCP reads and have no access to this root-only function. [Functions](https://developers.openai.com/api/docs/guides/agents-api/tools/functions) · [Subagent tools](https://developers.openai.com/api/docs/guides/agents-api/multi-agent#subagent-tools)

Saving a new profile revision or disabling it blocks further admission and tool calls under the previous revision. The API can observe saved work while its original key remains available. Replacing a WebUX key or clearing the profile stops further requests using the removed key, including remote cleanup that requires that key. Review any outstanding sessions in the OpenAI project. Revoke a session credential from Integration when its access must stop immediately. Review and archive the old conversation before starting a replacement.

## Workspace and artifacts

Package a single trusted plugin directory containing `.codex-plugin/plugin.json` and `skills/<skill>/SKILL.md`. Give the manifest a name, version, description and `"skills": "./skills/"`. Package the plugin directory as a ZIP with its single top-level folder. Use regular files and keep the archive under 1 MiB compressed and 4 MiB expanded. [Official plugin packaging](https://developers.openai.com/api/docs/guides/agents-api/tools/plugins)

In **Advanced settings** under **Workspace and skills**, use **Upload versioned skill ZIP** and save the profile. A profile revision stores the validated archive and its checksum. To use a deployment-managed bundle instead, mount the ZIP read-only on each API replica. For a reference named `fixture-report-v1`, set `AiProviders__SkillBundles__fixture-report-v1__Path` to the absolute mounted path and `AiProviders__SkillBundles__fixture-report-v1__Sha256` to the result of `sha256sum` for that ZIP. Choose the reference in the hosted profile. Changing a bundle requires saving a new revision.

Ask the agent to put results under `/workspace/outputs`. Completed-turn artifacts are saved to the conversation, up to ten files and 5 MiB per file. Use the download buttons in the authorized conversation. [Provider artifact behavior](https://developers.openai.com/api/docs/guides/agents-api/environments/files)

## Recovery, usage and retention

An OpenAI session starts with the first admitted message. When the provider response to creation is lost and the remote ID is unknown, the turn stays unresolved until an operator archives it. A follow-up with unconfirmed delivery is checked against saved provider state and is never automatically resent. Closing a browser or stopping the foreground wait leaves the bounded API observation active. Use **Cancel remote turn** when you intend to stop work and check the eventual provider outcome. Archiving retains the transcript. **Delete remote session** explicitly requests provider cleanup. Tool credentials expire after eight hours and the cleanup worker also checks expired sessions.

Optional OpenAI webhooks can wake the worker sooner. Configure the profile endpoint `/api/v1/ai/provider-webhooks/openai/<profile-id>` in the designated OpenAI project. Set `AiProviders__OpenAiWebhooks__<profile-id-without-dashes>` to that endpoint's signing secret. Subscribe to `agent.session.idle`, `agent.session.action_required` and `agent.session.failed`. The API verifies the signed delivery and retrieves saved provider state before recording outcomes. [OpenAI webhooks](https://developers.openai.com/api/docs/guides/agents-api/sessions/webhooks)

OpenAI receives submitted text, pinned guidance and ticket context, authorized MCP results, and any configured hosted bundle. Check applicable project retention and security settings before sending sensitive data. Review recorded turn usage and consult current provider pricing for model, tool and sandbox charges. [Data controls](https://developers.openai.com/api/docs/guides/your-data) · [Pricing](https://developers.openai.com/api/docs/pricing)

## Bounded live acceptance

Use a disposable RatelDesk PostgreSQL instance with HTTPS MCP, a designated OpenAI test project, and an organization containing only synthetic incidents. Use an enabled human operator and a second account scoped to a different synthetic organization. Record the source SHA, application version, model, relevant status and redacted session references.

1. Enable one `Chat and MCP` profile with `helpdesk_incidents.get`. With Netclaw disabled, start a chat and ask for the title of one synthetic incident. Confirm the actual MCP read and saved final text. Close and reopen the chat, resume with a follow-up, then restart the API while another turn is active and reopen the original transcript.
2. In AI Ops, select the same provider and add the synthetic incident before saving context. Repeat a read from the ticket assistant. Confirm the second account cannot use the profile, retrieve its conversation or fetch its artifacts.
3. Enable the internal note policy in a new profile revision. Ask for an internal note on the synthetic incident. Deny the first proposal and verify the incident stays unchanged. Allow an exact second proposal and verify one note is written. In a fresh chat leave an approval pending until it expires. Revoke another chat's MCP credential and confirm a subsequent read is denied.
4. Submit a bounded turn, choose **Cancel remote turn**, and check the recorded provider outcome. When idle or archived, request remote deletion. Confirm that the saved RatelDesk transcript remains readable.
5. In a hosted profile, configure a ZIP containing a skill that writes a known JSON payload to `/workspace/outputs/summary.json`. Ask the provider to use the skill. Verify the actual artifact download contents and request cleanup.

Before sending any prompt, verify the owner-designated project, allowed funding source, available credit, and any recharge or purchased balance in OpenAI. Record fixture and live results separately.

## Use my ChatGPT plan

A private customer-operated deployment can mount Codex CLI 0.170.0 in the API service. Obtain the matching Linux archive from the official release, verify its checksum and mount the extracted package read-only. Create an encrypted, persistent state directory owned by the API user with permissions `0700`. Use a single Codex-hosting API replica with that local volume. The child process receives the deployment's standard proxy and CA configuration. RatelDesk keeps API credentials and other providers' environment settings out of it. [Runtime and checksum downloads](https://github.com/openai/codex/releases/tag/rust-v0.170.0) · [Authentication decision](../adr/codex-app-server-authentication.md)

Set these deployment prerequisites on that API service. Substitute the mounted paths for your installation.

| API setting | Example |
| --- | --- |
| `AiProviders__CodexAppServer__PrivateDeviceLogin` | `true` |
| `AiProviders__CodexAppServer__ExecutablePath` | `/opt/codex/bin/codex` |
| `AiProviders__CodexAppServer__StateDirectory` | `/var/lib/rateldesk-codex` |

Register the canonical HTTPS MCP gateway in the pairing public-origin setting or in `AiProviders:OpenAiMcpResources`. The API host must reach it. The initial profile exposes scoped read-only ticket operations. Workspace tools, skills, artifacts, subagents and write approvals are unavailable for this access type.

1. In ChatGPT, enable Codex device sign-in for the intended account. In RatelDesk, go to **Administration → Integration → AI Providers → Use my ChatGPT plan**. Name the profile, choose the organization and select **Check runtime**.
2. Select **Continue with ChatGPT**. Open the verification page displayed by RatelDesk, enter the one-time code and complete sign-in. Return to the setup card to see the linked account. Use **Cancel sign-in** to abandon a pending attempt. Retry with a fresh code after expiry.
3. Select **Refresh Codex models**, choose a model and set a reasoning effort if the runtime reports options for that model. Choose the HTTP MCP gateway and permitted reads. Enable the profile and save it.
4. In Chat, start a new conversation and explicitly select the profile. Check **Using ChatGPT plan** near the composer. Run the synthetic read procedure below after confirming the selected account's usage settings.
5. Select **Disconnect ChatGPT** to revoke the local connection. Reconnect by starting a new sign-in. Conversations bound to the previous connection retain their history and require a new conversation for new work.

A runtime or API restart can interrupt active work. A new process reconciles the original thread if its state volume is available. A turn whose delivery remains unknown stays in recovery without resubmission. Use **Cancel remote turn** for an active turn and check its final reported status. The runtime host can continue a turn while the operator's browser is closed. [Codex usage controls](https://chatgpt.com/codex/settings/usage)

### Subscription acceptance with synthetic data

- Confirm the intended device login setting and signed-in ChatGPT plan. Start a sign-in, cancel it, then start another and complete it from a browser outside the API host. Reload the setup page during a pending attempt and verify the same challenge is shown.
- Refresh the model catalog, choose the model and verify the enabled reasoning options. Select only `helpdesk_incidents.get` for a test organization with one synthetic incident. Confirm the model and funding label before sending.
- Ask for the incident title. Verify the recorded MCP call and final text. Send one follow-up, close and reopen the browser during observation, and inspect the same transcript. Use a second operator to confirm the connection and private transcript are inaccessible.
- Restart the API with its existing state volume, recover the original thread and verify no repeat of an uncertain synthetic message. Test explicit cancellation and disconnect. Record the app version, source revision, model, final state and redacted thread and sign-in evidence.
