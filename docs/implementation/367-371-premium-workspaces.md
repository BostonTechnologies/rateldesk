# Issues 367–371: premium workspaces delivery register

Source-review status (2026-11-08): implementation and local acceptance complete. Current reviewed-merge, hosted CI and public-distribution receipts are maintained in [PR #372](https://github.com/BostonTechnologies/RatelDesk/pull/372) and the [beta.8 release](https://github.com/BostonTechnologies/RatelDesk/releases/tag/v0.1.3-beta.8). These later receipts are separate from the local source-review evidence below.

Source baseline: `6a7e7fb2ffc2c2429c82df563c182c211932a37d` (current main on 2026-11-08).
Integration branch: `feat/367-371-premium-workspaces`. Root product version prepared as `0.1.3-beta.8` after both tag prefixes, every draft/public release and all three anonymous image families were checked unused on 2026-11-08 at 18:21 UTC. No tag or publication is implied.

User authorization covers implementation, reviewed PR/merge, hosted release and publication; production deployment, real mail and customer automation are excluded.

## Verification receipts

- SDK 10.0.400: complete Debug solution build passed with zero errors and 23
  existing warnings. Final hosted Release/full-suite checks remain separate.
- Combined focused API/domain/rendered run: **129 passed, zero failed/skipped**
  (5m36s), including actual SQLite/PostgreSQL, approvals, concurrent draft writers
  and state/authority. Follow-up queue/audit coverage run: **22 passed**, including
  fixed 1,106 membership, stale explicit IDs, exclusions, arrivals, conflicts,
  revoked authority, concurrent processors, rollback/resume and zero submissions.
- Final rendered/component run: **49 passed, zero failed/skipped**, including
  both completion orders of superseded lookup callbacks, scope-bound choices,
  all-type plan labels, audit alignment and first Tasks query.
- Complete owning Playwright suites: Pending Emails **34/34**, operations
  **41/41**, creation/assignees **40/40**, ticket details **44/44**, contributor/
  manager roles **7/7**. No retries/skips. The category refresh test holds an
  actual fixture response between pointer-down and click, then verifies retained
  selection with zero saves. Existing worklogs, attachments, categories, approval
  and scope regressions remain enabled.
- Native Chrome `tabs.setZoom(2)` suites: pending-mail, operations, assignee and
  details **4/4 each** (light/dark, physical 1280/1920 by 1000). Layout width is
  independently checked as 640/960; full pager/footer intersection is asserted.
  Rules and Audit are both exercised. These are actual browser zoom receipts.
- Light/dark matrices cover 1440×900, 1366×768 and mobile 320/390px, realistic
  long labels and dense synthetic rows. Before screenshots use baseline main
  `6a7e7fb`; after screenshots use the final owning suites. Reviewed images include
  grids, rules/audit, all four creation steps, detail/edit and native zoom.
- Focused OpenCover/ReportGenerator analysis across the three query/operation
  services: 88.5% sequence / 71.73% branch coverage. New audit query: 95.34%
  sequence; selection resolution: 98.07%; atomic target processing: 100% sequence
  / 93.75% branch. Risk hotspots include generated async query complexity and the
  existing pending-mail projection; this focused report is not whole-app coverage.
  Native boundary/race tests substantiate the transaction and authorization paths.
- Slopwatch: zero new issues; baseline unchanged. Layout and release helper
  regressions passed. Documentation: 195 Markdown files / 912 local links passed.
  Public disclosure and gitleaks passed against the committed source archive.
- Earlier failed browser iterations are not acceptance receipts. They exposed
  encoded-space URL reloads, stale DOM interactions, clipped zoomed pagers,
  tooltip/action layout and asynchronous lookup/category click races, all fixed
  and verified in the final runs above. An initial interrupted coverage attempt
  is superseded by the completed 22-test OpenCover run.
- Local output evidence: owning suite screenshots, native-zoom metadata/traces,
  provider/rendered TRX and focused coverage reports. Hosted deep acceptance uses
  the existing workflow and uploads its own artifacts. PR/main/release links and
  public archive/image digests are recorded in the linked delivery receipts.
  No production deployment or owner acceptance is claimed.

## Acceptance mapping

Each issue criterion below retains its original wording. Local acceptance is recorded per row; hosted/deployment/publication receipts remain separately identified. Implementation locations are repository-relative under the existing owning Web/API/Infrastructure/Shared projects.

## [#367](https://github.com/BostonTechnologies/RatelDesk/issues/367) — Enhancement: premium Pending Emails table with MudBlazor paging and select-all-matching cancel/removal

Primary implementation: PendingEmails.razor; PendingEmailQueryService; PendingEmailActions; TimelineEndpoints.

| Criterion | Implementation | Verification | Actual result / evidence | Remaining |
| --- | --- | --- | --- | --- |
| Synthetic set of at least 1,106 matching deliveries plus unrelated controls can be selected across pages, selectively excluded, cancelled and removed through the reviewed workflow, with exact membership/outcomes and retained history. | `PendingEmails` selection; `PendingEmailOperationService.ResolveAsync/ProcessAsync`; both migrations | 1,106 + 7 provider fixture; browser bulk test with 2 exclusions, arrival, fence conflict and pause/reload/resume | Passed locally: 22 provider/coverage cases, 34 browser cases and 4 native zoom cases; see receipts | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| New arrivals, filter changes, permission changes and concurrent dispatch do not broaden the confirmed set or bypass per-record eligibility. | `PendingEmailOperationService.CaptureAsync/ProcessTargetAsync`; existing queue action CAS | SQLite/PostgreSQL revoked authority, foreign actor and concurrent processor tests; browser filter/Back tests | Passed locally: 22 provider/coverage cases, 34 browser cases and 4 native zoom cases; see receipts | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Dismiss/invalid confirmation makes zero mutations; partial failure/reconnect/repeated submission do not skip or double-process records. Cancel/removal produce zero outgoing mail submissions. | `PendingEmailActionDialog`; actor-bound operation receipts and per-target transactions | Owning normal/embedded dismissal/retry cases; fault/resume and zero dispatcher submissions | Passed locally: 22 provider/coverage cases, 34 browser cases and 4 native zoom cases; see receipts | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Real MudBlazor pager, fixed header and reachable footer work with 25/50/100 rows and all existing filters. | `PendingEmails.LoadTableAsync`, native pager and opt-in flex table CSS | Browser 25/50/100 pager/range, URL/Back and footer geometry assertions | Passed locally: 22 provider/coverage cases, 34 browser cases and 4 native zoom cases; see receipts | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Extend `tests/ux/pending-mail.spec.ts`, its fixture API and owning API/queue tests; cover SQLite and PostgreSQL for any changed backend behaviour. | Existing owning queue/API/Web fixtures extended; no new test stack | Queue tests on both actual providers, browser fixture kept separate | Passed locally: 22 provider/coverage cases, 34 browser cases and 4 native zoom cases; see receipts | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Verify actual rendered behaviour at 1440×900 and 1366×768, a narrow/mobile viewport and 200% zoom, in light and dark themes. Include long names/subjects, empty/loading/error states and keyboard navigation. Supply before/after synthetic screenshots. Passing source-string or CSS-presence assertions alone is insufficient. | `PendingEmails`; shared `WorkspaceAction`; scoped `.premium-workspace` styles | Light/dark 1440x900, 1366x768, 320px screenshots plus actual Chrome tabs.setZoom(2) | Passed locally: 22 provider/coverage cases, 34 browser cases and 4 native zoom cases; see receipts | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |

### Full functional requirements

## Table and action design
- [MudBlazor table: pagination and filtering](https://www.mudblazor.com/components/table#table-with-pagination-and-filtering)
- [Fixed header and footer](https://www.mudblazor.com/components/table#fixed-header-and-footer)
- [Simple icon buttons](https://www.mudblazor.com/components/iconbutton#simple-icon-buttons), [tooltips](https://mudblazor.com/components/tooltip), and [icons](https://www.mudblazor.com/features/icons#icons)

- Use a real `MudTable` with server filtering/sorting/paging and `MudTablePager`, default 25 and options 25/50/100. Keep total/range counts, first/previous/next/last navigation, existing filters and URL/Back/Forward state.
- Put search, common filters, refresh and an advanced-filter disclosure in a compact toolbar. Sortable headers should replace redundant sort controls where appropriate.
- Constrain the table to the available viewport; use `FixedHeader`/`FixedFooter` and a deliberately reachable pager/footer. Verify the pager itself stays reachable: a sticky data footer alone is not proof of that. No scrolling through 100 tall rows merely to change page.
- Suggested columns: selection, created, ticket tracking ID with secondary subject, recipient with concise sender summary, status/reason summary, compact trailing actions.
- Replace the four row text buttons with small `MudIconButton` controls inside `MudTooltip`: `Visibility` (Delivery details), `Replay` (Review / retry), `Block` (Cancel future delivery), `DeleteOutline` (Remove queue entry). Supply matching accessible labels; explain unavailable actions and retain all existing review/dialog paths. Keep destructive actions visually distinct without four colourful filled buttons.
- Use the existing MudBlazor 9.11.0 components and theme tokens. A professional finish means a compact aligned toolbar, readable typography, subtle borders/hover/selection, balanced spacing, restrained semantic colour and stable column widths. Keep light/dark/custom themes, visible keyboard focus, accessible names and touch targets. Long content must not expand a column indefinitely or turn a row into a wall of wrapped identifiers; provide full content through an accessible detail surface. Avoid global CSS that changes unrelated tables.

## Cross-page selection and bulk workflow
- Distinguish **Select this page** from **Select all N matching deliveries**, with explicit selected count, clear selection, mixed checkbox state and optional per-row exclusions. Paging must preserve the intended selection. A filter/scope change must clear or explicitly rebase it; never silently broaden it.
- Offer Cancel selected and Remove selected for the full reviewed selection. The unfiltered authorized population must also be selectable through this explicit all-matching flow. A larger page size or manually repeated batches does not meet the requirement.
- Resolve a stable selected set before confirmation, show the action, filter/scope, total, eligibility/skipped counts and required reason. Freeze membership so new arrivals or changed filters are not silently included.
- Process bounded server batches or a durable operation over that stable set. Retain per-target authorization and state/revision fencing; do not simply remove the 100-record guard or load every row into the browser. Avoid offset paging over a queue being removed, which can skip records.
- Show progress, completed/skipped/conflicted/failed counts and useful per-record outcomes. Support interrupted/partial operations without duplicate commits or falsely reporting success; define resume/reconciliation for the same operation.
- Cancellation prevents eligible future dispatch; queue removal retains ticket/email history and never implies recall. Keep unknown/submitting/partially accepted delivery safeguards and individually reviewed retry paths. Do not add automatic bulk retry or an unreviewed purge. If cancellation followed by removal is offered, make both stages explicit and retain unresolved outcomes.

## [#368](https://github.com/BostonTechnologies/RatelDesk/issues/368) — Enhancement: redesign Email Rules and Audit with paged MudBlazor tables and readable audit records

Primary implementation: EmailRulesAdmin.razor; InboundEmailRuleEndpoints; InboundEmailRuleAdminClient.

| Criterion | Implementation | Verification | Actual result / evidence | Remaining |
| --- | --- | --- | --- | --- |
| Header/cell alignment is correct for every audit outcome, with and without organization/ticket/error values. | `EmailRulesAdmin` seven audit headers/cells and matching DataLabel | Rendered bUnit header/cell counts and browser older failure details | Passed locally: native full-history query tests, rendered alignment, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| More than 200 synthetic audit records are searchable/pageable; a matching status/rule/date record older than the original 200-row window is found. | `InboundEmailAuditQueryService.SearchAsync`; additive `/audit/page` endpoint/client | 305 native-provider records, old status/rule/date/search matches, deterministic pages | Passed locally: native full-history query tests, rendered alignment, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Friendly labels and details work for long names, missing lookups, deleted rules and no-ticket events without N+1 lookups or leaking inaccessible records. | Batched lookup dictionaries and authorized ticket links; exact audit DTO in details | Native missing/deleted lookup and unsupported/unauthorized ticket cases; batched query review | Passed locally: native full-history query tests, rendered alignment, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Rules retain clone/binding/toggle/details/reorder semantics across sorting, filters and page boundaries; no incidental configuration changes from browsing. | `EmailRulesAdmin.ReorderGroup/MoveRuleAsync`, clone/bind/toggle/details paths | Existing CRUD/enable/disable/reorder API tests and rendered operations checks | Passed locally: native full-history query tests, rendered alignment, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Extend `EmailRulesAdminUxTests`, `InboundEmailRuleEndpointsTests` and a real browser fixture with synthetic data. Cover both databases for changed queries/contracts. | Existing `EmailRulesAdminUxTests`, `InboundEmailRuleEndpointsTests`, operations fixture | Owning tests; actual SQLite/PostgreSQL cases for the new query | Passed locally: native full-history query tests, rendered alignment, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Verify actual rendered behaviour at 1440×900 and 1366×768, a narrow/mobile viewport and 200% zoom, in light and dark themes. Include long names/subjects, empty/loading/error states and keyboard navigation. Supply before/after synthetic screenshots. Passing source-string or CSS-presence assertions alone is insufficient. | `EmailRulesAdmin`; shared actions; opt-in table/toolbar CSS | Rules and audit light/dark desktop/mobile screenshots and actual browser zoom | Passed locally: native full-history query tests, rendered alignment, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |

### Full functional requirements

## Shared table treatment
- [MudBlazor table: pagination and filtering](https://www.mudblazor.com/components/table#table-with-pagination-and-filtering)
- [Fixed header and footer](https://www.mudblazor.com/components/table#fixed-header-and-footer)
- [Simple icon buttons](https://www.mudblazor.com/components/iconbutton#simple-icon-buttons), [tooltips](https://mudblazor.com/components/tooltip), and [icons](https://www.mudblazor.com/features/icons#icons)

Use the existing MudBlazor 9.11.0 components and theme tokens. A professional finish means a compact aligned toolbar, readable typography, subtle borders/hover/selection, balanced spacing, restrained semantic colour and stable column widths. Keep light/dark/custom themes, visible keyboard focus, accessible names and touch targets. Long content must not expand a column indefinitely or turn a row into a wall of wrapped identifiers; provide full content through an accessible detail surface. Avoid global CSS that changes unrelated tables.
Use native MudBlazor pagers, useful search/filter controls, stable sorting and viewport-bounded tables with sticky headers and a reachable footer. Default 25 rows and offer 25/50/100. Show total/range and proper loading, empty, filtered-empty and retryable error states. A failed load must not masquerade as no audit records.

## Rules tab
- Keep rule name as the primary column; show a short description, friendly organization/scope and mailbox labels without repeated GUIDs.
- Present enabled state, priority/order and stop/continue processing with subdued, understandable labels.
- Compact trailing actions use suitable icons with tooltips and accessible names: `ContentCopy` (Clone), `KeyboardArrowUp/Down` (Move), `Visibility` (Details), `ToggleOn/Off` (Enable/Disable). Several existing icon controls have no accessible name: complete this consistently, including disabled reasons.
- Preserve current clone-as-disabled, mailbox binding, enable/disable confirmation, details, precedence and reorder scope. Reordering from a filtered/sorted/paged view must act on the correct complete scope; do not renumber only the visible subset or mix tenant/mailbox precedence.
- Move oversized per-row configuration into a clearly accessible existing detail/edit surface where appropriate; retain every current capability.

## Audit tab
- Proposed default columns: time, rule name, organization/mailbox, understandable action/outcome, related ticket and compact details action. Give every cell a matching header and mobile `DataLabel`.
- Resolve authorized ticket tracking IDs and rule/organization/mailbox names efficiently. A removed or unresolved rule should read “Deleted rule”/“Unknown rule” as appropriate, with the original ID in details; never invent a historical name or guess an incident link for an unsupported entity.
- Replace raw Message ID and long diagnostic strings in the main grid with a details drawer/dialog containing exact message/correlation/rule/ticket IDs, action key, status, time and complete diagnostic/error text with copy affordances.
- Offer human-useful filters: search, organization/mailbox, rule, outcome/status and date range. Keep precise technical-ID lookup in Advanced filters/details for diagnostics.
- Implement true server-side filtering, deterministic sorting, total counts and paging **before** limiting results; maintain compatibility for current API/CLI/MCP consumers, for example via an additive paged query contract.
- Keep all audit events and exact stored semantics. “Useless garbage” here means poor default presentation, not permission to delete history, hide failures, merge distinct processing outcomes or replay inbound messages.

## [#369](https://github.com/BostonTechnologies/RatelDesk/issues/369) — Enhancement: guided change submission and premium detail view with single field labels

Primary implementation: CreateChangeDialog; ChangeTemplateEditor; ChangeDetail.

| Criterion | Implementation | Verification | Actual result / evidence | Remaining |
| --- | --- | --- | --- | --- |
| Each field has one visible label and one meaningful accessible name in create, draft edit and read-only views. | `ChangeFieldHelp`, shared `ChangeTemplateEditor` and `ChangePlanSummary` | bUnit all-type single associated input labels and read-only summaries | Passed locally: both-provider drafts/authority, 49 rendered cases, 40 creation, 44 detail, 7 role and 8 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Standard, Normal and Emergency changes can be created under existing rules, saved/reopened and edited without lost hidden-step/type-specific data. | `CreateChangeDialog` one DTO, type retention; `ChangeDetail` shared editor | All three types complete/incomplete provider draft roundtrips and browser create/reopen/type switch | Passed locally: both-provider drafts/authority, 49 rendered cases, 40 creation, 44 detail, 7 role and 8 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Permitted incomplete drafts remain possible; completeness and later submission failures are explained and navigate to the correct section. | `CreateChangeDialog.ValidateDraft/GoToStepAsync/FocusErrorAsync`; additive SaveAsDraft | Browser hidden-step focus and optional plan completeness; domain validation suites | Passed locally: both-provider drafts/authority, 49 rendered cases, 40 creation, 44 detail, 7 role and 8 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Scope/participant changes retain existing eligibility/invalidation rules. Validation/save/network failure retains the draft; repeated final clicks create/update once. | Organization reset, existing participant/category pickers, Submit guard and retained draft | Owning scope/late lookup/save-failure tests and double-final-click once assertion | Passed locally: both-provider drafts/authority, 49 rendered cases, 40 creation, 44 detail, 7 role and 8 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Implementation start/end validation and time roundtrips remain correct. Cancel/discard leaves stored data unchanged. | UTC labelled date/time editors; `CombineDateTime`; dirty navigation/discard guard | All-type UTC provider/browser roundtrips; dirty cancel/discard and Save/Revert regressions | Passed locally: both-provider drafts/authority, 49 rendered cases, 40 creation, 44 detail, 7 role and 8 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Existing approval/lifecycle authority and locked-state rules remain enforced; no automatic submissions or production mail/workflow actions are used for verification. | `ChangeEndpoints` explicit draft opt-in; existing lifecycle/approval endpoints | Authenticated approval, concurrent writer and state/authority suites on both providers | Passed locally: both-provider drafts/authority, 49 rendered cases, 40 creation, 44 detail, 7 role and 8 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Extend owning rendered/browser coverage and retain `AuthenticatedChangeDraftWriterTests`, `AuthenticatedChangeApprovalTests` and `ChangeStateAndAuthorityHttpTests`. Include all existing ticket-detail integrations in regression review. | Owning draft-writer/approval, ticket detail and assignee rendered/browser suites | Existing ticket detail activity/worklog/attachments/categories/return tests retained | Passed locally: both-provider drafts/authority, 49 rendered cases, 40 creation, 44 detail, 7 role and 8 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Verify actual rendered behaviour at 1440×900 and 1366×768, a narrow/mobile viewport and 200% zoom, in light and dark themes. Include long names/subjects, empty/loading/error states and keyboard navigation. Supply before/after synthetic screenshots. Passing source-string or CSS-presence assertions alone is insufficient. | `CreateChangeDialog`, `ChangeDetail.razor.css`; four-step viewport/native zoom suites | Four-step create and detail/edit light/dark 1440x900, 1366x768, 390px plus native zoom | Passed locally: both-provider drafts/authority, 49 rendered cases, 40 creation, 44 detail, 7 role and 8 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |

### Full functional requirements

## Chosen creation flow
Use one responsive, bounded editor with four clear steps:
1. **Overview** — title, organization, change type, priority, requested-for and a concise description.
2. **Plan** — core scope/systems/implementation/validation/rollback, with the relevant Standard, Normal or Emergency requirements grouped clearly. Keep optional/advanced items behind purposeful disclosures.
3. **Schedule & people** — implementation start/end, an explicit timezone, implementor, approvers and existing categories.
4. **Review** — a readable summary, completeness/missing-field checklist and links back to the responsible step.

Use a compact step navigator, a persistent Back/Next/Cancel/final-action footer and a single deliberate body scroll region. On mobile, keep the current step and actions reachable without stacking the whole form. Bound reading/input widths; use at most two useful columns at desktop widths rather than three giant empty editors.

### Labels, help and validation
- Render **one visible label per input** with a correct programmatic association. Remove repeated external captions/floating labels; placeholders provide an example only when useful and never repeat the label.
- Keep required indicators and one concise helper where needed. Put longer explanations/examples behind keyboard-accessible help; do not eliminate useful guidance.
- Use sensible initial textarea heights and controlled auto-growth; structured multiline plans must remain easy to enter and read. Do not merge distinct business fields merely because their names are similar.
- Validate the whole draft, including hidden steps. Show a concise error summary and navigate/focus the first relevant invalid field. Step changes preserve every value, validation state and selected identity.
- Preserve the existing distinction between creating/saving a permitted incomplete draft and submitting a complete change for approval. Do not silently advance lifecycle, make every template field mandatory for draft creation, or mislabel Create as submission.
- Review/final actions must name their exact effect. Reuse existing API/lifecycle transitions; expose a submission action only when authorized and supported, without automatically combining it with draft creation.

## Matching detail/edit experience
- Compact identity header: tracking ID/title, restrained lifecycle/risk/completeness indicators and permission-aware actions.
- Present Overview, Plan, Schedule/people and existing activity/approval surfaces with consistent hierarchy and a readable summary. Default read-only content should read like a change record instead of an expanse of disabled textboxes.
- Draft editing uses the same field groups, labels, help, validation and data model as creation. Keep Save/Revert and unsaved state reachable. Do not fork two implementations that drift.
- Preserve all existing worklogs/activity, attachments, categories, approvals, lifecycle transitions and other ticket-detail capabilities. Existing locked/non-Draft changes retain their read-only and approval semantics.
- Keep accessible focus/error navigation, returnUrl/Back behaviour, proper timestamps/timezone handling and dirty-draft dismissal protection.

Use the existing MudBlazor 9.11.0 components and theme tokens. A professional finish means a compact aligned toolbar, readable typography, subtle borders/hover/selection, balanced spacing, restrained semantic colour and stable column widths. Keep light/dark/custom themes, visible keyboard focus, accessible names and touch targets. Long content must not expand a column indefinitely or turn a row into a wall of wrapped identifiers; provide full content through an accessible detail surface. Avoid global CSS that changes unrelated tables.

## [#370](https://github.com/BostonTechnologies/RatelDesk/issues/370) — Enhancement: default Tasks to all authorized assignments and redesign the dashboard table

Primary implementation: TaskDashboard.razor; task query/transition endpoints.

| Criterion | Implementation | Verification | Actual result / evidence | Remaining |
| --- | --- | --- | --- | --- |
| A fresh navigation and its first server request use Assigned to me=false; opting in correctly restricts results, and opting out restores the authorized population. | `TaskDashboard.assignedToMe` false; existing authorized endpoint | Rendered first-request assertion and browser all/opt-in/opt-out queries | Passed locally: rendered first-query/pager, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| More than one page of tasks is fully navigable; filtering resets invalid page state and never shows an inaccessible task. | `TaskDashboard.LoadServerData/Reload`; native server pager | 76 fixture tasks, page 2 and filter reset; existing authorization suites | Passed locally: rendered first-query/pager, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Manual/automation tasks in pending/running/completed/failed/blocked/reconciliation states have clear subdued presentation and correct permitted actions. | `WorkspaceLabels`, local status/type, binding tooltip and external status | Rendered local Running/status/action tests and browser populated state matrix | Passed locally: rendered first-query/pager, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Row controls do not accidentally select/navigate a row or execute twice. Bulk selection/counts stay truthful after filter/page changes. | Shared action stop-propagation; task/bulk busy guards; selection reset on reload | Code/action guard review and owning transition tests; selection reset checks | Passed locally: rendered first-query/pager, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Add meaningful rendered/server-query and browser tests around the current dashboard and existing task transition/automation tests; verify no change to execution semantics. | Existing `TicketListRenderedTests`, operations/task transition suites | Meaningful bUnit HTTP query assertions plus existing task automation tests | Passed locally: rendered first-query/pager, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Verify actual rendered behaviour at 1440×900 and 1366×768, a narrow/mobile viewport and 200% zoom, in light and dark themes. Include long names/subjects, empty/loading/error states and keyboard navigation. Supply before/after synthetic screenshots. Passing source-string or CSS-presence assertions alone is insufficient. | `TaskDashboard` column groups and scoped bounded table styles | Light/dark desktop/mobile and native Chrome zoom screenshot matrix | Passed locally: rendered first-query/pager, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |

### Full functional requirements

## Requested behaviour and layout
- Initialize Assigned to me to false, including the first API request. The default shows the full population the current actor is already authorized to see; it does not broaden access.
- Keep the opt-in filter useful, visible and stable during refresh/rerender. If explicit query/saved state is supported, honour deliberate user choices; do not invent a persistence system or migrate unrelated preferences merely to change a default.
- Align search, status/type filters and the assignment switch in a compact toolbar. Put raw Request ID/Service ID filters in an Advanced disclosure where appropriate and preserve their functionality.
- Use the [MudBlazor pagination/filtering](https://www.mudblazor.com/components/table#table-with-pagination-and-filtering) and [fixed-header/footer](https://www.mudblazor.com/components/table#fixed-header-and-footer) patterns: real server-backed pager, total/range, 25/50/100 page options, bounded scroll and reachable footer.
- Keep tracking ID, request title, task name, assignee and created time readable. Balance primary/secondary text; long labels must not force tiny tracking columns or excessive row height. Keep full text available in an accessible detail/tooltip surface.
- Type should be neutral text or a small muted label. Use a single understated status indicator with readable labels (“In progress”, “Failed”, “Pending”). Binding sync may be an unobtrusive icon/detail, with attention colour reserved for a meaningful warning/failure.
- Preserve the distinction between local task state, binding health and external orchestration/reconciliation state; simplify presentation without suppressing a blocked or unknown outcome.
- Treat Running as status, not a disabled action. Use compact [icon buttons](https://www.mudblazor.com/components/iconbutton#simple-icon-buttons) with tooltips and accessible names: `PlayArrow` (Start), `CheckCircleOutline` (Complete), `Replay` (Retry), `ManageSearch` (Open recovery). Keep bulk-action labels/counts explicit and expose disabled reasons.

Use the existing MudBlazor 9.11.0 components and theme tokens. A professional finish means a compact aligned toolbar, readable typography, subtle borders/hover/selection, balanced spacing, restrained semantic colour and stable column widths. Keep light/dark/custom themes, visible keyboard focus, accessible names and touch targets. Long content must not expand a column indefinitely or turn a row into a wall of wrapped identifiers; provide full content through an accessible detail surface. Avoid global CSS that changes unrelated tables.

## Behaviour to preserve
Keep existing role/assignment/organization authorization, filters, request navigation, history, row/bulk Start/Complete/Retry and recovery links. Maintain duplicate-action guards, automation binding eligibility and manual reconciliation requirements. A visual refresh must not offer retry for an uncertain external execution, auto-start tasks, alter provider states or make every task operable.

## [#371](https://github.com/BostonTechnologies/RatelDesk/issues/371) — Enhancement: premium Notifications table with human-readable events and no Reference column

Primary implementation: Notifications.razor; SystemNotificationApiClient; receipt/summary services.

| Criterion | Implementation | Verification | Actual result / evidence | Remaining |
| --- | --- | --- | --- | --- |
| Reference is absent from the default desktop/mobile grid while exact ID search, event/timeline details and links still work. | `Notifications` grid; existing reference/query/timeline DTOs and routes | Browser absent Reference header, exact-reference search and event details; existing timeline tests | Passed locally: native receipt/summary tests, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Long/unknown event types, verbose sources, no-reference/no-link events and all severities remain readable and diagnosable. | `WorkspaceLabels.Readable/Source`; payload summary with raw diagnostic fallback | Long synthetic event/source payloads and unknown readable fallback; raw details preserved | Passed locally: native receipt/summary tests, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Per-row and bulk mark-read update the correct actor's receipts/counts, remain idempotent and handle failure; the presentation does not claim an event is resolved. | `SystemNotificationApiClient` success checks; receiptBusy, reconcile after bulk | Browser failed/successful actor receipts and existing native repository/summary aggregation tests | Passed locally: native receipt/summary tests, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Filters, date range, sorting and every page remain accessible. Live/reconnect events preserve row identities, selection and reading position without duplication. | `Notifications` held rows plus refresh prompt; stream connection event and dedup | Browser page 2 arrival duplicate/reconnect selection/scroll assertions; existing filters/pager | Passed locally: native receipt/summary tests, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Add meaningful rendered/browser regressions using synthetic notifications and retain owning repository/service/summary tests. Verify page-scoped CSS has no effect on another table. | Owning operations fixture/browser and repository summary tests; scoped CSS test | Owning operations rendered browser suite and independent workflow table style comparison | Passed locally: native receipt/summary tests, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |
| Verify actual rendered behaviour at 1440×900 and 1366×768, a narrow/mobile viewport and 200% zoom, in light and dark themes. Include long names/subjects, empty/loading/error states and keyboard navigation. Supply before/after synthetic screenshots. Passing source-string or CSS-presence assertions alone is insufficient. | `Notifications` compact responsive filters, bounded native pager and actions | Light/dark desktop/mobile screenshots and native Chrome zoom | Passed locally: native receipt/summary tests, 41 operations cases and 4 native zoom cases | Hosted source/release receipts tracked in PR #372; owner deployment/testing remains separate |

### Full functional requirements

## Information hierarchy and interaction
- Remove Reference from the default grid, its mobile labels and associated width/layout assumptions. Keep stored references/correlation IDs, exact search, deep links, timeline grouping and copyable diagnostic details.
- Suggested columns: selection, severity, event/summary (primary flexible column), concise source/category, created, quiet read state and compact actions. Give meaningful content the recovered width.
- Use readable event labels and concise summaries where the existing payload supports them: for example “Task dashboard viewed” or “Ticket SLA breached”, while retaining the original event key in details. Unknown types need an honest readable fallback; do not invent a description, actor or resolution.
- Shorten displayed source/category labels; put namespace/assembly/environment/service metadata in event details. Constrain overflow with accessible full content. Do not change or truncate the underlying event/payload.
- Use neutral Read/Unread treatment (subtle dot/emphasis/label) rather than green/orange filled chips on every row. Severity remains independently labelled and correctly coloured; informational events must not look like completed business operations merely because they use a green check.
- Replace repeated Mark Read/Open text buttons with small [MudIconButton](https://www.mudblazor.com/components/iconbutton#simple-icon-buttons) controls and [tooltips](https://mudblazor.com/components/tooltip): `MarkEmailRead` (Mark as read), `OpenInNew` (Open related item), `InfoOutlined` or `Visibility` (Event details) where an explicit details trigger is useful. Every action needs an accessible name and keyboard access; it must not accidentally trigger the row's separate action.
- Keep search/common filters, advanced created-date filter, clear/reset and selection actions aligned. Present unread/total, 24-hour metrics and Live/Reconnecting state with restrained hierarchy and clear scope.
- Use [MudBlazor table paging/filtering](https://www.mudblazor.com/components/table#table-with-pagination-and-filtering) and [fixed header/footer](https://www.mudblazor.com/components/table#fixed-header-and-footer) patterns. Keep server-side sort/filter/page behaviour, reachable pager, useful row limits and a bounded table scroll.

Use the existing MudBlazor 9.11.0 components and theme tokens. A professional finish means a compact aligned toolbar, readable typography, subtle borders/hover/selection, balanced spacing, restrained semantic colour and stable column widths. Keep light/dark/custom themes, visible keyboard focus, accessible names and touch targets. Long content must not expand a column indefinitely or turn a row into a wall of wrapped identifiers; provide full content through an accessible detail surface. Avoid global CSS that changes unrelated tables.

## Preserve correctness
Do not delete notifications, remove reference/correlation fields from DTOs/storage, disable audit/event generation or suppress operational errors as a visual shortcut. Keep receipt state actor-specific, summary/filter meanings, bulk selection, authorized link navigation, event detail/raw JSON and timeline capabilities.

Live arrivals must preserve selection and current reading position, respect filters/sort and avoid duplicates/page jumps. On older pages or active filters, expose a restrained new-events indication as appropriate. Reconnecting/unavailable is distinct from live/empty; failed mark-read must not leave false success or counters.

## Required validation and release gates

- All six normal PR/main jobs: Public disclosure gate; .NET validation; Incident receiver HTTP and provider acceptance; External PostgreSQL Compose configuration; Layout and reference validation; Offline HTTP MCP campaign harness.
- Focused owning suites, SQLite/PostgreSQL changed behavior, real Blazor browser fixtures and sanitized before/after screenshots across required viewport/theme/zoom matrix.
- Exact release source successful main CI, reviewed merge, next-unused beta allocation through root Directory.Build.props only.
- Hosted release.yml complete native archives (Linux x64/ARM64 and Windows x64), deployment archive, checksums/manifest, all three amd64/arm64 images, notices/SBOM/provenance.
- Complete draft inspection, public prerelease publication with latest=false, independent anonymous download/pull verification and exact source/version/digest checks.

## Continuation state

No task-owned PR, version allocation, registry write or release has occurred. All five issues remain open.
