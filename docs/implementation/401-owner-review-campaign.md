# #401 owner-review campaign register

Baseline: `90751d76a4f1f908de9bc311a9b52e904bc7adbb` / 0.1.3-beta.10.
The re-inventory found the same **68 declarations in 52 files** across all
206 baseline Razor components: 60 MudTable, six MudSimpleTable and two raw
content tables. The new query pager adds a component, not a table declaration.
The table owners remain #402–#409 under the single coordinated #401 campaign.

The change uses scoped compact table/content/tab classes and existing workspace
actions, preserves authored email output and retains semantic editor/Markdown
content. The [canonical presentation guide](../customization/compact-workspaces.md),
[activation/cancellation guidance](../deployment/self-hosting.md) and
[retention workflow](../integrations/mailbox-ingestion.md#organization-cleanup-and-retained-mailbox-history)
describe supported behavior. Root AGENTS.md records the standing preference.

## Correctness journeys

The actual API and Web ran against an isolated disposable SQLite instance with
Local authentication and mailbox ingestion/Hangfire disabled. A synthetic
account completed first activation, normal local sign-in, administrator-issued
reset and another normal sign-in with compliant 15-character passphrases.
Empty/fourteen-character browser validation and consumed-token HTTP400 were
also exercised. Authenticated gateway reads returned HTTP200 after both sign-ins.
The masked original passphrase's length and original failure remain unproven.

The actual organization UI/API explained NeedsReview and denied DELETE/retirement
with HTTP409, retained safe archived history with an explicitly confirmed disabled
organization, and deleted an empty synthetic organization. The filter and final
page refreshed correctly. A valid synthetic revoked credential returned HTTP401
before and after retention. Independent database reads confirmed both revoked
principals/verifiers, both archived disabled mailboxes and both cursors remained;
NeedsReview and Removed outcomes were unchanged. No production cleanup, mail,
automation, real-person password reset, NetRatel change or owner deployment occurred.

Cancellation tests cover downstream reads and actual HTTP client abort during
access projection. The real notification logger is drained through a queue
barrier before absence assertions. Unexpected cancellation, dependency/request
timeouts, database faults and response-started cases preserve diagnostics.
The historical cancellation source remains unknown; no history is purged.

## Rendered inventory

The deep audit expands every owning route/dialog/tab with long synthetic values.
It checks 1366×768, 1440×900, 390×844, light/dark, browser-only custom palettes and
native Chrome 200% zoom. Table receipts record actual DOM dimensions, body scroll,
sticky header, pager reachability, named actions and keyboard focus. Repeated
component instances retain separate host contexts and screenshots.

The [complete declaration ledger](401-table-ledger.md),
[interactive screenshot index](assets/401-owner-review/index.html) and
[machine-readable receipts](assets/401-owner-review/ledger.json) cover every
declaration and all ten modes. The selected matrix has **110 baseline comparisons
and 110 successful workspace after cases**, plus **ten current-source preview regressions**, with no skipped acceptance cases. Each source row maps to
its owner, original/current declaration, component hash, actual route/dialog/tab,
before/after screenshots and measured scrolling/header/pager/focus outcomes.
Repeated Markdown tables and the three recent-feed instances retain separate
host contexts. All four email-template categories, both role tabs, delegated
directory routes, both credential hosts and public/setup review hosts are included.

The 60 MudTable declarations changed through scoped presentation and necessary
behavior corrections. The six MudSimpleTable previews, semantic Markdown renderer
and authored-email table have concrete content exceptions in the ledger. Small
editor/key-value collections retain their semantics; they do not invent CRUD
pagers. Authored email markup is unchanged and its actual preview retains the
white ticket link in every theme. Re-inventory still finds 68 declarations in
52 files; the query pager increases the component count from 206 to 207.

Rendered review found and repaired the request-editor grid's narrow overflow,
the calendar editor's 960px minimum width, Graph result-action overflow, the
neutral slider selector, stale email-preview frame and mobile request-detail action alignment. The final
matrix uses the corrected tree. Failed cases do not contribute to the passing
ledger. The admin owner records eight successful regular modes and a separate
two-case headed native-zoom rerun after the stale-frame and capture failures. The preview wrapper now refreshes on rendered HTML identity; its 495-byte authored HTML remains unchanged.
Original run statistics are preserved; the failed run is not relabeled as passing.
Automatic Playwright retries remain disabled. Baseline images show the old behavior and do not claim
that its layout met the new standard.

## Behavior and quality evidence

The [normalized Release results](assets/401-owner-review/provider-and-component-results.json)
contain **169 passed, zero failed/skipped** focused API/component cases, including
actual SQLite and PostgreSQL execution. They cover active/retained dependencies,
leases/delivery/cursor boundaries, race handling, denied access, revoked credentials,
complete task filtering/counts, role assignments beyond 200, activation failures,
HTTP cancellation and real notification-queue barriers. A separate eight-case
[timezone run](assets/401-owner-review/timezone-boundary-results.json) verifies URL
round trips, date-only boundaries on 23/25-hour DST
days and rejection of ambiguous/invalid local times.

The [owning browser suites](assets/401-owner-review/owning-browser-results.json) exercise filtering, history, paging, selection,
bulk actions, popup placement, approvals, dirty editors, denied access, failures
and recovery independently of the ten-mode inventory screenshots. Details and
Operations regressions were rerun after their fixes. The actual isolated
[activation/reset receipt](assets/401-owner-review/browser-receipt.json),
[organization browser receipt](assets/401-owner-review/retention-browser-receipt.json)
and [database read-back](assets/401-owner-review/retention-state-receipt.json)
retain the real API/Web journeys described above. Their synthetic screenshots
are in [correctness evidence](assets/401-owner-review/correctness/first-activation-signed-in.webp).
Those journey screenshots preceded the final presentation refinements; the table
ledger supplies final visual acceptance.

The pinned 10.0.400 SDK Release solution build succeeded with zero errors and
28 inherited warnings. Slopwatch found zero new issues; no baseline was expanded.
OpenCover/ReportGenerator analysis of the same focused run is recorded in the
[method risk review](assets/401-owner-review/focused-risk-review.json). This
filtered collection is not a whole-solution coverage claim. Layout and local
documentation checks pass after evidence generation.

An earlier full local solution run exhausted the disposable environment's disk
while starting a provider container. It is not recorded as a pass. Two assertion
expectations exposed before that interruption were corrected and their owning
cases reran successfully. The complete pinned-SDK solution build/test result is
supplied by the final normal hosted PR/main .NET gate, with the separate strict
HTTP/provider gate. Existing unrelated skips are not presented as new acceptance.

## Coordinated delivery receipts

The single coordinated change is [PR #410](https://github.com/BostonTechnologies/RatelDesk/pull/410).
The release manifest and the [#401 completion record](https://github.com/BostonTechnologies/RatelDesk/issues/401)
are the authoritative post-merge receipts for the exact merge SHA, six successful
current-main jobs, hosted release/native-archive/image jobs, explicit public
prerelease publication and independent anonymous downloads/pulls. Those receipts
are produced after this source register is merged; source acceptance alone does
not establish public distribution. Child issues close only after their complete
acceptance; the epic closes after public distribution verification.

The allocated candidate is `0.1.3-beta.11`, set only in root Directory.Build.props
after both accepted tag forms, releases and three image families were checked
unused. Stable `v0.1.2` and the three `latest` digests are recorded for comparison
after publication. Follow the
[owner retest checklist](../release-notes/0.1.3-beta.11.md#owner-retest).
