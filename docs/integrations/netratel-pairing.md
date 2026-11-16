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

## Automation traceability and internal reports

The coordinated feature requires **RatelDesk 0.1.3-beta.12** and **NetRatel 0.1.5-beta.6** or compatible later readers. Existing v1 flows, keys, accepted receipts and prepared deliveries remain supported.

Upgrade the RatelDesk provider first, using its ordinary SQLite or PostgreSQL migrations and retained database/key backups. Then upgrade NetRatel and apply its PostgreSQL migrations. Keep the saved connection and its protected credentials. The existing admission, callback and receipt mechanisms retain their authority and IDs; business payload keys cannot provide origin context. Do not activate the new writer while older receiver replicas still serve traffic.

In RatelDesk, open **Administration → Templates → NetRatel** (`/admin/templates`). Choose an organization and clone the appropriate explicit system default: job, request, script/task, alert or generic execution. Edit the customer-safe subject and internal body, preview synthetic data, save the draft and publish an immutable revision. Publishing expands and freezes the selected layout. Later draft/layout edits do not alter that revision. Retiring removes a template from new selection while retaining pinned history. Subject tokens are restricted to request/service/definition labels, outcome and totals; diagnostics, AI findings and recovery evidence are internal body tokens.

On the saved system connection, explicitly enable **Browse and preview internal incident templates** when custom templates are required. Saving uses the existing current-authority approval path and grants only `rateldesk.incident-templates.read` for the mapped organization. Standard presentation uses existing incident/receipt authority. No email-administration credential goes to NetRatel.

In NetRatel, edit each intended **RatelDesk: Create incident** action (including individual targets in an action group). Enable **Use human summary and source relations** for the pinned standard presentation. To customize it, enable **Use RatelDesk incident template**, search the scoped catalogue and choose the exact published revision. Preview, validate and publish normally. Existing published versions do not opt in automatically. Priority, category and assignment mappings remain effective; RatelDesk owns the restricted subject and internal report. An incompatible peer, missing/revoked revision or changed connector is an explicit error, never a substituted template.

Upgrading both applications does not enable those action settings. After publishing the updated failure flow, submit a fresh RatelDesk service request so the dispatch captures trusted origin context with the upgraded peer. A body beginning **Original execution facts** uses legacy presentation; inspect the executed flow version and its Create incident action receipt to identify which incident that action created. The request task's `BlockRequest` and `FailRequest` policies retain their separate confirmed-failure escalation and can create an additional incident. Keep the intended request failure policy when configuring the flow.

The NetRatel template tab's **System default to clone** dropdown selects a starting point. **New from default** opens its editor and synthetic preview; **Publish immutable revision** makes the organization-scoped template available to the approved connector. Selecting a default alone does not publish a template or change a flow.

A source dispatch snapshots the authoritative request, service/form/task, submitted-by and requested-for people and exact attempt before the first POST. The configured integration customer and machine actor remain separate; origin people do not become incident listeners. Missing legacy people are displayed as unavailable. Early incident delivery defers on the same key until retained admission/callback evidence proves the exact native execution. An unavailable origin fails explicitly; source-less monitoring/script events produce no invented origin relation.

The incident and source request show an **Automation** tab in ticket activity. It loads the staff-only report when selected and keeps the ticket header and Messages clear. Report navigation uses visibly underlined, keyboard-accessible links. The report contains source outcome, times, target counts, grouped failed activities, permitted excerpts, explicit omissions and real navigation. Failed activities are selected before bounds, so a later failing step is retained. Reports are staff-only records; ordinary subject/summary, notifications, search and public/portal serializers receive the restricted projection. Successful incident delivery does not change the original failed execution. Successful request automation keeps its existing fulfillment/closure behavior.

Inspect the internal report while signed in with staff incident/request-read authority, such as the normal helpdesk administrator. The configured incident customer or integration operator does not acquire that authority from being the incident's recipient. Existing incidents and already prepared deliveries retain their original presentation; activation applies to new deliveries from the published flow.

Neutral **Link ticket** is available in incident, request and change Relations tabs, with scoped type/reference/title search and reciprocal links. It changes no state, SLA, assignment, listener or notification. **Duplicate of** and **Close / Link** remain explicit incident operations. Both ends must be visible and manageable; counts and choices are permission-filtered before paging.

NetRatel indexes only trusted admitted source references and verified committed incident receipts. Search `REQ-DEMO-001` or `INC-DEMO-001` through the top bar, full results or Requests filter. Matches are exact-first, case-insensitive bounded prefixes, scoped to tenant/peer/organization. Request, job execution and flow run are distinct results. Related resources show safe source people/context and implemented request/job/flow routes. IDs such as `639300000000000001` remain decimal strings on JSON boundaries.

The existing local outbox projects associations in bounded transactions. Projection recovery never dispatches an incident. A resumable cursor scans retained bindings (100 per batch), then verified receipt sidecars (50 per batch); pending projections use batches of 32. An unprovable legacy origin is unavailable, not reconstructed from arbitrary payload/log text. The checkpoint records unavailable admission/receipt evidence. Backfill neither runs jobs/flows nor modifies remote tickets or historical source evidence.

For owner retesting after distribution verification: publish/select an internal template, submit a successful isolated request and confirm normal fulfillment, then submit a deliberately failing isolated multi-node directory check. Inspect the safe subject, **5 of 5 targets failed**, actual later activity/exit code, report and reciprocal request relation. Search both tracking references, open the exact job execution and flow, and confirm that source failure and successful incident delivery remain separate. Retry/restart the delivery without another incident, report or relation. Production deployment and owner acceptance remain separate from automated and distribution checks.

See the mirrored [v2 contract](../contracts/rateldesk-incident-create.v2.md) for exact bounds and fingerprint semantics.
