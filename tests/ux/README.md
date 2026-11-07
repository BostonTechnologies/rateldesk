# Helpdesk UX Playwright checks

Run against a local Development app with `HELPDESK_E2E_AUTH_MODE=development`, or an Authentik-backed instance with `HELPDESK_E2E_AI_TOKEN`.

```bash
HELPDESK_E2E_BASE_URL=https://helpdesk.example.com \
HELPDESK_E2E_AI_TOKEN=<token> \
npm run test:ux
```

The suite deliberately has no authentication bypass. It uses the existing `/auth/ai-agent/exchange` endpoint for public dev and the local-only `/auth/development` endpoint only when explicitly selected.

Run the NetRatel service credential suite with `tools/ci/run-ux-local.sh tests/ux/netratel-service-link.spec.ts`. The runner creates an isolated PostgreSQL installation, enables its separate service issuer and supplies `HELPDESK_E2E_LOCAL_EMAIL` and `HELPDESK_E2E_LOCAL_PASSWORD` for the synthetic bootstrap administrator. The manual journey and signed-out peer approval journey sign in through the real local login form; service credential management requires an enabled human administrator, so a development system principal or AI token cannot substitute for that account. The signed-out check requires a clean login URL with bounded guidance and no ceremony correlation. The full Web application fixture in `WebAuthRoutesTests` separately renders the awaiting initiator's Continue form and verifies actual antiforgery and protected-cookie reuse against an explicit backchannel test fixture; that check does not prove a deployed reciprocal peer. Direct runs need the same fixture values and an explicitly configured service issuer. Automatic traces and failure screenshots are disabled for this suite because create and rotate reveal a synthetic secret once.

CI also starts an isolated, unconfigured bootstrap API and local Web host to drive the anonymous first-run wizard through SQLite selection, instance details, administrator details, and review. The Compose smoke checks perform the final initialization and restart verification for both SQLite and PostgreSQL.

## AiAssistant presentation fixture

Build `src/HelpDesk.NewWeb`, then run `npm run test:ux:ai-assistant`. The separate
configuration starts a loopback-only deterministic API fixture and the actual
feature NewWeb host. It uses local Development authentication and a local HTTPS
certificate (the existing `__Host-` cookie requires HTTPS). Test-only placeholder
credentials never reach a real API. Screenshots and computed styles are saved
under `test-results/`.

This suite exercises UI grouping, streaming, keyboard/IME, replay, approval
presentation, archive, responsive layout and themes. It is not live AiAssistant,
attachment ingestion or authorization evidence. It is deliberately excluded
from the ordinary live-instance UX configuration.

`ai-parity.spec.ts` uses that same isolated fixture to exercise `/ai/chat` and
`/ai/ops`: normal/fullscreen draft and tool-state retention, saved preferences,
keyboard restoration, responsive light/dark rendering, streaming, approvals,
honest recovery and archived access. It also checks that layout/open/create
actions do not send messages. These are deterministic browser receipts, not
acceptance against a running Netclaw daemon or production data.

## Assignee picker interaction

Build the web project using the SDK pinned in `global.json`, then run
`npm run test:ux:assignee`. For a Release build set
`HELPDESK_ASSIGNEE_CONFIGURATION=Release`. The focused configuration starts the
real Blazor application with loopback manager/read-only hosts and a synthetic API.
It covers the three grids, create/detail form selection and Revert, bulk selection,
keyboard behavior, lookup/save failures, delayed requests and mobile themes.
These fixture checks establish UI behavior; the SQLite/PostgreSQL HTTP tests in
`AssigneeSearchEndpointsTests` establish authorization, eligibility and persistence.

For manual validation, open an incident, request or Draft change grid and activate
its assignee/implementor button. Initial suggestions must appear without assigning
anyone. Search a name or email fragment, select a result, and reload to check the
saved value. Clearing the query must leave the selection intact. Use the separate
Unassigned action for incidents/requests; changes require an implementor. In a
create/detail form, selection stays local until Create/Save Changes, and Cancel or
Revert discards it. Check Escape, Tab and arrow/Enter behavior as well.

## Ticket filter interaction

After building `src/HelpDesk.NewWeb`, run
`npx playwright test --config playwright.ticket-filters.config.ts`.
This focused suite runs the real Blazor lists against synthetic loopback records
and captures responsive light/dark screenshots. It covers all three modules,
complete-dataset searches, URL restoration, cancellation and failures. API
predicate, bounds, authorization and SQLite/PostgreSQL evidence lives in
`TicketListFiltersTests`; timezone/DST and query roundtrips live in
`TicketListStateTests`.

## Request service editor

Build `src/HelpDesk.NewWeb` with the pinned SDK, then run
`npx playwright test --config playwright.request-editor.config.ts`. For a Release
build set `HELPDESK_EDITOR_CONFIGURATION=Release`. This dedicated loopback fixture
uses synthetic organizations, tasks and read-only automation catalog entries;
it never dispatches work to a real provider. It checks visible pane geometry at
1440×900, 1366×768, 1280×720 and 390×844, task selection and drag/keyboard ordering,
draft retention across tabs, validation focus, save/reload, partial-save retry,
empty/many tasks, enlarged text and dark-theme screenshots. The existing optional
workspace UX acceptance matrix includes this suite.

For actual browser zoom acceptance, run
`HELPDESK_NATIVE_ZOOM_SUITE=request-editor npx playwright test --config playwright.native-zoom.config.ts`.
The shared extension sets Chromium to 200% zoom and opens the editor in light and
dark themes at physical widths of 1280 and 1920 pixels. It verifies the responsive
editor, keyboard tabs and reachable Save/Cancel controls without saving the form.

Set `HELPDESK_EDITOR_BASELINE=true` and `HELPDESK_EDITOR_WEB_CWD` to an unchanged
built Web checkout to capture synthetic before screenshots with the same fixture.
That capture is separate from the full changed-source acceptance run. Screenshots
and browser geometry assertions establish UI behavior; provider HTTP tests retain
responsibility for authorization and SQLite/PostgreSQL persistence.
