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

## Create an integration in the browser

Open **Integration → API & MCP credentials**
(`/admin/integration?tab=credentials`) and choose **Create credential**.
The existing `/account/integration-credentials` route opens the same page.
For an **HTTP MCP gateway** connection, complete these five steps:

1. **Connection** — enter the integration name, the exact public HTTPS MCP
   resource and expiry. Use the same resource in the client configuration.
   Expiry is an exact UTC instant, with up to six fractional-second digits;
   **Use 30 days from this reference** selects 720 hours from the displayed
   reference time.
2. **Server access** — keep **Tenant access only**, or explicitly select
   **Owner administration** if your current account is an instance
   administrator. Ordinary credentials have no server-wide grant.
3. **Tenant access** — select a named organization and its exact permissions for
   an ordinary client. An owner-administration credential instead selects a
   default organization context; its separately approved instance authority
   covers all enabled organizations, including organizations created later.
4. **Policies** — inspect the named base policy, its scope, selected catalog
   operations and expiry. An ordinary credential has one organization base; an
   explicitly approved owner credential has a **Server / instance** base.
   Choose **All access** within the reviewed grants or **Read only**, then
   narrow the selected operations if needed. Policy approval cannot add account
   authority or turn an ordinary credential into owner administration.
5. **Review & create** — check the grouped connection, server/tenant access and
   policy summaries. Use the section's **Edit** action to correct a selection;
   Back/Next preserves the draft. Explicitly approve the displayed result, then
   create the integration and store its one-time secret in the client's
   protected credential store.

### Save or recover the secret delivery

The HTTP MCP setup commits the credential, approved policy and protected
handoff together. If policy persistence fails, the setup leaves no usable
partial integration. A retry of the same approved draft can complete that
setup after the error is resolved.

Save the displayed secret and choose **I have saved it** before the displayed
five-minute handoff deadline. This acknowledges delivery and removes the
server's temporary encrypted copy. The saved credential then keeps its reviewed
normal expiry; Details does not reveal its secret again.

If creation times out or its response is interrupted, keep the same browser
page and approved draft. Choose **Resume setup** or **Retry delivery** to recover
the same credential and policy. The server binds that request to your account
and approved content; retries do not create another integration. A failed
acknowledgement keeps the displayed credential available for another **I have
saved it** attempt.

The browser does not store the secret or pending draft in persistent browser
storage. Reloading or leaving the page does not provide cross-browser recovery.
An unacknowledged handoff becomes unusable at its deadline. If the handoff has
expired or the page was lost, inspect the integration list before starting a
new reviewed setup. Previously acknowledged credentials retain their normal
expiry and are unaffected.

### API and ordinary organization scope

API/CLI/stdio credentials use the same connection and access review with the
Policies step omitted. Ordinary credentials remain single-organization; use a
separate credential/profile for another organization. Owner administration
requires its dedicated explicit consent and is never inferred from selecting
all ordinary permissions.

The final review distinguishes configured access from runtime connectivity.
After storing the secret, verify the exact endpoint, MCP initialization and an
allowed read using the client. A saved policy does not prove that the HTTP MCP
endpoint or an external provider is available.

## Edit an existing credential's access

Sign in as the account that owns the credential and open **Integration → API &
MCP credentials** (`/admin/integration?tab=credentials`; the existing
`/account/integration-credentials` route also works). Find the connection and
choose **Details → Configured access → Edit access**. This browser workflow
supports ordinary API/CLI/stdio and HTTP MCP credentials, including an HTTP MCP
credential already installed in NetClaw.

1. Check the credential's status, purpose, organization, canonical resource and
   **Exact expiry (UTC)** in Details. The secret is shown only at creation and
   cannot be recovered here.
2. Choose **Edit access**. The editor loads the saved permission set and your
   current grantable permissions for that same organization. Use the grouped
   permission selector to add or remove exact grants. Existing selections stay
   visible when searching or switching permission groups.
3. Choose **Review changes**. Inspect the explicit additions, removals and the
   complete resulting permission set. Keep at least one supported permission;
   use **Revoke** when the connection should lose all access.
4. Explicitly confirm the reviewed change, then choose **Apply access changes**.
   The server rechecks your enabled account, current organization permissions,
   credential status and the exact reviewed credential binding. The review is
   valid for five minutes. Expired or concurrently changed access needs a fresh
   review; failed review/apply keeps the editor's draft available for correction.
5. Check the refreshed list and Details permission counts, then **Current
   admission**. Configured grants, currently admitted permissions and blocked
   permissions are shown separately. Use **Refresh admission** after a related
   role or policy change, and verify a permitted read through the existing client.

Only the selected permissions change. The credential ID, secret, owner,
organization, purpose, canonical MCP resource and exact UTC expiry stay the
same; the client continues using its saved credential. A lost apply response
can be reconciled by refreshing saved access; replaying the same valid reviewed
confirmation does not issue another credential or write another change.

Ordinary RatelDesk credentials remain **single-organization**. A new organization
needs a separate credential and client profile. The editor cannot convert an
ordinary credential into instance-wide owner administration, change its
organization, extend its expiry or reactivate an inactive credential. For
instance administration, use the separate [owner-administration consent
flow](configuration.md#approve-an-agent-to-administer-the-instance). Temporary
setup-verification credentials keep their dedicated status-only purpose.

### Owner permissions and MCP policies

Saved credential grants are bounded by the owner's current durable permissions.
The credential editor changes neither the owner's roles nor MCP policies.
If a permission is no longer grantable, restore the required owner role through
the existing administration workflow or explicitly remove that permission from
the proposed credential grant set before reviewing again.

The HTTP MCP creation wizard explicitly approves a base policy with the
credential. The policy bounds its supported operations and expiry within the
owner's current authority and the credential's reviewed grant. Existing
credentials without a policy keep their established account/grant behavior;
upgrading does not silently add or expand a policy.

Three separate checks still apply: the owner's current durable authority, the
credential's approved access, and the active policy's operation bounds. An added
credential permission does not implicitly add an operation to a policy. A
disabled, expired, revoked or deleted base policy denies access; retiring a
policy never returns the credential to its former unconstrained state.

An explicitly approved owner-administration base uses **Server / instance**
scope. It retains the current instance-administrator context while the policy
checks requested operations. The default organization is a context selection,
not the boundary of that owner grant. Historical organization/permission ceiling
policies retain their original restriction, including removal of the
instance-administrator context; they are not silently converted into the new
server policy model.

### Manage policies and exceptions

Open **Administration → MCP Policies** (`/admin/operator-policies`) as an
interactive Helpdesk administrator. The default list shows active base policies;
use the history/state filter to inspect retired records. Select integrations
and organizations by name, and choose operations from the published catalog.
An unresolved historical identity remains identifiable without being rebound to
another account or organization.

Use **Edit** to review a base policy's limits. Closing the editor retains the
draft; choose **Resume draft** to continue or **Discard draft** to clear it.
Add a nested **Deny** exception for selected operations within that base.
Setting the base itself to **Deny** stops all access in its scope; use a nested
exception when only particular operations should be blocked. The exception can
narrow access; it cannot grant access beyond its parent or override a deny with
a higher priority. Multiple MCP operation names can perform the same API action;
denying that action also blocks its aliases so an alternate tool name cannot
bypass the exception. Actions that share a route but have different effects are
checked separately: reading an existing AI conversation requires a valid
`conversationId`, while opening one without that identifier is a mutating action
and is excluded by the Read only preset.

**Disable**, **Revoke** and **Delete** require review of the exact policy
revision. **Delete** removes the policy from normal inventory and future
authorization while retaining a terminal tombstone and its audit history.
Children cannot authorize access after their parent is retired or expires.
Disabled policies can be enabled through a fresh review; revoked and deleted
policies cannot be restored. Expired records are absent from Active inventory
and remain available through the explicit history view.
For bulk cleanup, review the selected records and affected counts; inspect each
reported outcome before retrying a conflict or failed item. Refresh the
credential's admission and verify an allowed read after changing a policy.

Policy management is interactive account administration. A delegated MCP
credential cannot change its own policy or mint a new delegation through these
browser consent endpoints.

The existing browser client-code review remains the way to approve a **new**
client credential. Maintaining an existing credential uses the owner-reviewed
editor above. RatelDesk does not provide NetRatel's `netratel_access`
request/approve/resume reconciliation protocol.

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

