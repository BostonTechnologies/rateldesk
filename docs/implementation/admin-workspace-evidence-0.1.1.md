# Admin workspace evidence for 0.1.1

Captured 2026-10-15 for issues [#171](https://github.com/BostonTechnologies/RatelDesk/issues/171), [#172](https://github.com/BostonTechnologies/RatelDesk/issues/172), [#173](https://github.com/BostonTechnologies/RatelDesk/issues/173) and [#174](https://github.com/BostonTechnologies/RatelDesk/issues/174). Current usage is documented in the [administration workspaces guide](../deployment/admin-workspaces.md).

## Source and validation receipt

The before captures use the original UI at `origin/main` commit `4e3023c`, built in Release with only the shared version advanced to `0.1.1`. The after captures use the isolated `codex/workspace-admin` Debug worktree: final source change `38c06f6`, final fixture revision `ee767c2`. Its unchanged version file still displays **`v0.1.1-beta.22`**. The differing badges reflect separate build inputs; these captures are UI evidence, not proof of an integrated stable release or promotion.

| Check | Result |
| --- | --- |
| Pinned .NET SDK | `10.0.400` |
| Focused Web Debug build | Passed; **0 warnings, 0 errors**, 50.28 seconds |
| Admin browser suite | **14/14 passed**, 0 skipped, 0 flaky, 80.788 seconds |
| Responsive after captures | 32: four workspaces × light/dark × 320/768/1440/1920 pixels |
| Additional focused capture | Schema editor at 320 pixels; 33 valid after captures total |
| Before captures | Eight: four workspaces × light/dark at 1440 pixels |

The [Playwright configuration](../../playwright.admin.config.ts) launches real Blazor hosts with admin, delegated and read-only identities against a [synthetic loopback fixture](../../tests/ux/admin-fixture-api.mjs). The [browser cases](../../tests/ux/admin-workspaces.fixture.ts) cover keyboard tabs and complete selected-tab bounds at 320 pixels, accessible schema controls, role metadata and permission ceilings, paging/search, failed saves and Revert, dirty-context confirmation, stale responses, retained Graph/API-key drafts, report editing, explicit calendar saves and routing cancellation/toggle payloads. API authorization and SQLite/PostgreSQL persistence require the separate integrated provider checks.

```bash
dotnet build src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj -m:1 -nr:false --no-restore
npx playwright test --config playwright.admin.config.ts
```

All identities and records shown below are synthetic. No production Graph ingestion, report delivery or routing bootstrap was run. Native **200% browser zoom belongs to the separate release zoom suite**. An auxiliary CDP viewport/DPR capture was rejected after visual review and is excluded from this report and its assets.

## Matching desktop comparisons

Each pair uses the same synthetic records, light theme and 1440-pixel viewport. Images preserve their original full-page height; the shorter after views illustrate the task separation.

| Workspace | Before | After |
| --- | --- | --- |
| Data Management: schema/rows, ingestion and Graph configuration become focused tasks | ![Original Data Management at 1440 pixels](assets/admin-workspaces-0.1.1/before-datasets-light-1440.png) | ![Focused Data Management at 1440 pixels](assets/admin-workspaces-0.1.1/after-datasets-light-1440.png) |
| Support Routing: coverage/rules, groups and diagnostics have separate tabs | ![Original Support Routing at 1440 pixels](assets/admin-workspaces-0.1.1/before-routing-light-1440.png) | ![Focused Support Routing at 1440 pixels](assets/admin-workspaces-0.1.1/after-routing-light-1440.png) |
| Roles: Custom roles opens first; authoritative built-in roles have their own tab | ![Original Roles at 1440 pixels](assets/admin-workspaces-0.1.1/before-roles-light-1440.png) | ![Custom-first Roles at 1440 pixels](assets/admin-workspaces-0.1.1/after-roles-light-1440.png) |
| Tenant SLA: policies, calendars and reporting share one tenant context | ![Original Tenant SLA settings at 1440 pixels](assets/admin-workspaces-0.1.1/before-sla-light-1440.png) | ![Focused Tenant SLA settings at 1440 pixels](assets/admin-workspaces-0.1.1/after-sla-light-1440.png) |

## Narrow schema editor

At 320 pixels, the selected tab stays fully visible; schema rows expose field labels and accessible names for name, type, key, display, search and removal. Save, Revert and Delete remain reachable. This additional image is from the final passing keyboard/accessibility case.

![Dataset schema editor with labeled controls at 320 pixels](assets/admin-workspaces-0.1.1/after-dataset-schema-light-320.png)

## Capability and authorization inventory

Inspected original route components, child editors, `ResourceDatasetService`, `SupportNotificationAdminClient`, role-definition endpoints/model, tenant SLA/policy/calendar/report endpoints and `WorkingCalendarResolver` before editing. No database/schema or authorization policy changes are required by the presentation work.

### Data Management

Route: `/admin/resources/data-management`, `DataManagement.razor`. Route roles: HelpdeskAdmin or DataManagement.Admin; `/api/v1/resources/datasets` retains DataManagementAccess and existing per-organization checks. Organization context continues to come from `auth/me` and authorized `ticketing/organizations`. Persistence remains DatasetDefinition/columns, DatasetRow, DatasetIngestCredential and TenantGraphDatasetSettings through the existing dataset service.

| Existing capability/location | New location |
| --- | --- |
| Tenant context and Custom/Built-in dataset search/list | Compact organization/dataset rail; same stable dataset IDs and Graph visibility rules |
| Built-in tab: Graph tenant/client/secret and users/devices/groups/sites switches | Graph sync → Connector settings disclosure |
| Built-in tab: Save Graph settings | Graph sync → Save Graph Settings / Revert settings |
| Built-in tab: explicit sync job initiation | Graph sync → Sync Built-Ins; only explicit action, disabled during active operation |
| Sync Settings dialog: tenant schedule enable/cron/runtime details | Same focused dialog from Graph sync |
| Sync status, per-source timestamps, schedule registration/runtime/provider/queue/last/next details | Graph sync → Sync status and schedule details disclosure |
| Administrator Hangfire Ops and dashboard links | Graph sync; original admin condition and `/ops/hangfire`, `/hangfire` paths |
| Custom Data → Dataset: name, column type/key/display/search/order, add/remove columns, active state | Schema tab, same DTO/schema save validation, explicit Save/Revert |
| Delete dataset | Schema → explicit Delete confirmation with dataset, organization and unfiltered row count |
| Custom/Built-in row browsers, row search/count and raw payload/updated timestamp | Shared Rows tab, encoded cancellable search, server page size 50 and reachable pagination |
| Create/revoke ingest keys, one-time key display, names/created/last-used/active metadata | Ingest API; revocation confirmation preserved as explicit mutation |
| Bash/curl and PowerShell schema-derived samples, Monaco/plain fallback | Ingest API → Ingest script examples disclosure |

There are no upload/import, export/download, dry-run, cleanup-job, progress-history or resumable-history operations in this route/service. No invented tabs/actions were added. Existing Graph status/schedule metadata is retained. Tabs retain drafts; context changes require explicit discard; navigation cannot reset active work. Read completions are checked against context, row queries cancel superseded requests; sync HTTP failure is no longer mislabeled as zero-dataset success.

### Support Routing

Route: `/admin/support-routing`, `SupportRoutingManagement.razor`. Route and groups, members, coverage, notification-subscriptions, bootstrap/preview APIs remain HelpdeskAdmin-only. No delegated access is invented. Existing support-group/member, organization coverage, subscription and recipient-preference persistence, audit/events, eligibility and resolver behavior are untouched.

| Existing capability/location | New location |
| --- | --- |
| Organization selector, provider/MSP, active recipient/coverage/subscription summaries | Shared compact context toolbar above tabs |
| Organization Routing: coverage route create/edit/enable/disable/delete, provider/group/role | Routing → Support coverage; same focused dialogs, confirmations and DTOs |
| Organization Routing: event/recipient/channel subscription create/edit/enable/disable/delete | Routing → Notification rules; same focused dialogs and recipient eligibility |
| Support Groups: search/owner/disabled filters, create/edit/enable/disable/delete, membership editor | Support groups; same owning-org/member semantics, paginated table |
| Event selector and effective recipient preview, grouped recipients and details | Diagnostics → Preview event / Recipient Preview |
| Toolbar and Tools duplicate Bootstrap Defaults actions and result counts | Diagnostics → single explicit Bootstrap Defaults action and confirmation/result summary |

No priority/precedence mutation or sorting persistence introduced. Existing visual ordering stays separate from resolver semantics. Dialog lifetime and mutation lock the context; cancelled or disposed dialogs cannot submit into a changed context. Routing/preview results are generation-checked. Tabs remain side-effect free.

### Roles

Route `/admin/roles` retains Tenant.Roles.Assign or HelpdeskAdmin access and the existing scoped role-definition/assignment APIs. `IsBuiltIn` determines tab membership; `IsProtected` and `IsBuiltIn` both gate mutations. Initial tab is Custom roles, query `tab=custom|built-in` preserves browser navigation. Existing permission ceiling, owner scope, identifiers and assignments remain unchanged. Table search and pagination cover authorized records, permission detail action replaces chip walls; assignment details retain their existing dialog/API.

### Tenant SLA Settings

Route `/admin/tenant-sla-settings` and its APIs remain HelpdeskAdmin-only. Shared tenant selector above Policies / Working calendars / Reporting. Policies preserve list/matching/response/resolution/escalation dialogs and delete confirmation. Working calendars preserves timezone, weekly ranges, exceptions/holidays, active state and explicit calendar association; empty association accurately describes resolver fallback (active tenant calendar, then active system calendar). Association edits now require explicit Save/Revert. Reporting provides summaries and a focused create/edit form for cadence, weekday/time, timezone, lookback, recipients and active state; clones retain non-email targets and attachment/filter metadata. Refresh/enable/disable/delete stay available, delete confirms. Scheduler/calculation services and historical deadlines are untouched. Tenant component identity, captured payload context, dirty guards and disposed/read-generation checks prevent stale data or saves crossing tenants.

### Validation ownership

`playwright.admin.config.ts` runs real Blazor hosts on loopback against synthetic `tests/ux/admin-fixture-api.mjs`. `tests/ux/admin-workspaces.fixture.ts` covers metadata, tab restoration, assignment details, draft preservation/discard, row pagination/stale search, failed saves, delete/bootstrap cancellation, report save context, calendar non-autosave, delegated permission choices and denied routes. It captures both themes at 320/768/1440/1920; root integration separately verifies native browser zoom at 200%. These are presentation checks; root integration owns real API/provider suites and full CI, with existing DatasetEndpointsAuthorizationTests, SupportNotificationRecipientResolverTests/BootstrapperTests, role-definition tests and SLA business-time/resolver/scheduler coverage retained.
