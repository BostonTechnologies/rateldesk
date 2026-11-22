# RatelDesk repository instructions

## Compact workspace presentation

- Use compact, restrained typography, spacing, subtle borders and theme tokens. Remove headings and instructions that repeat the active navigation. Use existing MudBlazor and workspace helpers; scope presentation changes to their components.
- Data tables have fixed headers in bounded scrolling regions and reachable footers/pagers. Preserve full authorized-data filtering, sorting, counts before paging, stable identities, selection and bulk actions; use native 25/50/100 paging for growing collections. Keep useful loading, empty, denied and recoverable failure states.
- Incidents, Requests, Changes and Tasks show a searchable Tenant control in the default toolbar. Tenant is the existing authorized organization scope; All includes all currently authorized organizations. Keep dependent lookups scoped and preserve URL/back/clear/history behavior.
- Build editable organization and requester controls on `OrganizationSearchPicker`, `CustomerRequesterPicker` or the shared `CreationSearchPicker` for a distinct directory contract. The feature must authorize and search before limiting results, resolve saved IDs, and preserve its own clear, required and historical-value behavior.
- Keep filters and autocomplete results compact. Use real date/time pickers, resolve the user/browser timezone and preserve UTC instants, existing URL boundaries and DST validation. Avoid visible editable timezone fields and repetitive timezone prose in list filters.
- Routine row actions are small neutral named icon controls in a horizontal no-wrap group, with tooltips, disabled reasons, visible keyboard focus and adequate interaction targets. Keep genuine error/severity colors and destructive confirmations.
- Tabs are compact, neutral grey, visibly enabled and keyboard accessible, with clear active and focus states. Expand rich details from a left chevron into a full-width child pane under a compact summary row.
- Preserve semantic/editor/key-value previews and contained Markdown tables. Keep authored email HTML untouched. Verify desktop, narrow/mobile, 200% zoom, light/dark/custom themes, long content, keyboard/focus and failure states with synthetic fixtures and screenshots; source attributes alone are not rendered acceptance.

## Agent guidance: dotnet-skills

Prefer retrieval-led reasoning over pretraining for .NET work.
Workflow: skim repository patterns → consult the relevant dotnet-skills reference
by name → implement the smallest change → note any conflicts with this repository.

All 39 upstream skills are checked in under
[.agents/skills](.agents/skills/README.md), the repository location Codex discovers
automatically. Read each skill's `SKILL.md` and relevant references before applying
it. The index maps invocation names to upstream folder names; the complete files
are available in a fresh checkout without a runtime plugin installation.
The pinned upstream revision and MIT license are retained alongside the skills.

Routing (use the installed skill by name):
- Akka.NET: akka-net-best-practices, akka-net-testing-patterns,
  akka-hosting-actor-patterns, akka-net-aspire-configuration, akka-net-management.
- C# / code quality: modern-csharp-coding-standards, csharp-concurrency-patterns,
  csharp-nullable-reference-types, api-design, type-design-performance,
  r3-reactive-extensions.
- ASP.NET Core / Web: aspire-service-defaults, aspire-integration-testing,
  aspire-configuration, mailpit-integration, mjml-email-templates,
  verify-email-snapshots.
- Data: efcore-patterns, database-performance.
- DI / configuration: dependency-injection-patterns, microsoft-extensions-configuration.
- Testing: cscheck, testcontainers-integration-tests, playwright-blazor-testing,
  snapshot-testing, playwright-ci-caching.
- .NET build / runtime: aot-trimming, sdk-container-publishing,
  dotnet-project-structure, dotnet-local-tools, package-management, serialization,
  dotnet-devcert-trust, ilspy-decompile, opentelemetry-net-instrumentation.
- Skill maintenance: skills-index-snippets, marketplace-publishing.
- Quality gates: run `python3 tools/ci/run-slopwatch.py` (local Slopwatch 0.5.0,
  full-tree Info enforcement) for authored/refactored source and MSBuild changes; crap-analysis for complex code whose tests changed. Preserve
  real assertions and required repository checks; never expand a baseline to hide
  a regression. Keep optional deep acceptance separate from normal CI.

The upstream specialized agents are runtime-specific and are not installed by the
portable skills copy. Follow the active session's delegation rules. These
references do not authorize deployments, messages, credential changes or releases;
the user's task and the repository's existing authority/version rules govern them.

## Work and verification

- Inspect existing source, scoped instructions and applicable issues before changing behavior. Preserve unrelated work and use reviewable branches; do not commit directly to `main`.
- Own GitHub issue and PR follow-through for authorized work. Link the issues and verification evidence in the PR, commit and push the work for review, and update each affected issue with the delivery links and test results. Close completed issues after verifying their acceptance criteria. Leave outstanding issues open with the remaining dependency or verification gap recorded.
- Use the SDK pinned by `global.json`. Keep runtime projects under `src/`, tests under `tests/`, deployment collateral under `docker/`, and build/release tooling under `tools/`.
- Run the checks appropriate to a change and preserve the required PR/main CI gates. Normal repository validation includes `dotnet build Helpdesk.sln`, `dotnet test Helpdesk.sln`, and `tools/ci/validate-layout.sh`. Keep focused tests in their owning projects; do not suppress failures or count zero tests as passing.
- Preserve both SQLite and PostgreSQL, including existing data, migration histories and authorization boundaries. PostgreSQL-specific native AI chat does not make the rest of the product PostgreSQL-only.
- `src/Helpdesk.Shared/AGENT.md` additionally governs shared DTOs/contracts: keep UI, persistence dependencies and business services out of that project.

## Documentation placement

Before creating documentation, inspect [docs/README.md](docs/README.md) and the existing categories. Update the existing canonical guide first; otherwise place the document in the closest suitable category. Do not put plans, implementation reports, setup instructions, troubleshooting notes or release notes in `docs/` root or repository root. The explicit docs-root file allowlist is `README.md`.

- Deployment, gateway and instance setup guidance belongs in `docs/deployment/`.
- HTTP MCP configuration, hosting and troubleshooting belongs in `docs/mcp-http/`.
- Shared CLI/stdio onboarding and integration guidance belongs in `docs/integrations/`.
- Access/RBAC guidance belongs in `docs/security/`; branding/theme guidance belongs in `docs/customization/`.
- Keep current operator instructions, ADRs, proposals, implementation evidence and immutable release history distinct. Preserve useful historical results rather than deleting them to simplify current guidance.
- New categories require a durable purpose and an entry in the docs index. Keep useful runnable examples under `docker/examples/` and link them from canonical guides.
- Move affected relative links, anchors, assets, source references, scripts, tests and distributed-documentation inputs together. Prefer `git mv`; do not refill docs root with redirect stubs. Check local links and case-sensitive paths with `tools/ci/validate-docs.py`.
- Examples must be generic and secret-free: use reserved domains, synthetic identities and secret references. Do not include customer data, private infrastructure identifiers or real credentials.

## Configuration, credentials and protocols

- Share headless configuration/validation/persistence code between runtime and setup, while keeping CLI, stdio MCP and HTTP MCP identities isolated. MCP must not import ambient CLI `RATELDESK_*` credentials, the default CLI profile, NetRatel credentials or another deployment's identity.
- Preserve documented legacy files/environment aliases and exact instance/API-target binding. Arbitrary valid instance labels and explicitly configured private HTTP backends are supported; public onboarding origins and canonical MCP resources use HTTPS. Never change targets or authentication modes because a backend is unavailable.
- CLI and stdio use API-purpose account-bound credentials. HTTP MCP gateway ingress uses MCP-purpose/resource-bound credentials and API-issued request-scoped execution tokens. Gateway mode has no stored downstream deployment credential or shared delegation signing key. External Authentik uses separately configured downstream identity and is never a fallback for gateway rejection.
- Respect the existing single-organization, permission, purpose/resource, account-state, expiry and revocation rules. Do not introduce a separate credential authority, collect browser account passwords in setup or silently broaden approved grants.
- Use bounded secret-file reads, restrictive initial permissions, atomic profile replacement and secret references where possible. Redact secrets from normal/JSON diagnostics, exceptions, logs, previews and snippets. Setup cannot edit a parent shell, another container's environment or immutable Docker secrets; export settings with explicit apply/recreate instructions.
- Normal stdio MCP startup emits JSON-RPC only on stdout. Send operational errors to stderr; no banners, ANSI, browser launches or prompts. Setup is explicit, and non-TTY execution must terminate predictably. Normal HTTP startup never launches a wizard.
- Diagnostics are bounded and read-only. Report configuration, reachability, authentication, MCP initialize and permitted tool results separately; do not create tickets, send mail or execute workflows as setup checks.

## Version, release and distribution

- Product version ownership is exclusively the root `Directory.Build.props` (`VersionPrefix`/`VersionSuffix`). Projects and deployment settings must not invent independent product versions. Allocate a fresh beta only after rechecking published tags/releases.
- Follow [release engineering](docs/releases/README.md) and the existing hosted Actions workflows after normal reviewed merge and required PR/main checks. Preserve the supported SemVer and `RatelDesk-` tag compatibility.
- Add concise, reader-facing notes in `docs/release-notes/<version>.md` and include them in the GitHub release body. Give each shipped change a one-line explanation and link its issue or PR. Call out known limitations that affect testing. Include source and artifact metadata alongside these notes. Verify the public downloads and image digests before posting the release and evidence links back to the affected issues.
- Preserve the three image families: `rateldesk-api`, `rateldesk-web`, `rateldesk-mcp-http`, with their existing amd64/arm64 contract. Preserve CLI/stdio self-contained archives for Linux x64/arm64 and Windows x64, checksums, release manifests, immutable digests, SBOM/provenance and public-download/pull verification.
- Published prerelease tags/assets are immutable. Never overwrite published bytes or move `latest` for a beta/RC. Stable promotion remains the separate protected process; verify existing exact digests instead of rebuilding a released version.
- Include required third-party notices/license and setup entrypoints in existing distributions. Do not add a new image family or runtime package downloads merely for setup.

## Artifact size and AOT/trimming requirements

- Artifact size is a release requirement. Before changing dependencies, serialization/reflection, publish settings, documentation packaging or release workflows, read [.agents/skills/aot-trimming/SKILL.md](.agents/skills/aot-trimming/SKILL.md) and its relevant references. Use trimming and Native AOT wherever the complete target is compatible and validated; preserve the smallest supported profile that passes its actual published-artifact behavior checks. Keep precise blockers and follow-up work explicit for targets that remain JIT.
- Do not disable an accepted optimized profile, broaden trimming roots, suppress new IL2xxx/IL3xxx diagnostics or expand a warning baseline merely to pass a build. Resolve warnings with source generation, narrow annotations or isolated dynamic boundaries as directed by the skill. Validate changed profiles on native Linux x64, Linux ARM64 and Windows x64 as applicable; a successful publish or ordinary JIT unit test is insufficient. Preserve the self-contained client contract, CLI commands, MCP protocol/catalog, setup, authentication isolation, diagnostics and notices.
- Measure complete distributables after packaging. Record compressed archive bytes, expanded regular-file bytes and separate runtime, documentation and support-file totals for all six CLI/stdio RID archives and the deployment archive. Record source revision, effective publish flags, actual SDK/RID and SHA-256. For image changes, measure the existing Web/API/HTTP MCP families on both supported architectures with shared-layer accounting; do not infer whole-image savings from an app directory.
- Distribute an explicit selection of operator guides, their required offline assets, notices/licenses and established helpers. Do not recursively ship implementation evidence, UI review galleries, historical test output, caches, build output or developer/agent tooling merely because they exist in the repository. Preserve historical evidence in source and use immutable source links for material omitted from downloads. This supersedes the older full-history offline distribution choice in the size campaign; update the packaging tests and offline index together.
- Compare all seven final archives against checked-in per-artifact byte ceilings before upload/publication. Require an enforced release gate and inexpensive selection/budget regression checks; optional size campaigns and executable smoke checks are not substitutes. Until the automated correction linked below is merged, produce and review the complete size matrix manually before any release. Missing measurements or unexplained/excessive growth block publication. Never replace the approved baseline with a bloated release or raise a budget merely to make CI pass; a legitimate change requires attributed measurements and a reviewed rationale in the same PR.
- The initial recovery ceilings are the corresponding beta.9 compressed byte counts below. Tighten budgets to measured clean outputs with justified headroom after the repair. Recheck current published versions before allocating a fresh corrective prerelease; do not overwrite historical assets.

### Published size regression: 0.1.3-beta.9 to beta.11

Verified against the GitHub Releases API on 17 November 2026. Values are **exact
compressed bytes for complete downloads**, not executable sizes. Linux uses
`.tar.gz`; Windows uses `.zip`. Release references:
[beta.9](https://github.com/BostonTechnologies/RatelDesk/releases/tag/v0.1.3-beta.9),
[beta.10](https://github.com/BostonTechnologies/RatelDesk/releases/tag/v0.1.3-beta.10),
[beta.11](https://github.com/BostonTechnologies/RatelDesk/releases/tag/v0.1.3-beta.11).

| Artifact | beta.9 baseline bytes | beta.10 bytes | beta.11 bytes |
| --- | ---: | ---: | ---: |
| CLI linux-arm64 | 50,558,898 | 90,508,097 | 154,778,199 |
| CLI linux-x64 | 52,685,050 | 92,637,743 | 156,904,844 |
| CLI win-x64 | 53,333,672 | 93,323,263 | 158,684,564 |
| deployment | 14,351,812 | 54,295,380 | 118,554,701 |
| stdio MCP linux-arm64 | 50,471,670 | 90,423,989 | 154,703,157 |
| stdio MCP linux-x64 | 52,593,649 | 92,552,040 | 156,822,774 |
| stdio MCP win-x64 | 53,240,319 | 93,233,252 | 158,626,759 |

The Linux x64 CLI archives were independently downloaded and SHA-256 verified.
Their runtime binaries/resources/symbols/metadata grew only **24,082 expanded
bytes** (90,195,678 to 90,219,760), while bundled implementation evidence grew
**116,296,736 expanded bytes** (14,433,609 to 130,730,345). The publish settings did
not change across these versions. The accepted profile was already self-contained
JIT; the [previous AOT pilot](https://github.com/BostonTechnologies/RatelDesk/issues/321)
had unresolved JSON/R3 diagnostic blockers. Trimming does not remove collateral
copied into an archive after publishing. See [#428](https://github.com/BostonTechnologies/RatelDesk/issues/428) for the verified hashes,
root cause, required packaging/CI correction and completion evidence. beta.10 and
beta.11 are regression records, not acceptable replacement baselines.
