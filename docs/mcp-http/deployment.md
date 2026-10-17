# HTTP MCP deployment reference

Start with [Set up HTTP MCP](setup.md) for the normal server/client procedure.
This reference covers alternate execution contexts, explicit configuration,
deployment ownership, legacy recipes and migration.

## Deployment-host launcher

Run `tools/mcp/setup-http-mcp.sh` from a source checkout, or
`./setup-http-mcp.sh` from the extracted deployment archive. The launcher needs
Docker with Compose and Python 3 on the deployment host; it installs no packages.
Its adjacent Python helper is included in the archive. It inspects Docker only
on that host; the serving MCP container receives no Docker socket or root access.
The archive's non-secret `setup-release.json` selects the exact inherited release
version, base recipe and image for a fresh deployment with MCP not yet selected.
It never guesses a version from the extraction directory's name. Existing
deployment sources and ownership take precedence over fresh-bundle defaults.

The ordinary invocation discovers an existing container, project, ordered
Compose sources, interpolation context, image and durable private storage.
Multiple candidates require a labeled selection. Missing source/ownership
metadata is a prerequisite to resolve explicitly. Release-owned deployments
carry `com.rateldesk.deployment.manager=release-compose`; an older stack without
that label needs an explicit manager choice. An external manager's stack is
never silently treated as release-owned.

Use `--container NAME` for an intentional candidate, repeated ordered
`--compose-file PATH` and `--env-file PATH` for missing source context,
`--project NAME` / `--project-directory DIR` for its actual Compose context,
or `--manager release-compose|external-compose` to declare ownership explicitly.
Use `--image EXACT_REFERENCE` for first standalone setup without a discoverable
MCP container. `--help` describes the complete installed launcher options.
Never copy secret values into these arguments or print rendered Compose output.
For an intentionally private HTTPS instance using a custom trust root, use
`--ca-file /absolute/path/to/public-ca-bundle.pem`. The public CA bundle is
mounted read-only into tooling and included in the source-generation hash;
certificate/hostname verification stays enabled. Normal publicly trusted HTTPS
needs no CA option.

`--apply` uses the reviewed release-owned apply action. External-manager mode
exports the complete private deployment document for review and application
through that manager. `--verify` reopens the persisted generation from a fresh
process after recreation. Source changes invalidate the reviewed generation;
re-run preflight and obtain fresh review rather than editing the checkpoint.

The launcher provisions separate private host output and client profile/secret
directories, checks ownership and read/write access before approval, and retains
the paths across invocations. It preserves unrelated services, custom ingress,
API targets, volumes and integration identities. Container-local paths are never
presented as invented host paths. A terminal inside a container cannot discover
or apply its deployment host's Compose sources; use the real launcher on that
host for those actions.

Choose the task first: **Connect a client** obtains an account-owned credential;
**Configure HTTP MCP hosting** exports local deployment settings for an
administrator/operator. Client pairing grants no server administration.
The explicit Termina setup shares headless configuration, pairing, persistence
and validation services with runtime. Normal startup never prompts.

## Compose prerequisites and Docker selection

The launcher checks the **selected** Docker/Compose implementation before
rendering sources, requesting consent, provisioning storage or changing
services. It requires both the source-preserving and fully resolved `config`
operations, including `--format json`, `--no-env-resolution`, `--no-interpolate`
and `--no-path-resolution` where used. The supported upstream floor is Compose
**v2.35.0**, which introduced `--no-env-resolution`; v2.34 lacks that feature.
The launcher checks capabilities rather than rejecting distribution suffixes or
unparseable versions by number alone. Missing plugin/daemon access, unsupported
features, source/env-file errors and timeouts have separate safe diagnoses; raw secret-bearing Docker
stderr is not printed. Do not remove flags or replace `--no-env-resolution`
with `--no-interpolate`: those operations have different semantics.

For `compose_feature_unsupported`, use the printed detected version/missing
capability and install Compose v2.35.0 or newer through the host's supported
package channel. Alternatively obtain the matching host architecture's
[official Compose release](https://github.com/docker/compose/releases/tag/v2.35.0)
and verify its published SHA-256 checksum **before** placing
it in a deliberately selected private Docker configuration's `cli-plugins`
directory as the executable `docker-compose`. Nothing is downloaded or installed
automatically. Preserve the system plugin and do not copy private Docker
credentials into a workaround directory.

Keep explicit `DOCKER_CONFIG`, `DOCKER_CONTEXT`, `DOCKER_HOST`, `DOCKER_TLS`,
`DOCKER_TLS_VERIFY`, `DOCKER_CERT_PATH` and `DOCKER_API_VERSION` selections for
every setup, client, apply and `--verify` invocation.
The launcher carries the selection into its printed follow-up commands; use
those quoted commands instead of reconstructing them for another daemon. If
the selected private Docker configuration needs a named context, configure that
context deliberately there rather than silently falling back to the default.

## Connect a client

Run `rateldesk setup`, `rateldesk-mcp setup`, or the HTTP image's bare `setup`
from an interactive terminal. The executable selects its client transport;
the first screen asks for the intended HTTPS Web URL. A root URL, trailing slash
or canonical `/mcp` form normalizes to the same origin. User information,
queries, fragments and unknown paths are rejected. Validate capabilities, then use
[browser approval](../integrations/browser-pairing.md) or explicit existing
credential/import for older installations. Browser opening is deliberate;
SSH users can open the visible trusted URL on another device.

Review actual approved organization/permissions/expiry, purpose/resource,
target, secret reference and profile path before saving. Environment-managed
fields show their source and are read-only. Browser pairing stages a private
candidate and recovery state before acknowledgement, then atomically activates
the profile only after acknowledgement is confirmed. Cancellation or expired
delivery cleanup preserves an older working profile; explicit setup re-entry
recovers staged delivery. CLI/stdio use API-purpose credentials; the HTTP client uses a separate
MCP-purpose credential and never supplies it as a host downstream identity.
External OAuth clients can choose **External Authentik / OAuth client**. Setup
verifies the exact protected-resource metadata endpoint and saves reviewed
non-secret resource/HTTPS authority information plus an optional public client
ID. The external MCP client retains sign-in, tokens and actual grant ownership;
setup does not collect a downstream API password or turn gateway pairing into
an external OAuth token response. Authentication, initialize and tool-result
remain explicitly unverified until that client connects.

Gateway HTTP client setup checks configuration, reachability, accepted authentication,
initialize, the actual tool catalog and a read-only `helpdesk_auth` status result against API. Stdio setup
also launches its selected executable through pipes for initialize and the
authenticated read-only status call. Setup from a different distribution needs
`--stdio-executable`; absent executable selection is reported as skipped and
does not establish protocol acceptance. Follow the secret-reference launch snippet and review
[CLI/stdio guidance](../integrations/cli-stdio.md).

## Client setup in a container console

Open the MCP container's terminal and run:

```sh
dotnet /app/Helpdesk.Mcp.Http.dll setup
```

The preferred deployment-host command, from the extracted tools directory, is:

```sh
./setup-http-mcp.sh --target http-client -- --profile incident-reader
```

This retains the launcher's selected Docker context and private client store.
In a browserless console,
the trusted URL/code remains visible for another device. Save uses the dedicated
durable client store supplied by the deployment; this store is separate from
host settings and never mounted into API. Finish with the printed protected
configuration snippet. An unwritable or ephemeral store is a prerequisite
failure, not successful Save. Bare `setup` means HTTP client; `setup --mode host`
(compatibility: `setup --target http-host`) explicitly selects host configuration.
For fresh saved-profile checks, use the actual container/profile template in
the [short guide](setup.md#check-or-reconnect-a-saved-client). Neither a host
filesystem profile path nor a guessed container name is a runnable substitute.

## Advanced standalone tooling

The existing HTTP image includes setup and third-party licenses. These standalone
Compose files require only setup output (and exact release version for the
release recipe), so unrelated API/database/Authentik variables cannot block
their interpolation. They do not start Web/API or mutate remote deployment.

The [launcher](#deployment-host-launcher) handles storage and dispatch for the
normal path. These lower-level compatibility recipes are for a deliberately
managed output mount. Prepare an explicit writable output directory. On Linux, the one-shot tooling
can use the operator's UID/GID through `RATELDESK_SETUP_UID`/`RATELDESK_SETUP_GID`,
avoiding a host-root ownership change. Their defaults are 10001/10001; other
hosts can provision the mount for those defaults through their normal mechanism.
The serving process remains UID 10001. Preserve existing paths; do not loosen
a private production mount just to run setup.

```sh
mkdir -p work/mcp-setup-output
chmod 700 work/mcp-setup-output
RATELDESK_SETUP_UID="$(id -u)" RATELDESK_SETUP_GID="$(id -g)" \
RATELDESK_SETUP_OUTPUT="$PWD/work/mcp-setup-output" \
docker compose -f docker/docker-compose.setup.source.yml \
  run --build --rm --no-deps mcp-http
```

For published images, export `RATELDESK_VERSION` as one exact selected release
version and use:

```sh
RATELDESK_SETUP_UID="$(id -u)" RATELDESK_SETUP_GID="$(id -g)" \
RATELDESK_SETUP_OUTPUT="$PWD/work/mcp-setup-output" \
docker compose -f docker/docker-compose.setup.yml \
  run --rm --no-deps mcp-http
```

The command is `setup --target http-host --export-directory /output`, routed by the
image's existing `dotnet Helpdesk.Mcp.Http.dll` entrypoint before normal host
validation. Direct image equivalent, with version/output already selected:

```sh
docker run --rm -it --user "$(id -u):$(id -g)" \
  --mount type=bind,src="$RATELDESK_SETUP_OUTPUT",dst=/output \
  "ghcr.io/bostontechnologies/rateldesk-mcp-http:$RATELDESK_VERSION" \
  setup --target http-host --export-directory /output
```

A running container can use the same dispatch, provided an explicit writable
output mount was provisioned in that deployment:

```sh
docker exec -it rateldesk-mcp-http-1 \
  dotnet /app/Helpdesk.Mcp.Http.dll setup --target http-host --export-directory /output
```

Use the actual container name. Normal serving stays non-root; no Docker socket,
host-root interactive process or runtime package download is needed. Setup
requires a TTY; stdio serving uses pipes without a TTY. Noninteractive users
configure the documented environment/private files instead of launching a
wizard.

## Reviewed host exports and applying them

The normal launcher binds discovery, preflight, the administrator's consent,
export/apply and observed-runtime verification to one effective proposal and its
source hashes. The redacted review shows winning sources and only required
changes. Higher-priority inline environment can mask an env-file change; fix
the winning source rather than relying on a lower-priority overlay. Revalidation
runs before export/apply. Saved settings and currently serving settings remain
distinct until recreation and observation.

The host workflow inspects existing exports and dedicated deployment environment
or isolated MCP file inputs, showing managed values/source as read-only. It configures public
origin, private API/MCP destinations, instance pin, gateway versus Authentik,
allowed origins, listener and delegation deadline. Advanced external ingress
requires authority/audience/scopes/groups and a separate reviewed downstream
API identity. Gateway uses no persistent downstream secret.
An existing external Authentik or API-purpose downstream identity can be retained
without re-entering its secret; it remains bound to the reviewed API backend.
The advanced delegation deadline is retained on reruns. When selecting a custom
listener, the shared-network `mcp-http` destination port must match it; exported
healthchecks follow the selected listener. Legacy overlays still publish direct
port 8223, so replace/remove that optional mapping explicitly for a changed
listener. A custom HTTPS listener also needs deployment-owned TLS certificate
configuration; the normal recipe terminates TLS at the outer ingress.

The coordinator produces:

| File | Contents |
| --- | --- |
| `host-settings.json` | Reviewed non-secret host settings; no downstream secret fields |
| `mcp-outbound.json` | Protected isolated downstream file; gateway has only URL and `credentialMode=gateway`; external mode includes its reviewed private identity |
| `compose.host.json` | Reviewed Web/API/MCP coordination and protected config-init source mount |
| `APPLY.txt` | Preservation, interpolation and explicit apply/recreate instructions |

The deployment-host launcher also writes `deployment.json`, the complete
reviewed manager document, after validating it against the approved proposal.
Apply this complete document through an external manager; `compose.host.json`
alone is an overlay. Keep the launcher's private checkpoint/receipt files with
the export for fresh apply/verify, without editing their contents.

All files are private, bounded and atomically replaced with conflict checks.
Existing output requires explicit reviewed overwrite; cancellation and reruns
preserve existing API identities/state. External secret references from the
operator machine are resolved into the protected isolated-file fallback for
container mounting, so protect the entire export directory. The preview is
redacted. The export does not change running env, parent-shell variables or
immutable Docker secrets.

For the lower-level compatibility export, layer the selected base source/release Compose file, its matching
gateway/Authentik MCP overlay, then `compose.host.json` last. The underlying
overlay still requires its interpolation inputs: config-file path, canonical
public resource, allowed origin and, for external mode, authority/version.
Set them to the reviewed export values before rendering. Inspect
`docker compose config` privately; avoid logging full secret-bearing output.
Explicitly recreate Web/API/MCP after review. Do not discard persistent volumes.
Set `RATELDESK_MCP_CONFIG_FILE` to the actual exported file path on the Docker
host; the generated mount interpolates that host path. A tooling-container
`/output/mcp-outbound.json` path is not the host bind source.

The legacy initializer reads the protected host input, copies it into
`mcp-http-config` as UID/GID 10001 with mode 0400, then exits. The MCP service
mounts only that configuration read-only. It does not mount API database,
bootstrap, data-protection keys or shared signing material.

## Environment-only single-origin gateway

For a source checkout with external HTTPS ingress already configured:

```sh
RATELDESK_PUBLIC_ORIGIN=https://rateldesk.example.test \
docker compose -f docker/docker-compose.yml \
  -f docker/docker-compose.mcp.env.yml up --build
```

For exact published images, export `RATELDESK_VERSION` and use the release pair:

```sh
RATELDESK_PUBLIC_ORIGIN=https://rateldesk.example.test \
docker compose -f docker/docker-compose.release.yml \
  -f docker/docker-compose.mcp.env.release.yml up -d
```

MCP is unselected in the base stack. These overlays explicitly configure Web
Enabled/Destination/mode, API pairing origin, and MCP target/resource/origin and
gateway outbound mode. They publish no direct MCP host port; public clients use
Web's `/mcp`. `RATELDESK_MCP_INSTANCE` defaults to `local`,
`RATELDESK_MCP_API_BASE_URL` to `http://api:8222/`, and
`RATELDESK_MCP_DESTINATION` to `http://mcp-http:8223/` in this recipe.
For a custom API backend, align Web `ApiBaseUrl` too through an explicit overlay.

File-based and standalone source/release recipes remain in the
[Docker example](../../docker/examples/mcp-http/README.md). Legacy file overlays
retain optional loopback MCP publication (`RATELDESK_MCP_PORT`, default 8223)
for intentional standalone access. Public HTTPS ingress must serve the configured
resource; a bare `http://+:8223` listener does not itself provide TLS.

In an extracted deployment archive, Compose files are at archive root: omit
the `docker/` prefix and use release overlays; source builds require a checkout.
Canonical guides and third-party licenses are bundled with the flat recipe.

## Migration, persistence and rotation

An existing protected JSON + env_file installation remains supported. Review
effective values and provenance before replacing files; preserve instance label,
custom target, API-owned database/bootstrap/key ring, mounts and account
credentials. Choose the same explicit ingress mode and exact resource. Adoption
does not reset NetRatel/NetClaw/mailbox configuration or account identities.

The recognized legacy selected outbound config at
`/run/rateldesk-mcp/config.json` is read and validated through its protected
source before consent. The complete reviewed proposal removes that obsolete
child-file mapping and installs the protected replacement through the initializer
and runtime volume. It retains effective settings, an explicit external
downstream identity, unrelated mounts/services/env-file references and shared
top-level definitions used elsewhere. It does not delete the source file or
Docker volumes. Review the short migration summary; apply the complete export
without hand-editing out the old mapping.

An unreadable/unowned/conflicting protected source or an unrecognized overlapping
volume/config/secret mount stops setup for a specific prerequisite. Resolve its
actual source/target deliberately and rerun ordinary setup. Do not remove all
configs/secrets, make the runtime volume writable or start the serving process
as root to conceal the collision. Unrelated paths such as
`/run/rateldesk-mcp-extra` are separate destinations.

After a material source/proposal edit, run ordinary setup with the same source,
storage and owner selection, obtain new review/approval, apply, then run fresh
`--verify`. An unchanged applied generation can resume directly with `--verify`.
Protected approval receipts/hashes are not operator-editable recovery inputs.
Reruns preserve ordinary client identity/expiry; the status-only verifier is
separately approved and cleaned up.

Gateway server upgrades need no shared-key provisioning. After target/resource
changes, reauthorize/review client bindings rather than forwarding old credentials
to a new endpoint. Rotate/revoke client credentials through account management.
External ingress and downstream credentials rotate separately. Environment or
generated host settings require restart/recreation; live explicit YARP file
reload is documented independently.

Keep published images pinned to exact tags/digests. Product version comes from
`Directory.Build.props`, and betas never move `latest`; see
[release engineering](../releases/README.md). Finish with the separate
[verification checklist](troubleshooting.md), not only port reachability.
