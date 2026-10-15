# 0.1.1 workspace implementation and acceptance evidence

This is the implementation receipt for release epic #162 and children #163–#177,
dated 2026-10-15. The refreshed starting source was `4e3023c`. It records reviewed
changes and test evidence while integration acceptance is still in progress. It
does not establish a merged PR, release publication, protected promotion or a
production deployment.

PR #147's eligible-assignee search and assignment rules remain part of the
regression boundary. PR #161's single-origin gateway, CLI/MCP setup, configuration,
documentation and navigation shell remain part of the integration boundary.
The existing assignee browser/API suites and gateway/CLI/MCP/setup tests remain
required; their integrated results are pending in this receipt.

## Issue-to-implementation and verification matrix

Web component paths below are relative to `src/HelpDesk.NewWeb/Components`; service paths are relative to `src/HelpDesk.NewWeb`. Test counts are executed
results only when explicitly marked passed; planned case counts are not passes.

| Issue | Implementation and preserved behavior | Owning verification and current status |
| --- | --- | --- |
| #163 | `Pages/SelfService/ServiceBrowser.razor` and scoped CSS: bounded accessible catalogue search, compact toolbar, existing search/card navigation retained. | `ServiceBrowserRenderedTests`; Operations browser matrix: passed as part of the final 70 component cases and 30 browser cases. |
| #164 | `Services/EmailDisplayDocument.cs`, `TicketMessageContent.cs`, shared `EmailContent`, `IncidentSubjectWithPeek`, `TicketMessages`, email JS, incident peek API and worklog/timeline boundaries: isolated readable original email, preserved sanitized rich content and plain text. | `playwright.email.config.ts`: 13 browser cases passed. Native zoom: 4 passed. Owning .NET tests and integrated acceptance pending. Separate receipt: `docs/implementation/email-display-164.md`. |
| #165 | Incident/request/change list pages remove obsolete AI-only UI/state while retaining compatible API consumers, stored provenance, Assistant tabs and conversations. | `TicketListRenderedTests`; ticket-filter browser suite: 30 passed, 0 failed, 0 skipped (2.7 minutes). Integrated provider/component results pending. |
| #166 | API `TicketListQuery.cs`, `TicketFilterEndpoints.cs`, `AssigneeQueries.cs`, all three list endpoints; Web `TicketListState`, shared `TicketListFilters`/`TicketFilterLookup`: authorized filters before count/page, stable IDs, bounded lookups, AND across dimensions, OR within dimensions, explicit Unassigned, UTC half-open time ranges, URL restoration and stale-response/selection protection. | `TicketListFiltersTests`, `TicketListStateTests`, `TicketListRenderedTests`, `AssigneeSearchEndpointsTests`: 39 planned focused cases, final result pending. `playwright.ticket-filters.config.ts`: 30 passed, 0 failed, 0 skipped; provider/component results pending. Contract: `docs/contracts/ticket-list-queries.md`; separate receipt: `docs/implementation/workspace-filters-evidence-0.1.1.md`. |
| #167 | Shared `TicketDetailsPanel`/`TicketSlaSummary` and all three detail pages: compact read/edit and SLA hierarchy, shared Save/Revert, ticket-specific data and permissions retained. Ticket lifetimes guard delayed saves, refreshes and modal callbacks while same-ticket refresh preserves drafts. | `playwright.details.config.ts`: 34 passed, 0 failed, 0 skipped after review fixes, including all three ticket types and stale-response regressions. Final integrated/native zoom checks remain separate. |
| #168 | Shared `ServiceCategoryPicker`, create/detail forms and existing `TicketCategoryEndpoints`: hydrate saved IDs, bounded cancellable encoded search, explicit authorized Add action, unavailable selections retained, existing category relationships preserved. | Category browser journeys passed within the 34-case detail suite. `TicketCategoryEndpointsTests`: 6 SQLite/PostgreSQL cases passed, including concurrent duplicates. Two category-assignment provider cases await the corrected harness rerun. |
| #169 | Shared `TicketActivityTabs`: semantic persisted selection, lazy applicable panels, inline count pills, accessible keyboard tab/panel associations and preserved message overrides. | The 34-case detail suite passed semantic restoration, keyboard use, lazy/retained panels, template drafts, count states/bounds and stale child-callback regressions. |
| #170 | `Pages/Ops/OperationsConsole` presents exactly SLA and Workflows tabs; original SLA/Workflow/Hangfire views keep independent authorization, legacy paths/query state and actions. Six workflow categories use one focused table. Notifications moves once to Operations for every authenticated user. | `WorkflowOpsDashboardTests`, role/category rendered tests and `playwright.operations.config.ts`: 70 component cases and 30 browser cases passed after the review fixes described below. Route/permission/metric inventory is in [Operations console](../deployment/operations-console.md). |
| #171 | `Pages/Admin/Resources/DataManagement`: compact organization/dataset rail and Rows/Schema/Graph sync/Ingest API capabilities, existing schema, keys, sync/schedule details and explicit destructive confirmations retained. | `playwright.admin.config.ts` and existing dataset API authorization tests; final browser/integrated results pending. Inventory: `docs/deployment/admin-workspaces.md`. |
| #172 | `Pages/Admin/SupportNotifications/SupportRoutingManagement` and shared administration panels: Routing, Support groups and Diagnostics with shared context, one explicit bootstrap action, unchanged coverage/subscription/recipient resolver semantics and permissions. | Admin browser suite plus existing resolver/bootstrap tests; final results pending. |
| #173 | `Pages/Admin/Role/RoleManagement`: Custom roles first, Built-in roles second, search/paging and permission details, preserved protected role/assignment API rules. | Admin browser and existing role-definition/assignment tests; final results pending. |
| #174 | `Pages/Admin/SLA/TenantSlaSettingsAdmin` and shared SLA panels: Policies, Working calendars and Reporting; shared tenant context, explicit calendar Save/Revert and retained full report metadata, calculations and historical deadlines. | Admin browser plus existing SLA resolver/business-time/scheduler tests; final results pending. |
| #175 | `PendingEmails`, shared pending DTOs, API timeline query/actions and outbox services: SQL search/filter/count/page at 25/50/100, bounded organization lookup, explicit selected actions, transactional audited tombstones and submission fencing. Sender/revision/non-delivery/partial-recipient previews remain. Legacy unscoped retry-all returns 409 directing callers to selected actions. | `playwright.pending-mail.config.ts`: 11 passed; native zoom: 4 passed. `PendingEmailQueueTests`: 12 planned SQLite/PostgreSQL cases; transport gates and API authorization/mixed-action tests await integrated execution. Both providers receive additive browse indexes; existing timeline content is retained. |
| #176 | `Layout/NavMenu`, `HelpdeskNavLink`, `HelpdeskNavGroup`, `TenantAdministrationNavigation`, theme drawer sizing and scoped navigation CSS: single-line common labels, balanced indentation, stable icon/focus geometry, accessible full-name tooltip and responsive drawer. | `NavMenuRenderedTests`; Operations browser matrix passed at 320–1920px in both themes, including keyboard focus, long account name and mobile Escape. Native zoom acceptance remains separately tracked below. |
| #177 | Root `Directory.Build.props` retains prefix 0.1.1 with empty suffix in integration; cumulative `docs/release-notes/0.1.1.md` and existing release/promotion workflows own final acceptance. | All shipped-project evaluated versions, full solution/provider/upgrade checks, exact merged-main CI, tag/publication and protected promotion remain pending here. No release/merge claim is made. |

## Executed Operations, catalogue and navigation checks

Environment: Linux, pinned .NET SDK 10.0.400, `DOTNET_PROCESSOR_COUNT=2`, Chromium
from the Playwright installation. All browser identities, tickets, organizations,
mail responses and action targets are synthetic. The fixture API is loopback
port 18400; actual Blazor hosts use ports 5170–5173 for admin, SLA-only,
Workflow-only and notification-only identities. Authentication/session handling
and the authenticated Web proxy execute normally.

The final component result was **70 passed, 0 failed, 0 skipped**, 16 seconds.
The earlier receipt had 67 passing cases in 22 seconds; the final run includes
three additional SLA review regressions. The temporary `Operations.Focus.csproj` harness compiled the canonical
`ServiceBrowserRenderedTests.cs`, `NavMenuRenderedTests.cs` and
`WorkflowOpsDashboardTests.cs` against the actual Web/Shared assemblies, avoiding
a duplicate API dependency build. This is not a full canonical solution result.
The owning canonical test files remain in `tests/Helpdesk.Tests/NewWeb`.

The final browser result at `b1d4a60` was **30 passed, 0 failed, 0 skipped**,
2.6 minutes, after the SLA and navigation review fixes.
It asserts actual search/clear/empty behavior and bounds; one focused diagnostic
table; six workflow categories; legacy routes and query/Back/Forward restoration;
seven-day compliance numerator/denominator fixtures; notification deep links;
Hangfire settings save, proxy asset GET and action POST; denied independent
views; unavailable/refresh recovery; navigation labels/icons/long-name tooltip;
keyboard focus and mobile drawer Escape. Matrix widths are 320, 390, 768, 1024,
1440 and 1920 in both themes. Its 200% test uses CSS root zoom; native zoom is
separate below.

Exact executed commands (workspace paths record the local harness/environment):

```bash
export PATH=/workspace/work/dotnet:$PATH
export DOTNET_ROOT=/workspace/work/dotnet
export DOTNET_CLI_HOME=/workspace/work/dotnet-home
export NUGET_PACKAGES=/workspace/work/nuget
export DOTNET_PROCESSOR_COUNT=2
dotnet build src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj -m:1 -nr:false \
  -p:UseSharedCompilation=false --no-restore --nologo
dotnet test /workspace/work/operations-focus/Operations.Focus.csproj -m:1 -nr:false \
  -p:UseSharedCompilation=false -p:BuildProjectReferences=false --no-restore \
  --logger 'trx;LogFileName=operations-focus.trx'
PLAYWRIGHT_BROWSERS_PATH=/workspace/work/playwright \
  npx playwright test --config playwright.operations.config.ts
tools/ci/validate-layout.sh
git diff --check
```

The completed Web build had **0 warnings and 0 errors**. Layout validation
passed, including 47 Markdown files, 169 local links and 6 Compose contracts at
that source revision. The new receipt/assets passed a fresh documentation check: 48 Markdown files and
183 local links.

Peer review subsequently identified that SLA tenant lookup errors were cleared
by metric refresh and an obsolete request could reread a disposed cancellation
source. The focused fix retains an independent lookup warning with Refresh
recovery, captures the cancellation token and query URLs before awaiting, and
adds lookup failure/recovery and stale tenant-response regressions. The post-fix Web build passed with **0 warnings and 0 errors**. The focused
component rerun passed **70 tests, 0 failed, 0 skipped**, in 16 seconds, including
all three added regressions. The post-fix browser run completed **13 passed, 17 failed**: three five-second
interactive-readiness timeouts, two `ERR_NETWORK_CHANGED` navigation failures
during concurrent PostgreSQL container-network activity, and twelve navigation
bounds failures showing a four-pixel common-label allocation gap. It is not a
passing receipt. The explicit interactive wait is now 30 seconds; product
assertions and retry settings are unchanged. Nested drawer indentation has been
reduced and the bounds test also checks an explicit Arial fallback font. The subsequent clean
rerun passed **all 30 cases**, including every fallback-font assertion, with
network creation paused. The affected representative screenshots below were
refreshed from that passing run. This supersedes the earlier 30-case pass on
pre-review source; reruns are not added together as distinct acceptance cases.

Native Operations command, **pending** on the final integrated source:

```bash
HELPDESK_NATIVE_ZOOM_SUITE=operations HELPDESK_OPERATIONS_CONFIGURATION=Release \
  npx playwright test --config playwright.native-zoom.config.ts
```

## Executed ticket detail, category and activity checks

The final owning browser run through `7c579ec` passed **34 tests, 0 failed, 0
skipped**, with no retries, in 1.6 minutes. The Web build passed with **0 warnings
and 0 errors** in 1 minute 22 seconds using the pinned SDK and environment above.
Logs: [browser results](assets/workspace-0.1.1/details/browser-results.log) and
[Web build](assets/workspace-0.1.1/details/web-build.log).

The suite uses actual Blazor pages with a loopback synthetic API and HelpdeskAdmin
and module Read identities. It exercises category add/remove/save/reload/revert
for all ticket types; confirmed catalogue creation; rejected saves and dirty
navigation; semantic tab restoration and keyboard use; retained change-template
drafts; exact, abbreviated and unavailable counts; read-only listener/category
controls; authorized listener removal; lifecycle restrictions; SLA pause/resume
without losing drafts; delayed searches, background refreshes and saves through
A→B→A navigation; modal cancellation; workflow filter races; and late child-panel
count callbacks. Responsive checks cover 320, 768 and 1920px, including a long
assignee and 26 selected categories. Its 200% checks use **CSS zoom**, not native
browser zoom; native acceptance remains separately tracked below.

```bash
PLAYWRIGHT_BROWSERS_PATH=/workspace/work/playwright \
  HELPDESK_E2E_CHROMIUM_PATH=/workspace/work/playwright/chromium-1243/chrome-linux64/chrome \
  node node_modules/@playwright/test/cli.js test \
  --config=playwright.details.config.ts --reporter=list
```

CI sets `HELPDESK_DETAILS_CONFIGURATION=Release`; the fixture, admin frontend and
read-only frontend use ports 18600, 5190 and 5191. Separate contributor/manager
identities, create-form submissions and list→detail→browser Back query restoration
are not independently exercised by this owning suite.

The integrated focused TRX recorded **6 of 6** `TicketCategoryEndpointsTests`
passing across SQLite and PostgreSQL: bounded authorized search/hydration,
inactive selections, scope validation and concurrent normalized duplicate
creation. The two category-assignment persistence cases initially failed because
their shared endpoint harness omitted `ChangeTemplateService` and
`ITimelineEventBus`. The harness registrations are corrected; their rerun remains
pending in this receipt. These changes introduce no database migration.

## Synthetic before/after evidence

The baseline Web publish was preserved from `4e3023c` before integration UI
changes. The updated captures use the isolated Operations implementation branch.
Both run against the same local synthetic fixture. Badges/version strings in
these visual artifacts are not evaluated release-version evidence. Representative
PNG pairs below cover every affected Operations/catalogue/navigation page group;
the complete owning browser matrix captures additional widths/themes. No customer
data, production credentials, live recipients or production instances are used.

| View | Before | After |
| --- | --- | --- |
| Catalogue, light, 1440px | [Baseline](assets/workspace-0.1.1/before-catalogue-light-1440.png) | [Bounded search](assets/workspace-0.1.1/after-catalogue-light-1440.png) |
| SLA, dark, 390px | [Baseline](assets/workspace-0.1.1/before-sla-dark-390.png) | [Compact metrics and focused table](assets/workspace-0.1.1/after-sla-dark-390.png) |
| Workflows, light, 1440px | [Baseline](assets/workspace-0.1.1/before-workflows-light-1440.png) | [Category selection](assets/workspace-0.1.1/after-workflows-light-1440.png) |
| Background jobs, dark, 390px | [Baseline](assets/workspace-0.1.1/before-background-jobs-dark-390.png) | [Workflows subordinate view](assets/workspace-0.1.1/after-background-jobs-dark-390.png) |
| Navigation, light, 1440px | [Baseline](assets/workspace-0.1.1/before-navigation-light-1440.png) | [Single-line labels](assets/workspace-0.1.1/after-navigation-light-1440.png) |
| Navigation, dark, 390px | [Baseline](assets/workspace-0.1.1/before-navigation-dark-390.png) | [Responsive drawer](assets/workspace-0.1.1/after-navigation-dark-390.png) |

Ticket detail pairs below use the same synthetic records and 1440 × 1000 viewport.
Before captures use the preserved `4e3023c` UI with the integration branch's
version-only 0.1.1 display update. After captures use detail implementation
`7c579ec`, whose isolated branch still displays beta.22. These labels do not
establish packaged release versions. The captures show hydrated active/inactive
categories, shared Details/SLA controls and activity tabs.

| Detail view | Before | After |
| --- | --- | --- |
| Incident, light | [Baseline](assets/workspace-0.1.1/details/before-incidents-light.png) | [Updated](assets/workspace-0.1.1/details/after-incidents-light.png) |
| Incident, dark | [Baseline](assets/workspace-0.1.1/details/before-incidents-dark.png) | [Updated](assets/workspace-0.1.1/details/after-incidents-dark.png) |
| Request, light | [Baseline](assets/workspace-0.1.1/details/before-requests-light.png) | [Updated](assets/workspace-0.1.1/details/after-requests-light.png) |
| Request, dark | [Baseline](assets/workspace-0.1.1/details/before-requests-dark.png) | [Updated](assets/workspace-0.1.1/details/after-requests-dark.png) |
| Change, light | [Baseline](assets/workspace-0.1.1/details/before-changes-light.png) | [Updated](assets/workspace-0.1.1/details/after-changes-light.png) |
| Change, dark | [Baseline](assets/workspace-0.1.1/details/before-changes-dark.png) | [Updated](assets/workspace-0.1.1/details/after-changes-dark.png) |

Read-only dark views: [incident](assets/workspace-0.1.1/details/reader-incidents-dark.png),
[request](assets/workspace-0.1.1/details/reader-requests-dark.png),
[change](assets/workspace-0.1.1/details/reader-changes-dark.png).
Responsive dark incident views: [320px](assets/workspace-0.1.1/details/details-320-dark.png),
[768px](assets/workspace-0.1.1/details/details-768-dark.png),
[1920px](assets/workspace-0.1.1/details/details-1920-dark.png).

## Remaining integrated acceptance

The preserved hosted required-provider gate at
`b106f77598af49e99a1bbc73539ae2fb207af25d` reported **128 passed, 0 failed, 0
skipped**, with all eight required test classes validated, in
[Actions run 38691499933](https://github.com/BostonTechnologies/rateldesk/actions/runs/38691499933).
This receipt is for that exact historical SHA. Final-candidate full solution and
provider validation remain pending; it is not substituted for those gates.

- `playwright.native-zoom.config.ts` is the explicit native-browser zoom gate,
  with `HELPDESK_NATIVE_ZOOM_SUITE=operations|admin|details|email|ticket-filters|pending-mail`.
  It uses `chrome.tabs.setZoom(2)` and verifies `innerWidth` is half the physical
  viewport, rather than substituting CSS zoom or device scale. Email and pending
  mail each reported 4 passing cases (light/dark, 1280/1920px); Operations,
  administration, details and filters native results are pending here.
- The owning detail browser suite passed all 34 cases after review fixes; its
  final combined-source checks remain pending. Final admin browser results and
  review follow-ups are pending. The ticket-filter browser suite reported 30
  passing cases; its integrated provider/component results remain pending.
  Their provisional or interrupted runs are not counted as acceptance.
- Full pinned-SDK `dotnet build Helpdesk.sln` / `dotnet test Helpdesk.sln`, both
  provider suites, authorization, migrations, public-disclosure/reference gates,
  #147/#161 regressions and final combined-source browser checks are pending.
- Beta.22-to-stable upgrade verification must retain settings, identities,
  tickets, assignments, category relationships, mail states and migration
  histories on SQLite and PostgreSQL. Results are pending.
- Required PR/main checks and approvals, one normal integration merge, exact
  merged-main CI, immutable stable tag/assets/images, anonymous verification and
  the separate protected stable/latest promotion are not established by this
  receipt. Keep #177 and #162 open until applicable acceptance is complete.

Independent read-only pending-email review found the legacy bulk retry bypass;
mail commit `5b7ee05` closes both aliases with explicit 409 and regressions proving
the retry service is not invoked. Cancellation/submission CAS, expiry-to-review,
tombstone retry prevention, transactional audit rollback, history retention and
per-target authorization were reviewed without another blocking finding. Source
review does not replace provider race tests or final integrated acceptance.
