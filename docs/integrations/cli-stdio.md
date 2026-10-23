# CLI, stdio MCP and client profiles

The `rateldesk` CLI and `rateldesk-mcp` stdio server are shipped as self-contained
archives for Linux x64/arm64 and Windows x64. Linux binaries need glibc; use the
existing HTTP MCP image for container hosting rather than running glibc archives
on Alpine/musl. Offline `--help` and `--version` load no credentials.

## Explicit setup and inspection commands

Run setup in an interactive terminal. The first focused field is the
**RatelDesk Web URL**; the executable selects CLI, stdio or HTTP client access.
Paste the root or canonical `/mcp` URL and press Enter. **Advanced** exposes
profile, authentication and topology choices. Durable private storage is chosen
automatically and checked before approval. The wizard retains masked secrets,
preserved inputs and a consistent Back/Next/Cancel footer. Use
Tab/Shift-Tab, Enter, Escape/Ctrl+C and visible focus; Ctrl+O deliberately opens
the trusted browser URL. SSH/container users can open the same URL elsewhere.
The approval view keeps purpose, URL/code, status/expiry and actions visible;
extended review is in **Details**. Client **Back** returns to authentication and
the selected profile. HTTP hosting/verifier navigation has its own context in
the [short HTTP MCP guide](../mcp-http/setup.md).
On approval, Ctrl+Y copies the URL/code and Ctrl+D toggles Details. Focus the
URL and use PageUp/PageDown to read a long address.
`--monochrome` reduces color. Non-TTY setup exits with an alternative instead
of hanging. Normal CLI scripts, HTTP startup and stdio initialize never launch
the wizard.

```sh
rateldesk setup
rateldesk-mcp setup
rateldesk setup --target http-client --profile work-http
```

`--mode host` explicitly selects hosting; the existing
`--target http-host` alias remains supported, but full host acceptance requires
actual Docker-host sources/runtime. From the extracted deployment tools
directory, select the verified matching native apphost instead of adding setup
mounts or a tooling service:

```sh
./setup-http-mcp.sh --native-executable "$NATIVE_SETUP"
./setup-http-mcp.sh --native-executable "$NATIVE_SETUP" --intent prepare-changes
# New process after baseline adoption or separately after approved owner apply:
./setup-http-mcp.sh --native-executable "$NATIVE_SETUP" --verify
```

Use the exact quoted resume invocation printed by the launcher; it retains
source/owner and Docker/Compose selection. **Verify existing deployment** is the
default and has no apply stage. **Prepare configuration changes** reviews a
minimal source candidate before the existing owner saves/deploys it. Defined
service counts and recreation impact remain visible; a four-service gateway
stays four by default. Client setup remains separate. Setup options include
`--target cli|stdio|http-client|http-host`, `--profile NAME` or
`--config PATH`, `--export-directory DIR` for private host checkpoint/candidate storage, `--monochrome`, and
`--stdio-executable PATH` for the stdio validation/launch target. The HTTP image
dispatches the same explicit setup family before normal serving validation;
see [HTTP deployment](../mcp-http/deployment.md).

Use `--url` (alias `--web-url`) for a supplied Web URL, `--instance` for the
intended existing label, and `--authentication browser|existing|manual|authentik|oauth`
for a deliberate authentication choice. `--mode client` selects the executable's
client transport. Conflicting mode/target options fail before starting the
screen. No secret values belong in command arguments.

Enter the intended HTTPS URL, then use browser approval or Advanced manual
existing-credential/import. Supplied profile/config/URL options seed the same
workflow; omitted flags preserve managed settings. Review actual purpose, one organization,
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

For an HTTP client from an extracted deployment tools directory on the **Docker
host**, use `./setup-http-mcp.sh --target http-client -- --profile incident-reader`.
Inside the **MCP container console**, use
`dotnet /app/Helpdesk.Mcp.Http.dll setup --profile incident-reader` instead.
These are separate from native CLI/stdio apphosts. For a fresh doctor process
against an explicitly selected private HTTP client file, use the
[container/profile template](../mcp-http/setup.md#check-or-reconnect-a-saved-client).
Keep any deliberately selected Docker configuration/context for host commands.

From the extracted **native CLI directory**, HTTP client setup and fresh doctor
also work independently of Compose:

```sh
RATELDESK_MCP_CLIENT_DIRECTORY="$PRIVATE_CLIENT_DIRECTORY" \
  ./rateldesk setup --target http-client \
  --url https://rateldesk.example.test/mcp/ --profile incident-reader
./rateldesk --config "$CLIENT_PROFILE" doctor --network --authenticate
```

Use the actual protected `CLIENT_PROFILE` displayed by setup. Choose an enabled
organization, exact permissions and expiry in the browser; a successful client
doctor does not complete host source/runtime acceptance.

## Ticket histories and effort

Use `rateldesk incidents timeline ID --json`,
`rateldesk changes timeline ID --json`, or
`rateldesk requests ticket-timeline ID --json` for ticket history and worklog
effort. The existing `rateldesk requests timeline ID` command continues to read
workflow notification history from `/workflow-timeline`.

Ticket timeline output includes `hours`, `recordedHours` and `workLogId`.
`recordedHours` contains the persisted effort, including a measured zero. A null
value means effort is unavailable or does not apply to that event; legacy
numeric `hours` remains zero in that case for compatibility. The upgrade can
restore a historical staff event's effort snapshot from an exact, unambiguous
retained ingress publication tied to a successful receipt for the same ticket
and organization. Its missing canonical worklog ID remains unavailable. Other
staff history stays unavailable; note text and timestamps never establish an
association. Signed public history reads its
visible canonical worklogs directly, so those historical worklog values remain
known. The Web timeline displays unavailable effort explicitly and preserves
fractional hours after refresh.

## Profiles and persistence

Native named profiles use per-user application data under
`rateldesk/profiles/cli` or `rateldesk/profiles/mcp`; on Unix the conventional
paths are below. An existing legacy `~/.config/rateldesk/profiles` tree remains
selected rather than silently moving its credentials:

- CLI: `~/.config/rateldesk/profiles/cli/NAME.json`.
- Stdio/HTTP client: `~/.config/rateldesk/profiles/mcp/NAME.json`.
- Legacy CLI default: `~/.config/helpdesk/cli.json`, retained for ordinary
  existing CLI configuration.

Bare CLI setup preserves the runtime's selected `RATELDESK_CONFIG` file or that
legacy CLI default when no profile/config option is supplied. `--profile NAME`
deliberately selects a named profile; stdio preserves its explicitly selected
`RATELDESK_MCP_CONFIG` unless a profile/config option takes precedence. Advanced
can retain a supported private HTTP(S) or non-root API target without guessing
another destination or rebinding an existing credential.

HTTP-image client setup uses `RATELDESK_MCP_CLIENT_DIRECTORY` when the deployment
provides that dedicated store, with `mcp/NAME.json` beneath it; otherwise its
per-user default is local application data under
`rateldesk/http-client/profiles/mcp/NAME.json`. Host output uses the separate
`RATELDESK_MCP_HOST_DIRECTORY` or local application data under
`rateldesk/http-host`. Normal native host checkpoints stay outside deployment
sources and need no serving mount; existing container client storage remains
separate. Neither namespace imports the serving host's downstream identity. A successful
Save requires a writable persistent store.

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

Reloading this same explicit profile in a fresh process needs no new approval
while the credential is valid. Expiry/revocation requires explicit setup and a
new reviewed grant; it does not silently extend access. Local profile removal
does not revoke the server credential. Host verification's temporary status-only
credential has its own cleanup and leaves ordinary profiles intact.

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

Explicit setup without a TTY exits **78**, writes the corrective action to
stderr and emits no stdout. Invalid stdio configuration also exits **78** with
a safe startup-validation message on stderr and empty protocol stdout. A
timeout, crash or shell traceback is a failed check. The packaged acceptance
script executes these cases against extracted apphosts.

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
