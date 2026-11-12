# Administration workspaces

The administration pages keep organization or tenant context visible while you work. Changing tabs retains local drafts; changing context or leaving a page requires saving or explicitly discarding pending changes. Active saves and focused routing dialogs keep their context fixed until they finish.

## Customers, Organizations and Ticket Categories

Open `/admin/customers`, `/admin/organizations` or `/admin/categories` as an
instance administrator. These directories search, filter, count and sort the
complete authorized dataset before selecting a page. The native pager defaults
to 25 rows and offers 25, 50 and 100. Search, filters, sorting and page position
are retained in the URL when you refresh or return from another route. Editing
or deleting a row refreshes the current query; a page that no longer exists is
clamped to the last available page. Failed refreshes show an error and retain
previously loaded rows.

**Customers** searches names, email and organization and filters customer state
separately from login/invitation state. Access summaries come from a bounded
server projection, including local activation/disabled state and external
invitation expiry. Loading or refreshing the directory never sends an invitation.
Invite/resend remains an explicit eligible action and is revalidated by the
existing invitation endpoint. Local account activation and sign-in are managed
in Team.

**Organizations** searches name, DNS and contact information, with state,
support-organization and saved-orchestration-mapping filters. Support names are
resolved in the page query. An orchestration mapping displays its persisted name
and tenant identifier; a saved mapping is configuration evidence, not a live
connectivity test. A remote catalog outage cannot remove it. The delegated
General & settings / Members & access views retain their organization context
and authority; the instance directory is not exposed to delegated administrators.

**Ticket Categories** searches names/descriptions and provides exact ticket-type,
active/inactive, global/organization and built-in/custom filters. All types is
explicit; creating from All requires choosing a valid type. Organization filters
use a bounded searchable selector. Built-in categories remain read-only. Deleting
a custom category requires confirmation and is blocked while a ticket or child
category references it; make it inactive to preserve historical assignments.
Failed saves retain the dialog and entered values. `/admin/categories0` remains a
compatible route.

The additive `GET /api/v1/admin/directories/{customers|organizations|categories}`
queries return `PagedResponse<T>` with zero-based `page`, validated `pageSize`
(25/50/100), `totalCount` and `items`. Legacy customer/organization array APIs and
bounded category pickers retain their response shapes; the category picker still
includes Service categories where it did before. The admin type filter is exact.

## Data Management

Open `/admin/resources/data-management` with HelpdeskAdmin or the organization-scoped `DataManagement.Admin` permission. The searchable **Organization** control searches the permitted Data Management population. The page separates **Custom datasets** from **Built-in integrations**; their actions and credentials have different scopes.

### Custom datasets

Choose **New dataset** for the large **Details → Schema → Review & create** dialog. The organization stays pinned while the dialog is open. Enter a name, then design fields with the supported Text, Number, Boolean and DateTime types. Choose exactly one key and one display field, enable search for suitable fields, and use the arrows to order them. Names must be unique without regard to case. Renaming a field keeps its key/display role; removing a selected field requires choosing a replacement.

Back retains the draft. Continue and Cancel create nothing. **Create dataset** is the explicit save. Failed saves retain the draft and show validation feedback. If completion is uncertain, choose **Check creation status** before retrying; the retained request identity prevents duplicate datasets. After success, **Create ingestion API key** is optional. **View dataset** or **Done** completes creation without a key.

The selected dataset has a separate detail workspace:

- **Rows** searches and pages through existing rows. **View Raw** reveals one row's payload. Empty data, unmatched searches and failed row loads have separate feedback; a failed search can be retried without changing it.
- **Schema** edits the existing definition, including its name, fields, key, display/search choices and active state. Use **Save Dataset** or **Revert**. **Delete** confirms the organization and total row count before deleting the dataset. Schema editing retains the dataset identity and existing rows.
- **API access** lists key metadata, including active/revoked status, created time and last use. **Create ingestion API key** opens a focused named-key dialog. Copy the one-time secret into your own secret store; closing the dialog clears it from the page. Existing secrets cannot be read back. Revocation requires confirmation. **Ingest script examples** retains schema-specific curl and PowerShell examples with placeholders and copy actions, plus the plain-text editor fallback.

### Built-in integrations

The Microsoft Graph catalog always shows Users, Computers/devices, Groups and SharePoint sites, including sources that have never been imported and datasets with zero rows. An unavailable count is shown as unknown rather than zero. **View data** reuses the row browser with managed schema information; custom key creation is unavailable for these sources.

Choose **Set up Microsoft Graph**, **Manage connection** or **Import now** for **Connection → Choose datasets → Test access → Review & import**. Enter the Entra tenant/application identifiers and a new secret, or explicitly use the existing configured secret for this organization. Stored secrets are never returned to the browser. Draft credentials remain transient until an explicit save.

**Test connection** authenticates and makes bounded sample reads for each selected source. It does not save settings, create datasets or keys, import rows, update sync timestamps, register schedules or enqueue jobs. Authentication and each source's access result appear separately. Changing the connection, credential or source selection invalidates the test. Correct failures and run a fresh test before importing.

**Save and import** is enabled only after the current connection and every selected source pass. The final action saves configuration and starts the existing paginated import. It upserts stable keys and removes rows missing from each selected source (`DeleteMissing`), so importing a genuinely empty source leaves zero rows. Review this replacement behavior before saving. Configuration saved and import completed are reported separately; per-source outcomes retain successful updates if another source fails.

If the response is lost, use **View import results** or reopen the connection to check the recorded run before retrying. Closing a dialog does not cancel server work. A long-interrupted run offers **Reconcile interrupted import** after the server's recovery boundary; reconciliation records the available results without importing. Inspect the source data, then explicitly retest/review a new import. **Retest selected sources** preserves the saved enabled-source selection and recurring schedule.

**Schedule** opens the optional recurring import settings. Cron expressions use UTC. First-run setup does not enable a schedule, and connection edits preserve an existing schedule. A saved schedule and an available runtime are separate states. **Advanced diagnostics** contains runtime registration, queue/job identifiers and privileged Hangfire links, plus **Edit connection without importing** for an explicit configuration-only save.

## Support Routing

Open `/admin/support-routing` as an administrator. The organization and provider/MSP remain visible above three tabs:

- **Routing** focuses on either **Support coverage** or **Notification rules**. Existing add/edit, enable/disable and confirmed delete actions remain next to their entries.
- **Support groups** retains group search, owner/disabled filters and membership administration.
- **Diagnostics** contains the event-specific effective recipient preview and **Bootstrap Defaults**. Bootstrap requires confirmation and reports the existing organization/group/coverage/subscription results.

Changing the visible concern does not change routing precedence. Group eligibility, coverage roles, fallback, subscription audience and provider boundaries continue to be enforced by the existing APIs and resolver.

## Roles

Open `/admin/roles` with the existing role-assignment permission. **Custom roles** opens first; **Built-in roles** is a separate tab. Tab selection is preserved in the URL. Search applies to the selected tab; paginated rows show scope/owner, permission details and explicit assignment counts. Open the permission count or assignment count for details. Built-in roles remain protected; custom editing, cloning, deletion, owner scope and delegated permission ceilings retain their existing restrictions.

## Tenant SLA Settings

Open `/admin/tenant-sla-settings` as an administrator and select a tenant once for all three tabs:

- **Policies** contains matching criteria, response/resolution targets and escalation configuration in the existing create/edit dialogs.
- **Working calendars** contains calendar creation/editing and calendar association. Business-hours and association edits require **Save settings**; **Revert** restores the saved values. An empty association inherits the active tenant calendar, then the active system calendar. An inactive explicit calendar also falls back according to the existing resolver.
- **Reporting** shows subscription schedules, recipients, lookback and active state. Create or edit opens a focused form for cadence, weekday/time, timezone, lookback, recipients and active state. Refresh, enable/disable and confirmed deletion remain available. Saving a subscription does not send a report immediately.

Tab navigation does not change calendar association or recalculate historical deadlines. Existing calendar/timezone calculations, report scheduling and escalation behavior are unchanged.
