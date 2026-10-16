# Releases

RatelDesk has one repository-owned release line. The root `Directory.Build.props` is the source of truth for `VersionPrefix` and `VersionSuffix`. Applications and release outputs inherit those values. Prereleases remain immutable.

## 0.1.2-beta.1 — self-service MCP candidate

The current source targets `0.1.2-beta.1`; see the
[candidate notes](../release-notes/0.1.2-beta.1.md) and
[short setup guide](../mcp-http/setup.md). The prior stable release is `v0.1.1`.
Recheck both accepted tag forms, releases, registry state and concurrent work
before tagging the exact reviewed merge. Keep
[#179](https://github.com/BostonTechnologies/rateldesk/issues/179) and
[#188](https://github.com/BostonTechnologies/rateldesk/issues/188) open until
hosted publication and fresh anonymous distribution verification complete.
This beta does not move `latest` or include owner production deployment.

## 0.1.1 — stable workspace release

The released `v0.1.1` source declares `0.1.1` with an empty prerelease suffix. The
[cumulative release notes](../release-notes/0.1.1.md) cover changes since 0.1.0,
including workspace filtering, email rendering, compact administration and
durable pending-mail controls. The beta.22 upgrade regression runs on both
SQLite and PostgreSQL and compares preserved business rows across migration
and restart.

The release epic and release-engineering issue stay open until publication and
protected promotion are verified. Before creating `v0.1.1`, inspect both
accepted aliases (`v0.1.1` and `RatelDesk-0.1.1`) and all release pages, confirm
the exact merged-main CI, and evaluate every shipped project's version.
Stage and publish through the existing workflows below; source notes or a
successful PR build do not establish a public stable release.

## 0.1.1-beta.20 — pairing-code cutover

Account → Integration credentials → System connections now uses Generate a
Pairing Code → Address and Pairing code → Pair & connect → tenant and
organization/customer, name and independent capabilities → Save. The saved
mapping supports View, read-only Test and immediate local Delete during a peer
outage. Upgrade both products and pair afresh: forward migrations retire old
NR↔RD authority while preserving installation identities, signing keys,
unrelated credentials, Netclaw and business history.

See the [upgrade and pairing guide](../integrations/netratel-pairing.md),
[current protocol](../contracts/bostec-pairing.v1.md) and
[release notes](../release-notes/0.1.1-beta.20.md). Source validation and publication
receipts are separate from owner acceptance of the deployed beta.

## Historical reconciliation — 6 October 2026

The reviewed baseline is `af71b9f9ae47bf75ea44f9fb486bd5c812d34910` and its
source version is `0.1.1-beta.13`. The latest observed published release at this
reconciliation is
[RatelDesk-0.1.1-beta.13](https://github.com/BostonTechnologies/RatelDesk/releases/tag/RatelDesk-0.1.1-beta.13),
from that exact source, published on 6 October at 10:05:04 SAST (08:05:04 UTC)
after [release run 37428393499](https://github.com/BostonTechnologies/RatelDesk/actions/runs/37428393499)
succeeded. It contains #131, but predates the #122 dependency merge and
focused #128 worker correction. The earlier real-peer receipts remain pinned
to beta.12 source `3cd63a776df67bd98d7efb81a330b2d0c57ad1f1` and its recorded
digests; beta.13 publication does not establish new interoperability acceptance.
Deploy only the exact artifact tag or digest verified for the intended release.

The historical beta.4 and beta.6 candidate sections below retain their
original review context. Their implementation subsequently merged: #94
(`e87d9342069931d8da26125b857960fbeee97b3a`), and Netclaw refinements
#95/#98/#100. Service linking and its follow-ups merged through #120, #123,
#125, #127, #129, #130 and #131. Earlier candidate/draft wording no longer
describes that source's merge status. The
[reconciliation ledger](../implementation/beta4-integration-progress.md)
preserves exact source pins, repository validation and protected-operation
receipts, alongside separately pending real NetRatel and NetClaw acceptance.
This reconciliation records the existing owner's publication; it starts no
release or deployment.

The ImageSharp 4.1.2 upgrade in
[#35](https://github.com/BostonTechnologies/RatelDesk/pull/35) remains separately
deferred for its required Six Labors license. Main's active CAPTCHA renderer
still uses ImageSharp 3.1.12; [#51](https://github.com/BostonTechnologies/RatelDesk/issues/51)
remains separate warning cleanup.

## 0.1.1-beta.13 — reciprocal link recovery

Background workers leave live attempts awaiting administrator consent untouched,
preserving the original approval's concurrency revision. Expired unused attempts
still abort and purge temporary pairing material.

Link recovery reuses short-lived verification and control tokens for the exact
approved peer, tenant, scope and credential revision. It checks current durable
authority on each use and retains the existing rate limits, finite recovery
permissions and rotation deadlines.

## 0.1.1-beta.9 — service credentials and reciprocal NetRatel linking

Adds dedicated, narrowly scoped NetRatel service clients, a durable asymmetric
client-credentials issuer, and administrator consent for both directions of
`bostec.service-link.v1`. The Integration hub offers guided linking and advanced
manual credentials alongside the existing account API and MCP credentials.
Link verification, commit recovery, revocation and directional rotation persist
across API restarts. Business requests check the current durable grant, including
cached tokens and protected outbound provider snapshots.

This release includes the #116 keyed incident receiver and receipt contract.
Both SQLite and PostgreSQL migrations preserve existing accounts, protected
provider profiles, receiver namespaces and receipts. Existing outbound
Orchestrator configuration and legacy callback authentication remain supported.
The issuer and guided protocol require separate explicit deployment settings;
That setup is retired by the beta.20 cutover. Use the
[current pairing guide](../integrations/netratel-pairing.md) and
[current protocol](../contracts/bostec-pairing.v1.md) when upgrading.

The current NetRatel release needs its companion implementation before guided
linking can complete. Synthetic contract-peer and real RatelDesk provider tests
are separate from the later two-product runtime acceptance documented above.
This candidate does not report that external acceptance or a customer deployment.

## 0.1.1-beta.6 — planned Netclaw onboarding update

The beta.6 candidate simplifies the Netclaw first-run flow to a daemon address,
one-time pairing code, and **Pair & connect**. RatelDesk normalizes the address
to its session and pairing routes, protects the returned device token, saves
the profile, applies it at runtime, and verifies an authenticated SignalR
session before reporting the connection as complete. For an explicitly
permitted private literal HTTP address, the pairing action supplies the
connection's private-HTTP opt-in; public HTTP remains rejected.

If beta.3 conversations still need a provider owner, the normal path asks
whether they used this same Netclaw server before exchanging the one-time code.
Confirming binds those conversations to this provider and continues pairing;
reviewing them separately retains the detailed recovery path. The connected
view keeps connection testing and re-pairing available, with token and runtime
details in advanced diagnostics.

This section describes the candidate under validation. It does not report
completed tests, live Netclaw acceptance, publication, or deployment.

## 0.1.1-beta.4 — planned integration hub update

The beta.4 candidate consolidates the separate NetRatel orchestrator, Netclaw
AI harness, and account integration credential workflows in an administrator
Integration hub. NetRatel uses a dedicated M2M client-credentials adapter and
catalogue/ingest contract; Netclaw supports canonical `Netclaw__...` settings,
protected persisted configuration, PostgreSQL runtime application, and an
authenticated SignalR diagnostic. Account API, CLI/stdio MCP, and HTTP MCP
credentials remain purpose-bound, organization-scoped, one-time-revealed, and
separate from provider secrets.

This section describes the candidate under validation. It is not a publication,
merge, deployment, or promotion announcement. Validate both SQLite migration
history and PostgreSQL native-chat behavior before adopting it.

## 0.1.1-beta.3 — planned mailbox delivery update

The beta.3 candidate adds mailbox-specific incoming processing and outgoing delivery. A dedicated organization mailbox retains its assignment for both directions; a paused or missing outgoing configuration does not silently use the instance-global mailbox. Outgoing SMTP requires an explicit submission endpoint and credentials. The mailbox workspace shows worker status and links to incoming receipt diagnostics and pending outgoing delivery work.

An upgrade preserves existing mailbox assignments, protected credentials, receipts, and failed delivery records. It does not automatically enable background ingestion or infer SMTP details from an IMAP or POP3 connection. Administrators should review the effective mailbox, choose the initial import policy, configure outgoing delivery, test both directions, and explicitly enable processing. Messages skipped by the initial baseline can be previewed and selected for a bounded historical import without resetting the source cursor.

This section describes the candidate under validation; it is not a publication or deployment announcement.

## Channel policy

`beta.N` and `rc.N` are GitHub prereleases and publish exact immutable image
tags only. A stable version has no suffix and is the only version eligible for
`latest` promotion after an owner-approved publication step. Staging creates a
draft and verifies archives, checksums, manifests and immutable image digests;
it does not make a release public. Consumers should deploy exact tags or
digests. If promotion needs recovery, verify every recorded digest first and
republish the same digests rather than rebuilding a versioned release.

The tag workflow deliberately does **not** move `latest`. An owner first
confirms the staged release is complete, publishes the non-prerelease GitHub
release, verifies its public downloads and recorded digests, then performs the
separate protected channel-promotion procedure. Do not promote an older
version over an existing stable channel; serialize that procedure and preserve
the immutable exact-version tags as the recovery source.

Before a maintainer dispatches `promote-stable.yml`, configure the repository
`release-promotion` environment with required reviewers and deployment-branch
policy in GitHub repository settings. The YAML environment name alone does not
prove those approval rules exist. Promotion validates the peeled tag commit,
its `main` ancestry, all release pages, the complete archive contract, and all
three immutable multi-architecture image digests before it changes any
`latest` reference. The three registry references are not atomic: if one write
or read-back fails, the workflow names the references already updated; after
correcting registry access, rerun the same stable tag rather than moving the
channel backwards or rebuilding an immutable image.

## 0.1.0 — first usable alpha

This is the first RatelDesk release intended for real alpha evaluation. It includes the complete first-run local-account setup flow, scoped role and tenant authorization, containerized Web/API deployment, PostgreSQL and SQLite support, deterministic API documentation, and bounded integration credentials for the CLI, stdio MCP, and HTTP MCP gateway modes.

The release packages self-contained `rateldesk` CLI and `rateldesk-mcp` stdio MCP executables for Linux x64, Linux ARM64, and Windows x64. The release also publishes Web, API, and HTTP MCP container images with provenance, SBOM data, immutable digests, and a verified release manifest.

As an alpha, validate upgrades and workflows with disposable or non-production data first, keep deployments pinned to exact tags or recorded digests, and report any problem with environment and reproduction details.

## 0.1.0-rc.9

### User experience and administration

- Consolidates Email Settings and Automation in the administration navigation, retaining the Orchestrator route (#59, #58).
- Restores tenant-scoped Team, Customers, and SLA administration with compact role editing (#57, #56).
- Prevents a light-theme flash during dark and system-theme startup (#55, #54).

### Dependency and delivery updates

- Updates MailKit from 4.17.0 to 4.18.0 and MudBlazor from 9.5.0 to 9.10.0 (#32). UX assertions now target stable semantic controls and rendered layout state.
- Updates ModelContextProtocol.AspNetCore from 1.4.1 to 2.2.0, NSubstitute from 5.3.0 to 6.2.0, and xunit.runner.visualstudio from 3.1.5 to 4.0.0 (#33, #34, #36).
- Refreshes Docker CI actions: setup-qemu 3.6.0 to 4.3.0, metadata 5.8.0 to 6.2.0, setup-buildx 3.11.1 to 4.3.0, login 3.5.0 to 4.6.0, and build-push 6.18.0 to 7.3.0 (#38-#42).

### Not included

- SixLabors.ImageSharp 4.1.2 is not included. Its upgrade PR (#35) requires an approved Six Labors license key or license file before it can pass validation.

## Build identity

Assemblies contain the semantic version, source repository, source revision, and a UTC `BuildTimestamp` metadata value. CI supplies one timestamp and the full commit SHA to every Web and API build. At runtime, the top-right Release Info panel and `GET /api/v1/system/version` report the independently deployed Web and API identities:

```json
{
  "version": "0.1.0",
  "commitHash": "abcdef1234567890",
  "buildTimestamp": "2026-09-10T10:34:00Z",
  "assemblyName": "Helpdesk.API",
  "environment": "Production"
}
```

Deployment configuration never owns the product version. A deployment-specific label, if one is needed later, must be modeled separately and cannot override this assembly metadata.

## Publishing a release

Before allocating a beta, recheck tags/releases and registry state so the version
is unallocated. Change product version only in `Directory.Build.props`, review
and merge normally, then verify the successful normal CI run for the exact
merged SHA. Evaluate the repository value before tagging that verified source:

```bash
release_version="$(dotnet msbuild src/Helpdesk.API/Helpdesk.API.csproj -nologo -getProperty:Version)"
release_tag="v$release_version"
git status --short
git tag -a "$release_tag" -m "RatelDesk $release_version"
git push origin "$release_tag"
```

The canonical tag is a SemVer tag (`vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH-prerelease`) on `main`, and its value must exactly equal MSBuild's evaluated repository version. For compatibility with the existing `RatelDesk-0.1.1-beta.3` release tag, the workflow also accepts the equivalent `RatelDesk-MAJOR.MINOR.PATCH[-prerelease]` form. The tag workflow reruns release validation, builds Web, API and HTTP MCP images for amd64 and arm64, attaches provenance/SBOM data, then creates the GitHub Release only after all three image pushes complete. Existing CLI/stdio archives include explicit setup, canonical guides and third-party license notices; deployment archives retain flat Compose paths and bundle the categorized guides with adapted local links.

If pushing a newly allocated tag did not start a release run, manual dispatch is
available from `main` with that exact tag. First check that no release run is
already active for it:

```bash
gh workflow run release.yml --ref main -f tag="$release_tag"
```

For tags containing this workflow revision, an existing GitHub Release receives
any missing validated assets and updated metadata while retaining its draft or
published state. A rerun reuses matching assets; it stops if an asset with the
same name has different content, so operators can resolve the conflict without
overwriting it. Dispatching an older tag checks out that tag's release scripts.

Before public completion, recovery can use `gh run rerun RUN_ID --failed` only
when the failed/dependent jobs will not rerun already successful image pushes.
Verify the green jobs' recorded digests and actual registry state first; preserve
their successful outputs. Do not use full `release.yml` redispatch/rebuild as
recovery for successful pushes or an already public tag: image jobs rebuild and
push exact tags with a fresh build timestamp, which can change digests even
though conflicting archive uploads are rejected. After partial publication,
preserve verified digests/assets; allocate a new reviewed beta if recovery cannot
avoid changing already published bytes. Older tags execute their own workflow
and scripts, so verify that version's recovery behavior rather than assuming
the current upload helper applies. Never call a rerun immutable recovery without
checking its actual jobs, inputs and recorded digests.

### Normal checks and provider evidence

`pull-request-validation.yml` automatically runs these checks on PRs to `dev` or
`main` and pushes to `main`: Public disclosure gate, .NET validation, Incident
receiver HTTP and provider acceptance, External PostgreSQL Compose configuration,
and Layout and reference validation. Layout includes offline documentation
placement/link checks, bounded launcher regressions and source/release MCP
setup/environment recipe rendering.
Branch protection settings remain a separate repository configuration; the
workflow alone does not prove which checks are enforced there.

Incident/service acceptance requires Docker and actual authenticated HTTP,
SQLite/PostgreSQL and golden-contract tests. Its TRX validator rejects absent,
failed or skipped selected results and requires eight named test classes, with
actual `postgres: true` and `postgres: false` cases for receiver, service identity,
managed orchestration callback, pairing service/upgrade and service identity
migration coverage. Do not substitute an empty pass or a skipped provider run.

Deep Playwright/mailbox/image/archive/Compose acceptance is an existing explicit
`workflow_dispatch` option (`deep_acceptance=true`); skipped deep jobs on ordinary
PR/main runs do not establish that acceptance. Deployment-managed PostgreSQL
first-run validation has its own manual workflow. Keep normal gates intact and
record optional/owner walkthroughs separately.

The deep release-asset lane sets `RATELDESK_ARCHIVE_PTY_ACCEPTANCE=true` when
executing extracted Linux x64 CLI/stdio apphosts. That lane covers actual 80x24
and wider terminal URL input, field failure, Advanced, resize, Back and cancel.
The archive runner separately asserts deterministic non-TTY setup and malformed
stdio configuration exit 78 with empty stdout and safe stderr. These bounded
process checks do not substitute for an approved synthetic host apply/recreate/
fresh-verify journey or a real external consumer daemon.

### Staging and public verification

The release workflow validates the supported tag, evaluated version and peeled
source's `main` ancestry; builds/tests the source and three containers; validates
layout and first-run Compose; packages archives; and executes the native archives
on Linux x64, Linux ARM64 and Windows x64. Image jobs publish exact amd64/arm64
tags with SBOM/provenance. After all jobs succeed, anonymous image pulls are
checked before the verified draft release is assembled.

The manifest contains source/tag/version, exactly seven archives and three
container digests. `SHA256SUMS` covers the archives; the detached manifest checksum
avoids self-reference. Asset uploads reuse identical existing bytes and reject
conflicts. The workflow creates/updates a draft without making it public. After
explicit publication, verify anonymous downloads/checksums, the manifest source
and exact tag-to-digest mapping, and clean anonymous image pulls. A tag, draft or
workflow URL alone is not verified public distribution.

Tag workflows publish Web, API and HTTP MCP images with the exact version.
Only the separate approved stable-channel promotion moves `latest`.
Prereleases publish exact tags and become GitHub prereleases after the verified
draft is published. Consumers should use an exact tag or an immutable digest.

To use released images locally, set required credentials and run:

```bash
RATELDESK_VERSION=0.1.0 docker compose -f docker/docker-compose.release.yml up -d
```

The GitHub Release records the source commit, UTC build timestamp, and immutable Web/API/HTTP MCP digests. Before a private deployment is updated, operators should clean-pull its selected public images and pin the deployment to those recorded digests.
