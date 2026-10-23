# RatelDesk repository instructions

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
