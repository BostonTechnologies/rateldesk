# RatelDesk repository instructions

## Compact workspace presentation

- Use compact, restrained typography, spacing, subtle borders and theme tokens. Remove headings and instructions that repeat the active navigation. Use existing MudBlazor and workspace helpers; scope presentation changes to their components.
- Data tables have fixed headers in bounded scrolling regions and reachable footers/pagers. Preserve full authorized-data filtering, sorting, counts before paging, stable identities, selection and bulk actions; use native 25/50/100 paging for growing collections. Keep useful loading, empty, denied and recoverable failure states.
- Incidents, Requests, Changes and Tasks show a searchable Tenant control in the default toolbar. Tenant is the existing authorized organization scope; All includes all currently authorized organizations. Keep dependent lookups scoped and preserve URL/back/clear/history behavior.
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
- Quality gates when applicable: dotnet-slopwatch for authored/refactored C# and
  project changes; crap-analysis for complex code whose tests changed. Preserve
  real assertions and required repository checks; never expand a baseline to hide
  a regression. Keep optional deep acceptance separate from normal CI.

The upstream specialized agents are runtime-specific and are not installed by the
portable skills copy. Follow the active session's delegation rules. These
references do not authorize deployments, messages, credential changes or releases;
the user's task and the repository's existing authority/version rules govern them.

## Work and verification

- Inspect existing source, scoped instructions and applicable issues before changing behavior. Preserve unrelated work and use reviewable branches; do not commit directly to `main`.
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
- Preserve the three image families: `rateldesk-api`, `rateldesk-web`, `rateldesk-mcp-http`, with their existing amd64/arm64 contract. Preserve CLI/stdio self-contained archives for Linux x64/arm64 and Windows x64, checksums, release manifests, immutable digests, SBOM/provenance and public-download/pull verification.
- Published prerelease tags/assets are immutable. Never overwrite published bytes or move `latest` for a beta/RC. Stable promotion remains the separate protected process; verify existing exact digests instead of rebuilding a released version.
- Include required third-party notices/license and setup entrypoints in existing distributions. Do not add a new image family or runtime package downloads merely for setup.
