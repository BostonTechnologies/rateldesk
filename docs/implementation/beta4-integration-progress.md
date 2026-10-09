> Historical validation record. The previous NetRatel ↔ RatelDesk setup is superseded by the pairing-code cutover; its old runtime and fallback are retired. Dated results below do not establish acceptance of the new pairing flow.

# RatelDesk beta.4 integration progress

This ledger preserves the beta.4 baseline and dated implementation evidence.
The current reconciliation below supersedes historical draft, unmerged and
blanket unrun descriptions. Original source pins and results remain historical.

## Current reconciliation — 6 October 2026

The refreshed baseline main is `af71b9f9ae47bf75ea44f9fb486bd5c812d34910`, source version
`0.1.1-beta.13`. All 17 normal main jobs succeeded in
[PR validation](https://github.com/BostonTechnologies/RatelDesk/actions/runs/37425893341)
and [managed PostgreSQL](https://github.com/BostonTechnologies/RatelDesk/actions/runs/37425893349).
The latest observed published prerelease is
[RatelDesk-0.1.1-beta.13](https://github.com/BostonTechnologies/RatelDesk/releases/tag/RatelDesk-0.1.1-beta.13),
source `af71b9f9ae47bf75ea44f9fb486bd5c812d34910`. The existing owner's separate
[beta.13 release continuation](https://github.com/BostonTechnologies/RatelDesk/actions/runs/37428393499)
succeeded and the release was published on 6 October at 10:05:04 SAST
(08:05:04 UTC). It includes #131, but predates the #122 dependency merge and
the focused #128 worker correction below. The actual peer receipts remain
pinned to published beta.12 source `3cd63a776df67bd98d7efb81a330b2d0c57ad1f1`
and its exact image digests; no beta.13 reciprocal acceptance is claimed.
This reconciliation does not start a release or deployment.

### Merged source and historical evidence

[PR #94](https://github.com/BostonTechnologies/RatelDesk/pull/94) became
non-draft on 27 September at 10:55:14 UTC and merged at 10:55:22 UTC:
final head `48d475a9ac6a3d92188ec45b6e831b07a3c8926b`, merge
`e87d9342069931d8da26125b857960fbeee97b3a`. It is an ancestor of current
main, with no unique unmerged head commits. Its retained branch and older
comments are not evidence that implementation remains unmerged. Final-head
[validation 36311046479](https://github.com/BostonTechnologies/RatelDesk/actions/runs/36311046479)
passed all 15 jobs; [managed PostgreSQL 36311046548](https://github.com/BostonTechnologies/RatelDesk/actions/runs/36311046548)
also succeeded. These results remain pinned to that historical head.

| PR | Merged behavior | Merge SHA |
| --- | --- | --- |
| #94 | Beta.4 hub, configuration, account credentials, CLI/MCP and validation | `e87d9342069931d8da26125b857960fbeee97b3a` |
| #95 | Netclaw pairing and published asset refinements | `0ee090e32085ee672477c60b8e21c9e4571c5174` |
| #98 | Netclaw onboarding and post-merge validation corrections | `57ef3ef4d5a820abcb697654abc8efbcdd2bd1e9` |
| #100 | Two-input Netclaw Pair & connect | `053c244fa5e3b66fee90198a18e9e44d52fbbf26` |
| #120 | Service M2M identities and reciprocal linking | `006381c6f78ac1d37965f52ddc28bff002753055` |
| #123 | Web discovery, sign-in continuation and journal concurrency | `bb478fbabee119015c2e2d639c7530cf4db7b030` |
| #125 | Cancellation/unlink convergence and current authority | `94199b2bef5ea94627ed0c07d2820983f41b60fe` |
| #127 | Noble PostgreSQL runtime and credential-mode callback ownership | `372d7b59c1c648e147fd1cb6b2724db6c803d6ae` |
| #129 | Verified serialization-abort recovery and orchestration correlation | `27543c00c0bfab0e6bafe93f5f37d7207d51fb5f` |
| #130 | Reject late tokens after sender authority changes | `3cd63a776df67bd98d7efb81a330b2d0c57ad1f1` |
| #131 | Preserve live human-consent waits and bounded protocol token reuse | `af71b9f9ae47bf75ea44f9fb486bd5c812d34910` |

The specific [#85 G4 receipt](https://github.com/BostonTechnologies/RatelDesk/issues/85#issuecomment-5854186952)
proves SQLite two-context 2/2 and actual paused saved-test endpoints on
SQLite/PostgreSQL 4/4. The [#86 protected-operation receipt](https://github.com/BostonTechnologies/RatelDesk/issues/86#issuecomment-5854187166)
and [#93 receipt](https://github.com/BostonTechnologies/RatelDesk/issues/93#issuecomment-5854187405)
identify product `c21b63af7241d7261c137ef910c05da3d98999af` and runner
`595892d220c4e3536450484dddb7a1feabd3fdf2`: actual local Release-published
API/Web/HTTP MCP, PostgreSQL16 and manifest-matched extracted Linux x64
CLI/stdio archives passed protected reads, scoped write denials without
mutation and post-revocation denials. The cross-organization tenant projection
regression failed 2/2 before correction and its class passed 19/19 afterward.
These local-stack operations supersede their early blanket unrun status;
they do not establish unchanged upstream product interoperability. Retained
browser evidence includes setup 1/1, PostgreSQL UX 32/32 and AI Assistant
14/14 at `c21b63a`, plus #94 final-head hosted Playwright artifact `10929042895`
at `48d475a9ac6a3d92188ec45b6e831b07a3c8926b`.

### Actual peer evidence and remaining ownership

The existing NetRatel #164/#166 continuation is owned by its PR author;
no assignee or accessible continuation agent
or acceptance environment is identified in this workspace. Its current head is
`d5bfa08550bd303e2d5d1388ed71336404c45fbc`, pushed on 6 October at 10:06:45
SAST (08:06:45 UTC). The owner started normal run `37433948570` against the
published beta.13 peer at `af71b9f9ae47bf75ea44f9fb486bd5c812d34910`;
the [service-link guide](../integrations/netratel-pairing.md) records its
exact API/Web image digests. At 10:18 SAST (08:18 UTC), source-Compose and both
extracted local-first bundle jobs had failed before preparing actual owner
images or reaching owner-browser acceptance; general tests were still running.
The source smoke timed out waiting for its credential-selector option
`credential-permission-1-telemetry.read` before reciprocal owner acceptance.
This is not an accepted final suite. Its final results are linked from #89.
NetRatel beta.1 is not observed published.
Reuse this active owner's results; its acceptance environment is not exposed
in this workspace.

At the previously reviewed head `19f27f28bce127a2fdaa264ae28d570b94498d23`,
the reported private run against
published RatelDesk beta.12 was 25 passed, 1 failed, 0 skipped: automatic
rotation's initial remote approval returned HTTP 409 before rotation began.
That peer lacks #131; the result does not prove a rotation defect.

The later NetRatel hosted run `37406559519`
at test-merge `3cb76bf926973e1471a734158f39a8851abb5ee0` contains actual
native execution/callback/replay receipts for both initiating roles and one
NetRatel-issuer background rotation receipt. Its overall conclusion is failure;
the general acceptance job was cancelled, final guards were not completed,
and the Web-owner job was skipped after a Compose startup failure. Successful
individual receipts are useful evidence, not a successful final reciprocal
suite. Exact receipt artifact identities and limitations are retained in the
[service-link guide](../integrations/netratel-pairing.md); the current
[#89 receipt](https://github.com/BostonTechnologies/RatelDesk/issues/89) supplies
the external continuation and artifact URLs.

One owner must repeat the unchanged approval case against the intended
corrected published pair, then run the existing full reciprocal suite once
with its actual native execution and no-mock guards. Map discovery through
the real Web origin, Local/OIDC/Hybrid continuation and redaction, both-role
cancellation/unlink with loss/duplicates/restart, runtime architectures and
credential-mode ownership, verified database-abort recovery and persisted
worker scheduling to their original issue criteria. A source-built candidate
can prove a correction; it cannot substitute for required published-peer
acceptance. The original `cc58661bff496825d68de4db9ec53da92de45942` outbound
M2M contract remains supported and its real catalog/payload/ingest/execution/
callback/uncertain-outcome coverage must remain explicit alongside
`bostec.service-link.v1`.

For #90, no authorized unchanged daemon/device, exact version or endpoint,
or operator owner was found; the issue has no assignee. Required input is a
real Netclaw daemon with reachable canonical `/hub/session`, an operator who
can obtain its single-use pairing code/device and message/approval policy,
and disposable RatelDesk PostgreSQL. Pair/save, authenticated session/message/
approval, reconnect/restart, runtime changes and same-provider continuation/
different-provider isolation must be exercised there. Repository Kestrel
fixtures and health probes remain regression evidence only.

### Issue disposition

| Issue | Completed scope and evidence | Disposition / exact remaining criterion |
| --- | --- | --- |
| #83 | Repository children merged through #94 and later follow-ups | Open; close last after #89/#90 actual acceptance |
| #84 | Original baseline, architecture, source pins and dated receipts preserved | Repository scope complete with this reconciliation; final merge/validation and completed closure receipt are tracked on [#84](https://github.com/BostonTechnologies/RatelDesk/issues/84) |
| #85 | Canonical aliases, precedence, protected profiles, revision/runtime application; G4 receipt | Closed completed; external acceptance belongs to #89/#90 |
| #86 | Guided credential lifecycle and tenant projection; actual local protected operations | Closed completed; existing authority boundaries retained |
| #87 | Isolated/pinned CLI/stdio processes and extracted archive operations | Closed completed; later receipts supersede early unrun comment |
| #88 | Purpose/resource/current-authority delegation and HTTP MCP denial/revocation | Closed completed; caller-bound design retained |
| #89 | Legacy adapter plus later service-link/current-authority/correlation improvements | Open; final exact real peer suite and explicit supported legacy-path acceptance |
| #90 | #94/#95/#98/#100 options, pairing, diagnostics/runtime and ownership | Open; authorized real daemon/device/version/endpoint/operator above |
| #91 | Hub destinations, compatible routes, credential discovery and responsive/browser evidence | Closed completed |
| #92 | Current canonical guides, examples and historical release notes | Repository scope complete with this reconciliation; final merge/validation and completed closure receipt are tracked on [#92](https://github.com/BostonTechnologies/RatelDesk/issues/92) |
| #93 | Build/security/migrations/UX, nonpublishing rehearsal, protected archives, final review and non-draft merged handoff | Repository scope complete with this reconciliation; final merge/validation and completed closure receipt are tracked on [#93](https://github.com/BostonTechnologies/RatelDesk/issues/93); original interoperability scope was "where available", #89/#90 and epic remain open |
| #114 | Coordinated #113/#115 and #104/#102/#74/#73 plus #122 merged; Kiota seven packages remain 2.1.2 | Closed completed after signed merge `5766c33dce02be141d060b45d3e135eb37ee1558` and all 17 successful post-merge jobs; #35/#51 remain separate exclusions |
| #121 | #123 implemented and published in beta.10 | Open; actual final published pair Web/browser/Local/OIDC/Hybrid and concurrent restart acceptance |
| #124 | #125 implemented and published in beta.11 | Open; actual both-role cancellation/unlink, duplicates, lost acknowledgement, restart and permanent revocation |
| #126 | #127 implemented and published in beta.11 | Open; final published API PostgreSQL architecture/runtime and credential-mode reciprocal acceptance; earlier browser flake has no proven callback-source cause |
| #128 | #129 implemented and published in beta.12; demonstrated worker follow-up corrected below | Actual published-peer closeout remains pending; existing beta.13 release source predates this follow-up |

### Focused #128 worker follow-up

The reviewed path reproduced using the existing real PostgreSQL competing-write
fixture and actual `WorkAsync`: a verified `40001` stage abort rolled back and
reloaded the durable attempt; its next peer request returned HTTP 503 or lost
the response. Both regressions failed before correction because a fresh context
read `LastErrorCode = null`, rather than `peer-operation-failed` or
`peer-unavailable`. The awaited assignment never returned the replacement
attempt to the worker after that peer failure.

The handled-error catch now resolves `Attempt(id, ct)` before setting the error.
EF tracking identity resolution returns the retry-owned instance and retains
pending tracked changes; the correction does not attach a stale object, clear
the tracker again or overwrite concurrent durable state. Serializable isolation,
four total attempts, narrow abort classification, transport/caller cancellation,
exact replay identity, fixed deadlines and live human-consent exclusion are
unchanged.

The same two actual PostgreSQL regressions passed after correction: 2 passed,
0 failed, 0 skipped, 39 seconds. They read error and correctly advanced due time
through a fresh context, require no failed open transaction or partial profile,
preserve attempt/link/consent/payload and protected principal/secret material,
require disabled business authority with no additional journal/verification/
rotation, then restart, reject premature polling and recover the identical
handoff to prepared at the scheduled tick. Related existing tests passed 92/92,
0 skipped, in 2m40s: abort/retry/four-attempt exhaustion, worker consent,
journal concurrency/replay, conflict classification, ordinary OAuth/restart,
canceled consent and protocol-token reuse/cancellation/current authority.
These focused runs used Debug and the repository's synthetic HTTP contract
peer with actual PostgreSQL; they do not establish real NetRatel acceptance.
Independent source/evidence review found no remaining concrete finding.
The final corrected source with #122's integrated dependency tree passed Release
solution restore/build (0 errors, 21 existing warnings) and the complete local
suite: 2,073 passed, 0 failed, 6 existing environment-gated skips, 29m36s. Both
new PostgreSQL regression variants passed in that Release run.
Slopwatch 0.4.2 analyzed both changed .NET files with no baseline suppression:
2 files, 0 findings. Normal final candidate validation is recorded in its PR.

### Maintenance boundary

Existing [PR #122](https://github.com/BostonTechnologies/RatelDesk/pull/122)
contains only MessagePack 3.1.10→3.1.11 and Scalar.AspNetCore 2.17.12→2.17.13.
Its old checks used an older base. GitHub's normal non-destructive update
preserved the signed dependency commit and integrated current main at head
`31858d7c4ae990e7c046c5c25b84dc78013cd600`; its tree is
`277d4fc7729f4a893054e14ec16421a3f2abf8f8`. Fresh candidate validation is
[37430682373](https://github.com/BostonTechnologies/RatelDesk/actions/runs/37430682373)
and [37430682651](https://github.com/BostonTechnologies/RatelDesk/actions/runs/37430682651).
All 17 final candidate jobs completed successfully, and the exact head received
approval review `5425332714`. The normal merge is
`5766c33dce02be141d060b45d3e135eb37ee1558`, with a verified GitHub signature
and tree identical to the tested candidate. Normal post-merge validation is
[37432601661](https://github.com/BostonTechnologies/RatelDesk/actions/runs/37432601661)
and [37432601642](https://github.com/BostonTechnologies/RatelDesk/actions/runs/37432601642);
both workflows completed successfully on that exact merge, with all 17 jobs
successful. [#114 was closed completed with its receipt](https://github.com/BostonTechnologies/RatelDesk/issues/114#issuecomment-6012198623).
The exact integrated tree passed restore, Release solution build (0 errors,
21 existing warnings) and the existing runtime OpenAPI/Web route filter:
43 passed, 0 failed, 0 skipped. No package-text assertion tests or other
dependency upgrades were added.

[PR #35](https://github.com/BostonTechnologies/RatelDesk/pull/35) remains
separately deferred: ImageSharp 4.1.2 fails its Six Labors license target
(job `110569922875`, run `36921956384`); main's active CAPTCHA renderer
remains on 3.1.12. No license bypass, renderer replacement or purchase is part
of this closeout. [#51](https://github.com/BostonTechnologies/RatelDesk/issues/51)
remains separate warning cleanup.

## Historical beta.4 baseline

- Repository: `BostonTechnologies/RatelDesk`
- Starting branch: `feat/beta4-integration-hub`
- Refreshed `origin/main`: `2027f686ac529607dcb1a6ce619c81f465a60e3d`
- Candidate version: `0.1.1-beta.4` (`Directory.Build.props` now evaluates to this version)
- Release tag: not created
- Rollup PR: [#94](https://github.com/BostonTechnologies/RatelDesk/pull/94), open and draft against `main` while external acceptance blockers remain
- NetRatel compatibility reference: unchanged commit `cc58661bff496825d68de4db9ec53da92de45942`
- NetRatel and Netclaw: read-only compatibility references; no upstream changes permitted

## Tracking

- Epic: [#83](https://github.com/BostonTechnologies/RatelDesk/issues/83)
- B4-00: [#84](https://github.com/BostonTechnologies/RatelDesk/issues/84)
- B4-01: [#85](https://github.com/BostonTechnologies/RatelDesk/issues/85)
- B4-02: [#86](https://github.com/BostonTechnologies/RatelDesk/issues/86)
- B4-03: [#87](https://github.com/BostonTechnologies/RatelDesk/issues/87)
- B4-04: [#88](https://github.com/BostonTechnologies/RatelDesk/issues/88)
- B4-05: [#89](https://github.com/BostonTechnologies/RatelDesk/issues/89)
- B4-06: [#90](https://github.com/BostonTechnologies/RatelDesk/issues/90)
- B4-07: [#91](https://github.com/BostonTechnologies/RatelDesk/issues/91)
- B4-08: [#92](https://github.com/BostonTechnologies/RatelDesk/issues/92)
- B4-09: [#93](https://github.com/BostonTechnologies/RatelDesk/issues/93)

## Historical beta.4 work breakdown

| Task | Scope | Status | Evidence |
| --- | --- | --- | --- |
| B4-00 | Baseline inventory, compatibility matrix, architecture, evidence ledger | Implemented | This ledger; baseline and source inventory recorded |
| B4-01 | Secure persisted provider configuration, aliases, precedence, and runtime application | Implemented and validated locally/hosted | Provider settings service, protected secrets, revision checks, runtime reconfiguration, provider/runtime barrier coverage, policy tests, the final implementation-head local suite, and hosted validation are green; unchanged live-provider gates remain pending |
| B4-02 | Scoped credential lifecycle UX and discoverability | Implemented and validated | Account-owned credential API/UI, one-time reveal, scoped permissions, expiry/revocation status, audit coverage, cross-organization tenant projection regression, and published CLI/stdio/HTTP MCP protected-operation journey |
| B4-03 | Packaged CLI and stdio MCP credential verification | Implemented and validated locally | Final implementation-head archive rehearsal produced Linux x64/ARM64 and Windows x64 CLI and stdio MCP archives; extracted Linux x64 CLI and stdio MCP protected reads, scoped write denials, and revocation passed against published RatelDesk and disposable PostgreSQL |
| B4-04 | Local HTTP MCP purpose/resource/delegation verification | Implemented and validated locally | Published HTTP MCP gateway protected read, scoped write denial, and credential revocation passed against the same disposable RatelDesk/PostgreSQL stack; purpose/resource/delegation repository tests retained |
| B4-05 | NetRatel M2M adapter, setup, diagnostics, and interoperability | Implemented; RatelDesk interoperability pending | Pinned source inspection, bounded clients, M2M identity probe, setup/API/UI paths, wire-contract tests, provider-side prerequisite guide, security tests, and a read-only Dev M2M token/health probe are green; full RatelDesk ingest/ticket acceptance still needs the pinned provider with a tenant job, enrolled agent, and live fenced gateway session |
| B4-06 | Netclaw options migration, setup, and authenticated diagnostics | Implemented; real daemon interoperability pending | Canonical options/aliases, protected token storage, draft/saved SignalR diagnostics, immutable runtime reconfiguration, UI, actual authenticated SignalR negotiate/WebSocket/fallback coverage, and focused tests are green; unchanged Netclaw daemon acceptance still needs an authorized paired-device environment |
| B4-07 | Integration hub, navigation, responsive UX, and accessibility | Implemented and validated | Hub cards, route cleanup, responsive UI, draft-test controls, and the `c21b63a` setup 1/1 plus PostgreSQL browser 32/32 and AI Assistant 14/14 journeys passed locally; hosted UX validation also passed |
| B4-08 | Public guides, examples, migration notes, and release notes | Implemented and under final review | Integration guide now includes source-backed NetRatel provider prerequisites; Netclaw/self-hosting guidance and compatibility notes retained |
| B4-09 | Integrated security, upgrade, UX, release rehearsal, and final review | In progress; not release-ready | Final implementation-head regression, migration, Release build, version/layout, UX, protected-operation, archive, and hosted validation are recorded below; unchanged provider journeys and human merge/release gates remain open |

## Evidence log

Record exact commands, results, test-merge/head provenance, dependency versions or
SHAs, and links to hosted checks here as the candidate advances. Do not record
secrets, private endpoints, local workstation diagnostics, or customer data.

| Date | Area | Evidence | Result |
| --- | --- | --- | --- |
| 2026-09-26 | Baseline | `origin/main` refreshed and inspected | `2027f686ac529607dcb1a6ce619c81f465a60e3d`; no beta.4 tag found |
| 2026-09-26 | Compatibility | NetRatel source inspected read-only at pinned commit | Internal health, catalog, ingest, and M2M identity contracts mapped without modifying the upstream checkout |
| 2026-09-26 | Final rework focused reliability set | Runtime state, provider settings, chat migration/transport, SignalR integration, endpoint policy, internal client, request-task lifecycle, and chat API filters in `Helpdesk.Tests` | 73 passed, 0 failed; revision barriers, trusted legacy adoption, token rotation, actual negotiate/WebSocket/fallback behavior, response classification, remote-ID preservation, and protected route filtering are covered |
| 2026-09-26 | Database upgrades | PostgreSQL upgrade/chat migration and SQLite provider regression filters | 6 passed, 0 failed; normal migration chains preserve existing business, webhook, and automation-binding data |
| 2026-09-26 | Native chat transport | `ChatPostgresTests` filter in `Helpdesk.Tests` | 13 passed, 0 failed after retaining the legacy factory entry point for older implementations while the built-in factory binds immutable runtime snapshots |
| 2026-09-26 | Full local suite | `dotnet test Helpdesk.sln --configuration Release --no-build --no-restore --logger "console;verbosity=minimal"` | 1,474 passed, 6 skipped, 0 failed, 1,480 total; the six skips are the repository's existing environment-gated cases |
| 2026-09-26 | Release build | `dotnet build Helpdesk.sln --configuration Release --no-restore` | Succeeded with 0 errors and 20 pre-existing warnings |
| 2026-09-26 | Version and layout | `tools/release/validate-release-version.sh v0.1.1-beta.4` and `tools/ci/validate-layout.sh` | Candidate version and repository layout validation passed |
| 2026-09-26 | Final-head UX | `HELPDESK_E2E_ARTIFACT_DIR=artifacts/e2e/beta4-final-clean tools/ci/run-ux-local.sh` | Setup 1/1 and the full browser suite 31/31 passed; clean rerun covered the setup wizard and responsive/dark integration-hub journeys, and screenshots were inspected for layout and secret leakage |
| 2026-09-26 | Quality | `slopwatch analyze --directory . --stats` and `git diff --check` | 1,310 files analyzed with zero Slopwatch findings; whitespace check passed |
| 2026-09-26 | Package and archive rehearsal | `tools/release/package-assets.sh 0.1.1-beta.4 local-beta4-final-worktree artifacts/release/beta4-final`; checksum, Compose, archive, and release-helper tests | Seven final-head archives generated; `SHA256SUMS`, release manifest, deployment Compose validation, Linux x64 executable/stdio initialize probe, stable-promotion helpers, and draft-upload safety tests passed without publishing |
| 2026-09-26 | NetRatel Dev connectivity | Read-only `netratel_health`, `netratel_system`, `netratel_capabilities`, `netratel_connectivity`, and `netratel_access whoami`; bounded `netratel_connectivity test` | M2M token acquisition Green (49 ms) and protected remote health Green (HTTP 200, 3,887 ms); no RatelDesk ingest or ticket operation was performed |
| 2026-09-26 | External acceptance | NetRatel ingest/ticket, Netclaw paired-device SignalR, and deployed CLI/stdio/HTTP MCP journeys | Full live/deployed acceptance remains unrun because no exact deployed RatelDesk endpoint and no authorized paired Netclaw device target were identified; do not infer these gates from mocks, local fixtures, or connectivity health |
| 2026-09-26 | Hosted/release gates | [Pull request validation run 36263572804](https://github.com/BostonTechnologies/RatelDesk/actions/runs/36263572804) and [managed PostgreSQL run 36263572778](https://github.com/BostonTechnologies/RatelDesk/actions/runs/36263572778) | Final implementation head `0f73da7` passed .NET, UX, mailbox lifecycle, Docker, Compose/PostgreSQL, disclosure/layout, release rehearsal, and all Linux x64/ARM64/Windows x64 archive-runtime jobs; PR #94 remains draft and no release, registry, deployment, or upstream write has been performed |

## Third-review follow-up (2026-09-27)

The earlier evidence above describes the earlier implementation head. The
following checks apply to the G1–G4 follow-up on PR #94. An isolated Kestrel
fixture or provider-shaped responder is repository regression evidence, not an
unchanged Netclaw or NetRatel deployment interoperability result.
The G1–G4 product and regression repair is
`9cd19b15a3665f55d3ada7a433b46184c738adc3`. A test-fixture-only CI
disclosure correction follows at `bd71ab748302775928971408a7f1f748ccb85356`.
The disposable protected-operation run then exposed a scoped tenant projection
defect, fixed at `c21b63af7241d7261c137ef910c05da3d98999af`; this is the
final product implementation head. The disposable protected-operation runner
is an evidence-only follow-up at `595892d220c4e3536450484dddb7a1feabd3fdf2`;
it changes no product assembly. This ledger's final metadata commit follows it.

| Finding | Repair | Failing-before evidence | Passing-after evidence | Gate |
| --- | --- | --- | --- | --- |
| G1 / B4-06 | Pin the WebSocket upgrade to addresses validated by the outbound connection callback; validate every SignalR request against the configured origin/path and retain authenticated negotiation/fallback. | Source review showed `ClientWebSocket.ConnectAsync(context.Uri, ct)` resolved independently after a DNS preflight; no executable failing-before result was captured. | Real authenticated Kestrel WebSocket/fallback tests and injected changed-resolver, rejected-address, allowed-address, cancellation, disposal, and pooled-connection path tests passed in the combined 51/51 focused run. | Unchanged paired Netclaw daemon still requires an authorized target. |
| G2 / B4-06, B4-09 | Refuse unbound legacy remote sessions by default; explicitly confirm selected historical conversations against provider A in an authenticated, audited transaction and persist the fingerprint on each conversation. | Source review showed any saved profile or nonblank startup endpoint enabled blanket adoption; no executable failing-before result was captured. | SQLite settings and real endpoint tests, PostgreSQL chat lifecycle, A token rotation, B-after-restart isolation, and uncertain/approval recovery passed in the combined 51/51 focused run. | Historical owner must be established by an administrator before legacy session reuse. |
| G3 / B4-05 | Parse fields independently, require an explicit execution ID for admission, preserve distinct remote IDs, and redact bounded diagnostics on all acknowledgement paths. | Focused pre-fix client regression run: 9 failed, 17 passed. | Provider-shaped actual client plus SQLite lifecycle: 16/16 passed; client/lifecycle focused run: 34/34 passed. Covers null/missing/typed status, request/run-only identity, terminal statuses, HTTP error identity, secret echoes, and one-dispatch uncertainty. | Unchanged pinned NetRatel ingest/ticket journey remains separate. |
| G4 / B4-01 | Persist saved diagnostic metadata with a conditional revision/fingerprint write; audit superseded results and return 409 without making a second provider call. | Two-context SQLite regression: 2 stale audit saves failed with `DbUpdateConcurrencyException`. | Two-context SQLite 2/2 and actual paused saved-test endpoints on SQLite/PostgreSQL 4/4 passed. | No additional provider dependency for this race regression. |

The G1/G2 transport, settings, lifecycle, and confirmation-endpoint combined
Release filter passed 51/51; the G3/G4 focused results are recorded above.
The corrected OpenAPI inventory asserted 369 Release operations and passed
2/2 focused tests. `dotnet build Helpdesk.sln --configuration Release
--no-restore --verbosity minimal` passed with 0 errors and 20 pre-existing
warnings. `dotnet test Helpdesk.sln --configuration Release --no-build
--no-restore --logger 'console;verbosity=minimal'` passed 1,525/1,531 with
6 existing skips and 0 failures at `c21b63a`. The version (`0.1.1-beta.4`), layout,
public-disclosure, and whitespace checks passed. The earlier implementation-head
[PR validation run 36302488682](https://github.com/BostonTechnologies/RatelDesk/actions/runs/36302488682)
completed with 15 successful jobs, including .NET, mailbox, UX, Docker,
Compose, disclosure/layout, release assets and all three archive runtimes;
[managed PostgreSQL run 36302488687](https://github.com/BostonTechnologies/RatelDesk/actions/runs/36302488687)
also succeeded. Both runs used `bd71ab748302775928971408a7f1f748ccb85356`;
they precede the scoped tenant projection fix. The `c21b63a`
[managed PostgreSQL run 36305185601](https://github.com/BostonTechnologies/RatelDesk/actions/runs/36305185601)
succeeded; [PR validation run 36305185632](https://github.com/BostonTechnologies/RatelDesk/actions/runs/36305185632)
completed with all 15 jobs successful, including .NET, published mailbox
lifecycle, setup/UX, Docker, Compose, disclosure/layout, release rehearsal,
and all three archive runtimes.
The PostgreSQL upgrade and SQLite chat-migration filter passed 4/4.
`HELPDESK_AI_ASSISTANT_CONFIGURATION=Release npm run test:ux:ai-assistant`
passed 14/14 browser cases against its disposable fixture at `c21b63a`. The disposable
PostgreSQL UI harness passed setup 1/1 and the complete browser suite 32/32 at
`c21b63a`, including
administrator-only Netclaw and NetRatel profile save/reload, revision/source
application, and protected-secret absence from the rendered page. The first
two UX attempts exposed Playwright test-harness defects (unsupported nested
capture configuration, then an ambiguous label). These were corrected before
the green rerun. The final test generates synthetic credentials at runtime,
disables automatic trace/screenshots for that case, and retains explicit
post-redaction screenshots; retained artifacts contain no synthetic credential
values.
`tools/release/package-assets.sh 0.1.1-beta.4 c21b63af7241d7261c137ef910c05da3d98999af
artifacts/release/beta4-third-review-scoped` produced seven archives for Linux x64,
Linux ARM64, Windows x64, and deployment. All seven checksums, manifest source
revision/version, extracted Linux x64 CLI and stdio initialize/version/help,
deployment Compose configuration, stable-promotion validation, and draft-upload
safety checks passed. These archive startup probes are separate from the
protected-operation acceptance below.

The protected-operation journey used Release publishes of the API, Web, and
HTTP MCP gateway, a disposable `postgres:16` Compose sidecar, local TLS,
synthetic administrator and incident data, and the extracted Linux x64 CLI and
stdio MCP archives from the `c21b63a` manifest. An early run reached the
protected CLI read but returned an empty list: a credential for organization B
retained its owner's primary organization A as the tenant query filter. The new
`CurrentUserAccessServiceTests` theory failed 2/2 before the fix and passed
19/19 with its class after the fix. The projection now uses the credential's
canonical organization and clears a customer identity from another organization.
The full published journey then passed: CLI, stdio MCP, and HTTP MCP each read
the synthetic incident, received a scoped write denial, and rejected a revoked
credential. The runner checked that the incident was unchanged, emitted only
bounded stage results, and removed its task-owned database, volume, processes,
and temporary credential files. A second run from evidence commit `595892d`
also passed, proving that the manifest ancestry check accepts the unchanged
`c21b63a` product archives after the runner-only commit. Run it with
`tools/ci/run-integration-credential-acceptance.sh --api-publish-dir <api-publish>
--web-publish-dir <web-publish> --mcp-http-publish-dir <mcp-publish>
--release-assets <asset-directory>` against matching Release publishes and the
manifest-matched archive directory. This proves local protected operations,
not unchanged upstream NetRatel or Netclaw interoperability.
The local validation toolchain used .NET SDK 10.0.401 (repository pin
10.0.400 with latest-feature roll-forward) and Playwright 1.63.0. The
Netclaw responder was the in-repository authenticated Kestrel hub fixture;
no unchanged paired daemon version or endpoint was asserted.

Read-only inspection of unchanged NetRatel commit
`cc58661bff496825d68de4db9ec53da92de45942` established a concrete
disposable interoperability plan: its local M2M token, protected health, and
catalogue can run with its own issuer plus PostgreSQL and synthetic client
configuration. A successful ingest additionally requires a tenant-bound job,
enabled Akka job authority, an enrolled active agent target, and a live fenced
Agent Job Gateway session. The pinned source has not been built or started for
this follow-up, and no successful RatelDesk ingest/ticket acceptance is claimed.
The unchanged Netclaw paired-device journey also remains unrun without an
authorized paired device. Local provider-shaped responders and authenticated
Kestrel hubs cover regression behavior only.

## Private-HTTP legacy-confirmation correction (2026-09-27)

H1 was found at reviewed head `6db10589f69eb8c19e4f352c583792bfcc7d95cd`:
`ConfirmNetclawLegacySessionsAsync` passed the request's private-HTTP opt-in to
URL normalization but omitted it from the temporary `AiAssistantChatOptions`
validator. The isolated regression was added before changing production code.
Both synthetic RFC1918 IPv4 and IPv6 ULA service cases threw the expected
invalid historical endpoint error, and both authenticated endpoint cases
returned HTTP 400 instead of success: **4 failed, 0 passed** on the reviewed
production code. This is executable failing-before evidence, not a claim of a
real daemon failure.

The failing theories were
`IntegrationProviderSettingsTests.Legacy_confirmation_accepts_explicit_private_http_and_preserves_only_selected_history`
and
`NetclawLegacySessionConfirmationEndpointsTests.Authenticated_admin_can_confirm_private_http_legacy_owner_for_selected_session`
(two address-family cases each). The test additions were run against the
unmodified `6db1058` production method before the repair.

The one-field production repair and its regression tests are in
`12d770ff5aba4cbcc64672bfccda6d9a3005f2cc`. The temporary validator now
uses `AllowPrivateHttp = request.AllowPrivateHttp`; the existing endpoint,
instance, path, and private-address policy remains in force. SQLite service and
authenticated endpoint tests cover opted-in private IPv4 and ULA, selected-only
binding, preserved session IDs and transcripts, one safe administrator audit,
no chat-client call, omitted/false opt-in, public HTTP, invalid path/instance,
count/ID conflicts, and HTTPS default behavior. The PostgreSQL chat fixture
retains the HTTPS provider A success path, adds opted-in private-HTTP provider
A resume after a fresh manager, and fences provider B from A's session. These
fixtures use synthetic metadata and a controlled chat client; they do not
contact an unchanged Netclaw daemon.

After the repair, the service and endpoint class filter passed **35/35** and
the initial PostgreSQL A/B filter passed **2/2**. After restoring the original
HTTPS deployment-provider test alongside the private-HTTP case, the final
focused Release filter passed **38/38**. The final Release build passed with
**0 errors and 20 pre-existing warnings**; the full .NET suite passed
**1,536 tests with 6 existing skips and 0 failures**. Whitespace, layout,
version `0.1.1-beta.4`, public-disclosure, and Slopwatch checks passed
(0 findings across 1,314 files). Tests used .NET SDK 10.0.401 under the
repository's 10.0.400 latest-feature pin and disposable `postgres:16`.

The final focused command was `dotnet test tests/Helpdesk.Tests/Helpdesk.Tests.csproj
--configuration Release --no-build --no-restore --filter
'FullyQualifiedName~IntegrationProviderSettingsTests|FullyQualifiedName~NetclawLegacySessionConfirmationEndpointsTests|FullyQualifiedName~ChatTransportLifecycleTests.Administrator_confirmed_legacy_session_resumes_with_its_historical_provider|FullyQualifiedName~ChatTransportLifecycleTests.Different_deployment_provider_cannot_resume_administrator_bound_legacy_session'`.
The full command was `dotnet test Helpdesk.sln --configuration Release
--no-build --no-restore --logger 'console;verbosity=minimal'`.

At the same product commit, [PR validation run 36310096983](https://github.com/BostonTechnologies/RatelDesk/actions/runs/36310096983)
passed all **15/15 jobs**, including .NET, published mailbox lifecycle, UX,
Docker, Compose, disclosure/layout, the nonpublishing `0.0.0-pr` release-asset
rehearsal, and Linux x64,
Linux ARM64, and Windows x64 archive runtimes.
[Managed PostgreSQL run 36310096954](https://github.com/BostonTechnologies/RatelDesk/actions/runs/36310096954)
also passed. These runs validate the H1 product head, rather than the earlier
`6db1058` review checkpoint. This ledger update is evidence-only and follows
the product commit.

`docs/netclaw.md` now states that `allowPrivateHttp` must be set in the legacy
confirmation request and is not inherited from deployment settings. PR #94
remains draft. The unchanged paired Netclaw journey and full pinned NetRatel
ingest/ticket journey remain separate external interoperability gates.

## Compatibility decisions

- Canonical outbound provider namespaces are `Orchestrator` and `Netclaw`.
- `Orchestration:Provider`, `M2M`, and `AiAssistantChat` remain read-compatible
  aliases for beta.4, with canonical values winning at equal configuration
  priority.
- NetRatel outbound authentication uses its dedicated OAuth client-credentials
  contract; account-issued `rdk_` credentials are not substituted.
- Account-issued inbound credentials remain one-way hashed and account-owned.
- Outbound provider secrets are recoverable only through protected server-side
  storage and are never returned to the Web client.
- Existing reverse callbacks and legacy Authentik HTTP MCP service-identity mode
  remain separate from caller-bound local MCP delegation.
- Endpoint hardening requires explicit private-HTTP opt-in, rejects reserved
  resolved addresses, disables redirects, and validates DNS results at socket
  connection time.

## Historical beta.4 release-state checklist

- [x] Epic and child issues searched/reused or created with real relationships
- [x] Implementation complete for the repository-scoped beta.4 surfaces and reviewed locally
- [x] SQLite and PostgreSQL migrations generated and exercised by the applicable test/build paths
- [ ] Real NetRatel compatibility evidence recorded against a pinned unchanged target
- [ ] Real Netclaw SignalR evidence recorded against a pinned unchanged target
- [x] Final implementation-head CLI/stdio/HTTP MCP protected-operation acceptance against published local hosts and disposable PostgreSQL recorded
- [x] UI light/dark/mobile evidence captured without secrets
- [x] Full applicable tests and hosted checks green for the latest product implementation head (`12d770f`)
- [x] Nonpublishing PR release rehearsal and all archive runtimes rerun at `12d770f`; beta.4 candidate-specific package rehearsal previously passed at `c21b63a`
- [x] Branch clean and pushed after the final review edits and evidence follow-up
- [x] Rollup PR draft and reviewable
- [x] Human merge/release/deployment steps handed off; no merge or publication performed
