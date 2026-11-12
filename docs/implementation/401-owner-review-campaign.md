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

## Rendered inventory and delivery receipts

The deep audit expands every owning route/dialog/tab with long synthetic values.
It checks 1366×768, 1440×900, 390×844, light/dark, browser-only custom palettes and
native Chrome 200% zoom. Table receipts record actual DOM dimensions, body scroll,
sticky header, pager reachability, named actions and keyboard focus. Repeated
component instances retain separate host contexts and screenshots.

The complete per-declaration ledger, before/after assets, final test counts and
exact-source PR/main/release/public-distribution receipts are being assembled.
Pending evidence is not an acceptance claim. Child issues and epic stay open
until their required acceptance and public distribution verification are complete.

The allocated candidate is `0.1.3-beta.11`, set only in root Directory.Build.props
after both accepted tag forms, releases and three image families were checked
unused. Stable `v0.1.2` and the three `latest` digests are recorded for comparison
after publication. Release dispatch and publication remain pending; see the
[owner retest checklist](../release-notes/0.1.3-beta.11.md#owner-retest).
