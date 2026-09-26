# RatelDesk beta.4 integration progress

This ledger tracks the beta.4 integration candidate. It distinguishes work that
is implemented, validated, ready to merge, merged, and released. The candidate
must remain unpublished until the human merge and release gates are satisfied.

## Baseline

- Repository: `BostonTechnologies/rateldesk`
- Starting branch: `feat/beta4-integration-hub`
- Refreshed `origin/main`: `2027f686ac529607dcb1a6ce619c81f465a60e3d`
- Candidate version: `0.1.1-beta.4` (`Directory.Build.props` now evaluates to this version)
- Release tag: not created
- Rollup PR: [#94](https://github.com/BostonTechnologies/rateldesk/pull/94), open and non-draft against `main`
- NetRatel compatibility reference: unchanged commit `cc58661bff496825d68de4db9ec53da92de45942`
- NetRatel and Netclaw: read-only compatibility references; no upstream changes permitted

## Tracking

- Epic: [#83](https://github.com/BostonTechnologies/rateldesk/issues/83)
- B4-00: [#84](https://github.com/BostonTechnologies/rateldesk/issues/84)
- B4-01: [#85](https://github.com/BostonTechnologies/rateldesk/issues/85)
- B4-02: [#86](https://github.com/BostonTechnologies/rateldesk/issues/86)
- B4-03: [#87](https://github.com/BostonTechnologies/rateldesk/issues/87)
- B4-04: [#88](https://github.com/BostonTechnologies/rateldesk/issues/88)
- B4-05: [#89](https://github.com/BostonTechnologies/rateldesk/issues/89)
- B4-06: [#90](https://github.com/BostonTechnologies/rateldesk/issues/90)
- B4-07: [#91](https://github.com/BostonTechnologies/rateldesk/issues/91)
- B4-08: [#92](https://github.com/BostonTechnologies/rateldesk/issues/92)
- B4-09: [#93](https://github.com/BostonTechnologies/rateldesk/issues/93)

## Work breakdown

| Task | Scope | Status | Evidence |
| --- | --- | --- | --- |
| B4-00 | Baseline inventory, compatibility matrix, architecture, evidence ledger | Implemented | This ledger; baseline and source inventory recorded |
| B4-01 | Secure persisted provider configuration, aliases, precedence, and runtime application | Implemented and validated | Provider settings service, protected secrets, revision checks, runtime reconfiguration, policy tests, and focused provider tests |
| B4-02 | Scoped credential lifecycle UX and discoverability | Implemented and validated | Account-owned credential API/UI, one-time reveal, scoped permissions, expiry/revocation status, audit coverage, and focused API/UI navigation tests |
| B4-03 | Packaged CLI and stdio MCP credential verification | Implemented; packaged acceptance pending | CLI/MCP credential-path tests and archive validation tooling are in place; final archive rehearsal is below |
| B4-04 | Local HTTP MCP purpose/resource/delegation verification | Implemented and validated in repository tests | Existing HTTP MCP purpose/resource/delegation coverage retained and credential-bearing CLI coverage added; packaged runtime probe remains part of release rehearsal |
| B4-05 | NetRatel M2M adapter, setup, diagnostics, and interoperability | Implemented; real interoperability pending | Pinned contract inspection, bounded clients, M2M identity probe, setup/API/UI paths, wire-contract tests, and security tests; unchanged live NetRatel acceptance still needs an authorized test environment |
| B4-06 | Netclaw options migration, setup, and authenticated diagnostics | Implemented; real daemon interoperability pending | Canonical options/aliases, protected token storage, SignalR session diagnostic, runtime reconfiguration, UI, and focused tests; unchanged Netclaw daemon acceptance still needs an authorized paired-device environment |
| B4-07 | Integration hub, navigation, responsive UX, and accessibility | Implemented and validated | Hub cards, route cleanup, light desktop and dark mobile Playwright coverage, overflow assertion, and setup-wizard regression coverage |
| B4-08 | Public guides, examples, migration notes, and release notes | Implemented and reviewed | Integration guide, Netclaw/self-hosting configuration guidance, README links, beta.4 release notes, and compatibility notes |
| B4-09 | Integrated security, upgrade, UX, release rehearsal, and final review | In progress | Automated tests/builds and UX are green; release rehearsal, final head checks, hosted CI, and real external interoperability remain |

## Evidence log

Record exact commands, results, test-merge/head provenance, dependency versions or
SHAs, and links to hosted checks here as the candidate advances. Do not record
secrets, private endpoints, local workstation diagnostics, or customer data.

| Date | Area | Evidence | Result |
| --- | --- | --- | --- |
| 2026-09-26 | Baseline | `origin/main` refreshed and inspected | `2027f686ac529607dcb1a6ce619c81f465a60e3d`; no beta.4 tag found |
| 2026-09-26 | Compatibility | NetRatel source inspected read-only at pinned commit | Internal health, catalog, ingest, and M2M identity contracts mapped without modifying the upstream checkout |
| 2026-09-26 | Build | `dotnet build Helpdesk.sln --no-restore` and Release equivalent | Debug and Release builds passed with zero errors; existing warnings remain recorded by the build |
| 2026-09-26 | Tests | `dotnet test Helpdesk.sln --no-restore` | 1,419 passed, 6 skipped, 0 failed, 1,425 total |
| 2026-09-26 | Focused tests | Provider, endpoint-policy, internal-client, OpenAPI, CLI, chat-runtime, and navigation filters | 68 passed, 0 failed, 0 skipped |
| 2026-09-26 | Quality | `slopwatch analyze --directory . --stats` and `git diff --check` | 1,299 files analyzed with zero Slopwatch findings; whitespace check passed |
| 2026-09-26 | UX | `HELPDESK_E2E_ARTIFACT_DIR=<temporary> bash tools/ci/run-ux-local.sh tests/ux/integration-hub.spec.ts` | Setup 1/1 and Integration Hub desktop/mobile 2/2 passed; screenshots verified for separate destinations, dark mobile layout, and no horizontal overflow |
| 2026-09-26 | Release metadata | `bash tools/release/validate-release-version.sh v0.1.1-beta.4` and `bash tools/ci/validate-layout.sh` | Version/prerelease and repository layout validation passed |
| 2026-09-26 | Release assets | `bash tools/release/package-assets.sh 0.1.1-beta.4 <candidate-revision> <temporary>` | Six self-contained CLI/stdio MCP archives, deployment archive, checksums, and release manifest generated for `f8ab43d6e150feffec1428a476dff13d9cc3a047` |
| 2026-09-26 | Archive acceptance | `tools/release/test-executable-archives.sh <temporary> linux-x64` and `tools/ci/test-deployment-archive-compose-config.sh <deployment-archive>` | Extracted CLI/MCP binaries passed version/help checks; extracted MCP completed JSON-RPC initialize; standalone, gateway, and Authentik Compose configurations rendered successfully |
| 2026-09-26 | Source image acceptance | Source MCP image build plus `tools/ci/test-mcp-compose-config.sh <local-image>` | Compose recipes, extracted deployment recipes, config ownership, permissions, health, and non-root runtime checks passed |
| 2026-09-26 | Release upload semantics | `tools/release/test-publish-release-assets.sh` | Dry-run, draft creation, resumable upload, failed-upload recovery, and conflicting-asset rejection all passed without a registry or GitHub release write |
| 2026-09-26 | Manifest finalization | `tools/release/prepare-release-manifest.sh 0.1.1-beta.4 <candidate-revision> <temporary> <web-digest> <api-digest> <mcp-digest> v0.1.1-beta.4` | Archive checksums verified and detached manifest checksum generated using rehearsal-only image digest placeholders |
| 2026-09-26 | Final-head release rerun | Same package, archive, deployment, checksum, and manifest probes against `872ba66d7139600b514ce2ae50e17a3807067157` | Final branch-head asset rehearsal passed; manifest source revision matched the candidate head used for the run |
| 2026-09-26 | Review handoff | [PR #94](https://github.com/BostonTechnologies/rateldesk/pull/94) | Non-draft PR opened against `main`; hosted checks are running against the pushed candidate head |

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
- Endpoint hardening is based on literal URL policy checks and disabled redirects;
  the application does not claim DNS-resolution enforcement.

## Release-state checklist

- [x] Epic and child issues searched/reused or created with real relationships
- [x] Implementation complete for the repository-scoped beta.4 surfaces and reviewed locally
- [x] SQLite and PostgreSQL migrations generated and exercised by the applicable test/build paths
- [ ] Real NetRatel compatibility evidence recorded against a pinned unchanged target
- [ ] Real Netclaw SignalR evidence recorded against a pinned unchanged target
- [x] Packaged CLI/stdio/HTTP MCP acceptance evidence recorded
- [x] UI light/dark/mobile evidence captured without secrets
- [ ] Full applicable tests and hosted checks green for the final head
- [x] Non-publishing beta.4 release rehearsal complete
- [x] Branch clean and pushed
- [x] Rollup PR non-draft and reviewable
- [ ] Human merge/release/deployment steps handed off; no merge or publication performed
