# 0.1.1 workspace implementation and acceptance evidence

This is the implementation receipt for release epic #162 and children #163–#177,
dated 2026-10-15. The refreshed starting source was `4e3023c`. It records reviewed
changes and test evidence while integration acceptance is still in progress. It
does not establish a merged PR, release publication, protected promotion or a
production deployment.

PR #147's eligible-assignee search and assignment rules remain part of the
regression boundary. PR #161's single-origin gateway, CLI/MCP setup, configuration,
documentation and navigation shell remain part of the integration boundary.
The integrated assignee, gateway/CLI/MCP/setup and provider results are recorded
below with their exact source boundaries. Final-head and post-merge gates remain
required.

## Integrated evidence superseding earlier receipts

This **pre-merge acceptance snapshot** records observed results through
**2026-10-15 07:26 UTC**. The
historical owning-worktree runs and screenshots below retain their original source
boundaries. Results from reruns overlap and must not be added as distinct tests.
The [machine-readable receipt](assets/workspace-0.1.1/integrated-receipt.json)
records the source pins, commands, job links and outstanding checks.

Both hosted runs used product and test source
`16963fa07129b5fe8862c623edd06f16155b2ef3`. The later local browser rerun used
test source `34210f041b1f23f4bf66fbbbd0827658aa75b49b` against frozen Release
assemblies from `16963fa`; the intervening changes affect four browser test files,
not product code. The separate setup repair proof used the setup test file at
email-worktree commit `e2182e9` (later integrated as `6a00cb2`) against product
`16963fa`; it is not pinned to the browser rerun's `34210f0` test source.
The integration PR is [#178](https://github.com/BostonTechnologies/RatelDesk/pull/178).

| Validation boundary | Executed result |
| --- | --- |
| [Normal CI #595](https://github.com/BostonTechnologies/RatelDesk/actions/runs/38695061632) | **All five normal repository gates passed**: .NET, incident receiver/provider acceptance, public disclosure, layout/reference validation and external PostgreSQL Compose configuration. Eight optional deep jobs were skipped by workflow conditions. |
| Hosted full .NET suite, normal and deep runs | Each recorded **2,121 passed, 0 failed, 8 skipped** (2,129 total). The eight platform/disabled skips match the starting `origin/main`; optional skipped workflow jobs are separate. Both runs include the SQLite and PostgreSQL `Stable011UpgradeTests` cases. |
| Hosted required provider gate, normal and deep runs | Each recorded **128 passed, 0 failed, 0 skipped**, with the eight required test classes verified by `check-service-provider-acceptance.py`. |
| [Deep CI](https://github.com/BostonTechnologies/RatelDesk/actions/runs/38695205537) | **Overall conclusion: failure.** Passing jobs and later local corrections are listed separately below; this run is not complete acceptance. |
| Local integrated Release solution build at `16963fa` | **0 errors, 57 warnings**, 5m01s, SDK **10.0.400**. Warnings include dependency vulnerability advisories and compiler/analyzer warnings; this was not a warning-free build. |
| Stable version inspection at `16963fa` | **14/14** evaluated runtime projects use `Version=PackageVersion=0.1.1`, empty suffix and assembly/file version `0.1.1.0`; **14/14** compiled assemblies use informational version `0.1.1+16963fa…`, the recorded source revision and shared build timestamp. |
| Local final provider regressions at `16963fa` | **22/22 passed**, 0 skipped, covering all 20 ticket-filter provider cases and both category-assignment persistence cases. |
| Local final UI/API regressions at `16963fa` | **44/44 passed**, 0 skipped, including incident email/API boundaries, OpenAPI/version, email administration, list rendering and delegated tenant navigation. |
| Local corrected first-run setup journey against `16963fa` | **1/1 passed**, 0 skipped, 1.5m: SQLite setup/restart, public Scalar, explicit credentials tab, one-time credential disclosure, reader/writer boundaries, revocation, MFA/TOTP and recovery-code sign-in. No build or Docker creation was needed for this rerun. |
| Local corrected mailbox outage and POP stages, tests `1199e742` / product `16963fa` | **1/1 outgoing-outage repair passed** (75.838s) and **1/1 POP lifecycle passed** (162.394s), each with 0 failed, skipped or flaky. The canonical lifecycle script ran against no-build Release publishes, PostgreSQL, GreenMail and the fake SMTP transport. These are two independently rerun stages, not a complete sequential hosted lifecycle pass. |

Hosted .NET jobs installed SDK **10.0.402**, permitted by the existing
`global.json` `latestFeature` roll-forward policy; the local SDK was exactly
10.0.400. Full-suite and focused results cover overlapping tests.

### Integrated browser results

| Suite | Hosted deep run at `16963fa` | Later local frozen-binary rerun, tests `34210f0` / product `16963fa` |
| --- | --- | --- |
| Assignee / #147 regression | **16 passed** | No duplicate rerun claimed. |
| Email | **13 passed + 4 native zoom passed** | No duplicate rerun claimed. |
| Operations/catalogue/navigation | **30 passed + 4 native zoom passed** | No duplicate rerun claimed. |
| Administration | **14 passed + 4 native zoom passed** | No duplicate rerun claimed. |
| Ticket filters | **32 passed, 1 failed** before row-navigation test correction | **33 passed + 4 native zoom passed**. |
| Ticket details/categories/activity | **34 passed, 3 failed** before search/tab synchronization corrections | **37 passed + 4 native zoom passed**. |
| Pending mail | **11 passed, 1 failed** before dialog sequencing correction | **12 passed + 4 native zoom passed**. |

The six later local invocations total **94/94 passed**, **0 failed, skipped,
not run or flaky**, with one worker and zero retries. They completed at
07:13:07 UTC with container-network creation paused. The frozen output hashes
and source boundary are recorded in the receipt. Native zoom uses
`chrome.tabs.setZoom(2)` and checks that the CSS viewport halves; each suite has
four cases across light/dark themes and physical widths 1280/1920. Earlier CSS
zoom or DPR emulation remains separate from these native results. The earlier
native full-page PNGs clipped the physical rendering at the CSS width and are
**not complete visual evidence**; their 24 passing DOM bounds and keyboard cases
remain valid. Test-only capture correction `11925fac` (integrated as `dc9e03b`)
passed a focused **1/1**
case in **13.908s**, covering incident, request and change at native zoom 2,
1280-pixel physical width and 640-pixel CSS viewport. All three corrected PNGs
are 1280×1000; the corrected request image was visually inspected. This focused
rerun does not add a distinct acceptance case or establish complete visual review
of every native image. Corrected captures for all six suites remain for the
final hosted rerun.

The separate contributor/manager fixture extension also passed **7/7**, 0 failed,
skipped or retries, in **45.2s**, at test commit `d5ecdb4` (integrated as `f9679c4`)
against the same frozen Release product `16963fa`. It covers organization-scoped
module User permissions with customer ownership, Manager edits for another
customer in the authorized organization, read-only controls outside that scope,
category Save/Revert without catalogue creation and locked-change restrictions.
These are UI/request observations against a synthetic API; actual API permission
evidence remains in the separate provider tests. The original detail suite covers
HelpdeskAdmin and module Read identities.

Using each suite's latest successful source-specific run, workspace acceptance
contains **162 functional + 24 native zoom = 186 distinct cases**. This total
combines the stated source boundaries; it is not a single final-head hosted run.
The setup journey and two mailbox lifecycle stages are separate. The filter suite
independently proves list → detail → browser Back restoration for incidents,
requests and changes. Expanded create-form submission journeys are not
established by the detail fixture.

### Distribution rehearsal and platform coverage

The deep run passed all three existing Docker build jobs: API, Web and HTTP MCP.
It also passed two actual Compose startup/setup jobs, one for SQLite and one for
PostgreSQL, including sign-in, core ticket CRUD, self-service request, attachment
and public Web discovery. Build success does not establish published images,
immutable release digests or multi-platform registry manifests.

Hosted `package-assets.sh` used the explicit rehearsal version **`0.0.0-pr`**.
Its archive checks passed, followed by native extracted CLI/stdio execution on
**linux-x64** (`ubuntu-latest`), **linux-arm64** (`ubuntu-24.04-arm`) and
**win-x64** (`windows-latest`). These are the three separate successful
[archive runtime jobs](https://github.com/BostonTechnologies/RatelDesk/actions/runs/38695205537).

The separate local **0.1.1** package rehearsal used a clean detached worktree at
`16963fa`. All **seven archives** passed checksums, content/license/documentation
contracts; all **six tool archives** passed assembly metadata, source, architecture
and self-contained runtime checks. The two Linux x64 tools passed **seven native
process invocations**, including version/help, non-TTY setup exit 78 and one
successful stdio MCP initialize response. Linux arm64 and Windows x64 were
inspected locally but **not executed locally**; their native execution evidence
comes from the hosted `0.0.0-pr` rehearsal. Three local deployment Compose renders
passed. No tag, release, uploaded asset, image push or final container digest was
created by this rehearsal.

### Commands and durable evidence

The hosted workflow is [pull-request-validation.yml](../../.github/workflows/pull-request-validation.yml).
Its passing full .NET jobs executed:

```bash
dotnet build Helpdesk.sln --configuration Release --no-restore
dotnet test Helpdesk.sln --configuration Release --no-build \
  --logger "trx;LogFileName=helpdesk-tests.trx" --results-directory TestResults
```

The required provider job executed the filtered test command and then validated
its TRX rather than treating a zero-test result as success:

```bash
dotnet test tests/Helpdesk.Tests/Helpdesk.Tests.csproj --configuration Release --no-build \
  --filter "FullyQualifiedName~IncidentReceiver|FullyQualifiedName~ServiceIdentityHttpTests|FullyQualifiedName~ManagedOrchestrationCallbackHttpTests|FullyQualifiedName~PairingServiceTests|FullyQualifiedName~PairingEndpointTests|FullyQualifiedName~PairingUpgradeTests|FullyQualifiedName~ServiceIdentityMigrationTests" \
  --logger "trx;LogFileName=incident-receiver.trx" --results-directory TestResults/incident-receiver
python3 tools/ci/check-service-provider-acceptance.py TestResults/incident-receiver/incident-receiver.trx
```

The later browser receipt preserves each full command and output directory.
Each invocation used `node node_modules/@playwright/test/cli.js test`, the owning
`playwright.ticket-filters.config.ts`, `playwright.details.config.ts` or
`playwright.pending-mail.config.ts`, `--workers=1 --retries=0 --reporter=list,json`
and Release configuration. The three native invocations used
`playwright.native-zoom.config.ts` with `HELPDESK_NATIVE_ZOOM_SUITE` selecting the
same suite. Hosted archive jobs ran
`tools/release/test-executable-archives.sh release-assets <rid>` on their native
runner. Local package commands were:

```bash
bash /workspace/work/validation/package-rehearsal-isolated.sh
tools/ci/test-deployment-archive-compose-config.sh \
  /workspace/work/validation/package-rehearsal-0.1.1-227gwCh5/rateldesk-deployment-0.1.1.tar.gz
```

Local source receipts are `ci-normal-595-receipt.json`,
`ci-deep-38695205537-receipt.json`, `compiled-versions-final.json`,
`final-provider-regressions/final-provider-regressions.trx`,
`final-ui-regressions/final-ui-regressions.trx`,
`setup-tab-rehearsal-receipt.json`, `browser-final-34210f041b1f/receipt.json` and
`package-rehearsal-0.1.1-227gwCh5/rehearsal-receipt.json`, all beneath
`/workspace/work/validation`. The committed consolidated receipt preserves the
results and canonical hosted job URLs without requiring that scratch workspace.
The mailbox stage source/report records are in `mailbox-repair-local/`; they use
test source `1199e7424ced6d625efef8c50e62aa6a5dd25fd7` and unchanged product
`16963fa`. Each executed `bash tools/ci/run-mailbox-lifecycle.sh` with
`HELPDESK_MAILBOX_PUBLISH_NO_BUILD=true` and `HELPDESK_MAILBOX_SPEC` set to
`tests/ux/mailbox-outgoing-failure-lifecycle.spec.ts` or
`tests/ux/mailbox-pop-lifecycle.spec.ts` respectively.
The role-extension receipt is `details-roles-16963-final/receipt.json`, with
seven cases from `playwright.details-roles.config.ts` and nine synthetic captures.
The focused native artifact correction is recorded in
`native-capture-fix/capture-receipt.json`; its source is test-only `11925fac`,
against unchanged product `16963fa`.

### Historical failures and remaining acceptance

The deep run's failures remain part of the record: details search/tab timing,
filter row navigation, pending-mail dialog sequencing, the baseline setup test's
inactive credentials tab and mailbox outgoing-repair selectors. The first four
now have the passing local reruns above; the failed hosted run is not relabeled.
The mailbox job passed initial lifecycle, forwarding and replica stages, then
the outgoing repair stage timed out after 300 seconds against an obsolete
selector. Later hosted stages were not executed. Corrected outgoing and POP stages
now each passed locally against the frozen product. First-baseline activation,
explicit deployment policy blocked/running, and a complete sequential hosted
mailbox lifecycle rerun remain **pending**.

At this snapshot, final-head hosted checks after all test/documentation corrections,
corrected native captures across all six suites, required review/approval, normal integration merge, exact
merged-main CI and the protected release/promotion process remain **pending**.
The separate general **35-case** baseline UX suite is still running against tests
`f9679c4` and product `16963fa`; its planned count is not a passing result here.
Later PR checks, Actions runs and the external final receipt carry subsequent
results; this dated document does not require another commit to attest its own CI.
No stable tag, release assets/images, anonymous download/pull verification,
immutable digest verification or `latest` promotion is established here. Keep
#177 and #162 open until their applicable acceptance is complete.


## Issue-to-implementation and verification matrix

Web component paths below are relative to `src/HelpDesk.NewWeb/Components`; service paths are relative to `src/HelpDesk.NewWeb`. Test counts are executed
results only when explicitly marked passed; planned case counts are not passes.

| Issue | Implementation and preserved behavior | Owning verification and current status |
| --- | --- | --- |
| #163 | `Pages/SelfService/ServiceBrowser.razor` and scoped CSS: bounded accessible catalogue search, compact toolbar, existing search/card navigation retained. | `ServiceBrowserRenderedTests`; historical owning component harness: 70 passed. Hosted Operations browser: **30 passed + 4 native zoom passed** at `16963fa`; canonical component tests are included in the green full .NET run. |
| #164 | `Services/EmailDisplayDocument.cs`, `TicketMessageContent.cs`, shared `EmailContent`, `IncidentSubjectWithPeek`, `TicketMessages`, email JS, incident peek API and worklog/timeline boundaries: isolated readable original email, preserved sanitized rich content and plain text. | `playwright.email.config.ts`: hosted **13 passed + 4 native zoom passed** at `16963fa`. Owning .NET tests are included in the **2,121-pass** full run; final UI/API selection **44/44 passed**. Separate historical receipt: [email display](email-display-164.md). |
| #165 | Incident/request/change list pages remove obsolete AI-only UI/state while retaining compatible API consumers, stored provenance, Assistant tabs and conversations. | `TicketListRenderedTests` is included in final **44/44** UI/API regressions. Frozen-binary ticket-filter browser suite: **33 passed + 4 native zoom passed**, tests `34210f0` / product `16963fa`. |
| #166 | API `TicketListQuery.cs`, `TicketFilterEndpoints.cs`, `AssigneeQueries.cs`, all three list endpoints; Web `TicketListState`, shared `TicketListFilters`/`TicketFilterLookup`: authorized filters before count/page, stable IDs, bounded lookups, AND across dimensions, OR within dimensions, explicit Unassigned, UTC half-open time ranges, URL restoration and stale-response/selection protection. | `TicketListFiltersTests`: all **20 provider cases passed** within final 22/22; URL/time/rendered/assignee tests are included in the passing full .NET run. Browser: **33 passed + 4 native zoom passed**, including list→detail→Back restoration for all three modules. [Query contract](../contracts/ticket-list-queries.md); [historical visual receipt](workspace-filters-evidence-0.1.1.md). |
| #167 | Shared `TicketDetailsPanel`/`TicketSlaSummary` and all three detail pages: compact read/edit and SLA hierarchy, shared Save/Revert, ticket-specific data and permissions retained. Ticket lifetimes guard delayed saves, refreshes and modal callbacks while same-ticket refresh preserves drafts. | `playwright.details.config.ts`: frozen-binary **37 passed + 4 native zoom passed**, tests `34210f0` / product `16963fa`, including all three types and stale-response regressions. Separate contributor/manager extension: **7/7 passed**, tests `d5ecdb4` / product `16963fa`. |
| #168 | Shared `ServiceCategoryPicker`, create/detail forms and existing `TicketCategoryEndpoints`: hydrate saved IDs, bounded cancellable encoded search, explicit authorized Add action, unavailable selections retained, existing category relationships preserved. | Category journeys passed within the **37-case** detail suite. All **6** SQLite/PostgreSQL `TicketCategoryEndpointsTests` and both corrected category-assignment persistence cases passed; the latter are included in final **22/22** provider regressions. Expanded create-form submission browser journeys are not established by this fixture. |
| #169 | Shared `TicketActivityTabs`: semantic persisted selection, lazy applicable panels, inline count pills, accessible keyboard tab/panel associations and preserved message overrides. | Frozen-binary detail suite: **37 passed + 4 native zoom passed**, including semantic restoration, keyboard use, retained/lazy panels, template drafts, counts and stale callbacks. |
| #170 | `Pages/Ops/OperationsConsole` presents exactly SLA and Workflows tabs; original SLA/Workflow/Hangfire views keep independent authorization, legacy paths/query state and actions. Six workflow categories use one focused table. Notifications moves once to Operations for every authenticated user. | `WorkflowOpsDashboardTests` and role/category rendered tests: historical focused **70 passed**; canonical files included in the green full .NET suite. Hosted Operations browser: **30 passed + 4 native zoom passed** at `16963fa`. [Route/permission/metric inventory](../deployment/operations-console.md). |
| #171 | `Pages/Admin/Resources/DataManagement`: compact organization/dataset rail and Rows/Schema/Graph sync/Ingest API capabilities, existing schema, keys, sync/schedule details and explicit destructive confirmations retained. | Hosted admin browser: **14 passed + 4 native zoom passed** at `16963fa`; existing dataset API authorization tests are included in the passing full .NET run. [Capability inventory](../deployment/admin-workspaces.md); [historical visual receipt](admin-workspace-evidence-0.1.1.md). |
| #172 | `Pages/Admin/SupportNotifications/SupportRoutingManagement` and shared administration panels: Routing, Support groups and Diagnostics with shared context, one explicit bootstrap action, unchanged coverage/subscription/recipient resolver semantics and permissions. | Hosted admin browser: **14 passed + 4 native zoom passed** at `16963fa`; resolver/bootstrap integration tests are included in the passing full .NET run. No production bootstrap was executed. |
| #173 | `Pages/Admin/Role/RoleManagement`: Custom roles first, Built-in roles second, search/paging and permission details, preserved protected role/assignment API rules. | Hosted admin browser: **14 passed + 4 native zoom passed** at `16963fa`; existing role-definition/assignment integration tests are included in the passing full .NET run. |
| #174 | `Pages/Admin/SLA/TenantSlaSettingsAdmin` and shared SLA panels: Policies, Working calendars and Reporting; shared tenant context, explicit calendar Save/Revert and retained full report metadata, calculations and historical deadlines. | Hosted admin browser: **14 passed + 4 native zoom passed** at `16963fa`; existing SLA resolver/business-time/scheduler tests are included in the passing full .NET run. No production report delivery was executed. |
| #175 | `PendingEmails`, shared pending DTOs, API timeline query/actions and outbox services: SQL search/filter/count/page at 25/50/100, bounded organization lookup, explicit selected actions, transactional audited tombstones and submission fencing. Sender/revision/non-delivery/partial-recipient previews remain. Legacy unscoped retry-all returns 409 directing callers to selected actions. | Frozen-binary pending-mail browser: **12 passed + 4 native zoom passed**, tests `34210f0` / product `16963fa`. `PendingEmailQueueTests`: **12** SQLite/PostgreSQL cases passed; timeline authorization: **6 passed**, also included in the green full run. Corrected outgoing-outage and POP stages each passed **1/1** locally; activation, policy modes and the complete sequential hosted lifecycle remain pending. |
| #176 | `Layout/NavMenu`, `HelpdeskNavLink`, `HelpdeskNavGroup`, `TenantAdministrationNavigation`, theme drawer sizing and scoped navigation CSS: single-line common labels, balanced indentation, stable icon/focus geometry, accessible full-name tooltip and responsive drawer. | Hosted Operations **30 + 4 native zoom passed**, widths 320–1920px/both themes, keyboard/long account/mobile Escape; delegated tenant navigation is covered in final **44/44** UI/API regressions and full .NET. |
| #177 | Root `Directory.Build.props` retains prefix 0.1.1 with empty suffix in integration; cumulative `docs/release-notes/0.1.1.md` and existing release/promotion workflows own final acceptance. | **14/14** evaluated and compiled runtime versions verified stable; normal hosted five gates green, .NET **2,121 passed/8 baseline skips**, provider **128 passed/0 skipped**. Both upgrade cases, three native archive platforms, three Docker builds, two Compose startups and local seven-archive 0.1.1 rehearsal passed. Deep run overall failed; final-head/merged-main CI, review, merge, publication and protected promotion remain pending. |

## Historical owning Operations, catalogue and navigation checks

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

The later hosted native Operations run passed **4/4** at `16963fa`. Its command:

```bash
HELPDESK_NATIVE_ZOOM_SUITE=operations HELPDESK_OPERATIONS_CONFIGURATION=Release \
  npx playwright test --config playwright.native-zoom.config.ts
```

## Historical owning ticket detail, category and activity checks

The historical owning browser run through `7c579ec` passed **34 tests, 0 failed, 0
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
browser zoom; the later **4/4 native** result is recorded above.

```bash
PLAYWRIGHT_BROWSERS_PATH=/workspace/work/playwright \
  HELPDESK_E2E_CHROMIUM_PATH=/workspace/work/playwright/chromium-1243/chrome-linux64/chrome \
  node node_modules/@playwright/test/cli.js test \
  --config=playwright.details.config.ts --reporter=list
```

CI sets `HELPDESK_DETAILS_CONFIGURATION=Release`; the fixture, admin frontend and
read-only frontend use ports 18600, 5190 and 5191. Contributor/manager identities and create-form submissions were not independently
exercised by this historical owning suite. The later separate contributor/manager
extension passed **7/7**; the later 33-case filter suite verifies list→detail→browser
Back query restoration for all three modules. Expanded create-form submission
journeys remain outside this detail fixture.

The integrated focused TRX recorded **6 of 6** `TicketCategoryEndpointsTests`
passing across SQLite and PostgreSQL: bounded authorized search/hydration,
inactive selections, scope validation and concurrent normalized duplicate
creation. The two category-assignment persistence cases initially failed because
their shared endpoint harness omitted `ChangeTemplateService` and
`ITimelineEventBus`. The corrected harness rerun passed both cases within the final **22/22** provider
selection at `16963fa`; the full hosted suite is also green. These changes introduce
no database migration.

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

Administration evidence and the complete capability mapping are linked in the
[administration receipt](admin-workspace-evidence-0.1.1.md). Its final owning
browser run passed 14 cases without retries. Later hosted integration at
`16963fa` passed **14 browser + 4 native zoom** cases, recorded above.

Pending-mail comparisons use the preserved baseline and the owning implementation
against synthetic deliveries at 1440px. They show the old unbounded list and the
new 25-row page, query controls and explicit queue actions. The owning browser
suite initially passed 11 cases; the later frozen-binary run passed **12 + 4 native
zoom** cases. These isolated UI captures do not establish final package
version or actual SMTP delivery. Real provider tests use fake mail transports.

| Pending mail | Before | After |
| --- | --- | --- |
| Light | [Baseline](assets/workspace-0.1.1/pending-mail/pending-before-light-desktop.png) | [Paged queue](assets/workspace-0.1.1/pending-mail/pending-light-1440.png) |
| Dark | [Baseline](assets/workspace-0.1.1/pending-mail/pending-before-dark-desktop.png) | [Paged queue](assets/workspace-0.1.1/pending-mail/pending-dark-1440.png) |

## Preserved earlier provider receipt

The earlier hosted required-provider gate at
`b106f77598af49e99a1bbc73539ae2fb207af25d` reported **128 passed, 0 failed, 0
skipped**, with all eight required classes validated, in
[Actions run 38691499933](https://github.com/BostonTechnologies/RatelDesk/actions/runs/38691499933).
It remains historical evidence for that SHA. The later `16963fa` full/provider
results and outstanding final-head/release gates are recorded in the superseding
section above; this older run is not substituted for them.

Independent read-only pending-email review found the legacy bulk retry bypass;
mail commit `5b7ee05` closes both aliases with explicit 409 and regressions proving
the retry service is not invoked. Cancellation/submission CAS, expiry-to-review,
tombstone retry prevention, transactional audit rollback, history retention and
per-target authorization were reviewed without another blocking finding. Source
review does not replace provider race tests or final integrated acceptance.
