# Shared ticket requester containment (#356)

Baseline: `ee7067d12dafb22de81b70a58f61b211b9e37226` (beta.5). This
presentation change applies to the common table used by active and historic
incidents, requests and changes. It does not change stored customer values.

The shared For cell now bounds both its custom and default render fragments.
The desktop budget responds between 10 and 16rem; the stacked mobile content
can use up to 17rem while retaining the existing For DataLabel. Tooltip roots,
chips and chip content can shrink and wrap, including unbroken names and email
addresses. The adjacent desktop Subject keeps at least 10rem. The customer
picker no longer imposes an independent 180px minimum. Changes display their
complete email fallback instead of discarding its domain.

## Focused verification

Use the SDK in `global.json`:

```sh
dotnet build src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj
dotnet test tests/Helpdesk.Tests/Helpdesk.Tests.csproj --filter FullyQualifiedName~TicketCustomerRenderingTests
npx playwright test --config playwright.ticket-filters.config.ts ticket-customer-layout.spec.ts --workers=1
dotnet tool run slopwatch -- analyze --fail-on warning --exclude '**/bin/**,**/obj/**'
python3 tools/ci/validate-docs.py
tools/ci/validate-layout.sh
```

The rendered tests exercise complete, unchanged Unicode customer values under
both render paths and retained row navigation. The focused browser suite uses
the real Blazor host with the existing synthetic ticket API fixture. It checks
computed cell/chip geometry, Subject width and wrapping for short, spaced,
unbroken, email fallback and Unicode names in all three modules and both views.
Desktop widths are 1440 and 1366px with the sidebar expanded. The suite also
checks MudBlazor's inclusive 1280px stacked-table boundary and 390px mobile.
The incident/request picker tests assign exactly once, preserve the current URL,
then open the row and return through browser Back with the saved selection.

The fixture proves presentation and interaction. Provider authorization and
persistence remain covered by the existing SQLite/PostgreSQL endpoint tests;
these synthetic browser checks are not production deployment acceptance.

## Recorded candidate results

- Web build: passed, zero warnings/errors with SDK 10.0.400.
- `TicketCustomerRenderingTests` plus existing `TicketListRenderedTests`: 23
  passed, zero skipped. Both default and custom customer fragments retain the
  complete value and tracking-cell navigation.
- Focused Playwright: 20 passed, zero retries/failures. The twelve geometry
  scenarios cover all three modules at four widths; eight interaction scenarios
  cover incident/request selection and navigation at the same widths.
- Slopwatch: zero new issues. Documentation and full layout validation passed.

The retained [baseline geometry](assets/356-for-column/baseline-geometry.json)
comes from the actual unmodified beta.5 Web build with the same synthetic fixture.
The [candidate geometry](assets/356-for-column/after-geometry.json) records the
passing browser assertions. At 1440px the long value previously expanded For to
over 1500px; candidate For content is 172.8px, its cell 212.8px and Subject 160px.
At 1366px candidate content is 163.9px. At the inclusive 1280px stacked boundary
it uses 272px; at 390px it uses 248px. All candidate content overflow is zero.

| Viewport | Original beta.5 | Candidate |
| --- | --- | --- |
| 1440 × 900 | [Before](assets/356-for-column/before-1440.png) | [After](assets/356-for-column/after-1440.png) |
| 1366 × 900 | [Before](assets/356-for-column/before-1366.png) | [After](assets/356-for-column/after-1366.png) |
| 1280 × 900 (stacked) | [Before](assets/356-for-column/before-1280.png) | [After](assets/356-for-column/after-1280.png) |
| 390 × 900 | [Before](assets/356-for-column/before-390.png) | [After](assets/356-for-column/after-390.png) |

Screenshots use disposable synthetic values and retain the baseline version label
because version preparation follows implementation. They do not show an operator
deployment.

## Operator check

Open active and historic Incidents, Requests and Changes with short, long spaced,
unbroken, email fallback and Unicode requester values. Keep navigation expanded
at desktop widths, then resize through the stacked-table boundary and to mobile.
Check the complete name wraps, Subject and ticket controls remain usable, and the
For DataLabel remains visible. On a synthetic incident or request without a
requester, select a long customer value, open the tracking link and return with
Back; reload to confirm the selected value. No customer renaming is required.
