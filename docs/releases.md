# Releases

RatelDesk has one repository-owned release line. The root `Directory.Build.props` is the source of truth: maintainers update `VersionPrefix` when preparing the next release. All application projects inherit that value. `VersionSuffix` creates prereleases without changing the release line. The current planned test release is `0.1.1-beta.3`; earlier prereleases remain immutable.

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

After the release-engineering pull request is merged and `main` is green, verify the evaluated value rather than reading the props file directly:

```bash
dotnet msbuild src/Helpdesk.API/Helpdesk.API.csproj -nologo -getProperty:Version
git status --short
git tag -a v0.1.0 -m "RatelDesk 0.1.0 — first usable alpha"
git push origin v0.1.0
```

The canonical tag is a SemVer tag (`vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH-prerelease`) on `main`, and its value must exactly equal MSBuild's evaluated repository version. For compatibility with the existing `RatelDesk-0.1.1-beta.3` release tag, the workflow also accepts the equivalent `RatelDesk-MAJOR.MINOR.PATCH[-prerelease]` form. The tag workflow reruns release validation, builds Web and API images for amd64 and arm64, attaches provenance/SBOM data, then creates the GitHub Release only after both image pushes complete.

To recover an exact existing tag after the tag push has already happened, dispatch `release.yml` with that tag as both the workflow ref and the required input:

```bash
gh workflow run release.yml --ref RatelDesk-0.1.1-beta.3 -f tag=RatelDesk-0.1.1-beta.3
```

The existing draft-release safety checks still refuse to modify a published release.

Stable releases publish `ghcr.io/bostontechnologies/rateldesk-web` and `ghcr.io/bostontechnologies/rateldesk-api` with the exact version and `latest`. Prereleases publish only their exact tag and become GitHub prereleases. Consumers should use an exact tag or an immutable digest, never rely on `latest` for a production deployment.

To use released images locally, set required credentials and run:

```bash
RATELDESK_VERSION=0.1.0 docker compose -f docker/docker-compose.release.yml up -d
```

The GitHub Release records the source commit, UTC build timestamp, and immutable Web/API digests. Before a private deployment is updated, operators should clean-pull both public images and pin the deployment to those recorded digests.
