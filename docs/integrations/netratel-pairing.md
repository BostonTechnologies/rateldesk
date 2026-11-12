# Connect NetRatel and RatelDesk

Open **Account → Integration credentials → System connections** at
`/account/integration-credentials` in either product. API & MCP credentials
remain in their separate tab.

1. On either product, select **Generate a Pairing Code**. Copy the displayed
   code to the other product. It expires after five minutes, is single use,
   and a newly generated code immediately replaces the previous code.
2. On the other product, select **Create Connection**. Enter the peer's
   **Address** and **Pairing code**, then select **Pair & connect**. Supported
   addresses include DNS names and IP addresses with HTTP/HTTPS and a port.
   The exchange runs on the server and stays on this page.
3. After **Systems paired**, select the real **NetRatel tenant** and
   **RatelDesk organization**. Choose that organization's customer when
   enabling incident creation. Enter a connection name.
4. Select the independent capabilities you need: **Create incidents**
   (NetRatel → RatelDesk), **Run automation** (RatelDesk → NetRatel), or both.
   Automation is never enabled implicitly.
5. Select **Save** once. It validates the current permissions and selected
   resources, provisions the connector or automation binding, and activates
   the connection. **Configured** describes a saved and authorized mapping;
   **Never tested** remains until the real read-only connection test runs.

Pairing alone permits setup and authorized directory choices. It does not
permit incident creation or automation. Only existing authorized tenants,
organizations and customers are selectable; pairing creates none of them.
There is no consent page, second sign-in, approval checkbox, enable switch or
mandatory test. If Save fails, correct the actionable inline error and retry
the same form. Refresh preserves the existing unconfigured pairing. Its compact
card shows **Setup required** and **Continue setup**; resume that record instead
of generating a replacement code or duplicate connection. Setup opens in a
focused dialog with bounded scrolling and stable actions.

## Manage a connection

The cards separate the provider/connection identity, source tenant and destination
organization, capability directions, configuration state and last test snapshot.
**View** opens the saved configuration and exact connection identity.
**Test connection** checks authenticated access, the current mapping and
selected capabilities without creating an incident or task. Testing is
optional after Save.

A saved **Configured** mapping is not a live health result. The card distinguishes
**Never tested**, a recent passing/failed check and a **Stale snapshot**. A test
older than 15 minutes is stale; the timestamp and expandable **Test details**
retain its original result and any safe diagnostic reference. Refresh retrieves
saved snapshots and does not automatically run a test. Search and the status
filter can isolate configuration work or a particular test state.

**Add mapping** adds another tenant/organization mapping to the already paired
system. It does not replace the selected connection, re-pair the installations
or change existing job bindings. Review and save that new mapping explicitly.

Administration/account entry points preserve the selected tab and authorized
`organizationId` context across navigation and refresh. An organization URL
value is only a hint checked against the current authorized directory. It may
seed a new mapping; it never changes a saved mapping or grants access. A
missing/deleted/unauthorized hint requires an explicit recoverable choice and
does not silently select another organization.

**Delete connection** removes the selected mapping and revokes its local
authority immediately, including when the peer is unavailable. Other mappings
between the same installations remain usable. Deleting the last mapping
also removes the pair credentials. An unused pairing can be deleted. Any
peer cleanup runs automatically; an offline peer has not acknowledged it.
Existing incidents, receipts, Flow runs and task history remain intact.

## Addresses and credentials

Valid HTTPS works with public or private DNS and reverse proxies. Deliberately
entered private HTTP hostnames or IPs use the same two-field form. Normal TLS
certificate and hostname verification remain enabled. The server checks DNS
destinations at connection time, blocks reserved and metadata destinations,
uses fixed protocol routes, refuses redirects and bounds request time and
response size. Separate Web/API origins are resolved through verified peer
metadata; there is no additional API-address input or private-HTTP switch.

Codes and credentials belong outside URLs, logs, notifications and browser
storage. Long-lived credentials stay protected on the server. The pairing
retains each administrator's setup authority and rechecks current permissions
when selecting resources, saving and using a business capability.

## Durable execution and requester upgrades

The requester/deadline upgrade adds forward migrations for stable submission
receipts, identity-bound requester contacts, per-attempt execution deadlines,
reconciliation leases and state versions, operator holds, and application-user
retirement. SQLite and PostgreSQL remain supported. Back up the domain and
identity databases and persistent protection keys, replace older API/worker
replicas, and apply the normal migrations before admitting new work. Preserve
saved connections, tenant mappings, existing runs and protected credentials.
Do not repair identity links or move organizations through SQL. Downgrading an
identity-bound contact database requires restoring its matching backup; managed
contacts can legitimately share an email with unrelated legacy contacts.

Use the exact paired versions and tested revisions listed in the release notes.
The compatible NetRatel execution-receipt contract adds an authenticated read of
the original request/run with the recorded RatelDesk request, task and attempt
headers. It reuses the existing read grant and current saved connection; it does
not require another pairing, credential or admission. Upgrade the provider first
when adopting outcome reads. An older or unavailable provider cannot confirm
termination: RatelDesk records **Timed out — outcome unknown / Needs
reconciliation**, retains the original identifiers and holds dependent work.
It does not infer remote cancellation, invoke ordinary failure/Continue policy,
create a confirmed-failure incident, or resubmit automatically.

A confirmed late outcome for that same attempt recomputes the task and parent
and clears only its uncertainty hold. Historical timeout timing remains visible;
an independent operator hold remains until explicitly released. The durable
continuation resumes eligible work after that release without requiring another
callback. See [execution timing and recovery](netratel-orchestrator.md) and
[application user deletion](../security/rbac.md#application-user-deletion).

## Target-aware self-service automation

For that target-aware repair, the compatible pair is RatelDesk **0.1.3-beta.9** and NetRatel **0.1.5-beta.4**.
Upgrade NetRatel first, complete its ordinary automatic database migrations and
replace older API replicas before admitting modern automation. Then upgrade
RatelDesk. Keep the current pairing, named tenant connections, primary
organization, protected credentials and persistent keys. This upgrade needs no
new credential, scope, manual policy/configuration change or re-pairing.

NetRatel's additive migration permits an absent fixed agent and records the
aggregate target-plan digest on managed bindings. Existing fixed-agent bindings,
in-flight intents, callbacks and history retain their identities. RatelDesk adds
no database migration for this repair and continues to support SQLite and
PostgreSQL. Back up the databases and persistent keys through the normal upgrade
procedure.

For a saved connection with **Run automation**, open the request-service workflow,
select that exact connection and choose **Browse jobs**. Canonical fixed-agent,
explicit-node, HA group, selector and declared runtime-target jobs are projected
from the normal NetRatel Jobs catalogue. Target summaries, required inputs and
readiness describe definitions; online-worker eligibility is checked at invocation.
An empty catalogue can still be opened and refreshed. Bindings retain the exact
connection, canonical job ID, input mappings and runtime settings across reopening.

**Create job** offers current tenant nodes and enabled HA groups, a supported
command runner, executable content, execution policy and optional inputs. Choose
a runner installed on the workers. Failed creation retains the draft and shows
the structured operation/status/code/reference. Creation refreshes the same
connection and selects the resulting ordinary NetRatel job. Existing input-sync
operations preserve the job's identity and execution target.

HA admits one eligible worker. Explicit-node jobs retain their complete frozen
node set and native concurrency/failure policy. Each request-task attempt owns
one durable aggregate parent, persisted before child dispatch. Exact retries
observe that parent; changed payloads conflict and later attempts require a
verified terminal predecessor. Child success cannot complete the RatelDesk task.
Final callbacks bind aggregate terminal readiness and result hashes, include
bounded child summaries/results, and preserve acknowledgement, rejection and
explicit replay. Unknown physical outcomes stay unknown.

Contract 2 is selected by `X-NetRatel-Automation-Contract: 2` on catalogue requests.
Read-only capability and tenant target-choice endpoints use the existing paired
read grant; creation and invocation retain the paired invoke grant. Older
consumers continue receiving fixed-agent catalogue entries. A newer consumer
identifies an older peer through the missing capability endpoint and reports its
fixed-agent limitation; modern creation/execution requires the compatible pair.
A connection test proves current authentication/mapping and reports supported
contract capability without creating or executing a job.

After redeployment, verify both API/Web release identities. Keep the saved
connections, refresh the intended connection, bind the HA job, save/reopen and
submit. Verify one worker and final task status. Repeat for the five-node job,
checking the intended nodes and parent outcome. Create a job from real target and
content choices, independently find it in NetRatel, bind and run it, then inspect
correlation/run links and any structured errors. Hosted source/distribution
verification is separate from this owner deployment acceptance.

## Upgrade from the retired pre-pairing setup

Upgrade both products before pairing. Forward migrations retire previous
NetRatel ↔ RatelDesk setups, revoke their credentials and disable their old
connector/provider bindings. Pair afresh and save each desired mapping using
the steps above. Old permissions are not transferred to new connections.
No manual SQL cleanup, installation identity reset or cleanup wizard is needed.
Mixed-version use of the discarded protocol is unsupported.

Installation and Flow producer identities, signing keys, unrelated API/MCP
credentials, Netclaw connections and business history are preserved. Keep the
normal database and persistent-key backups when upgrading.

After installation, check pairing from either product, Save the intended
mapping, optionally Test, exercise each desired business capability, and
Delete a disposable connection. Published artifact integrity and source-pair
validation are separate from acceptance of your deployed installation.
