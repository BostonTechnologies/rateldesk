# 7 November 2026 next-beta delivery

The owner authorized implementation, reviewed merges and complete prerelease
publication for #355–#358. Production deployment remains a separate operator
step. All acceptance data is synthetic and disposable.

## Baseline and allocation

Main was refetched before edits: `ee7067d12dafb22de81b70a58f61b211b9e37226`
(published beta.5). The current issues, active PRs/branches, repository/scoped
instructions, canonical guides and relevant checked-in skills were inspected.
Validation uses pinned SDK 10.0.400, local Docker 28.4.0 and Compose 2.40.3.

The final pre-allocation check at 2026-11-07 11:19 UTC inspected both accepted
beta.6 tag prefixes, all 34 draft/public releases and all three anonymous GHCR
families: `0.1.3-beta.6` was unused. Main was exactly
`12f57e1712694e5956e0aed600bebc52fc782be5`. Root `Directory.Build.props` now
exclusively owns that version in the release-preparation branch. The hosted
workflow repeats allocation checks before creating any immutable tag.

## Issue, PR and test register

| Issue | PR | Exact current PR head | Focused acceptance |
| --- | --- | --- | --- |
| #358 Warning defaults and bounded logging | [#359](https://github.com/BostonTechnologies/RatelDesk/pull/359) | `76ca4cdfcdaf9f0ac26302c63164e792de1c0d65` | 64 application +159 deployment cases; emitted events, real bootstrap/stdio, Compose/archive/HostConfig/recreation/rollover |
| #356 shared For column | [#360](https://github.com/BostonTechnologies/RatelDesk/pull/360) | `04e94c27ee59c0e0f1d9e6a34a5b9502ec1bd8f8` | 23 rendered +20 real browser cases, four widths, both renderers, all modules/views, picker/navigation |
| #357 task-first service editor | [#360](https://github.com/BostonTechnologies/RatelDesk/pull/360) | `04e94c27ee59c0e0f1d9e6a34a5b9502ec1bd8f8` | 36 focused +7 real browser +4 native Chromium 200% zoom cases; 13 synthetic screenshots |
| #355 native child follow-ups | [#361](https://github.com/BostonTechnologies/RatelDesk/pull/361) | `14f785a847a440b80fdc8671159bd03e760bba3f` | 117 owning cases +5 browser cases +actual supported Netclaw ticket/organization paths |

All listed focused runs passed with zero failures/skips. Independent source
reviews passed, with separate critical reviewer reruns. Exact code receipts,
source pins, bounds and synthetic evidence are in the
[logging policy](358-logging-policy.md), [Docker retention](358-docker-retention.md),
[table](356-for-column.md), [editor](357-task-first-editor.md) and
[chat](355-child-followups.md) records.

Implementation commits before coordinator integration were application logging
`31b43fa834932543d99455665a703acfd5f397ed`, retention
`aef1a6bed4272a00ed63280f8a9f26020d6fd7f4`, table
`e435d33d65551d36820a2a8007f73006b3290582`, editor
`154cc51984f1d4a780a0a5384af6d5f1f29a632b` and chat
`6b17029889002f27005975b7dd3c4f254a75b6cb`.

Source review corrected early-bootstrap OTLP loss, the table's inclusive 1280px
stacked breakpoint, native-zoom CI ownership, stale task navigator names,
reattached uncertain-child expiry and missing pending/outcome presentation.
The UI scanner identified a source-file hash as a generic API key because its
JSON key contained `fixture-api`. The evidence now separates `path` and `sha256`;
all source/screenshot hashes remain intact, and exact Gitleaks 8.30.1 scans pass.
No allowlist, scanner setting, assertion or baseline was weakened.

## Current normal CI and merge status

- [#359 current-head run](https://github.com/BostonTechnologies/RatelDesk/actions/runs/42362267827): all six normal jobs green; normally merged to main as `fa8214daeb94aa8673b03afdb326b632c384a142`. Its [exact-main run](https://github.com/BostonTechnologies/RatelDesk/actions/runs/42364792866) also passed all six normal jobs.
- [#360 corrected-head run](https://github.com/BostonTechnologies/RatelDesk/actions/runs/42364134428): all six normal jobs green; normally merged as `c8eca53494f1030f53ec4c0bff33e842563a6bc9`. Its superseded head is not a passing receipt.
- [#361 corrected combined-head run](https://github.com/BostonTechnologies/RatelDesk/actions/runs/42365233867): all six normal jobs green; normally merged as `12f57e1712694e5956e0aed600bebc52fc782be5`, containing all four changes. The previous run is superseded after a documentation-only packaging fix: an unbundled source README reference now points explicitly to immutable reviewed source. All six release-helper commands pass; strict validation/export rules are unchanged.

The editor/table and chat PRs merged as one reviewed batch after the logging
main run passed. Their [combined exact-main run](https://github.com/BostonTechnologies/RatelDesk/actions/runs/42367665461)
is running. The resulting main tree is byte-identical to the reviewed and green
chat PR head. All four work items are on main.

The six normal jobs include Offline HTTP MCP campaign harness in addition to
the release helper's five named gates. A combined local
solution build passed with 23 preexisting warnings and zero errors; its full
solution test passed: 3,868 passed, eight preexisting platform/legacy skips,
zero failures. The skipped test source files are unchanged from the inspected
baseline. A prior baseline test was stopped for browser network isolation and is not counted as a passing result. Conditional deep-job skips do
not establish browser/native/deployment acceptance; the focused receipts above
establish those results separately.

## Native evidence boundary

Official Netclaw 0.28.1 (source `0d86cefc32a7c05ffd7b71f9e93f9a864eb1bae8`)
executes actual session/child actors and SignalR against the checked-in controlled
loopback model. The production RatelDesk manager/client and disposable PostgreSQL
store observe one operator input, two replies/native turns, live pending then
clear child snapshots, 13 durable events and zero resends in both scopes.
This is connected-daemon acceptance with a model fixture, distinct from mocked
transport coverage, external-LLM quality, historical replay and operator deployment.
Broader #308/#351 interruption limitations remain explicit.

## Release preparation and remaining distribution

All four reviewed feature work items have merged. Release preparation changes
root version metadata and canonical release/operator documentation only. The
[beta.6 notes](../release-notes/0.1.3-beta.6.md) contain all four operator checks,
logging keys and source/environment/container recreation instructions. The
release-preparation PR must receive independent source review and all six
current-head normal checks; the combined feature main run must also pass before
that merge. Its final exact-current-main normal run must succeed before
dispatching `release.yml` from `main` with `tag=v0.1.3-beta.6` and the full
verified source SHA.

Wait for all source, image, archive, native and draft-staging jobs. Verify the
complete draft, publish explicitly as a prerelease with latest=false, and verify
anonymous archive/checksum/manifest downloads plus both image architectures,
immutable digests, source labels and SBOM/provenance. Inspect the extracted actual
deployment archive and effective logging configuration. Preserve stable latest.

The [public release receipt](https://github.com/BostonTechnologies/RatelDesk/releases/tag/v0.1.3-beta.6)
will identify the final verified source, CI/release runs, ten immutable assets
and three image index digests after completion. This source record does not
claim staging, publication or subsequent production deployment has occurred.
