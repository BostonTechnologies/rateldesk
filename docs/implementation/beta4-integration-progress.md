# RatelDesk beta.4 integration progress

This ledger tracks the beta.4 integration candidate. It distinguishes work that
is implemented, validated, ready to merge, merged, and released. The candidate
must remain unpublished until the human merge and release gates are satisfied.

## Baseline

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

## Work breakdown

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

## Release-state checklist

- [x] Epic and child issues searched/reused or created with real relationships
- [x] Implementation complete for the repository-scoped beta.4 surfaces and reviewed locally
- [x] SQLite and PostgreSQL migrations generated and exercised by the applicable test/build paths
- [ ] Real NetRatel compatibility evidence recorded against a pinned unchanged target
- [ ] Real Netclaw SignalR evidence recorded against a pinned unchanged target
- [x] Final implementation-head CLI/stdio/HTTP MCP protected-operation acceptance against published local hosts and disposable PostgreSQL recorded
- [x] UI light/dark/mobile evidence captured without secrets
- [x] Full applicable tests and hosted checks green for the final product implementation head (`c21b63a`)
- [x] Non-publishing beta.4 release rehearsal rerun after this rework
- [x] Branch clean and pushed after the final review edits and evidence follow-up
- [x] Rollup PR draft and reviewable
- [x] Human merge/release/deployment steps handed off; no merge or publication performed
