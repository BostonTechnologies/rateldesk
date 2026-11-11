# Campaign #381: premium UX and request execution

Source baseline: `11c8997816833d753caf8df912a8279490da34fb` (11 November 2026).
All issues and discussions #380–#394 were read before implementation. No open
concurrent PRs were found at that baseline. This register distinguishes authored
code, passing acceptance, reviewed merge and verified public distribution.

## Requirement register

| Issue | Implementation branch / owning area | Verification required | Delivery state |
| --- | --- | --- | --- |
| #380 | `codex/381-campaign`: trusted local requester, explicit organization, safe form errors and idempotent submission | Both-provider HTTP identity/ownership; rendered error/correction; real paired self success/failure, on-behalf and correlated final state | In progress; acceptance incomplete |
| #382 | `codex/382-deadlines`: durable attempt deadlines, reconciliation and timeout holds | Both providers; deterministic timing, restart, concurrency, races, late/old outcomes; paired lost callback | In progress; acceptance incomplete |
| #383 | `codex/383-live-details`: request-scoped committed live updates and quiet detail | Actual persisted transition → gateway SSE → rendered task/header/workflow; reconnect/revocation | In progress; acceptance incomplete |
| #384 | `codex/384-self-service`: one navigation item and URL-backed tabs | Routes, history, ownership, focus and retained query state | In progress; acceptance incomplete |
| #385 | `codex/384-self-service`: explicit responsive catalog tracks | Sparse and many cards; drawer, mobile and actual 200% zoom | In progress; acceptance incomplete |
| #386 | `codex/384-self-service`: complete My Requests SQL query/paging | >100 records; full filter/count/sort, ties, stale responses and ownership | In progress; acceptance incomplete |
| #387 | `codex/387-team-lifecycle`: Team directory and separate retirement/sign-in operations | Both providers; revoked sessions/credentials, retained attribution, reprovision and tenant fences | In progress; acceptance incomplete |
| #388 | `codex/388-admin-directories`: bounded customer directory/access projection | >100 records, whole-query status filters, bounded request/query count, invitations | In progress; acceptance incomplete |
| #389 | `codex/388-admin-directories`: organization directory | Complete queries, saved mapping/catalog-unavailable truth, delegated views | In progress; acceptance incomplete |
| #390 | `codex/390-operational-cards`: structured connection cards | Saved IDs, pairing/actions and truthful fresh/stale/never-tested state | Implemented; local acceptance passed at `42d676f6`; integrated CI/review/merge pending |
| #391 | `codex/390-operational-cards`: Service Explorer card geometry | 0/2/20/100 assignments, bounded keyboard-accessible lists/actions | Implemented; local acceptance passed at `42d676f6`; integrated CI/review/merge pending |
| #392 | `codex/388-admin-directories`: additive category admin query | Exact type versus legacy Service inclusion, full queries and protections | In progress; acceptance incomplete |
| #393 | `codex/390-operational-cards`: Operations Overview | Dynamic counters/links, all three feeds, roles and partial errors | Implemented; local acceptance passed at `42d676f6`; integrated CI/review/merge pending |
| #394 | `codex/390-operational-cards`: effective branded search | Branding precedence/fallback, long names, shortcut/focus and responsive shell | Implemented; local acceptance passed at `42d676f6`; integrated CI/review/merge pending |

The companion NetRatel issue #729
adds an authenticated read-only exact-attempt receipt. Its source baseline is
`99322844929aece494c42480fe4a19642646b152`. The existing internal contract had
catalogue/admission but no outcome read; watchdog reconciliation must never replay
admission. Provider implementation and paired acceptance remain separate gates.

## Environment and initial checks

- Fetched current `origin/main`; baseline above matches the planning revision.
- Installed the pinned .NET SDK 10.0.400 into an isolated workspace tool directory.
- `dotnet restore Helpdesk.sln` completed. Existing package advisories remain
  visible; no warnings or required checks were suppressed.
- Root release version remains unchanged while implementation is incomplete.

## Completion gates

Every final PR head must pass the existing six normal jobs before merge. The
exact final main SHA must then pass all six again. Optional deep browser/provider
acceptance runs through the existing dispatch lane and is recorded separately.

No merge, public release, public image/archive verification or complete campaign
acceptance is claimed by this initial register. Release allocation must recheck
both tag prefixes, every release state and all three registry families. Published
bytes and stable latest remain immutable. Owner production rollout is separate.


## Initial regression findings

The first owning self-service run executed 23 cases: 19 passed, four failed,
none skipped. All four failures demonstrated that the existing global customer
email index prevented a legitimate identity-bound requester when an unrelated
contact had the same email. The forward identity-key migration and explicit
identity resolver address that cause without claiming or moving the contact.
This failed run is retained as investigation evidence and is not final acceptance.

Rendered error acceptance also exposed automatic retry/circuit-breaker behavior
in the shared read client. A dedicated no-retry command client now makes a single
submission attempt and leaves retry to the user. Both repairs require fresh
owning and integrated acceptance before their checklist entries can complete.
## Self Service local verification

The scoped [verification receipt](assets/381-self-service/verification-receipt.json)
records implementation, actual results and source boundaries for #384–#386 and
the rendered #380 error/retry regression. It does not establish reviewed merge,
real provider execution or public distribution.

| Requirement | Implementation | Owning verification |
| --- | --- | --- |
| #384 | One Self Service navigation item, URL-backed catalog/request sections, retained list URLs and form return context | 63 navigation and six shell rendered cases; actual folder/search return after cancel/success; browser section/query/history behavior |
| #385 | Explicit content-width three/two/one catalog tracks, stable sparse slots, bounded text and accessible full names/descriptions | Ten themed viewport matrices, four themed sparse cases, empty/error/no-match and keyboard behavior, four actual native 200% cases |
| #386 | Whole-query SQL search/status/release/date filters, five sorts with ID ties, page sizes 25 (default), 50 and 100, reset/clamp, stale cancellation and recoverable reads | 13 HTTP query/ownership cases including real SQLite/PostgreSQL; browser query over 137 requests, native 25/50/100 controls, sticky geometry and retained state; two typed resilience rendered cases |
| #380 rendered regression | Explicit requester/organization context, retained inputs and submission identity, safe unsuccessful/malformed responses and separate on-behalf confirmation | Eleven real Web→synthetic HTTP form cases, including empty 401/403/400/500, malformed errors/success, correction/retry, duplicate clicks and on-behalf cancellation/confirmation. Real NetRatel paired acceptance remains separate. |

The final local Web subject is `10f4e0e424bb5b7073d80713e0b051037f419413`,
SHA256 `12d300cf5c74179e5b4ec346971c3d3a274e703a7dffa0ec44d13f5bd374f2bf`,
built with SDK 10.0.400. The fresh process began at 16:46:57 UTC on 11 November 2026,
after that build. Later commits changed test drivers/configuration only; source
under `src/` is unchanged from that subject. Playwright 1.64.1 used official
Chromium 1248 with one worker and fresh fixture/Web listeners.

The ordinary run produced 28 passes and two strict-locator failures after the
operations had succeeded. The ambiguous folder breadcrumb/heading assertion was
corrected to the heading role; both cases then passed. The strengthened native
100-row query case also passed. All 30 unique ordinary behavior cases and all four
native 200% cases have passing local outcomes, with zero skipped cases; the
separate run receipts retain the actual failures. Hosted deep acceptance still
needs its single integrated exact-source receipt.

The initial .NET selection passed 84/84: query 13, navigation 63, shell 6 and
catalog 2. Its build began at `e205b928` with working-tree implementation changes,
so it is not presented as an exact final-build receipt. The two additional
open-circuit/timeout rendered cases passed 2/2 using test assembly `e7213caf` and
the frozen Web subject above. The existing layout/documentation validators and
Slopwatch passed without new baselines or warning suppression.

Local receipts are retained under `/workspace/work/results/` as
`self-service-workspaces/self-service-workspaces.trx`,
`self-service-read-errors/my-requests-read-errors.trx`, `self-service-final.json`,
`self-service-catalog-final.json` and `self-service-query-final.json`.
`self-service-native-final` contains native traces, source receipts and physical
PNG captures. The corresponding commands used the repository's selected SDK:

```sh
dotnet test tests/Helpdesk.Tests/Helpdesk.Tests.csproj --filter 'FullyQualifiedName~SelfServiceMyRequestsEndpointsTests|FullyQualifiedName~NavMenuRenderedTests|FullyQualifiedName~SelfServiceShellRenderedTests|FullyQualifiedName~ServiceBrowserRenderedTests'
dotnet test tests/Helpdesk.Tests/Helpdesk.Tests.csproj --filter FullyQualifiedName~MyRequestsRenderedTests
CI=true npx playwright test --config playwright.self-service.config.ts --workers=1
CI=true npx playwright test --config playwright.self-service.config.ts --grep 'catalog error, empty' --workers=1
CI=true npx playwright test --config playwright.self-service.config.ts --grep 'query, native page' --workers=1
CI=true HELPDESK_NATIVE_ZOOM_SUITE=self-service npx playwright test --config playwright.native-zoom.config.ts --workers=1
```

The [before capture receipt](assets/381-self-service/capture-receipt.json) binds
36 synthetic PNGs (4,395,905 bytes) to dimensions and SHA256 values. Catalog,
My Requests, navigation and scoped styles shown were unchanged from baseline
`11c89978`; the pre-UX Web output included unrelated draft #380 form changes and is
not claimed as a clean complete baseline build. Matching before images are
copied beside generated after images by the owning browser/native tests so the
existing hosted deep artifact upload publishes both. Early mixed-network and
reused-server runs remain excluded from the fresh-runtime result above.

## Operational cards, overview and branded search: local acceptance

Issues #390, #391, #393 and #394 have passing isolated presentation and owning
regression acceptance at application source
`42d676f641231dce809e6a88b21dbf65e75a4c8c`. Evidence commit
`d37aee99e08496483fd8e3cad4293a9086bacc19` adds the accepted after screenshots
and the [exact-source receipt](assets/381-premium-cards/after-receipt.json).
The matching before screenshots use baseline
`11c8997816833d753caf8df912a8279490da34fb`; each receipt records frame hashes,
physical dimensions, theme, route and source identity.

- #390 separates saved configuration from never-tested, recent, stale and failed
  test snapshots, with anchored horizontal actions and retained saved IDs.
  Browser acceptance exposed card reordering after Test; `42d676f6` replaces the
  matching saved card in place, with a rendered order regression assertion.
- #391 verifies complete 0/2/20/100 organization assignments, bounded keyboard
  scrolling, equal row heights, anchored actions, complete description access,
  assignment search/retention, and distinct load errors with retry.
- #393 retains every dynamic state counter and filtered link, all three recent
  feeds, non-administrator variants, partial failures/retry and truthful manual
  freshness.
- #394 verifies effective branding and fallback, full accessible search names,
  long-name truncation, responsive layout, Shift+S typing exclusions, Escape,
  focus return and navigation focus. Four existing branding-provider cases also
  pass against the real environment-over-database/default resolution service.

Accepted executions contain no failed, skipped or flaky cases: 36 owning
rendered .NET cases, four branding-provider cases, 17 ordinary Playwright cases
and four native 200% Playwright cases. The viewport matrix captures 1920×1080,
1440×900, 1366×768, 768×1024 and 390×844 in light/dark themes. Native zoom uses
the official pinned Chrome, asserts `chrome.tabs.getZoom() == 2` and the halved
layout width at physical widths 1280/1920, and records physical CDP screenshots.
Together these provide 42 matching before/after frames across Home, Service
Explorer and system connections. The native driver is unchanged shared source
`45458ab1bf280cbe54c29c50f69448fe29934041`; its hash and exact commands are in
the receipt. Documentation, public-disclosure, layout and slopwatch checks pass.

These executions use synthetic loopback presentation fixtures plus owning
rendered/service tests. Reviewed merges, all six required jobs on each final PR
and the exact final main SHA, integrated provider/upgrade acceptance, and public
release/distribution verification remain campaign gates. This local evidence
does not claim those gates or owner production rollout are complete.
