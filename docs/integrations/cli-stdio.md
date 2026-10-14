# CLI, stdio MCP and client profiles

The `rateldesk` CLI and `rateldesk-mcp` stdio server are shipped as self-contained
archives for Linux x64/arm64 and Windows x64. Linux binaries need glibc; use the
existing HTTP MCP image for container hosting rather than running glibc archives
on Alpine/musl. Offline `--help` and `--version` load no credentials.

## Explicit setup and inspection commands

Run setup in an interactive terminal. The Termina wizard offers **Connect a
client** and **Configure HTTP MCP hosting**, responsive forms/stepper, masked
secrets, preserved inputs and a consistent Back/Next/Cancel footer. Use
Tab/Shift-Tab, Enter, Escape/Ctrl+C and visible focus; Ctrl+O deliberately opens
the trusted browser URL. SSH/container users can open the same URL elsewhere.
`--monochrome` reduces color. Non-TTY setup exits with an alternative instead
of hanging. Normal CLI scripts, HTTP startup and stdio initialize never launch
the wizard.

```sh
rateldesk setup --target cli --profile work
rateldesk-mcp setup --target stdio --profile work
rateldesk setup --target http-client --profile work-http
rateldesk setup --target http-host --export-directory ./work/mcp-export
```

Setup options are `--target cli|stdio|http-client|http-host`, `--profile NAME` or
`--config PATH`, `--export-directory DIR` for host export, `--monochrome`, and
`--stdio-executable PATH` for the stdio validation/launch target. The HTTP image
dispatches the same explicit setup family before normal serving validation;
see [HTTP deployment](../mcp-http/deployment.md).

Select the named profile, intended HTTPS URL, then browser approval or manual
existing-credential/import. Review actual purpose, one organization,
permissions, expiry and private save path. Externally managed values remain
read-only. Target/profile changes cancel stale asynchronous work. Browser-approved
credentials are staged privately before acknowledgement; the prior working
profile is replaced only after confirmed acknowledgement. Explicit setup
re-entry recovers staged delivery after a crash. Cancellation and expired
delivery cleanup preserve the prior working profile; see
[pairing rules](browser-pairing.md).

| Command | Behavior |
| --- | --- |
| `status [--profile NAME | --config PATH]` | Redacted configuration/profile status; no credential creation |
| `config validate` (also `config-validate`) | Local validation; machine-readable result and nonzero failure exit |
| `doctor [--network] [--authenticate] [--profile NAME | --config PATH]` | Bounded explicit read-only checks; local-only by default |
| `profile list` | List local profiles for this surface |
| `profile show NAME` | Redacted saved profile |
| `profile remove NAME --confirm` | Delete only the selected local profile |
| `auth logout` | Clear selected local authentication references; does not revoke a server credential |
| CLI `auth status` | Existing authenticated server account-status check |
| CLI `config show/get/set/unset` | Existing config management; secret values remain redacted/protected |

Use each command's `--help` for its supported output flags. Inspection performs
no pairing, business writes or hidden network checks. Doctor distinguishes
configuration and optional network/auth results; MCP initialize and permitted
tool results are separate verification. Stdio setup launches the selected
executable through pipes for initialize and `helpdesk_auth/status`. Run
`rateldesk-mcp setup` or specify `--stdio-executable` from another distribution;
otherwise transport checks are explicitly skipped.

## Profiles and persistence

Named profiles use:

- CLI: `~/.config/rateldesk/profiles/cli/NAME.json`.
- Stdio/HTTP client: `~/.config/rateldesk/profiles/mcp/NAME.json`.
- Legacy CLI default: `~/.config/helpdesk/cli.json`, retained for ordinary
  existing CLI configuration.

Names contain 1–64 lowercase letters/digits/hyphens. Select profiles explicitly;
stdio/HTTP never import the ambient legacy CLI profile. `--config PATH` chooses
a private file deliberately. A selected missing/invalid file fails instead of
falling through to another profile or example URL (new explicit setup can create
its selected file).

Secure save creates sibling `secrets/credential-<GUID>.secret` references rather
than embedding new secrets in the profile. Files use restrictive initial
permissions, bounded reads, atomic replacement and revision/conflict checks.
Secret paths must be real files, not symlink/reparse-point chains; on Unix they
must be owned by the current user and owner-readable only (0600 files/0700 new
directories). Windows creation uses protected current-user ACLs from the first
file/directory creation. Existing private files must be owned by the current
user, with allowed access confined to that user, SYSTEM and Administrators;
unsafe broad ACLs fail without changing the existing file. Atomic replacement
retains/checks restricted access. No desktop keyring is
required in containers. Keep prior profiles and secret references until a
replacement is verified; deleting a profile differs from revoking its server
credential.

Browser pairing additionally uses private `.pairing-candidate` and
`.pairing-pending` sidecars for staged-before-ack recovery. Normal serving ignores
them. Their private state/device capability must never appear in logs, public
snippets or diagnostics; use explicit setup re-entry for recovery/cleanup.

Saved metadata includes `credentialTarget`, `clientTransport`, `mcpResourceUri`,
`credentialPurpose`, approved organization/permissions and expiry. Persistent
credentials bind to the reviewed target; editing the URL does not authorize
sending an existing credential to another instance. Reauthorize for a new
target. Permissions metadata is a replaceable list; server authorization still
checks current grants.

## Shared configuration and environment contracts

The shared headless engine resolves defaults < selected file/profile < canonical
MCP host-file settings < dedicated environment < explicit non-secret command
options. Canonical MCP environment keys beat its aliases within the environment
layer; dedicated environment aliases override lower-priority host-file settings.
Empty explicit environment values clear a
setting; missing options do not override it. A higher-layer mode selection
replaces the lower credential identity; mixed authentik/integration/gateway
fields or same-level inline/file alternatives fail before network I/O.
JSON unknown fields and canonical outbound unknown keys fail validation.
Legacy CLI `--app-password` parsing remains a compatibility adapter; use private
file references instead, because command arguments can be exposed in process
lists/history. New setup and MCP command paths do not accept secret arguments.

The [outbound field table](../mcp-http/configuration.md#dedicated-outbound-settings-and-aliases)
lists every JSON field, suffix, default and required condition. CLI keeps the
same suffixes with prefix `RATELDESK_`; dedicated MCP uses
`RATELDESK_MCP_` and canonical `Helpdesk__Mcp__Outbound__<Field>`.
These include `API_BASE_URL`, `CREDENTIAL_MODE`, `INTEGRATION_CREDENTIAL`,
`INTEGRATION_CREDENTIAL_FILE`, all `AUTHENTIK_*` fields, `AGENT_USER_EMAIL` and
`CREDENTIAL_TARGET`. `RATELDESK_CONFIG` selects a CLI file;
`RATELDESK_MCP_CONFIG` selects an isolated MCP file. Supported `_FILE` alternatives
are explicitly the integration credential and Authentik app password; other
arbitrary `_FILE` names are not automatic aliases.

CLI retains the historical default API example URL and Authentik mode/scope for
compatibility. A deliberately selected profile and MCP runtime require their
explicit intended target. Gateway mode is the HTTP host's per-request delegation
mode, not a usable CLI/stdio deployment account. CLI/stdio normally use
`integration` with an API-purpose credential; explicit legacy downstream
Authentik configuration remains supported separately.

Environment-only CLI example, using a separately created private API credential
file (never a bearer argument):

```sh
RATELDESK_API_BASE_URL=https://rateldesk.example.test/ \
RATELDESK_CREDENTIAL_MODE=integration \
RATELDESK_INTEGRATION_CREDENTIAL_FILE=/run/secrets/rateldesk-api \
rateldesk auth status
```

Effective status/config inspection redacts secrets and reports source/managed
values. A wizard cannot change parent-shell env, another running container or
an immutable secret; export changes and apply/recreate them explicitly. Compare
Compose interpolation `.env`, service `env_file` and .NET `__` binding in the
[HTTP configuration guide](../mcp-http/configuration.md#compose-configuration-layers).

## Launch stdio with isolated credentials

After saving an API-purpose stdio profile, configure the MCP client's process
command as `rateldesk-mcp` and pass explicit environment entries:

```sh
RATELDESK_MCP_CONFIG="$HOME/.config/rateldesk/profiles/mcp/work.json" \
RATELDESK_MCP_INSTANCE=work \
RATELDESK_MCP_EXPECTED_API_BASE_URL=https://rateldesk.example.test/ \
rateldesk-mcp
```

Dedicated environment-only configuration can instead supply
`RATELDESK_MCP_API_BASE_URL`, `RATELDESK_MCP_CREDENTIAL_MODE=integration` and
`RATELDESK_MCP_INTEGRATION_CREDENTIAL_FILE`, with the same explicit instance and
expected target. Existing `RATELDESK_MCP_<INSTANCE>_API_BASE_URL` pinning remains
supported; the EXPECTED form avoids shell-invalid hyphenated label variables.
Exact configured/expected target matching is required. Explicit private HTTP
backends remain supported, and public onboarding identity remains HTTPS.

Normal stdio stdout is JSON-RPC only. Operational logs/errors go to stderr;
no banners, ANSI, prompts or automatic browser launches. Start it through pipes
without a TTY. Verify initialize and one allowed read-only tool in the actual
MCP client, separately from local config and API status.

## HTTP clients and dependency notices

An HTTP client profile contains an MCP-purpose credential for the exact public
`/mcp` resource. Use its secret reference in the external MCP client's bearer
configuration. Do not use that profile for direct CLI/API operations or paste
it into HTTP hosting's outbound identity. Setup's HTTP checks perform initialize
and a read-only upstream `helpdesk_auth/status` tool request; a 401/403 is a
failed authenticated setup.

For an external Authentik/OAuth HTTP client, choose **External Authentik / OAuth
client** in setup. It verifies the exact protected-resource metadata URL and
saves only non-secret `httpClientAuthentication=oauth`, HTTPS
`oauthAuthorizationServers` (a replacement list), and optional public
`oauthClientId` alongside the reviewed HTTPS `/mcp` resource. It does not save an
API/downstream password, integration bearer or OAuth client secret. The selected
OAuth-capable MCP client owns sign-in, token storage and actual grants. Setup
and doctor leave authentication, initialize and tool-result unverified until
that external client connects; metadata discovery is a separate passed check.
These are client-profile JSON fields, not HTTP host ingress or Outbound keys.

Termina 0.16.4 is pinned to the package source at
`22a4bb205404660f7be8ab7b2c7936d565f4dc07` (Apache-2.0), with R3 1.3.1 at
`f6eed2dd4208dc4ae171c601e799e85f82aca25e` (MIT). Notices/license texts are
included in the existing archives and HTTP image; see
[THIRD-PARTY-NOTICES.txt](../../licenses/THIRD-PARTY-NOTICES.txt),
[Termina license](../../licenses/Termina-Apache-2.0.txt) and
[R3 license](../../licenses/R3-MIT.txt). Product release versions remain governed
by [release engineering](../releases/README.md).
