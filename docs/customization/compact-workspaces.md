# Compact workspaces and tables

The standing presentation preference is in the product-root
[AGENTS.md](../../AGENTS.md). Use existing MudBlazor controls, workspace helpers
and theme tokens. Apply `compact-table`, `compact-content-table` and
`compact-tabs` only to their owning surfaces; authored email HTML has its own
isolated renderer.

Data lists have a bounded scrolling body, fixed headers, reachable pagers and
25/50/100 page sizes. Search, filters, sorting and totals apply to the complete
authorized collection before paging. Remote pagers must request the next server
page, rather than page a previously truncated response. Stable IDs retain
selection and expansion across refreshes. Routine row actions use small neutral
icons, descriptive accessible names, tooltips and a horizontal group. Keep
destructive confirmations and meaningful severity colors.

Editor schemas, setup summaries, submitted field/value previews, diagnostics
and Markdown remain semantic content. Bound their scroll region without adding
irrelevant CRUD controls or hiding content. Markdown retains semantic headers,
keyboard scrolling, encoding, streaming and independent copy controls. Small
previews shrink to their content instead of occupying an empty fixed-height box.

Enabled tabs use neutral text and a clear selected and keyboard-focus state.
Loading, no results, denied access and failed queries must remain distinguishable;
failure must offer a supported retry without silently changing scope or reporting
an unavailable count as zero.

## Tenant and date filters

Incidents, Requests, Changes and Tasks show a type-to-search **Tenant** control
in their normal toolbar. Tenant means the existing authorized organization
scope. **All authorized tenants** includes that complete scope. Suggestions are
searched on the server and remain bounded; a selected tenant has a removable
chip. The existing `customerOrgId` URL parameter is retained for compatibility.
Tasks apply organization authorization and filtering before their count and page.

Ticket date filters use separate date and time pickers. A saved explicit `tz`
continues to control interpretation; otherwise the browser's IANA time zone is
resolved before applying edited dates. UTC is the fallback if the browser cannot
provide a zone. Existing UTC instants, including seconds, survive an unchanged
Apply. A date-only upper boundary retains the existing whole-local-day meaning.
Invalid/nonexistent/ambiguous local times and reversed ranges require correction.
There is no editable timezone text field in the compact toolbar.

Self Service displays the catalogue tab name at its root and introduces folder
breadcrumbs only inside a folder. Its accessible page heading remains available
to assistive technology. My Requests and request detail navigation keep their
existing scope and identity.

Tasks expand from a dedicated left chevron. The child pane spans the selection,
chevron and summary columns and retains execution, provider, timing and recovery
information. Expansion never enlarges the Status cell or enables an uncertain
execution to be replayed.

## Rendered verification

Use synthetic loopback fixtures and the owning browser/API suites. The optional
`playwright.owner-review.config.ts` expands uncommon dialogs and host contexts
through 1366×768, 1440×900, 390×844, light/dark palettes, browser-only custom token
palettes and native Chrome 200% zoom. Set `RATELDESK_REVIEW_OWNER` to the owning
suite and `RATELDESK_TABLE_EVIDENCE_DIR` to an artifact directory. This deep audit
is separate from normal CI; it does not replace required gates.
Native zoom uses headed Chromium (an Xvfb display on headless Linux) and real
viewport screenshots. Full-page/element screenshot emulation changes native
zoom geometry and must not be used to manufacture zoom evidence. Raw run
statistics and bounded reruns remain visible in the evidence register.

Record actual DOM geometry, scrolling, pager reachability, visible content and
screenshots. A source `FixedHeader` flag or audit identifier alone establishes
none of these properties. See the [owner-review register](../implementation/401-owner-review-campaign.md)
for the complete component/route mapping and executed results.
