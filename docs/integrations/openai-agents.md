# OpenAI Agents API

RatelDesk uses the managed Codex harness for Chat, AI Ops and ticket assistants. Each OpenAI profile belongs to one RatelDesk organization. The browser uses the same authenticated chat and event stream as the [Netclaw integration](netclaw.md). Native conversations require PostgreSQL. SQLite installations can continue using ticket webhook assistance.

## Configure a profile

1. Create an application key in a designated OpenAI project. Grant `api.agents.read`, `api.agents.write` and `api.responses.write`. Choose a model available to that project. [OpenAI Agents API quickstart](https://developers.openai.com/api/docs/guides/agents-api/quickstart)
2. Supply the key to the **API** service as a protected configuration value under `AiProviders:OpenAiKeys:<reference>`. The environment variable `AiProviders__OpenAiKeys__support` supplies the reference `support`. Use your deployment's secret store or protected environment file. Share the API's Data Protection key ring between replicas so they can decrypt saved profile revisions. [RatelDesk self-hosting](../deployment/self-hosting.md)
3. Set up the paired HTTP MCP gateway on its canonical, publicly reachable HTTPS `/mcp` resource. OpenAI connects from its service network, including when the agent has no execution environment. [HTTP MCP setup](../mcp-http/setup.md) · [OpenAI MCP connections](https://developers.openai.com/api/docs/guides/agents-api/tools/mcp)
4. Open **Administration → Integration → AI Providers → Add OpenAI profile**. Enter the organization, model, key reference, MCP resource and permitted read operations. Enter the OpenAI project and organization identifiers if your project requires those headers. Save and enable the profile. The key reference resolves server-side at save time and the key is encrypted into that revision.
5. Run **Verify Agents read access** to check project authentication and Agents read permission. Check model access and the MCP connection by opening a new conversation and asking for one permitted read of a synthetic ticket. That conversation uses OpenAI model and tool usage. [OpenAI usage](https://developers.openai.com/api/docs/guides/agents-api/observability)

**Chat and MCP** uses the `none` environment. It supports application questions, direct MCP reads and managed delegation. **Workspace and skills** uses a small OpenAI-hosted sandbox with outbound sandbox networking disabled. The MCP connection still originates from the OpenAI service. Use a trusted hosted template if configured and an optional versioned skill bundle for reproducible artifact work. [Hosted environments](https://developers.openai.com/api/docs/guides/agents-api/environments/openai-hosted)

An installation can run with OpenAI alone. Leave Netclaw disabled or unconfigured. In deployment-managed setups, set `Netclaw__Enabled=false` if a previous deployment supplied Netclaw settings. The providers can also be enabled together. Choose **Set default** on a profile or select a provider when starting new work in Chat or AI Ops. On a ticket, choose **Provider for next conversation** and start a new conversation. Existing conversations retain their provider and revision. The organization uses another eligible profile when the instance default is unavailable to it. A deployment can set `AiProviders__DefaultProfileId` to the desired profile ID.

## Tool authority and approvals

The API issues an expiring, ordinary MCP-purpose credential for each OpenAI session. It uses the original operator's current grants, the profile's organization and the exact canonical MCP resource. The gateway exchanges it for a short-lived execution token. OpenAI receives the MCP credential through its encrypted session transport configuration. The OpenAI project key stays in the RatelDesk API.

Direct MCP calls are restricted to the profile's selected operations. Grouped tools expose multiple operations, so the API enforces the operation policy on the route that actually runs. The OpenAI credential cannot perform writes through the direct MCP connection.

If **Allow approved internal incident notes from the main agent** is enabled, the root agent can request approval for an exact note on an incident. The original operator can review, allow once or deny the request in the existing chat. RatelDesk rechecks the account, grant, policy, incident and exact arguments before executing the note through its worklog service. Approval expires after ten minutes. After an interrupted write, check the incident before authorizing another attempt. OpenAI subagents can use the configured direct MCP reads and have no access to this root-only function. [Functions](https://developers.openai.com/api/docs/guides/agents-api/tools/functions) · [Subagent tools](https://developers.openai.com/api/docs/guides/agents-api/multi-agent#subagent-tools)

Saving a new profile revision or disabling it blocks further admission and tool calls under the previous revision. The API can still observe saved work with the original key. Rotate the OpenAI key by updating its reference and saving a revision. Revoke a session credential from Integration when its access must stop immediately. Review and archive the old conversation before starting a replacement.

## Workspace and artifacts

Package a single trusted plugin directory containing `.codex-plugin/plugin.json` and `skills/<skill>/SKILL.md`. Give the manifest a name, version, description and `"skills": "./skills/"`. Package the plugin directory as a ZIP with its single top-level folder. Use regular files and keep the archive under 1 MiB compressed and 4 MiB expanded. [Official plugin packaging](https://developers.openai.com/api/docs/guides/agents-api/tools/plugins)

Mount the ZIP read-only on each API replica. For a reference named `fixture-report-v1`, set `AiProviders__SkillBundles__fixture-report-v1__Path` to the absolute mounted path and `AiProviders__SkillBundles__fixture-report-v1__Sha256` to the result of `sha256sum` for that ZIP. Choose the reference in the hosted profile. Changing a bundle requires saving a new revision.

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

Use a small usage budget in the test project. Keep fixture and live results separate in release evidence.
