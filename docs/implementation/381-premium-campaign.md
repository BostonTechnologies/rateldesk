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
| #388 | `codex/388-admin-directories`: bounded customer directory/access projection | >100 records, whole-query status filters, bounded request/query count, invitations | Authored; synthetic presentation passed; provider/integrated acceptance pending |
| #389 | `codex/388-admin-directories`: organization directory | Complete queries, saved mapping/catalog-unavailable truth, delegated views | Authored; synthetic presentation passed; provider/integrated acceptance pending |
| #390 | `codex/390-operational-cards`: structured connection cards | Saved IDs, pairing/actions and truthful fresh/stale/never-tested state | In progress; acceptance incomplete |
| #391 | `codex/390-operational-cards`: Service Explorer card geometry | 0/2/20/100 assignments, bounded keyboard-accessible lists/actions | In progress; acceptance incomplete |
| #392 | `codex/388-admin-directories`: additive category admin query | Exact type versus legacy Service inclusion, full queries and protections | Authored; synthetic presentation passed; provider/integrated acceptance pending |
| #393 | `codex/390-operational-cards`: Operations Overview | Dynamic counters/links, all three feeds, roles and partial errors | In progress; acceptance incomplete |
| #394 | `codex/390-operational-cards`: effective branded search | Branding precedence/fallback, long names, shortcut/focus and responsive shell | In progress; acceptance incomplete |

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

These results establish local synthetic Web behavior. Both-provider owning API
tests, integrated-source validation, the six normal CI jobs, reviewed merge and
release/distribution remain separate gates; none is inferred from screenshots.
