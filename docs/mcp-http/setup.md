# Set up HTTP MCP

Start here for existing RatelDesk local accounts and **gateway** HTTP MCP.
Enter the Web URL, review/approve hosting, apply through its owner, verify,
then optionally approve a client. Host approval creates no consumer access,
downstream administrator credential or shared signing key. Browser sign-in uses
your normal account flow; no account password is entered in the terminal.
Existing linked-OIDC accounts can use gateway credentials too. Preserve an
explicitly selected external Authentik/OAuth mode; its complete reference is
[configuration](configuration.md).

## Prepare the server

Use Web, API, HTTP MCP and tools from the same verified release. You need Docker access on the deployment host, administrator access to RatelDesk, its public HTTPS Web URL and authority to apply through the existing deployment owner. Preserve the database, ingress, volumes and protected configuration.

1. Extract the verified deployment archive into a separate tools directory. Open a terminal **on the deployment host**, enter that directory and run:

   ```sh
   ./setup-http-mcp.sh
   ```

   The launcher checks the selected Compose implementation before deployment work,
   then discovers the existing container and saved sources. Resolve its precise
   prerequisite diagnosis before approval. Preserve any deliberately selected
   `DOCKER_CONFIG`, Docker context/host and TLS settings for setup, apply, client
   setup and fresh verification; see [Compose prerequisites](deployment.md#compose-prerequisites-and-docker-selection).
   Source-checkout and manual alternatives are in the [deployment reference](deployment.md).

2. Enter the **RatelDesk Web URL**, either its root or `/mcp` address. Review the normalized Web and MCP addresses. Existing instance, API destination and authentication settings are retained. **Advanced** exposes deliberate topology changes; a hostname never chooses a different instance or authentication mode.

3. Review the short redacted change summary; **Details** opens the extended review.
   The approval URL/code, purpose, status and actions remain in the compact view.
   Open the trusted URL on this or another device, sign in as an authorized
   administrator and choose **Review host**. Explicitly approve the proposal.
   The recognized legacy config-file mount is migrated in this reviewed complete
   proposal, preserving protected settings; it needs no manual export repair.
   Resolve other reported source/mount conflicts first. Host **Back** returns to
   Hosting and cancels the old approval attempt.

4. Save the proposal. **Saved—apply pending** means the running services still need updating. For release-owned Compose, use the exact reviewed apply command. For an external deployment manager, privately review the complete exported deployment document and apply it through that same manager. Keep the printed checkpoint and durable storage; preserve unrelated services and volumes.

5. After recreation, run a **new process** with the command below.
   **Applied—verify pending** requires live checks. Separately approve the
   **Temporary verification client**: it has only short-lived,
   organization/resource-bound status access and is cleaned up afterward.
   A cleanup-pending result requires the displayed recovery action. Verifier
   **Back** returns to the applied-host verification checkpoint.

   ```sh
   ./setup-http-mcp.sh --verify
   ```

   Finish at **End-to-end verified** after observed runtime configuration,
   authentication, compatible MCP initialization, the actual catalog and
   successful API-backed `helpdesk_auth/status`. Keep the sanitized result and
   next action. This verification does not grant incident access.

6. Optionally connect an ordinary HTTP MCP client from this same **Docker-host
   tools directory**:

   ```sh
   ./setup-http-mcp.sh --target http-client -- --profile incident-reader
   ```

   Enter the Web URL, open the visible approval URL and choose **Review client**.
   Explicitly select one enabled organization, exact permissions (for example
   `Incident.Read`) and expiry. Save to the displayed durable private client
   store and use its protected configuration snippet. Client **Back** returns
   to its authentication/profile context. Each consumer/organization needs its
   own credential; the temporary verifier is never a reusable client.

## Check or reconnect a saved client

An unchanged saved profile reloads in a new process without another approval
while its credential remains valid. From the **Docker host**, select the actual
MCP container and the private profile path displayed by setup (a path **inside
that container**), then use this template:

```sh
docker exec "$MCP_CONTAINER" \
  dotnet /app/Helpdesk.Mcp.Http.dll \
  --config "$CLIENT_PROFILE" doctor --network --authenticate
```

In the **MCP container console**, omit `docker exec "$MCP_CONTAINER"` and run
the `dotnet /app/Helpdesk.Mcp.Http.dll ...` command directly. The host's launcher
and Python helper run on the Docker host. Native `rateldesk`/`rateldesk-mcp`
commands run where those archives were extracted; see [CLI/stdio setup](../integrations/cli-stdio.md).
Bare HTTP-image `setup` connects a client; `setup --mode host` explicitly
configures hosting.

Expiry or revocation requires explicit client reauthorization with the same
profile selection and a newly reviewed grant. Removing a local profile does
not revoke the server credential; revoke it in account credential management.
Host verification cleanup leaves ordinary client credentials intact.

## Resume hosting after a change

For the unchanged applied generation, use `--verify`. If any material source or
proposal changed, run ordinary `./setup-http-mcp.sh` with the same ordered
source, storage and deployment-owner selectors, review/approve the new proposal,
apply through that owner, then invoke `--verify` again. Preserve the selected
Docker environment. Never edit a protected receipt/hash or reuse stale approval.
See [recovery](troubleshooting.md#recovery-and-rotation) and
[migration](deployment.md#migration-persistence-and-rotation).

Use [native CLI/stdio setup](../integrations/cli-stdio.md), [browser approval and recovery](../integrations/browser-pairing.md), or [NetClaw integration](../integrations/netclaw.md#connect-netclaw-to-rateldesk-with-http-mcp). The [configuration](configuration.md), [deployment](deployment.md) and [troubleshooting](troubleshooting.md) references cover advanced and external Authentik/OAuth deployments.
