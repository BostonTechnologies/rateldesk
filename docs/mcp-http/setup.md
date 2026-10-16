# Set up HTTP MCP

**Server setup prepares the HTTP MCP service's connection to RatelDesk once per deployment. Client setup grants one consumer its own explicitly approved access.** Gateway host approval authorizes a deployment proposal; it creates no shared downstream service credential or signing key.

## Prepare the server

Use Web, API, HTTP MCP and tools from the same verified release. You need Docker access on the deployment host, administrator access to RatelDesk, its public HTTPS Web URL and authority to apply through the existing deployment owner. Preserve the database, ingress, volumes and protected configuration.

1. Extract the verified deployment archive into a separate tools directory. Open a terminal **on the deployment host**, enter that directory and run:

   ```sh
   ./setup-http-mcp.sh
   ```

   The launcher discovers the existing container and saved Compose sources. Select a container only for actual ambiguity. Supply missing ownership/source information when requested; fix reported storage or configuration prerequisites before approval. Source-checkout and manual alternatives are in the [deployment reference](deployment.md).

2. Enter the **RatelDesk Web URL**, either its root or `/mcp` address. Review the normalized Web and MCP addresses. Existing instance, API destination and authentication settings are retained. **Advanced** exposes deliberate topology changes; a hostname never chooses a different instance or authentication mode.

3. Review the complete effective configuration and redacted changes. Open the visible approval URL on this or another device, sign in as an authorized administrator and choose **Review host**. Explicitly approve the reviewed deployment proposal. Host approval grants no consumer access; higher-priority configuration conflicts must be resolved first.

4. Save the proposal. **Saved—apply pending** means the running services still need updating. For release-owned Compose, use the exact reviewed apply command. For an external deployment manager, privately review the complete exported deployment document and apply it through that same manager. Keep the printed checkpoint and durable storage; preserve unrelated services and volumes.

5. After recreation, resume with the command below. **Applied—verify pending** requires live checks. Separately approve the **Temporary verification client**: it has only short-lived, organization/resource-bound status-check access and is cleaned up afterward. A cleanup-pending result requires the displayed recovery action.

   ```sh
   ./setup-http-mcp.sh --verify
   ```

6. Finish at **End-to-end verified** only after observed runtime configuration, authentication, compatible MCP initialization, the actual catalog and successful API-backed `helpdesk_auth/status`. Keep the sanitized result. A saved file, open port or HTTP 200 alone is insufficient. Resume reuses the approved generation; changed sources require fresh review.

## Connect a CLI/MCP client

In the MCP container's interactive console, run:

```sh
dotnet /app/Helpdesk.Mcp.Http.dll setup
```

Enter the Web URL, open the visible approval URL, choose **Review client**, then explicitly approve one enabled organization, exact permissions and expiry. Save to the displayed durable private client store and use the printed protected configuration snippet. Each consumer/organization needs its own credential; a temporary verifier is never a reusable consumer credential. Bare HTTP-image `setup` connects a client; `setup --mode host` explicitly configures hosting.

Use [native CLI/stdio setup](../integrations/cli-stdio.md), [browser approval and recovery](../integrations/browser-pairing.md), or [NetClaw integration](../integrations/netclaw.md#connect-netclaw-to-rateldesk-with-http-mcp). The [configuration](configuration.md), [deployment](deployment.md) and [troubleshooting](troubleshooting.md) references cover advanced and external Authentik/OAuth deployments.
