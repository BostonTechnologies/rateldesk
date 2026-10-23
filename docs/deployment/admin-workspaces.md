# Administration workspaces

The administration pages keep organization or tenant context visible while you work. Changing tabs retains local drafts; changing context or leaving a page requires saving or explicitly discarding pending changes. Active saves and focused routing dialogs keep their context fixed until they finish.

## Data Management

Open `/admin/resources/data-management` with an administrator or authorized Data Management role. Select the organization and dataset in the rail, then choose a task:

- **Rows** searches and pages through existing rows. **View Raw** reveals one row's payload. Searching does not modify rows.
- **Schema** edits a custom dataset's name, columns, key, display/search columns and active state. Use **Save Dataset** or **Revert**. Column removal changes the schema; **Delete** removes the selected dataset after confirmation of its organization and total row count.
- **Ingest API** creates and revokes credentials for the selected custom dataset. A new key is displayed once. **Ingest script examples** provides the existing schema-specific Bash/curl and PowerShell samples.
- **Graph sync** manages the organization connector, enabled source types and schedule. Open **Connector settings** to edit credentials, then save. **Sync Built-Ins** explicitly runs ingestion using saved settings. **Sync status and schedule details** shows source timestamps, last result, runtime/schedule registration, queue and next-run information. Administrator job links retain their existing destinations.

No sync runs simply because you visit a tab. API ingestion remains a separate existing operation; this workspace does not provide file import/export or a new cleanup/history job system.

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
