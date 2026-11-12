# #387 Team directory and application user retirement

The Team directory queries the complete authorized dataset on the server and
offers separate **Delete user** and **Enable/Disable sign-in** operations. The
canonical [access and deletion policy](../security/rbac.md#application-user-deletion)
describes the retained attribution, reserved identity tombstones and atomic
revocation behavior.

This register records isolated source verification. Reviewed merge, exact-main
normal/deep checks and public beta distribution remain campaign #381 gates.

## Requirement → implementation → verification

| Requirement | Implementation | Owning verification |
| --- | --- | --- |
| Complete Team queries and compatible consumers | Additive `GET /api/v1/users/query`; SQL search/filter/count/order before bounded paging; existing user arrays retain their shape | SQLite/PostgreSQL tests with 133 active rows, stable duplicate-name ordering, status/role/cohort/search filters, page clamp and mode capabilities |
| Native table interaction | 25/50/100 pager, bounded body, sticky header/footer, readable long text, horizontal actions and URL-backed query state | Seven ordinary browser cases: ten viewport/theme captures, actual geometry, all page sizes, keyboard sort, reload, combined filters, stale responses, error/retry and final-page deletion/focus |
| Genuine Delete and immutable retirement | Shared serializable transaction across domain and identity changes; retained User/customer/login records; revoked passwords, sessions, credentials, grants and pending deliveries | Both providers verify migrations with populated upgrade data, revocation, history, idempotence, stale delivery concurrency and failed-audit rollback |
| Identity and tenant fences | Self/last-admin safeguards; shared account/provider/subject alias dependencies; incomplete legacy external links require verified sign-in; tenant delegates cannot retire global access | Both providers plus actual local HTTP and OIDC TestServer requests; own/foreign tenant mutations denied; retired upstream tickets rejected on generic and fixed-scheme private routes while public routes stay anonymous |
| Local/OIDC/Hybrid boundaries and recoverable failures | Startup-mode capabilities and UI gating; legacy customer activation rejects deleted, disabled or externally owned credentials; typed timeout/circuit/cancel/IO errors retain query/dialog input | Sixteen rendered mode/error/retry cases, six legacy account-shape HTTP cases, explicit OIDC-only endpoint metadata and browser route-availability checks |
| Theme, mobile and actual 200% zoom | Existing theme tokens; deliberate labelled overflow; native Chrome zoom extension and physical CDP captures | Ten ordinary before/after images each and four native before/after images each; original 137-row dataset is loaded before baseline captures |

The complete [verification receipt](assets/381-team/verification-receipt.json)
records **68 passed, 0 failed, 0 skipped**. Runtime source is
`e2be6b48a9a39f5486e4523a1ae8deacc931a85c`; test-only navigation setup advances
the tested head to `7e171b238c3a291aafc7ac6eefc1eed2e887a0b7`.

Relevant suites are
[retirement/provider cases](../../tests/Helpdesk.Tests/Infrastructure/Persistence/UserRetirementProviderTests.cs),
[OIDC request revocation](../../tests/Helpdesk.Tests/Api/UserRetirementOidcSessionTests.cs),
[local and legacy lifecycle requests](../../tests/Helpdesk.Tests/Api/LocalAuthenticationEndpointsTests.Retirement.cs),
[rendered dialogs and transport failures](../../tests/Helpdesk.Tests/NewWeb/UserLifecycleDialogTests.cs),
[Team browser behavior](../../tests/ux/team-directory.spec.ts) and
[the separate OIDC-only Web host](../../tests/ux/team-oidc-mode.spec.ts).

## Synthetic visual comparisons

The [browser receipt](assets/381-team/browser-receipt.json) includes every PNG
digest, tested viewport, exact source/binary boundary and command. The retained
before binary is a full Web-graph build of
`11c8997816833d753caf8df912a8279490da34fb`, SDK 10.0.400, SHA256
`4db367fa0ed6e7ecd5addd7687e47e84d15c275797cc0f176a4a8d824210b12b`.
It replaces the provisional pre-UX-root captures. Baseline rows are the same 137
synthetic identities; tall ordinary baseline images reflect the original unpaged
table.

The after presentation binary is SHA256
`257714fb164102110bac68e4191de2d5a80c9502084d62cf7c6567d1b8626c35`, with Team
Razor/CSS matching `b74159b1d007c7f7a4f90f21876724faf8c41a35`. Later helper and
transport-error changes are covered by the final rendered/provider tests and do
not establish a new integrated campaign screenshot. The native driver verifies
`chrome.tabs.setZoom=2`, `innerWidth=physicalWidth/2` and full physical screenshot
dimensions; CSS zoom and a CSS-pixel screenshot clip are not used.

| View | Before | After |
| --- | --- | --- |
| 1366×768, light | [Original table](assets/381-team/before-team-light-1366.png) | [Paged directory](assets/381-team/after-team-light-1366.png) |
| 390×844, light | [Original mobile table](assets/381-team/before-team-light-390.png) | [Labelled horizontal directory](assets/381-team/after-team-light-390.png) |
| Physical 1280×1000 at native 200%, light | [Original native zoom](assets/381-team/before-team-light-1280-native-200.png) | [Directory native zoom](assets/381-team/after-team-light-1280-native-200.png) |

Light/dark captures also cover 1920×1080, 1440×900 and 768×1024; native zoom
covers physical widths 1280 and 1920 in both themes. Every capture is synthetic.
No production accounts, data, external IdP mutations or provider jobs were used.

## Quality and integration boundary

Scoped API, Web and test builds passed with the pinned SDK. Slopwatch reported
zero new issues; documentation and repository layout validation passed. The
OpenCover/ReportGenerator receipt records 92.3% retirement and 94.1% directory
query line coverage. Helper extraction reduced retirement orchestration
cyclomatic complexity from 98 to 16 and directory read orchestration from 64 to 2.
The pure DTO mapper remains a recorded CC42 hotspot at 95% line coverage.

These local receipts establish the owning behavior and comparisons above. The
campaign register must add integrated managed-contact safeguards, peer review,
all six required checks on each final PR/main head, deep acceptance and release
distribution receipts before issue/campaign closure.
