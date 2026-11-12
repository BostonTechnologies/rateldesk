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
| #390 | `codex/390-operational-cards`: structured connection cards | Saved IDs, pairing/actions and truthful fresh/stale/never-tested state | In progress; acceptance incomplete |
| #391 | `codex/390-operational-cards`: Service Explorer card geometry | 0/2/20/100 assignments, bounded keyboard-accessible lists/actions | In progress; acceptance incomplete |
| #392 | `codex/388-admin-directories`: additive category admin query | Exact type versus legacy Service inclusion, full queries and protections | In progress; acceptance incomplete |
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
