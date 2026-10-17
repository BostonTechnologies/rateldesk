# Set up HTTP MCP

Start here for existing RatelDesk local accounts and **gateway** HTTP MCP.
Choose **Verify existing deployment**, **Prepare configuration changes**, or
**Connect a client**. A working deployment defaults to verification unchanged.
Host baseline/proposal approval creates no consumer access,
downstream administrator credential or shared signing key. Browser sign-in uses
your normal account flow; no account password is entered in the terminal.
Existing linked-OIDC accounts can use gateway credentials too. Preserve an
explicitly selected external Authentik/OAuth mode; its complete reference is
[configuration](configuration.md).

## Verify or change the server

Use tools from a verified release and record the existing Web, API and HTTP MCP versions. Verifying a compatible existing deployment does not require upgrading its running services. You need Docker access on the deployment host, administrator access to RatelDesk, its public HTTPS Web URL and authority to apply through the existing deployment owner. Preserve the database, ingress, volumes and protected configuration.

1. Extract the verified deployment archive and matching native CLI or stdio
   archive into private tools directories. Open a terminal **on the Docker
   host**, enter the deployment directory and select the verified native apphost:

   ```sh
   ./setup-http-mcp.sh --native-executable "$NATIVE_SETUP"
   ```

   `NATIVE_SETUP` is the absolute path to the extracted `rateldesk` or
   `rateldesk-mcp` executable. A colocated matching native executable is selected
   automatically. No tooling container, serving mount or restart is needed.

2. Let the launcher discover the existing container, ordered source files and
   deployment owner; choose only when a source or instance is ambiguous. If the
   selected Compose lacks a required capability, select an existing verified
   isolated binary, explicitly prepare the pinned private tool, or cancel:

   ```sh
   ./setup-http-mcp.sh --native-executable "$NATIVE_SETUP" \
     --prepare-compose "$PRIVATE_COMPOSE_DIRECTORY"
   ```

   This is explicit consent to a private, integrity-checked Compose v2.39.4;
   it preserves the global plugin and Docker context. Offline verified cache
   and existing-binary choices are in [Compose prerequisites](deployment.md#compose-prerequisites-and-docker-selection).
   Preserve any deliberately selected
   `DOCKER_CONFIG`, Docker context/host and TLS settings for setup, apply, client
   setup and fresh verification; see [Compose prerequisites](deployment.md#compose-prerequisites-and-docker-selection).
   Source-checkout and manual alternatives are in the [deployment reference](deployment.md).

3. Enter one **RatelDesk Web URL**, either its root or `/mcp` address. Existing
   instance, private API destination and authentication mode are retained.
   Keep **Verify existing deployment** for an unchanged deployment. To request
   a real correction, choose **Prepare configuration changes**; **Advanced**
   exposes editable settings. The equivalent explicit launch is:

   ```sh
   ./setup-http-mcp.sh --native-executable "$NATIVE_SETUP" --intent prepare-changes
   ```

4. Review the purpose, defined service count before/after and short redacted
   no-change/change summary; **Details** opens the extended review.
   The approval URL/code, purpose, status and actions remain in the compact view.
   Open the trusted URL on this or another device, sign in as an authorized
   administrator and choose **Review host**. Explicitly approve the proposal.
   Existing valid protected files and their mappings are retained. Host **Back**
   preserves the same host draft and cancels the old attempt. The normal four
   services remain four: API, Web, MCP and database. A missing Docker healthcheck
   is reported as absent; verification does not add one.

5. Save the reviewed baseline or candidate. **Existing baseline adopted; fresh
   verification pending** has no apply stage: source bytes and Docker resources
   stay unchanged. **Changes prepared—not applied** requires the displayed owner
   action. For an external manager, review the minimal readable source diff and
   save/deploy through that same manager. For release-owned Compose, use the exact
   printed apply command. Do not import generated JSON, add `mcp-config-init`,
   replace source formatting or add serving mounts merely to verify. Deliberate
   changes may recreate only their reviewed affected services. Keep the private
   checkpoint outside deployment sources.

6. After baseline adoption, or separately after approved owner apply, run a
   **new process** using the exact quoted command printed by the launcher:

   ```sh
   ./setup-http-mcp.sh --native-executable "$NATIVE_SETUP" --verify
   ```

   The printed command also retains selected sources, Docker identity and private
   Compose selection. **Adopted—verify pending** / **Applied—verify pending**
   requires fresh saved-source/runtime checks. Separately approve the
   **Temporary verification client**: it has only short-lived,
   organization/resource-bound status access and is cleaned up afterward.
   A cleanup-pending result requires the displayed recovery action. Verifier
   **Back** returns to the same adopted/applied verification checkpoint.

   Finish at **Existing deployment verified unchanged**, or **End-to-end verified
   after approved owner apply**, after observed runtime configuration,
   authentication, compatible MCP initialization, the actual catalog and
   successful API-backed `helpdesk_auth/status`. Keep the sanitized result and
   next action. This verification does not grant incident access.

## Connect an ordinary client

Optionally connect a client from this same **Docker-host tools directory**:

```sh
./setup-http-mcp.sh --target http-client -- --profile incident-reader
```

Enter the Web URL, open the visible approval URL and choose **Review client**.
Explicitly select one enabled organization, exact permissions (for example
`Incident.Read`) and expiry. Save to the displayed durable private client
store and use its protected configuration snippet. Client **Back** returns
to its authentication/profile context. Each consumer/organization needs its
own credential; the temporary verifier is never a reusable client.

From the extracted **native CLI archive**, the equivalent HTTP client flow is:

```sh
RATELDESK_MCP_CLIENT_DIRECTORY="$PRIVATE_CLIENT_DIRECTORY" \
  ./rateldesk setup --target http-client \
  --url https://rateldesk.example.test/mcp/ --profile incident-reader

# CLIENT_PROFILE is the actual protected file displayed by setup.
./rateldesk --config "$CLIENT_PROFILE" doctor --network --authenticate
```

Native client setup/doctor needs no Compose feature upgrade. Client doctor
success establishes client checks; host acceptance additionally requires source
and runtime binding plus fresh host verification.

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

For the unchanged adopted/applied generation, use the printed `--verify` command.
If source, environment, image or Docker identity changed, re-read the actual
saved sources and review a new baseline or explicit `--intent prepare-changes`
candidate. A manager's reformatting also needs explicit re-read/review; a
candidate's bytes do not prove what the manager saved. Apply actual changes
through that owner, then invoke `--verify` again. Preserve the selected
Docker environment. Never edit a protected receipt/hash or reuse stale approval.
See [recovery](troubleshooting.md#recovery-and-rotation) and
[migration](deployment.md#migration-persistence-and-rotation).

Use [native CLI/stdio setup](../integrations/cli-stdio.md), [browser approval and recovery](../integrations/browser-pairing.md), or [NetClaw integration](../integrations/netclaw.md#connect-netclaw-to-rateldesk-with-http-mcp). The [configuration](configuration.md), [deployment](deployment.md) and [troubleshooting](troubleshooting.md) references cover advanced and external Authentik/OAuth deployments.
