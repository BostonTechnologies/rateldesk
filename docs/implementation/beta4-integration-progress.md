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
| B4-01 | Secure persisted provider configuration, aliases, precedence, and runtime application | Implemented and validated locally | Provider settings service, protected secrets, revision checks, runtime reconfiguration, policy tests, focused provider tests, and the final-head full local suite are green; hosted and live-provider gates remain pending |
| B4-02 | Scoped credential lifecycle UX and discoverability | Implemented and validated | Account-owned credential API/UI, one-time reveal, scoped permissions, expiry/revocation status, audit coverage, and focused API/UI navigation tests |
| B4-03 | Packaged CLI and stdio MCP credential verification | Implemented; final-head package rerun pending | Existing CLI/MCP credential-path and archive coverage retained; final beta.4 rework head still needs the release-asset rehearsal |
| B4-04 | Local HTTP MCP purpose/resource/delegation verification | Implemented in repository tests; deployed journey pending | Existing HTTP MCP purpose/resource/delegation coverage retained; a deployed CLI/stdio/HTTP lifecycle remains an explicit acceptance gate |
| B4-05 | NetRatel M2M adapter, setup, diagnostics, and interoperability | Implemented; real interoperability pending | Pinned source inspection, bounded clients, M2M identity probe, setup/API/UI paths, wire-contract tests, provider-side prerequisite guide, and security tests; unchanged live NetRatel acceptance still needs an authorized test environment |
| B4-06 | Netclaw options migration, setup, and authenticated diagnostics | Implemented; real daemon interoperability pending | Canonical options/aliases, protected token storage, draft/saved SignalR diagnostics, immutable runtime reconfiguration, UI, and focused tests; unchanged Netclaw daemon acceptance still needs an authorized paired-device environment |
| B4-07 | Integration hub, navigation, responsive UX, and accessibility | Implemented and validated locally | Hub cards, route cleanup, responsive UI, draft-test controls, existing Playwright coverage, and the final-head desktop/mobile browser journey are green; hosted evidence remains pending |
| B4-08 | Public guides, examples, migration notes, and release notes | Implemented and under final review | Integration guide now includes source-backed NetRatel provider prerequisites; Netclaw/self-hosting guidance and compatibility notes retained |
| B4-09 | Integrated security, upgrade, UX, release rehearsal, and final review | In progress; not release-ready | Local regression, migration, Release build, version/layout, and final-head UX evidence is recorded below; hosted CI, real provider journeys, package rehearsal, and release publication gates remain open |

## Evidence log

Record exact commands, results, test-merge/head provenance, dependency versions or
SHAs, and links to hosted checks here as the candidate advances. Do not record
secrets, private endpoints, local workstation diagnostics, or customer data.

| Date | Area | Evidence | Result |
| --- | --- | --- | --- |
| 2026-09-26 | Baseline | `origin/main` refreshed and inspected | `2027f686ac529607dcb1a6ce619c81f465a60e3d`; no beta.4 tag found |
| 2026-09-26 | Compatibility | NetRatel source inspected read-only at pinned commit | Internal health, catalog, ingest, and M2M identity contracts mapped without modifying the upstream checkout |
| 2026-09-26 | Provider settings and draft diagnostics | Focused provider/runtime filters in `Helpdesk.Tests` | 18 passed, 0 failed; metadata reads remain non-decrypting, draft tests do not persist/apply, and stale test results cannot overwrite newer revisions |
| 2026-09-26 | Orchestration, catalogue, and outbound policy | Focused catalogue, endpoint-policy, connectivity, and internal-client filters in `Helpdesk.Tests` | 36 passed, 0 failed; empty/incompatible catalogue responses, IPv6 reserved-address handling, private IPv6 Netclaw validation, and redacted provider errors are covered |
| 2026-09-26 | Submission lifecycle | Request-task lifecycle and internal-client filters in `Helpdesk.Tests` | 13 passed, 0 failed; definite rejection remains retryable while uncertain acknowledgement remains manual reconciliation without automatic resend |
| 2026-09-26 | Database upgrades | PostgreSQL upgrade/chat migration and SQLite provider regression filters | 6 passed, 0 failed; normal migration chains preserve existing business, webhook, and automation-binding data |
| 2026-09-26 | Native chat transport | `ChatPostgresTests` filter in `Helpdesk.Tests` | 13 passed, 0 failed after retaining the legacy factory entry point for older implementations while the built-in factory binds immutable runtime snapshots |
| 2026-09-26 | Full local suite | `dotnet test Helpdesk.sln --no-restore --logger 'console;verbosity=minimal'` | 1,445 passed, 6 skipped, 0 failed, 1,451 total; the six skips are the repository's existing environment-gated cases |
| 2026-09-26 | Release build | `dotnet build Helpdesk.sln --configuration Release --no-restore` | Succeeded with 0 errors and 20 pre-existing warnings |
| 2026-09-26 | Version and layout | `tools/release/validate-release-version.sh v0.1.1-beta.4` and `tools/ci/validate-layout.sh` | Candidate version and repository layout validation passed |
| 2026-09-26 | Final-head UX | `tools/ci/run-ux-local.sh tests/ux/integration-hub.spec.ts` | Setup wizard, desktop integration hub, and mobile dark integration hub journeys passed; captured screenshots were inspected for layout and secret leakage |
| 2026-09-26 | Quality | `slopwatch analyze --directory . --stats` and `git diff --check` | 1,309 files analyzed with zero Slopwatch findings; whitespace check passed |
| 2026-09-26 | External acceptance | NetRatel M2M, Netclaw paired-device SignalR, and deployed CLI/stdio/HTTP MCP journeys | Not run against live/deployed services in this pass; keep these as authorized environment gates rather than inferred from mocks or source inspection |
| 2026-09-26 | Hosted/release gates | Final-head full test matrix, browser evidence, hosted CI, package/archive rerun, merge, publication, and deployment | Pending; PR #94 remains draft and no release, registry, deployment, or upstream write has been performed |

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
- [ ] Final-head packaged CLI/stdio/HTTP MCP acceptance evidence recorded
- [x] UI light/dark/mobile evidence captured without secrets
- [ ] Full applicable tests and hosted checks green for the final head
- [ ] Non-publishing beta.4 release rehearsal rerun after this rework
- [x] Branch clean and pushed after the final review edits
- [x] Rollup PR draft and reviewable
- [x] Human merge/release/deployment steps handed off; no merge or publication performed
