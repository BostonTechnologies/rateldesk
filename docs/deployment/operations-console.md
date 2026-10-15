# Operations console

Open **Operations → Operations console** (`/ops`). SLA and Workflows are the two
main tabs. Notifications remains a separate destination at `/notifications`,
available to every signed-in user, including users without console access.

Existing bookmarks `/ops/slas`, `/ops/workflows` and `/ops/hangfire` open the
corresponding tab or Background jobs view. Their query strings remain available;
tab links retain tenant/type/threshold/metric and unrelated query state. Browser
Back and Forward restore the selected tab/category and SLA filters.

| Previous destination or capability | Current location | Existing access retained |
| --- | --- | --- |
| `/ops/slas`: tenant, type, threshold and metric filters | SLA toolbar | Incident, Request or Change Write/Manager; HelpdeskAdmin |
| Resolution/response compliance | SLA summary | Same API-scoped access and tenant filtering |
| Near-breach and breached ticket tables and Open links | SLA ticket-view selector | Same ticket access |
| `/ops/workflows`: overdue, escalated, critical failures, callback rejections, binding issues | Workflows category selector, one focused table | Request.Read/Write/Execute/Manager; HelpdeskAdmin |
| Pending retries and Retry now | Workflows → Retries pending | HelpdeskAdmin |
| Callback correlation notification link | Workflows → Callback rejections → Notifications | Existing notification audience and correlation query |
| `/ops/hangfire`: runtime toggle, Save/Reset and details | Workflows → Advanced / Background jobs | HelpdeskAdmin |
| Dashboard, jobs/actions and dashboard assets | Existing `/hangfire` authenticated proxy | HelpdeskAdmin; unchanged proxy methods, resources and streaming |
| Account → Notifications | Operations → Notifications | All authenticated users |

Each active view loads only after its own authorization gate. SLA permission does
not grant workflows or job administration. Hidden tabs do not poll. Refresh
updates the active view and its last-refreshed time. Unavailable responses are
shown as unavailable rather than counted as zero.

## Metric definitions retained

The UI uses the existing report/operations endpoints without changing queries,
calculation services, authorization scopes or persistence:

- Resolution and response compliance use the rolling seven-day UTC window and
  `CompletedTotal` denominator. Numerators remain `CompletedWithinResolutionSla`
  and `CompletedWithinResponseSla` respectively.
- Breached count uses the open breached endpoint. The summary's near-breach count
  always uses resolution at 90%; the focused near-breach table follows the
  selected response/resolution metric and 80%/90% threshold.
- Overdue and escalated retain their endpoint definitions. Critical failures
  use the preceding 24 hours; callback rejections retain seven days. Retry and
  binding counts retain their endpoint totals. Each results table retains its
  existing first 25 rows and ticket/details/action fields.
- Administrators can select a tenant; other users retain their organization
  scope, with the backend continuing to enforce authorized tenant visibility.

Browser fixtures use synthetic catalogue items, tickets, tenants and identities.
Run the owning browser suite with `npx playwright test --config
playwright.operations.config.ts` after building the Web project with the pinned
SDK. The loopback fixture is presentation evidence; real API/provider security
tests remain separate required checks.
