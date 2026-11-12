# Campaign #381: premium UX and request execution

Source baseline: `11c8997816833d753caf8df912a8279490da34fb` (11 November 2026).
All issues and discussions #380–#394 were read before implementation. Existing
dependency PRs were preserved; no parallel campaign feature change was identified.
This register distinguishes authored
code, passing acceptance, reviewed merge and verified public distribution.

## Requirement register

| Issue | Implementation branch / owning area | Verification required | Delivery state |
| --- | --- | --- | --- |
| #380 | `codex/381-campaign`: trusted local requester, explicit organization, safe form errors and idempotent submission | Both-provider HTTP identity/ownership; rendered error/correction; real paired self success/failure, on-behalf and correlated final state | Implemented; focused and complete real paired acceptance passed; final-head/main/distribution gates pending |
| #382 | `codex/382-deadlines`: durable attempt deadlines, reconciliation and timeout holds | Both providers; deterministic timing, restart, concurrency, races, late/old outcomes; paired lost callback | Implemented; owning provider and complete paired recovery acceptance passed; final-head/main/distribution gates pending |
| #383 | `codex/383-live-details`: request-scoped committed live updates and quiet detail | Actual persisted transition → gateway SSE → rendered task/header/workflow; reconnect/revocation | Implemented; 11/11 gateway cases and 11/11 layout cases passed; integrated gates pending |
| #384 | `codex/384-self-service`: one navigation item and URL-backed tabs | Routes, history, ownership, focus and retained query state | Implemented; scoped local .NET/browser acceptance passed; integrated gates pending |
| #385 | `codex/384-self-service`: explicit responsive catalog tracks | Sparse and many cards; drawer, mobile and actual 200% zoom | Implemented; scoped local themed/native zoom acceptance passed; integrated gates pending |
| #386 | `codex/384-self-service`: complete My Requests SQL query/paging | >100 records; full filter/count/sort, ties, stale responses and ownership | Implemented; scoped full-query/ownership/browser acceptance passed; integrated gates pending |
| #387 | `codex/387-team-lifecycle`: Team directory and separate retirement/sign-in operations | Both providers; revoked sessions/credentials, retained attribution, reprovision and tenant fences | Implemented; 68/68 owning cases and themed/native before/after passed; integrated gates pending |
| #388 | `codex/388-admin-directories`: bounded customer directory/access projection | >100 records, whole-query status filters, bounded request/query count, invitations | Implemented; scoped local acceptance passed; integrated CI/review/merge pending |
| #389 | `codex/388-admin-directories`: organization directory | Complete queries, saved mapping/catalog-unavailable truth, delegated views | Implemented; scoped local acceptance passed; integrated CI/review/merge pending |
| #390 | `codex/390-operational-cards`: structured connection cards | Saved IDs, pairing/actions and truthful fresh/stale/never-tested state | Implemented; local acceptance passed at `42d676f6`; integrated CI/review/merge pending |
| #391 | `codex/390-operational-cards`: Service Explorer card geometry | 0/2/20/100 assignments, bounded keyboard-accessible lists/actions | Implemented; local acceptance passed at `42d676f6`; integrated CI/review/merge pending |
| #392 | `codex/388-admin-directories`: additive category admin query | Exact type versus legacy Service inclusion, full queries and protections | Implemented; scoped local acceptance passed; integrated CI/review/merge pending |
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
- Root release preparation now assigns `0.1.3-beta.10` after both tag prefixes,
  draft/public releases and all three image families were verified unused.
  NetRatel source preparation assigns the compatible `0.1.5-beta.5`.

## Completion gates

Every final PR head must pass the existing six normal jobs before merge. The
exact final main SHA must then pass all six again. Optional deep browser/provider
acceptance runs through the existing dispatch lane and is recorded separately.

No merge, public release, public image/archive verification or complete campaign
acceptance is claimed by the source-preparation register. Release allocation checks
both tag prefixes, every release state and all three registry families. Published
bytes and stable latest remain immutable. Owner production rollout is separate.

## Integrated source and focused functional verification

The review batches are [PR #395](https://github.com/BostonTechnologies/RatelDesk/pull/395)
for trusted submission/deadlines and
[PR #396](https://github.com/BostonTechnologies/RatelDesk/pull/396) for administration
queries and account retirement. [PR #397](https://github.com/BostonTechnologies/RatelDesk/pull/397) contains the
premium UI/release-preparation batch stacked on these branches. NetRatel PR #730
implements companion #729.
All merges, exact-main checks and public distribution remain pending here.

The functional build completed with no errors using SDK 10.0.400. Its first
52-case upgraded-contract selection passed 51 and exposed one stale expected
OpenAPI tag. After correcting that expectation to the canonical **Self-Service**,
the owning 32-case requester/legacy/OpenAPI selection passed 32/32 with no skips.
The actual runs cover 67 distinct passing cases; this is not a single 67-case
aggregate. Earlier failed receipts remain retained. Requester/error/endpoint
focused coverage is 85.5% line and 70.4% branch.

The deadline provider series executed 190 cases: 188 passed and two old SQLite
fixtures attempted deliberately unsupported reverse migrations. The fixtures
were repaired to construct the historical model and migrate forward; all four
changed SQLite/PostgreSQL upgrade cases then passed, without product relaxation.
All 190 distinct cases therefore have passing outcomes across the retained runs,
not a claimed green rerun of the original aggregate. They exercise timing,
restart, multiworker races, late/old outcomes, operator/approval holds, atomic
dispatch intent and durable continuation lost-wake recovery. Start-fence coverage
is 91.1%; the hold policy is 100%. Async API coverage omitted by the exporter is
not presented as zero or as complete coverage.

Customer deletion guarding passed 19/19 owning cases without failures/skips on
source `481161be`: both real providers, the optional in-memory provider and actual
CRUD routes. Managed requester keys/auth links cannot be removed; PostgreSQL
locks the current contact inside the transaction before eligibility checks.
Domain/identity deletion, history and reprovision fences remain separate lifecycle
operations. The standard coverage-derived CRAP value for this focused handler is
28.98, with 79.48% sequence and 91.66% branch coverage; no suppression was added.

All four real EF model/snapshot checks passed at source
`fddcb52076941840c90f60f17b7234853b29afa7`, with unchanged model inputs in the
integrated source. These check both contexts against SQLite and PostgreSQL using
EF 10.0.13 and SDK 10.0.400. Provider metadata and all 94 snapshot entity hashes
are retained in the model receipt; no database schema was written for this check.

NetRatel's authenticated receipt/replay regression passed 2/2 with no skips at
`2b9984e60618f195b672110db425853885c3d093`. The original failure exposed an ingest
replay projection recovery returned before its transaction committed. The fix
commits that authorized replay; receipt GET remains strictly read-only. The real
rendered native pair, exact candidate normal/deep jobs, reviews and publication
are independent gates and are not inferred from these focused results.


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
| #384 | One Self Service navigation item, URL-backed catalog/request sections, retained list URLs and form return context | Implemented; scoped local .NET/browser acceptance passed; integrated gates pending |
| #385 | Explicit content-width three/two/one catalog tracks, stable sparse slots, bounded text and accessible full names/descriptions | Implemented; scoped local themed/native zoom acceptance passed; integrated gates pending |
| #386 | Whole-query SQL search/status/release/date filters, five sorts with ID ties, page sizes 25 (default), 50 and 100, reset/clamp, stale cancellation and recoverable reads | Implemented; scoped full-query/ownership/browser acceptance passed; integrated gates pending |
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

## Directory evidence: #388, #389 and #392

The additive administration routes filter, count and sort before paging. Customer
access is projected with the page rather than fetched in a serial per-row HTTP
loop. Organization mapping values retain saved identity when the remote catalog
is unavailable. Category administration uses exact type filters while the legacy
Service picker inclusion contract remains separate. Tenant/delegated fences,
existing edit dialogs and category dependency protections remain part of the
owning provider acceptance gate.

Synthetic presentation acceptance used the baseline
`11c8997816833d753caf8df912a8279490da34fb` and changed Web source
`a9a1d4a31787bfc5357ffb17a668af40708785e5` with the same fixture data: 133
customers, 137 organizations and 143 categories. The final baseline run passed
2/2 cases and captured 30 images. The changed ordinary browser run passed 5/5
cases and captured 30 images across 1920×1080, 1440×900, 1366×768, 768×1024 and
390×844 in both themes. It covers query/history restoration, keyboard sorting,
25/50/100 paging, stale response cancellation, retained results after a read
failure, recovery, exact category filters and explicit create type. Thirty
computed style samples measured 30px headings, 15px table text/headers/sort
buttons and at least 16px surface padding.

The separate native browser zoom run passed 4/4 cases and captured 12 images
across these three routes, both themes and physical widths 1280/1920 at 1000px
height. The extension called `chrome.tabs.setZoom(2)` and verified the browser
layout width was half the physical width. Team and the integrated four-route
directory lane have separate owning acceptance.

The bounded checked-in evidence has twelve images plus the
[machine-readable receipt](assets/381-directories/acceptance.json), including
commands, source revisions, test outcomes, computed typography, native zoom
measurements and asset hashes. Representative Customers
[before](assets/381-directories/before-customers-light-1440x900.png),
[after](assets/381-directories/after-customers-light-1440x900.png),
[mobile dark](assets/381-directories/after-customers-dark-390x844.png) and
[native 200%](assets/381-directories/admin-customers-dark-1920-native-200.png)
captures share the same synthetic fixture. Equivalent Organization and Category
captures are listed in the receipt.

The owning API/query series at application source
`56607e7f69efa6d08bf877c176392e063e56399e` passed 31/31 cases with no failures or
skips: sixteen directory HTTP cases across SQLite/PostgreSQL, eight legacy
category HTTP cases across both providers, six data-source cases and one URL
query-state case. The 133-record customer query and 122-record retirement/orphan
query each assert at most two SQL reader commands on both databases. Retired or
missing requester keys cannot advertise Invite; domain-only requester profiles
remain valid without an application account. The
[provider receipt](assets/381-directories/provider-acceptance.json) includes all
outcomes, source/binary hashes and the query-bound assertions; the
[coverage summary](assets/381-directories/provider-coverage.txt) records 89.6%
line and 72.8% branch coverage across the four selected endpoint/query classes.

The subsequent rendered UI series passed 20/20 cases with no failures or skips
on Web source `e20c53efe6ff4035cbc23e46f3b69236938556a1` and test correction
`d0b018023545f57c80251f10dc567bbeb924d509`. Thirteen new cases cover saved tenant
ID/name/timestamp retention during unavailable or missing catalogs, truthful
unknown names/dates, safe empty save errors, retry, selector resilience and late
lookup responses; the seven existing data-source/query-state cases were rerun.
These overlap with the seven query cases above rather than adding distinct
coverage to the total. The [UI receipt](assets/381-directories/ui-acceptance.json)
and [coverage summary](assets/381-directories/ui-coverage.txt) record 94.8% line
and 66.6% branch coverage across the four selected UI/query classes. Strict
Slopwatch reports zero issues, documentation links pass, and authored nullable
test warnings were corrected without suppressing the remaining baseline/package
warnings.

The pinned Coverlet OpenCover output omits CRAP scores and incorrectly marks
ordinary methods as constructors. ReportGenerator therefore has no CRAP score
column; missing scores are not reported as zero. Coverage, raw complexity and
source review remain available in the scoped receipts/reports.

These results establish scoped local acceptance. Final integrated-source
provider/authorization/lifecycle validation, the shared four-route directory
presentation run, the six normal CI jobs, reviewed merge and release/distribution
remain campaign gates; none is inferred from screenshots or an earlier binary.

## Scoped live detail and Team receipts

The [live-detail register](383-live-request-details.md) records 11/11 actual
gateway cases, including 935 ms SQLite and 1,658 ms PostgreSQL convergence, and
11/11 frozen layout cases. The [Team register](387-team-lifecycle.md) records
68/68 owning cases plus the complete ordinary/native baseline comparison. These
are isolated source receipts; final integrated CI and paired execution remain
separate gates.

The [complete baseline directory receipt](assets/381-directories/baseline-complete-receipt.json)
adds 30 exact ordinary viewport and 12 physical native-zoom frames from the clean
full baseline, totaling 5.5 MB. Original representative evidence is retained.
The [supplemental request-detail matrix](383-live-request-details-visual-matrix.md)
completes ordinary and actual 200% before/after comparisons without changing the
frozen application binaries or original evidence. The deep lane publishes these
source-bound comparison bundles alongside its newly executed browser artifacts.

## Final review corrections and hosted verification snapshot

At the final fixture-correction snapshot on 11 November, the functional head is
`86473186c02da77b2ed81380546096e81cf59fdc` and the administration head is
`60bc754a`. Independent source reviews and root's
follow-up checks are recorded on PRs #395/#396 and companion NetRatel PR #730.
The functional normal run is
[43077718615](https://github.com/BostonTechnologies/RatelDesk/actions/runs/43077718615);
all six required jobs passed. Its .NET receipt contains **4,014 passed, zero
failed and eight pre-existing skips**; provider acceptance passed **485/485**
without skips. The earlier administration normal run is
[43077803948](https://github.com/BostonTechnologies/RatelDesk/actions/runs/43077803948).
Its five non-.NET jobs passed, including **485/485** provider cases. The .NET
receipt contained **4,113 passed, five failed and eight pre-existing skips**,
then exceeded the 45-minute job budget after uploading results. It is retained as
a failed/canceled run. Final-head reruns, merge/main/deep and public distribution
receipts belong to the subsequent campaign delivery comments.

All eight skipped .NET cases predate the campaign; their four owning files are
unchanged from the baseline. Four retain the literal reason `RevisitCodex`, two
require JWT validation not configured in those legacy tests, and two require
Windows filesystem ACLs. None is newly required campaign/provider acceptance;
the final delivery receipt records their exact names and source reasons.

The first complete functional run executed 4,015 cases: 3,999 passed, eight failed
and eight pre-existing optional cases were skipped. It is retained as a failed
run. Five failures came from historical fixtures that seeded current fields into
an old schema or attempted reverse migration through a forward-only identity
upgrade. One dashboard fixture expected an obsolete status label. Two failures
exposed repeated quick-state commands changing saved workflow metadata.

The repairs preserve the old assertions and schema history. The five historical
upgrades, two quick-state no-op/scope cases, eight supported operator routes,
two readiness fences and two lost-wake cases passed **19/19**, with no skips.
The dashboard's owning class passed **25/25** after supplying real normalized
provider status and checking the intended local **In progress** / provider
**External: running** display, rendered paging/filter completion and valid actions.
Its original failure remains retained.

Requester-link review repairs follow the same trusted Local, verified issuer/subject,
then supported legacy provider-ID precedence as the access resolver. A current
binding that differs from the prior access snapshot is rejected. All **7/7** new
SQLite HTTP cases passed without failures/skips or request/task/link writes on
denials. Retirement review repairs reject a deleted domain before unavailable-account
early exits and refresh tracked external account state during long-lived authorization.
All **29/29** focused SQLite/InMemory cases passed without failures/skips, including
second-context disable/delete/removal and persisted provider-role projection changes.

The first hosted native-pair run reached both real local logins and saved both
pairings, but failed before business execution because its assertion still
expected **Connected** instead of the truthful **Connection configured** notice.
The helper now checks the actual configured receipt. That failed run establishes
no native business acceptance; final source-pair execution remains required.

The final request-stream review also found that a failed/canceled best-effort
notification after a committed request edit could leave an existing viewer stale.
A hidden, non-global journal marker is now saved in the same mutation transaction;
it has no inbox recipient and does not send a pre-commit notification. Deterministic
marker IDs and a batched existing-ID query preserve idempotence. All **8/8** focused
cases passed, covering 16 route/provider/fault combinations across priority PUT,
quick state, bulk state and bulk assignment. Actual committed state → gateway SSE
→ canonical rendered convergence was **287–693 ms**. Both selected bulk requests,
healthy single visible notification, rollback and duplicate staging are covered.
The local test used source `a4594d65` and explicitly reused matching prerequisite
binaries carrying beta.9 metadata; it establishes bounded behavior, not a beta.10
release build. The final hosted integrated run rebuilds all beta.10 projects.

## Integrated browser-driver corrections

The first integrated deep run at `bb07f72a` exposed stale driver assumptions:
Operations expected the old Running label; Integrations expected old connection
prose, stacking and result placement; the generic config discovered 29 cases
without their dedicated fixtures; and cached approver menus could satisfy a
visibility wait before the current query was ready. A navigation locator also
matched the recent-incidents heading instead of the primary workspace heading.

The corrected real-Web owning cases passed without retries: Operations **1/1**,
Integrations **7/7**, primary navigation **3/3**, and approver selection **10/10**
(five normal plus five with 150 ms lookup latency). Query/paging/actions, exact
saved mappings, duplicate stable-ID selections, scope reset and retained drafts
remain asserted. Generic discovery retains all 34 non-fixture cases; all 29
fixture-owned cases remain unchanged in dedicated admin/premium discovery.
No required case was removed from its owning lane or changed to an expected skip.
The first failed/flaky and superseded diagnostic runs are retained.

These corrections change test drivers/configuration only. The complete `src`
tree is byte-identical to `bb07f72a` (tree `0e75a6d32dc60877d53e54e6b4fdb6ec648f4be7`).
A fresh exact-head normal/deep dispatch and native source-pair repeat remain
required before merge; their run identities are recorded on PR #397 and #381.

The second native-pair run proved both rendered pairings, incident response-loss
replay and three native execution modes, then stopped at the ordinary self-service
form. Its fixture had left that form InTesting, correctly denying an ordinary
non-test user. The helper now publishes Production forms, checks real submission
eligibility and pins FailRequest with no retries for controlled failure/uncertainty
proof. This partial run does not complete rendered self-service business acceptance.

## Final administration fixture and native-driver corrections

The five administration failures were test setup/expectation defects. Two disabled
legacy-provider-link cases expected the submission-context GET to reach a later
200 response after the strengthened access policy correctly returned 403. Their
GET and POST expectations now both require the early denial, retained disabled
link and zero request/task writes; the whole owning class passed **7/7**. Three
managed-contact status cases used the real Identity UserManager while their test
host replaced only the domain database and skipped startup migrations. The
captured exception was an attempted connection to an unavailable test PostgreSQL
Identity database. The fixture now supplies an isolated in-memory Identity store
while retaining the real UserManager/EF services. The whole class passed **13/13**,
including fresh-scope checks of the managed identity key, owner retirement state,
zero application accounts/logins and zero invitation side effects. Product code
and the existing provider acceptance selections are unchanged.

The complete .NET test step took 42m18s before evidence upload in that hosted
administration run. Its job budget is now 60 minutes so setup, build, every test
and upload can finish; no test, assertion or required job was removed. This
budget and the fixture corrections are integrated into the final administration
and UX heads.

At UX source `258366d7`, the separate hosted deep run
[43081489092](https://github.com/BostonTechnologies/RatelDesk/actions/runs/43081489092)
passed all 22 optional acceptance jobs and five normal jobs, with the full .NET
job still pending at this snapshot. These include the complete workspace/native
zoom matrix, default UX, three container builds, seven-archive rehearsal and
Linux x64/arm64 and Windows x64 archive execution. The final updated head is
dispatched through the same existing lane again. The complete runtime `src` tree
remains `0e75a6d32dc60877d53e54e6b4fdb6ec648f4be7`.

Native source-pair run `43084679626`
at NetRatel `7321c02e` and RatelDesk `258366d7` again passed both pairings,
incident replay and the earlier three native execution modes. Its first new
rendered self submission failed required-field validation before any form request,
task or requester link was written. Bounded flags and a masked screenshot showed
the entered value had been reset. The helper filled the prerendered page without
waiting for the existing interactive marker, unlike the owning form tests.
NetRatel `b1736369` adds that readiness wait on each full main-page navigation,
checks the retained value after blur, and uses the actual MudAlert selector for
bounded failure diagnostics. This is a browser-driver correction; complete real
rendered success/failure/on-behalf/deadline recovery acceptance remains required.

The superseded UX run also exposed a sixth .NET failure in the existing bulk
resolution notification test. Its minimal host omitted `IDomainEventPublisher`
and `ICorrelationContext`; the captured HTTP 500 identified the missing publisher
registration before the handler ran. The fixture now uses the same two helpers
already registered by its adjacent self-service host. The complete owning class
passed **15/15**, with no skips. Original generic-versus-self-service notification
assertions remain, alongside fresh-scope SQLite verification of both resolved
requests and exactly two private correlation-bound journal markers. Product
source is unchanged. The register retains the native run identity without a
companion URL outside the disclosure gate's existing allowlist; the unchanged
public-disclosure gate and documentation validator both pass locally.

## Completed paired acceptance and release resumption

The complete hosted native pair, run `43090244581`, passed all twelve stages at
NetRatel `26369b14db0666cd11f93f9140e77690f79a0390` and RatelDesk
`28d2d3f9c9b6d232be4233c1fcfdbec5e743b161`. Its four rendered modes proved
ordinary unlinked self success, controlled unlinked-administrator failure,
distinct ordinary on-behalf execution, and lost-callback recovery. Each mode
admitted exactly one native job with correlated requester, organization, request,
task, saved connection, provider request, run and attempt. The healthy committed
state-to-render observations were 195, 153 and 153 ms.

The recovery mode withheld the actual callback, observed unavailable receipt
reads, restarted RatelDesk, reached persisted outcome uncertainty, and delivered
the original same-attempt terminal callback. All seven recovery observations
passed, including dependent-work blocking, zero uncertainty-induced incidents,
and later recovery of the original attempt. Both real rendered pairings,
receiver response-loss replay, offline credential revocation and full final
cleanup also passed. This supersedes the incomplete native-run snapshots above.

The raw NetRatel source metadata reports a dirty tree solely because the workflow
created the untracked nested companion directory `work/rateldesk/`; its
`cleanSourceRequired` field is false. Independent reconstruction verified all
3,376 NetRatel and 3,051 RatelDesk tracked blobs against the exact commits and
captured source hashes. The raw flag is retained, rather than described as a
clean Git checkout. The complete evidence and source-boundary review are recorded
on companion PR #730, which merged as
`e083e088f74159b22343133dd2c1dcd8f146fd95` with an identical tracked tree.

The final administration head `60bc754a9cbb7610f96e910aa2c3cd3aeb73db2f`
passed all six normal jobs in run `43087231520`: 4,118 .NET cases passed, none
failed, and the same eight baseline cases were skipped; provider acceptance
passed 485/485 with no skips. The final UX run's first attempt passed its test
step and the other 27 jobs, but failed to retain the .NET artifact during the
GitHub Actions incident. That attempt is not counted as a passed required gate.

After service recovery on 12 November, attempt 2 exposed an unchanged baseline
pending-mail test sampling pager geometry during a keyed table replacement.
The screenshot records an empty loading table after the previous row-count
assertion. The driver now waits for completed filter reset and page-size state
before asserting the original footer geometry. No product source, geometry
assertion, test selection or retry budget changes. Fresh final-head validation,
reviewed RatelDesk merges, all six exact-main jobs and verified public
distribution remain required; their immutable receipts belong to #381 and the
release. The runtime `src` tree remains
`0e75a6d32dc60877d53e54e6b4fdb6ec648f4be7`, matching the complete paired run.

The follow-up hosted run `43155757946` passed all 34 pending-mail ordinary
cases and all four native-zoom cases. It exposed three further baseline driver
races: Escape before the newly focused customer picker finished opening; Enter
before the state popup finished closing; and Reporting clicked before a tenant
change replaced the keyed SLA tabs. Retained DOM/focus evidence and unchanged
baseline product/test sources identify the missing readiness conditions.
The owning tests now wait for the current focused/open customer menu, explicit
state-popup closed/open attributes, and the new tenant's default selected tab.
All original geometry, zero-option dismissal, keyboard-activation, draft,
mutation-count and persisted-readback assertions remain. No sleeps, retries,
product changes or required-case exclusions were added. This superseded run is
not a passed final gate; fresh final-head CI remains required.
The focused corrections passed ten light/dark customer-picker executions, ten
complete SLA draft/tab journeys and nine state-popup journeys across all three
ticket modules, with zero failed, skipped, flaky or retried cases. These 29
executions include predefined repetitions; they are not 29 distinct test cases.
They use the retained frozen Web whose relevant picker/SLA/state-popup, layout
and stylesheet sources match the candidate, rather than claiming a fresh release
build. The new hosted final-head dispatch rebuilds the full candidate.
