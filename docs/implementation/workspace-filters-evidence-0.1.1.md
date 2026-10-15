# Workspace ticket filters: 0.1.1 evidence

Recorded 2026-10-15 UTC for [#165](https://github.com/BostonTechnologies/rateldesk/issues/165) and [#166](https://github.com/BostonTechnologies/rateldesk/issues/166). Source tested: `f58788fd252d0fa4cafeeded814f73a547ce2c20`; the Web assembly was built at `9b520a3`, followed by an API-only projection correction and browser-locator correction. These are local implementation checks; the integrated release report owns final provider and release validation.

The three lists remove their obsolete AI-only controls and generated predicates. External request `aiOnly`, CLI compatibility, AI provenance, audit history and the AI Assistant retain their contracts. The shared filter panel adds Customer, For, State, Assignee, Unassigned, Priority and time filters, named removable chips, search, sort and page size. Full-data predicates run before count, sort and page; values OR within a dimension and AND across dimensions. Authorization remains scoped by module, organization and own-contact access.

Customer is `Ticket.OrganizationId`. For is `CustomerId` on incidents/requests and `RequestedForUserId` on changes. Assignee is `AssignedToId` on incidents/requests and `ImplementorUserId` on changes. Queries preserve stable IDs. Lookup requests are bounded; normal assignee suggestions reuse the assignment picker eligibility rules. Restored chip labels can resolve former assignees only when an authorized ticket still references them.

Time filters use `from <= timestamp < to`, defaulting to updated time with creation fallback. Created time is selectable. Date-only To expands to the next local midnight; local DST gaps and ambiguous wall times are rejected. URLs preserve UTC instants and display timezone. URL navigation, Back/Forward, refresh, query cancellation and selection clearing share one state contract. See the [query contract](../contracts/ticket-list-queries.md), [shared UI](../../src/HelpDesk.NewWeb/Components/Shared/TicketListFilters.razor), [URL/time state](../../src/HelpDesk.NewWeb/Services/TicketListState.cs) and [SQL predicates](../../src/Helpdesk.API/Endpoints/Ticketing/TicketListQuery.cs).

## Executed checks

The real Blazor UI ran against the synthetic loopback API from `tests/ux/ticket-filter-fixture-api.mjs`. The final browser run passed **30**, failed **0**, skipped **0**, in **2.7 minutes**:

- Three module journeys cover full-data filtering beyond the first page, selection clearing, URL Back/Forward/refresh, named chips, keyboard removal and absence of AI-only filters.
- Two regression journeys cover pending search during Back, stale responses, failed queries, Retry and no matches.
- Twenty-four layout cases cover each module at **320, 768, 1440 and 1920 CSS pixels**, in **light and dark** themes, at 900-pixel viewport height; horizontal document overflow is asserted.
- One combined journey covers all dimensions, time validation, local-date conversion to UTC, sorting and 50-row pages.

The Web-only build passed with **0 warnings and 0 errors**. Layout and documentation checks passed: 47 Markdown files, 169 local links and six Compose contracts before this evidence page was added. The separate focused .NET inventory contains 20 provider cases, eight URL/time cases, three rendered-list cases and eight assignee cases. Final SQLite/PostgreSQL and integrated solution results are recorded by the release workstream; this browser receipt does not claim their success.

The exact commands used in `/workspace/work/filters` were:

```bash
export PATH=/workspace/work/dotnet:$PATH
export DOTNET_ROOT=/workspace/work/dotnet
export DOTNET_CLI_HOME=/workspace/work/dotnet-home
export NUGET_PACKAGES=/workspace/work/nuget
export DOTNET_PROCESSOR_COUNT=2
export PLAYWRIGHT_BROWSERS_PATH=/workspace/work/playwright
dotnet build src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -v:q
npx playwright test --config playwright.ticket-filters.config.ts --workers=1
tools/ci/validate-layout.sh
python3 tools/ci/validate-docs.py
```

Release builds use the same Playwright command with `HELPDESK_FILTERS_CONFIGURATION=Release`. The [full receipt](assets/workspace-filters-0.1.1/receipt.json) records commands, source pins, all 54 screenshot paths and local logs. Its original workspace artifact is `/workspace/work/filters-evidence-receipt.json`. Concurrent PostgreSQL test-container network changes produced occasional Chromium disconnect messages; final assertions passed. External font/static resource warnings are retained in the log.

## Representative before and after captures

All captures contain synthetic identities and tickets. Before uses preserved main `4e3023c` UI with only its version suffix changed to 0.1.1. After uses the real shared filters and the same synthetic data. The committed set is eight desktop PNGs; the full receipt additionally identifies all responsive captures. Full-page mobile images are tall because 25 ticket rows render as cards. Element-only mobile images may include the sticky header during automatic screenshot scrolling, so the full-page images are the evidence for those widths.

| List and theme, 1440 × 900 viewport | Before | After |
| --- | --- | --- |
| Incidents, light | [Before](assets/workspace-filters-0.1.1/before-incidents-light-1440.png) | [After](assets/workspace-filters-0.1.1/after-incidents-light-1440.png) |
| Requests, dark | [Before](assets/workspace-filters-0.1.1/before-requests-dark-1440.png) | [After](assets/workspace-filters-0.1.1/after-requests-dark-1440.png) |
| Changes, light | [Before](assets/workspace-filters-0.1.1/before-changes-light-1440.png) | [After](assets/workspace-filters-0.1.1/after-changes-light-1440.png) |
| Changes, dark | [Before](assets/workspace-filters-0.1.1/before-changes-dark-1440.png) | [After](assets/workspace-filters-0.1.1/after-changes-dark-1440.png) |
