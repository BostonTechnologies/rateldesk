#!/usr/bin/env python3
"""Bounded host-side Docker discovery, private storage, apply and resume.

Docker Compose owns source parsing, interpolation and merging. This helper never
interprets a shell command from deployment metadata and never prints a rendered
deployment, container environment, or Docker error containing its input.
"""
import argparse
from contextlib import contextmanager
import hashlib
import json
import os
import posixpath
from pathlib import Path
import re
import shlex
import stat
import subprocess
import sys
import tempfile
import uuid
from datetime import datetime, timezone
import importlib.util
_tool_spec = importlib.util.spec_from_file_location("rateldesk_compose_toolchain", Path(__file__).with_name("compose_toolchain.py"))
compose_toolchain = importlib.util.module_from_spec(_tool_spec)
_tool_spec.loader.exec_module(compose_toolchain)
try:
    import resource
except ImportError:  # Unix host launchers normally have resource; byte checks remain portable.
    resource = None

MAX_BYTES = 1024 * 1024
MAX_SOURCE_BYTES = 262144
MAX_FILES = 32
SERVICE_NAMES = ("api", "web", "mcp-http")
HOST_TARGET = "/output"
CLIENT_TARGET = "/var/lib/rateldesk/http-client"
MANAGER_LABEL = "com.rateldesk.deployment.manager"
RELEVANT_PREFIXES = ("helpdesk__", "helpdesk:", "rateldesk_mcp_", "authentication__",
                     "webgateway__", "reverseproxy__", "integrationcredentials__",
                     "aspnetcore_", "dotnet_")
PROXY_KEYS = {"http_proxy", "https_proxy", "all_proxy", "no_proxy"}
DOCKER_SELECTION_KEYS = ("DOCKER_CONFIG", "DOCKER_CONTEXT", "DOCKER_HOST", "DOCKER_TLS",
                         "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH", "DOCKER_API_VERSION",
                         compose_toolchain.BINARY_KEY, compose_toolchain.DIGEST_KEY, compose_toolchain.VERSION_KEY,
                         compose_toolchain.CONTEXT_KEY)
COMPOSE_FLOOR = "2.35.0"
COMPOSE_FLAGS = ("--no-env-resolution", "--no-interpolate", "--no-path-resolution", "--format")
ANCESTOR_INSPECTION_SCRIPT = 'for path do test ! -L "$path"; if test -e "$path"; then test -d "$path"; test -x "$path"; fi; done'


class Prerequisite(Exception):
    """An actionable error whose text is safe to show to the operator."""


def fail(code, action):
    raise Prerequisite(f"{code}: {action}")


def export_validation_rules(reason):
    """Return fixed rule labels only; never forward validator/process text."""
    codes = ("HOST_SOURCE_CHANGED", "HOST_EXPORT_CHANGED", "HOST_COMPOSE_OBSERVATION_INVALID", "HOST_EFFECTIVE_INVALID", "HOST_SERVICE_MISSING",
        "HOST_SERVICE_ROLE_CONFLICT", "HOST_MOUNT_COLLISION", "HOST_RUNTIME_CONFIG_MOUNT",
        "HOST_RUNTIME_CONFIG_STORAGE_REQUIRED", "HOST_STORAGE_REQUIRED", "HOST_STORAGE_MOUNT_MISSING",
        "HOST_STORAGE_ISOLATION", "HOST_STORAGE_API_EXPOSURE", "HOST_API_TARGET_MISMATCH",
        "HOST_AUTHENTICATION_MISMATCH", "HOST_RESOURCE_MISMATCH", "HOST_PAIRING_ORIGIN_MISMATCH",
        "HOST_INSTANCE_MISMATCH", "HOST_LISTENER_MISMATCH", "HOST_EFFECTIVE_OPTIONS_MISMATCH",
        "HOST_ROUTE_INVALID", "HOST_ROUTE_DESTINATION", "MCP_OPTIONS_INVALID", "MCP_EXTERNAL_INVALID",
        "MCP_OUTBOUND_MODE", "WEB_MCP_DISABLED")
    topology = [(service, key) for service in SERVICE_NAMES for key in
                ("service configuration", "effective imported configuration")]
    topology += [("deployment", key) for key in ("defined service inventory", "volumes references", "configs references",
        "secrets references", "networks references")]
    topology += [("mcp-config-init", "service configuration"), ("mcp-config-init", "protected initializer")]
    rules = set()
    for line in reason.splitlines():
        for code in codes:
            if line.startswith(code.casefold() + " ") or line.startswith(code.casefold() + ":"):
                labels = [f"{code}[{layer}:{key}]" for layer, key in topology if code == "HOST_SOURCE_CHANGED"
                          and line.startswith(f"{code} [{layer}] {key} (".casefold())]
                rules.update(labels or [code])
    return sorted(rules)[:16]


def safe_label(value, maximum=256):
    if not isinstance(value, str) or len(value) > maximum or any(ord(c) < 32 or ord(c) == 127 for c in value):
        fail("deployment_metadata", "Saved Docker metadata is malformed. Restore its actual source or supply the explicit source options.")
    return value


def normalized_container_path(value):
    safe_label(value, 4096)
    # POSIX permits preserving exactly two leading slashes. Docker container
    # destinations and the effective-deployment validator use a single root.
    return posixpath.normpath("/" + value.lstrip("/"))


def container_file_target(kind, entry):
    name = entry if isinstance(entry, str) else entry.get("source", "")
    target = entry.get("target") if isinstance(entry, dict) else None
    target = target or ("/run/secrets/" + name if kind == "secrets" else "/" + name)
    return normalized_container_path(target)


def read_bytes(path, maximum=MAX_SOURCE_BYTES):
    path = Path(path)
    try:
        with path.open("rb") as source:
            content = source.read(maximum + 1)
    except OSError:
        fail("source_unreadable", "A saved source is missing or unreadable. Restore it before setup.")
    if len(content) > maximum:
        fail("source_oversized", f"A saved source exceeds {maximum} bytes. Split the source before setup.")
    return content


def file_record(path, optional=False):
    path = Path(path)
    if not path.is_absolute() or path.is_symlink():
        fail("source_unreadable", "Use an existing absolute regular source file. Restore missing files; symlink sources require their actual resolved path.")
    if optional and not path.exists():
        # Presence is part of the reviewed generation: an optional provider must
        # not silently appear during browser consent or before apply.
        return {"path": str(path), "sha256": hashlib.sha256(b"RatelDesk absent optional source v1\0" + str(path).encode()).hexdigest(),
                "optional": True, "present": False}
    if not path.is_file():
        fail("source_unreadable", "A saved source is not a regular readable file. Restore it before setup.")
    record = {"path": str(path), "sha256": hashlib.sha256(read_bytes(path)).hexdigest()}
    if optional:
        record.update(optional=True, present=True)
    return record


def check_compose_dependencies(path):
    # Compose owns parsing. This is only a conservative prerequisite guard for
    # source dependencies which its rendered output no longer identifies.
    content = read_bytes(path).decode("utf-8-sig")
    dependency = False
    try:
        document = json.loads(content)
        def visit(value):
            if isinstance(value, dict):
                return any(key in ("include", "extends") or visit(child) for key, child in value.items())
            return isinstance(value, list) and any(visit(child) for child in value)
        dependency = visit(document)
    except ValueError:
        dependency = bool(re.search(r"(?m)^\s*(?:['\"]?(?:include|extends)['\"]?)\s*:", content)
                          or re.search(r"[{},]\s*['\"]?(?:include|extends)['\"]?\s*:", content))
    if dependency:
        fail("compose_dependency_unsupported", "Full source/runtime host verification cannot bind include/extends dependencies with this launcher. Keep the original saved YAML; review its dependency files through the owning manager or use ordinary client endpoint diagnostics. No source replacement or deployment apply was requested.")


def private_directory(path):
    path = Path(path)
    if path.is_symlink():
        fail("storage_symlink", "The setup directory must be a private directory, not a symlink.")
    try:
        path.mkdir(mode=0o700, parents=True, exist_ok=True)
        info = path.stat()
        if info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700:
            fail("storage_permissions", "The existing setup directory must be owned by this user with mode 0700. Restore that directory's protection before setup.")
        with tempfile.TemporaryFile(dir=path) as probe:
            probe.write(b"storage preflight")
            probe.flush()
    except OSError:
        fail("storage_unwritable", "The saved project directory is not privately writable. Supply its actual writable --output-directory.")
    return path


def write_private(path, value):
    path = Path(path)
    if path.is_symlink():
        fail("storage_symlink", "A setup file is a symlink. Restore the private directory before setup.")
    content = value if isinstance(value, bytes) else (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()
    if len(content) > MAX_BYTES:
        fail("snapshot_oversized", "The effective deployment exceeds the bounded snapshot limit. Reduce unrelated generated configuration before setup.")
    descriptor, temporary = tempfile.mkstemp(dir=path.parent, prefix=".stage-")
    try:
        with os.fdopen(descriptor, "wb") as target:
            os.fchmod(target.fileno(), 0o600)
            target.write(content)
            target.flush()
            os.fsync(target.fileno())
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def read_private_json(path):
    path = Path(path)
    info = path.stat()
    if path.is_symlink() or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) & 0o077:
        fail("storage_permissions", "A saved setup file is not private to this user. Restore its ownership and mode before resuming.")
    try:
        return json.loads(read_bytes(path, MAX_BYTES))
    except (ValueError, UnicodeError):
        fail("snapshot_invalid", "The saved setup state is malformed. Rerun ordinary setup to review a new generation.")


class Docker:
    def __init__(self, output=None):
        self.output = output
        self.compose_tool = None
        # Capture only CLI selection, never the private config/credential file.
        self.selection = {key: os.environ[key] for key in DOCKER_SELECTION_KEYS if key in os.environ}
        # Docker's implicit config and TLS paths depend on HOME. Make their
        # original locations explicit so a new terminal/process cannot retain
        # the same daemon while silently selecting different registry helpers.
        if not self.selection.get("DOCKER_CONFIG"):
            self.selection["DOCKER_CONFIG"] = str(Path.home() / ".docker")
        if (self.selection.get("DOCKER_TLS") or self.selection.get("DOCKER_TLS_VERIFY")) and not self.selection.get("DOCKER_CERT_PATH"):
            # Docker CLI InstallFlags defaults certificates to config.Dir(),
            # which already honors DOCKER_CONFIG, unless DOCKER_CERT_PATH is set.
            self.selection["DOCKER_CERT_PATH"] = self.selection["DOCKER_CONFIG"]
        for key, value in self.selection.items():
            safe_label(value, 4096)
            if key == "DOCKER_HOST" and re.search(r"://[^/]*@", value):
                fail("docker_selection_sensitive", "Use a Docker context or protected TLS files instead of credentials embedded in DOCKER_HOST before setup.")
            if key in ("DOCKER_CONFIG", "DOCKER_CERT_PATH") and value:
                self.selection[key] = str(Path(value).absolute())
        self.daemon_identity = None
        if self.selection.get(compose_toolchain.BINARY_KEY):
            self.compose_tool = compose_toolchain.Selection(self.selection[compose_toolchain.BINARY_KEY],
                self.selection.get(compose_toolchain.DIGEST_KEY), fail)
            self.selection.update(self.compose_tool.environment())

    def configure(self, args):
        if args.compose_binary and args.prepare_compose:
            fail("compose_tool_conflict", "Choose --compose-binary or --prepare-compose, not both.")
        if args.compose_cache and not args.prepare_compose:
            fail("compose_tool_conflict", "--compose-cache is used with explicit --prepare-compose only.")
        if args.compose_binary:
            self.compose_tool = compose_toolchain.Selection(args.compose_binary, args.compose_sha256, fail)
        elif args.prepare_compose:
            self.compose_tool = compose_toolchain.prepare(args.prepare_compose, fail, cache=args.compose_cache)
        elif args.compose_sha256:
            fail("compose_tool_conflict", "Supply --compose-binary with --compose-sha256.")
        if self.compose_tool:
            self.selection.update(self.compose_tool.environment())

    def process_environment(self):
        environment = os.environ.copy()
        for key in DOCKER_SELECTION_KEYS:
            environment.pop(key, None)
        environment.update(self.selection)
        return environment

    def preflight(self, require_compose=True):
        """Probe actual operation capabilities before reading deployment sources."""
        if not require_compose:
            self.bind_daemon()
            return
        try:
            self._compose_preflight()
        except Prerequisite as failure:
            if (self.compose_tool is not None or not sys.stdin.isatty()
                    or not str(failure).startswith(("compose_feature_unsupported:", "compose_plugin_missing:"))):
                raise
            self.compose_tool = compose_toolchain.choose(fail, output=sys.stderr)
            self.selection.update(self.compose_tool.environment())
            self._compose_preflight()

    def _compose_preflight(self):
        """Retain the complete beta.2 synthetic source/env/path/order probe."""
        version = self.run(["compose", "version", "--short"], maximum=1024).decode("utf-8", errors="replace").strip()
        self.compose_version = version if re.fullmatch(r"v?\d+\.\d+\.\d+[A-Za-z0-9.+-]{0,64}", version) else "unparseable"
        if self.compose_tool:
            expected_version = self.selection.get(compose_toolchain.VERSION_KEY)
            if expected_version and expected_version != self.compose_version:
                fail("compose_tool_changed", "The selected Compose version differs from the reviewed tool receipt. Restore that exact verified executable before resuming.")
            self.selection[compose_toolchain.VERSION_KEY] = self.compose_version
        help_text = self.run(["compose", "config", "--help"], maximum=65536).decode("utf-8", errors="replace")
        for flag in COMPOSE_FLAGS:
            if flag not in help_text:
                self.unsupported("config " + flag)
        with tempfile.TemporaryDirectory(prefix="rateldesk-compose-probe-") as temporary:
            directory = Path(temporary)
            interpolation_key = "RATELDESK_CAPABILITY_VALUE_" + uuid.uuid4().hex
            interpolation = "${" + interpolation_key + "}"
            (directory / "first.env").write_text(interpolation_key + "=first\n")
            (directory / "second.env").write_text(interpolation_key + "=resolved\n")
            (directory / "service.env").write_text("RATELDESK_CAPABILITY_PRIVATE=synthetic-probe\n")
            (directory / "base.json").write_text(json.dumps({"services": {"probe": {
                "image": "rateldesk-capability:never-run", "environment": {
                    "VALUE": interpolation, "ORDER": "first"},
                "env_file": [{"path": "service.env", "required": True}, {"path": "absent.env", "required": False}],
                "volumes": [{"type": "bind", "source": "./fixture", "target": "/fixture"}]}}}))
            (directory / "override.json").write_text(json.dumps({"services": {"probe": {"environment": {"ORDER": "last"}}}}))
            command = ["compose", "--project-directory", temporary, "--project-name", "rateldesk-capability",
                "--env-file", str(directory / "first.env"), "--env-file", str(directory / "second.env"),
                "-f", str(directory / "base.json"), "-f", str(directory / "override.json"), "config", "--format", "json"]
            try:
                source = self.json(command + ["--no-env-resolution", "--no-interpolate", "--no-path-resolution"])["services"]["probe"]
                unresolved = self.json(command + ["--no-env-resolution"])["services"]["probe"]
                resolved = self.json(command)["services"]["probe"]
                source_environment = environment_map(source.get("environment", {}))
                valid = (source_environment.get("VALUE") == interpolation
                    and source_environment.get("ORDER") == "last"
                    and "RATELDESK_CAPABILITY_PRIVATE" not in source_environment
                    and source.get("env_file", [{}])[0].get("path") == "service.env"
                    and any(item.get("required") is False for item in source.get("env_file", []))
                    and source.get("volumes", [{}])[0].get("source") == "./fixture"
                    and environment_map(unresolved.get("environment", {})).get("VALUE") == "resolved"
                    and unresolved.get("volumes", [{}])[0].get("source") == str(directory / "fixture")
                    and environment_map(resolved.get("environment", {})).get("RATELDESK_CAPABILITY_PRIVATE") == "synthetic-probe"
                    and environment_map(resolved.get("environment", {})).get("VALUE") == "resolved"
                    and not resolved.get("env_file"))
            except Prerequisite as failure:
                if not str(failure).startswith(("compose_source_invalid:", "compose_env_file_unreadable:", "docker_json:")):
                    raise
                self.unsupported("config source/interpolation/env-file/path/order semantics")
            except (KeyError, IndexError, TypeError, AttributeError):
                self.unsupported("config source/interpolation/env-file/path/order semantics")
            if not valid:
                self.unsupported("config source/interpolation/env-file/path/order semantics")
        self.bind_daemon()

    def bind_daemon(self):
        # Capture the effective context without changing the active context or
        # copying config.json, registry authentication, or TLS private keys.
        if not self.selection.get("DOCKER_CONTEXT") and not self.selection.get("DOCKER_HOST"):
            context = self.run(["context", "show"], maximum=1024).decode("utf-8").strip()
            safe_label(context, 256)
            if not context:
                fail("docker_context_unresolved", "The active Docker context could not be resolved. Supply the original explicit DOCKER_CONTEXT or DOCKER_HOST before setup.")
            self.selection["DOCKER_CONTEXT"] = context
        if self.selection.get("DOCKER_CONTEXT"):
            context = self.json(["context", "inspect", "--format", "{{json .Endpoints.docker}}", self.selection["DOCKER_CONTEXT"]])
            if not isinstance(context, dict) or not context.get("Host"):
                fail("docker_context_unresolved", "The selected Docker context has no resolvable Docker endpoint. Restore that same context; setup will not fall back to a local daemon.")
            context_digest = hashlib.sha256(json.dumps(context, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
            if self.selection.get(compose_toolchain.CONTEXT_KEY) not in (None, context_digest):
                fail("docker_context_changed", "The selected Docker context endpoint or TLS policy differs from the reviewed tool receipt. Restore the original context, or restart ordinary setup for a new review.")
            self.selection[compose_toolchain.CONTEXT_KEY] = context_digest
        identity = self.run(["info", "--format", "{{json .ID}}"], maximum=1024)
        try:
            identifier = json.loads(identity)
            if not isinstance(identifier, str) or not identifier or len(identifier) > 256:
                raise ValueError()
        except (ValueError, UnicodeError):
            fail("docker_daemon_identity", "The selected Docker daemon did not provide its stable identity. Restore the selected Docker engine/context before setup; approval cannot bind an unidentified deployment.")
        identity = hashlib.sha256(identifier.encode()).hexdigest()
        if self.daemon_identity is not None and identity != self.daemon_identity:
            fail("docker_daemon_changed", "The selected Docker daemon changed during setup. Restore the original Docker context/host before proceeding; no operation was retried.")
        self.daemon_identity = identity

    def unsupported(self, feature):
        fail("compose_feature_unsupported", f"Selected Compose version {self.compose_version} lacks {feature}. Rerun with --compose-binary /absolute/path/docker-compose --compose-sha256 EXPECTED_SHA256, or explicitly consent to pinned private preparation with --prepare-compose /absolute/private/directory (offline: --compose-cache /absolute/platform/file). To cancel, install Docker Compose v{COMPOSE_FLOOR}+ through the host's supported package channel, then rerun. The normal Docker plugin/configuration/context is unchanged.")

    def check_selection(self, state):
        if "dockerSelection" in state and state["dockerSelection"] != self.selection:
            fail("docker_selection_changed", "The Docker configuration/context/host/TLS selection differs from this approved generation. Rerun its printed apply/verify command with the original explicit Docker settings; changed deployment ownership needs ordinary setup and new review.")
        if state.get("composeVersion") and state["composeVersion"] != getattr(self, "compose_version", None):
            fail("compose_tool_changed", "The selected Compose version differs from the reviewed checkpoint. Restore the reviewed tool or run ordinary setup for a new source review.")
        if state.get("dockerDaemonIdentity") and state["dockerDaemonIdentity"] != self.daemon_identity:
            fail("docker_daemon_changed", "This is a different Docker daemon from the approved generation. Restore the original Docker context/host; do not apply or verify another deployment's approval.")

    def run(self, arguments, *, input_data=None, maximum=MAX_BYTES, optional=False, owner_tool=False):
        selected_compose = self.compose_tool is not None and arguments and arguments[0] == "compose" and not owner_tool
        guarded_compose = bool(arguments and arguments[0] == "compose"
            and (selected_compose or owner_tool and self.daemon_identity is not None))
        if guarded_compose:
            self.bind_daemon()
        if selected_compose:
            # Both invocations retain precisely the original Docker config and
            # its context/TLS/auth-helper selection. Bind before and after every
            # private renderer/apply call; an unresolved daemon never falls back.
            command = self.compose_tool.command(arguments[1:])
        else:
            command = ["docker", *arguments]
        # Temporary files bound memory even when an untrusted daemon/source emits
        # huge output. Stderr is deliberately never copied into a public error.
        with tempfile.TemporaryFile() as result, tempfile.TemporaryFile() as error:
            try:
                completed = subprocess.run(command, input=input_data, env=self.process_environment(),
                                           stdout=result, stderr=error, timeout=45, check=False,
                                           preexec_fn=bound_child_files if resource else None)
            except subprocess.TimeoutExpired:
                fail("docker_timeout", "Docker exceeded the 45-second host-operation limit. Check the selected Docker context/host and retry; no operation was retried.")
            except FileNotFoundError:
                fail("docker_cli_missing", "Docker CLI is unavailable. Install the host's supported Docker CLI and Compose plugin, then rerun.")
            except OSError:
                fail("docker_permission_denied", "Docker CLI could not be executed. Restore host Docker CLI permissions and retry.")
            if result.tell() > maximum:
                fail("docker_output_oversized", "Docker returned an oversized deployment snapshot. Reduce the saved configuration and retry.")
            if completed.returncode:
                if optional:
                    if arguments and arguments[0] == "cp":
                        error.seek(0)
                        reason = error.read(4096).decode("utf-8", errors="replace").casefold()
                        if "could not find" not in reason and "no such file or directory" not in reason:
                            fail("protected_provider_unreadable", "Docker could not read a protected provider. Restore its regular readable source; permission and size failures cannot be treated as missing defaults.")
                    return None
                error.seek(0)
                reason = error.read(8192).decode("utf-8", errors="replace").casefold()
                if any(arguments[index:index + 2] == ["host", "export-check"] for index in range(len(arguments) - 1)):
                    rules = export_validation_rules(reason)
                    if rules:
                        failure = Prerequisite("docker_operation: The approved export failed its serving and mount validation; raw process output remains private.")
                        failure.export_validation_rules = rules
                        raise failure
                if arguments[:2] == ["compose", "version"]:
                    fail("compose_plugin_missing", "The selected Docker CLI cannot run its Compose plugin. Select an existing verified tool with --compose-binary /absolute/path/docker-compose --compose-sha256 EXPECTED_SHA256, or explicitly prepare pinned private Compose with --prepare-compose /absolute/private/directory (offline: add --compose-cache /absolute/platform/file). Otherwise install Docker Compose v2.35+ through the host supported package channel and rerun; no plugin/config/context was changed.")
                if "permission denied" in reason or "access is denied" in reason:
                    code = "compose_source_permission" if arguments[0] == "compose" and "config" in arguments else "docker_permission_denied"
                    fail(code, "Docker could not read the selected source or access the selected daemon. Restore that source/daemon permission and retry; raw errors remain private.")
                if arguments[0] == "info" or any(marker in reason for marker in ("cannot connect to the docker daemon", "is the docker daemon running", "error during connect", "docker daemon is not running")):
                    fail("docker_daemon_unavailable", "The selected Docker daemon/context is unavailable. Start it or restore access to that same Docker host, then rerun.")
                if arguments[0] == "compose" and "config" in arguments:
                    if any(marker in reason for marker in ("env file", "env_file", "failed to read .env")):
                        fail("compose_env_file_unreadable", "Compose could not read a required interpolation/service env file. Restore its saved path and read permission, then rerun; no values were printed.")
                    fail("compose_source_invalid", "Compose rejected the saved source or interpolation. Validate the private source with the selected Compose plugin, correct it, then rerun; no rendered values were printed.")
                fail("docker_operation", "Docker could not resolve the saved deployment. Check its saved sources and local images, then retry. No raw environment was printed.")
            result.seek(0)
            content = result.read()
        if guarded_compose:
            self.bind_daemon()
        return content

    def json(self, arguments):
        try:
            return json.loads(self.run(arguments))
        except (ValueError, UnicodeError):
            fail("docker_json", "Docker returned invalid JSON. Use a supported Docker Compose v2 installation and retry.")

    def inspect(self, container):
        # JSON projection handles embedded newlines and '=' in environment values.
        template = '{{json .}}'
        values = self.json(["inspect", "--format", template, container])
        if not isinstance(values, dict):
            fail("docker_metadata", "Docker container metadata is unavailable. Select its actual saved container.")
        return values


def bound_child_files():
    # docker cp can otherwise materialize an arbitrarily large provider before
    # read_bytes gets a chance to enforce its application bound.
    resource.setrlimit(resource.RLIMIT_FSIZE, (MAX_BYTES + 4096, MAX_BYTES + 4096))


def environment_map(values):
    result = {}
    canonical = set()
    records = list(values.items()) if isinstance(values, dict) else values or []
    if len(records) > 2048:
        fail("environment_oversized", "A saved environment exceeds the bounded key count. Consolidate its owning sources before setup.")
    for value in records:
        if isinstance(values, dict):
            key, content = value
            content = None if content is None else str(content)
        else:
            if not isinstance(value, str):
                fail("environment_invalid", "A Docker environment record is malformed. Restore the saved source before setup.")
            key, separator, content = value.partition("=")
            if not separator:
                content = None
        if (not isinstance(key, str) or not key or len(key) > 512
                or any(ord(character) < 33 or ord(character) == 127 or character == "=" for character in key)
                or content is not None and (len(content) > 65536 or "\0" in content)):
            fail("environment_invalid", "A saved environment key/value is malformed or oversized. Correct its owning source without printing credentials.")
        normalized = key.replace(":", "__").casefold()
        if key in result or normalized in canonical and normalized not in PROXY_KEYS:
            fail("environment_duplicate", "A relevant Docker environment key is duplicated. Remove the ambiguous source before setup.")
        canonical.add(normalized)
        result[key] = content
    return result


def relevant_environment(values):
    environment = environment_map(values)
    result = [f"{key}={value}" for key, value in environment.items()
              if key.casefold() in ("apibaseurl", "contentroot") or key.casefold().startswith(RELEVANT_PREFIXES)]
    exclusions = environment.get("no_proxy", environment.get("NO_PROXY"))
    if exclusions is not None:
        result.append("NO_PROXY=" + exclusions)
    return result


def effective_environment(image, inline):
    result = {key.replace(":", "__").casefold(): value for key, value in environment_map(image).items()}
    for key, value in environment_map(inline).items():
        canonical = key.replace(":", "__").casefold()
        if value is None:
            result.pop(canonical, None)
        else:
            result[canonical] = value
    return result


def command_configuration(command):
    """Read literal configuration arguments; the serving validator owns validity."""
    result = {}
    if not isinstance(command, list):
        return result  # Custom shell dispatch is rejected by serving preflight.
    if len(command) > 2048:
        fail("command_oversized", "The saved service command exceeds its bounded argument count.")
    index = 0
    while index < len(command):
        argument = command[index]
        if not isinstance(argument, str) or len(argument) > 65536 or "\0" in argument:
            fail("command_invalid", "The saved service command must contain bounded literal string arguments.")
        key, separator, value = argument.lstrip("-/").partition("=")
        if not separator and argument.startswith(("-", "/")):
            index += 1
            value = command[index] if index < len(command) else ""
            if not isinstance(value, str) or len(value) > 65536 or "\0" in value:
                fail("command_invalid", "The saved service command must contain bounded literal string arguments.")
        if separator or argument.startswith(("-", "/")):
            result[key.casefold()] = value
        index += 1
    return result


def resolved_deployment_hash(document):
    # Compose has already resolved paths, merges and interpolation. Only our
    # observation metadata is outside the complete deployment's semantics.
    complete = {key: value for key, value in document.items() if not key.startswith("x-rateldesk-")}
    return hashlib.sha256(json.dumps(complete, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()).hexdigest()


def validate_content_root(name, service, environment, command, image_working_directory):
    working_directory = service.get("working_dir") or image_working_directory or "/"
    source = "Compose working_dir" if service.get("working_dir") else "image Config.WorkingDir"
    if working_directory != "/app":
        fail("content_root_unsupported", f"The {name} {source} selects an unsupported application directory. Host setup snapshots require /app. Restore the canonical application directory before setup; no approval or provider snapshot was created.")
    # WebApplication owns provider interpretation. Do not silently read /app
    # when an accepted host setting could select providers in another root.
    roots = [("DOTNET_CONTENTROOT", environment.get("dotnet_contentroot")),
             ("ASPNETCORE_CONTENTROOT", environment.get("aspnetcore_contentroot")),
             ("contentRoot environment", environment.get("contentroot")),
             ("command contentRoot", command.get("contentroot"))]
    for source, value in roots:
        if value is not None and value != "/app":
            fail("content_root_unsupported", f"The {name} {source} selects an unsupported content root. Host setup snapshots require /app. Restore that owning setting before setup; alternate provider roots need an explicit supported snapshot implementation.")


def json_configuration_value(document, wanted):
    found = None
    def visit(value, prefix=""):
        nonlocal found
        if isinstance(value, dict):
            for key, child in value.items():
                visit(child, prefix + ":" + key if prefix else key)
        elif prefix.casefold() == wanted.casefold():
            found = value
    visit(document)
    return found


def selected_secret_references(proposal):
    """Plan mounts for the selected identity; C# still owns serving validation."""
    fields = ("CredentialMode", "IntegrationCredential", "IntegrationCredentialFile", "AuthentikAppPassword", "AuthentikAppPasswordFile")
    values = {"CredentialMode": "authentik"}
    def apply(layer):
        mode = layer.get("CredentialMode")
        if mode and mode.casefold() != values.get("CredentialMode", "authentik").casefold():
            values.clear()
        for inline, file in (("IntegrationCredential", "IntegrationCredentialFile"), ("AuthentikAppPassword", "AuthentikAppPasswordFile")):
            if inline in layer and file not in layer:
                values.pop(file, None)
            if file in layer and inline not in layer:
                values.pop(inline, None)
        values.update(layer)
    providers = proposal["x-rateldesk-protected-configuration"]["mcp-http"]
    for provider in providers:
        if not Path(provider["path"]).name.startswith("appsettings"):
            document = json.loads(provider["content"])
            apply({field: str(value).strip() for field in fields
                   if (value := json_configuration_value(document, field)) is not None})
    for provider in providers:
        if Path(provider["path"]).name.startswith("appsettings"):
            document = json.loads(provider["content"])
            apply({field: str(value).strip() for field in fields
                   if (value := json_configuration_value(document, "Helpdesk:Mcp:Outbound:" + field)) is not None})
    environment = effective_environment(proposal["x-rateldesk-image-environment"]["mcp-http"], proposal["services"][proposal.get("x-rateldesk-service-roles", {}).get("mcp-http", "mcp-http")].get("environment", {}))
    suffixes = {"CredentialMode": "credential_mode", "IntegrationCredential": "integration_credential", "IntegrationCredentialFile": "integration_credential_file",
                "AuthentikAppPassword": "authentik_app_password", "AuthentikAppPasswordFile": "authentik_app_password_file"}
    apply({field: environment["rateldesk_mcp_" + suffix].strip() for field, suffix in suffixes.items() if "rateldesk_mcp_" + suffix in environment})
    apply({field: environment["helpdesk__mcp__outbound__" + field.casefold()].strip() for field in fields
           if "helpdesk__mcp__outbound__" + field.casefold() in environment})
    command = proposal["services"][proposal.get("x-rateldesk-service-roles", {}).get("mcp-http", "mcp-http")].get("command", proposal.get("x-rateldesk-image-commands", {}).get("mcp-http", []))
    command_layer = {}
    if isinstance(command, list):
        for index, argument in enumerate(command):
            key, separator, value = argument.lstrip("-/").partition("=")
            for field in fields:
                if key.replace("__", ":").casefold() == "helpdesk:mcp:outbound:" + field.casefold():
                    command_layer[field] = value if separator else command[index + 1] if index + 1 < len(command) else ""
    apply(command_layer)
    mode = values.get("CredentialMode", "authentik").casefold()
    selected = "IntegrationCredentialFile" if mode == "integration" else "AuthentikAppPasswordFile" if mode == "authentik" else None
    return {values[selected]} if selected and values.get(selected) else set()


def approved_import_secret_mounts(proposal, complete, outbound_content):
    """Bind only identities still selected by the reviewed complete deployment."""
    service = complete["services"]["mcp-http"]
    image_environment = proposal["x-rateldesk-image-environment"]
    environment = effective_environment(image_environment["mcp-http"], service.get("environment", {}))
    command = command_configuration(service.get("command", proposal["x-rateldesk-image-commands"]["mcp-http"]))
    providers = proposal["x-rateldesk-protected-configuration"]["mcp-http"]
    selected_json_path = None
    for provider in providers:
        if Path(provider["path"]).name.startswith("appsettings"):
            value = json_configuration_value(json.loads(provider["content"]), "Helpdesk:Mcp:ConfigurationPath")
            if value is not None:
                selected_json_path = value
    selected = command.get("helpdesk:mcp:configurationpath", environment.get("helpdesk__mcp__configurationpath", selected_json_path))
    selected = selected if isinstance(selected, str) and selected.strip() else environment.get("rateldesk_mcp_config")
    final_providers = [provider for provider in providers if Path(provider["path"]).name.startswith("appsettings") or provider["path"] == selected]
    if selected == "/run/rateldesk-mcp/config.json":
        final_providers = [provider for provider in final_providers if provider["path"] != selected]
        final_providers.append({"path": selected, "content": outbound_content})
    expected = {**complete, "x-rateldesk-image-environment": image_environment,
        "x-rateldesk-image-commands": proposal["x-rateldesk-image-commands"],
        "x-rateldesk-protected-configuration": {"mcp-http": final_providers}}
    references = selected_secret_references(expected)
    descriptors = [item for item in proposal.get("x-rateldesk-secret-mounts", []) if item["path"] in references]
    if {item["path"] for item in descriptors} != references:
        fail("approved_secret_source", "The approved complete deployment selects an identity file outside the reviewed protected sources. Review that exact owning source before export.")
    return descriptors


def unique_container(docker, project, service, running=False):
    command = ["ps"]
    if not running:
        command.append("--all")
    command += ["--filter", f"label=com.docker.compose.project={project}", "--filter",
                f"label=com.docker.compose.service={service}", "--format", "{{json .ID}}"]
    identifiers = []
    for line in docker.run(command, maximum=8192).splitlines():
        try:
            identifier = json.loads(line)
        except ValueError:
            fail("docker_json", "Docker returned invalid discovery metadata. Check the host Docker installation.")
        metadata = docker.inspect(identifier)
        if str((metadata.get("Config", {}).get("Labels", {}) or {}).get("com.docker.compose.oneoff", "false")).casefold() != "true":
            identifiers.append((identifier, metadata))
    if len(identifiers) > 1:
        fail("runtime_ambiguous", f"More than one {service} container belongs to this project. Select a single serving deployment before setup.")
    return identifiers[0] if identifiers else (None, None)


def choose_container(docker, explicit, foundation=False):
    if explicit:
        return explicit, docker.inspect(explicit)
    identifiers = []
    service = "web" if foundation else "mcp-http"
    for line in docker.run(["ps", "--all", "--filter", f"label=com.docker.compose.service={service}",
                            "--format", "{{json .ID}}"], maximum=8192).splitlines():
        try:
            identifier = json.loads(line)
        except ValueError:
            fail("docker_json", "Docker returned invalid discovery metadata. Check Docker and retry.")
        metadata = docker.inspect(identifier)
        if foundation and not str(metadata.get("Config", {}).get("Image", "")).startswith(("ghcr.io/bostontechnologies/rateldesk-web:", "ghcr.io/bostontechnologies/rateldesk-web@sha256:")):
            continue
        labels = metadata.get("Config", {}).get("Labels", {}) or {}
        if str(labels.get("com.docker.compose.oneoff", "false")).casefold() != "true":
            identifiers.append((identifier, metadata))
        if len(identifiers) > 32:
            fail("deployment_ambiguous", "More than 32 MCP containers were found. Supply --container for the intended instance.")
    if len(identifiers) == 1:
        return identifiers[0]
    if not identifiers:
        return (None, None) if foundation else choose_container(docker, None, foundation=True)
    if not sys.stdin.isatty() or not sys.stdout.isatty():
        fail("deployment_ambiguous", "Multiple MCP instances exist. Rerun interactively to select one or supply --container.")
    print("Select the MCP instance:")
    for index, (_, metadata) in enumerate(identifiers, 1):
        labels = metadata.get("Config", {}).get("Labels", {}) or {}
        print(f"  {index}) {safe_label(metadata.get('Name', '').lstrip('/'), 80)} · {safe_label(labels.get('com.docker.compose.project', ''), 80)}")
    try:
        choice = input("Instance number: ")
    except EOFError:
        fail("setup_cancelled", "No instance was selected. Rerun when ready.")
    if not choice.isdecimal() or not 1 <= int(choice) <= len(identifiers):
        fail("deployment_ambiguous", "Select one listed instance number.")
    return identifiers[int(choice) - 1]


def metadata_paths(value):
    # Compose records comma-separated file metadata. Do not claim it can encode
    # ambiguous filenames; explicit file arguments recover those deployments.
    if not value:
        return []
    safe_label(value, 16384)
    return value.split(",")


def validate_runtime_options(definition, metadata):
    """Attest configured inspect options; retain actual daemon defaults privately."""
    from decimal import Decimal, InvalidOperation
    host, config = metadata.get("HostConfig", {}), metadata.get("Config", {})

    def mismatch(key):
        fail("runtime_option_mismatch", f"The actual Docker option {key} differs from the saved source. Reconcile that exact option through its deployment owner, then review again; setup changed no service.")

    def unsupported(key):
        fail("runtime_option_unsupported", f"Full host attestation cannot resolve the configured {key} option. Keep the original saved source and review this exact option through its deployment owner; use ordinary client endpoint diagnostics until its runtime binding is supported. No reformatting or deployment change is required by this check.")

    def integer(value, key):
        if isinstance(value, bool) or not re.fullmatch(r"-?[0-9]+", str(value)):
            unsupported(key)
        return int(value)

    def number(key, field, value):
        expected = integer(value, key)
        actual = host.get(field)
        if actual is None or integer(actual, key) != expected:
            mismatch(key)

    def cpu_number(value, key):
        try:
            number = Decimal(str(value)) * 1000000000
            if not number.is_finite() or number != number.to_integral_value():
                unsupported(key)
            return int(number)
        except (InvalidOperation, ValueError):
            unsupported(key)

    for key, field in (("dns", "Dns"), ("dns_search", "DnsSearch"), ("dns_opt", "DnsOptions")):
        if key in definition and definition[key] is not None:
            if definition[key] != (host.get(field) or []):
                mismatch(key)
    if "extra_hosts" in definition:
        def extra_hosts(values):
            entries = values.items() if isinstance(values, dict) else (str(value).replace("=", ":", 1).split(":", 1) for value in values or [])
            try:
                return sorted((str(name), str(address)) for name, address in entries)
            except (TypeError, ValueError):
                unsupported("extra_hosts")
        if extra_hosts(definition["extra_hosts"]) != extra_hosts(host.get("ExtraHosts")):
            mismatch("extra_hosts")
    if "sysctls" in definition:
        expected = definition["sysctls"]
        if not isinstance(expected, dict):
            unsupported("sysctls")
        if any(str((host.get("Sysctls") or {}).get(key)) != str(value) for key, value in expected.items()):
            mismatch("sysctls")
    if "devices" in definition:
        expected = []
        for device in definition["devices"] or []:
            if not isinstance(device, dict) or not str(device.get("source", "")).startswith("/") or not str(device.get("target", "")).startswith("/"):
                unsupported("devices")
            expected.append((device["source"], device["target"], "".join(sorted(device.get("permissions") or "rwm"))))
        actual = [(device.get("PathOnHost"), device.get("PathInContainer"), "".join(sorted(device.get("CgroupPermissions") or "rwm"))) for device in host.get("Devices") or []]
        if sorted(expected) != sorted(actual):
            mismatch("devices")
    if "device_cgroup_rules" in definition and sorted(definition["device_cgroup_rules"] or []) != sorted(host.get("DeviceCgroupRules") or []):
        mismatch("device_cgroup_rules")
    if "ulimits" in definition:
        actual = {entry.get("Name"): (entry.get("Soft"), entry.get("Hard")) for entry in host.get("Ulimits") or []}
        for name, limits in (definition["ulimits"] or {}).items():
            soft, hard = (limits.get("soft"), limits.get("hard")) if isinstance(limits, dict) else (limits, limits)
            if actual.get(name) != (integer(soft, "ulimits"), integer(hard, "ulimits")):
                mismatch("ulimits")
    for key, field in (("pid", "PidMode"), ("ipc", "IpcMode"), ("uts", "UTSMode"), ("userns_mode", "UsernsMode"),
                       ("cgroup", "CgroupnsMode"), ("cgroup_parent", "CgroupParent"), ("cpuset", "CpusetCpus"), ("isolation", "Isolation")):
        if key in definition and definition[key] is not None:
            if key in ("pid", "ipc") and str(definition[key]).startswith(("service:", "container:")):
                unsupported(key)
            if str(definition[key]) != str(host.get(field) or ""):
                mismatch(key)
    for key, field in (("hostname", "Hostname"), ("domainname", "Domainname")):
        if key in definition and definition[key] != config.get(field):
            mismatch(key)
    numeric = (("mem_limit", "Memory"), ("mem_reservation", "MemoryReservation"), ("memswap_limit", "MemorySwap"),
        ("mem_swappiness", "MemorySwappiness"), ("shm_size", "ShmSize"), ("pids_limit", "PidsLimit"),
        ("cpu_period", "CpuPeriod"), ("cpu_quota", "CpuQuota"), ("cpu_shares", "CpuShares"),
        ("cpu_rt_period", "CpuRealtimePeriod"), ("cpu_rt_runtime", "CpuRealtimeRuntime"),
        ("cpu_count", "CpuCount"), ("cpu_percent", "CpuPercent"), ("oom_score_adj", "OomScoreAdj"))
    for key, field in numeric:
        if key in definition and definition[key] is not None:
            number(key, field, definition[key])
    if "cpus" in definition and cpu_number(definition["cpus"], "cpus") != (host.get("NanoCpus") or 0):
        mismatch("cpus")
    for key, field in (("oom_kill_disable", "OomKillDisable"), ("init", "Init")):
        if key in definition and bool(definition[key]) != bool(host.get(field)):
            mismatch(key)
    for key, field in (("storage_opt", "StorageOpt"),):
        if key in definition and (definition[key] or {}) != (host.get(field) or {}):
            mismatch(key)
    resources = definition.get("deploy", {}).get("resources", {}) or {}
    for kind, fields in (("limits", (("memory", "Memory"), ("pids", "PidsLimit"))), ("reservations", (("memory", "MemoryReservation"),))):
        values = resources.get(kind) or {}
        for key, field in fields:
            if key in values:
                number("deploy.resources." + kind + "." + key, field, values[key])
    limits = resources.get("limits") or {}
    if "cpus" in limits and cpu_number(limits["cpus"], "deploy.resources.limits.cpus") != (host.get("NanoCpus") or 0):
        mismatch("deploy.resources.limits.cpus")
    reservations = resources.get("reservations") or {}
    for key in ("cpus", "devices", "generic_resources"):
        if reservations.get(key):
            unsupported("deploy.resources.reservations." + key)
    for key in ("gpus", "credential_spec", "use_api_socket", "provider", "blkio_config"):
        if definition.get(key):
            unsupported(key)
    fields = ["Dns", "DnsSearch", "DnsOptions", "ExtraHosts", "Sysctls", "Devices", "DeviceCgroupRules", "Ulimits",
        "PidMode", "IpcMode", "UTSMode", "UsernsMode", "CgroupnsMode", "CgroupParent", "CpusetCpus", "Isolation",
        "Memory", "MemoryReservation", "MemorySwap", "MemorySwappiness", "ShmSize", "PidsLimit", "NanoCpus",
        "CpuPeriod", "CpuQuota", "CpuShares", "CpuRealtimePeriod", "CpuRealtimeRuntime", "CpuCount", "CpuPercent",
        "OomScoreAdj", "OomKillDisable", "Init", "StorageOpt", "DeviceRequests"]
    return {"host": {field: host.get(field) for field in fields},
        "config": {field: config.get(field) for field in ("Hostname", "Domainname", "User", "WorkingDir")}}


def compose_runtime_model(document):
    """Decode Compose's dollar escaping at the rendered runtime boundary.

    `compose config` serializes literal dollars as $$ so its output can be fed
    back into Compose. Engine inspect and application providers contain the
    literal values instead. Decode only explicit service runtime values, once;
    raw sources, interpolation inputs and image defaults remain untouched.
    """
    document = json.loads(json.dumps(document))

    def literal(value):
        if isinstance(value, str):
            return value.replace("$$", "$")
        if isinstance(value, list):
            return [literal(item) for item in value]
        return value

    for service in document.get("services", {}).values():
        environment = service.get("environment")
        if isinstance(environment, dict):
            service["environment"] = {key: literal(value) for key, value in environment.items()}
        elif isinstance(environment, list):
            service["environment"] = literal(environment)
        for field in ("command", "entrypoint"):
            if field in service:
                service[field] = literal(service[field])
        if isinstance(service.get("healthcheck"), dict) and "test" in service["healthcheck"]:
            service["healthcheck"]["test"] = literal(service["healthcheck"]["test"])
    return document


def validate_runtime_topology(deployment, name, definition, metadata):
    """Compare selected saved topology with actual inspect data, without writes."""
    config, host = metadata.get("Config", {}), metadata.get("HostConfig", {})
    validate_runtime_options(definition, metadata)
    image = deployment.docker.json(["image", "inspect", "--format", "{{json .}}", definition["image"]])
    image_config = image.get("Config", {})
    if metadata.get("Image") != image.get("Id"):
        fail("runtime_image_mismatch", "A running service image differs from its saved source; owner apply and new review are required.")
    expected_environment = environment_map(image_config.get("Env", []))
    for key, value in environment_map(definition.get("environment", {})).items():
        if value is None:
            expected_environment.pop(key, None)
        else:
            expected_environment[key] = value
    actual_environment = environment_map(config.get("Env", []))
    if any(actual_environment.get(key) != value for key, value in expected_environment.items()) or any(
            key not in expected_environment and key.casefold() not in PROXY_KEYS for key in actual_environment):
        fail("runtime_environment_mismatch", "A running service environment differs from its saved source/image defaults. No environment values were printed.")
    if bool(host.get("Privileged", False)) != bool(definition.get("privileged", False)) or bool(host.get("ReadonlyRootfs", False)) != bool(definition.get("read_only", False)):
        fail("runtime_security_mismatch", "The running privilege/read-only policy differs from saved source. Setup does not relax serving permissions.")
    for source_field, actual_field in (("cap_add", "CapAdd"), ("cap_drop", "CapDrop"), ("group_add", "GroupAdd")):
        if sorted(str(value).upper() for value in definition.get(source_field, []) or []) != sorted(str(value).upper() for value in host.get(actual_field, []) or []):
            fail("runtime_security_mismatch", "The running capabilities or supplemental groups differ from saved source.")
    def security_options(values):
        return sorted(str(value).removesuffix(":true").removesuffix("=true") for value in values or [])
    if security_options(definition.get("security_opt")) != security_options(host.get("SecurityOpt")):
        fail("runtime_security_mismatch", "The actual security options differ from saved source.")
    expected_health = dict(image_config.get("Healthcheck") or {})
    health = definition.get("healthcheck")
    if health is not None:
        if health.get("disable"):
            expected_health = {"Test": ["NONE"]}
        else:
            for key, field in (("test", "Test"), ("retries", "Retries")):
                if key in health:
                    expected_health[field] = health[key]
            for key, field in (("interval", "Interval"), ("timeout", "Timeout"), ("start_period", "StartPeriod"), ("start_interval", "StartInterval")):
                if key in health:
                    expected_health[field] = duration_nanoseconds(health[key])
    actual_health = {key: value for key, value in (config.get("Healthcheck") or {}).items() if value not in (0, None)}
    expected_health = {key: value for key, value in expected_health.items() if value not in (0, None)}
    if actual_health != expected_health:
        fail("runtime_healthcheck_mismatch", "A saved and running healthcheck differ. Missing healthchecks remain absent; use the owner to reconcile the reviewed source.")
    restart = definition.get("restart", "no")
    restart_name, _, retries = restart.partition(":")
    actual_restart = host.get("RestartPolicy", {})
    if actual_restart.get("Name", "no") != restart_name or (retries and actual_restart.get("MaximumRetryCount") != int(retries)):
        fail("runtime_restart_policy_mismatch", "The actual restart policy differs from saved source. No service was restarted.")
    expected_mounts = []
    for mount in definition.get("volumes", []):
        if not isinstance(mount, dict) or not mount.get("source"):
            fail("runtime_mount_unbound", "Anonymous or unresolved mounts cannot attest the saved runtime. Select their explicit source through the owner before review.")
        kind, source = mount.get("type"), mount["source"]
        if kind == "volume":
            top = deployment.rendered.get("volumes", {}).get(source, {})
            source = top.get("name") or (source if top.get("external") else deployment.project + "_" + source)
        expected_mounts.append((kind, source, normalized_container_path(mount["target"]), bool(mount.get("read_only", False))))
    for kind in ("configs", "secrets"):
        for entry in definition.get(kind, []):
            alias = entry if isinstance(entry, str) else entry.get("source")
            spec = deployment.rendered.get(kind, {}).get(alias, {})
            if not spec.get("file"):
                fail("runtime_protected_source_unbound", "A config or secret has no inspectable saved file. Read it back through its owner before review.")
            expected_mounts.append(("bind", spec["file"], container_file_target(kind, entry), True))
    actual_mounts = [(mount.get("Type"), mount.get("Name") or mount.get("Source"),
        normalized_container_path(mount.get("Destination", "")), not mount.get("RW", False)) for mount in metadata.get("Mounts", [])]
    if sorted(expected_mounts) != sorted(actual_mounts):
        fail("runtime_mount_mismatch", "Actual mounts differ from the saved source. Restore/review the owner's exact mount configuration; no resources were created.")
    expected_labels = definition.get("labels", {})
    actual_labels = config.get("Labels", {}) or {}
    if not isinstance(expected_labels, dict) or any(str(actual_labels.get(key)) != str(value) for key, value in expected_labels.items()):
        fail("runtime_owner_labels_mismatch", "The running owner/service labels differ from saved source. Select the actual deployment owner before review.")
    if actual_labels.get("com.docker.compose.project") != deployment.project or actual_labels.get("com.docker.compose.service") != name:
        fail("runtime_owner_labels_mismatch", "The running container does not belong to the selected source service/project.")
    for field, image_field, actual in (("command", "Cmd", config.get("Cmd") or []), ("entrypoint", "Entrypoint", config.get("Entrypoint") or [])):
        expected = (definition[field] if definition.get(field) is not None else image_config.get(image_field)) or []
        if isinstance(expected, str):
            expected = shlex.split(expected)
        if actual != expected:
            fail("runtime_command_mismatch", "The running command or entrypoint differs from saved source and image defaults.")
    if config.get("WorkingDir", "") != (definition.get("working_dir") or image_config.get("WorkingDir", "")):
        fail("runtime_working_directory_mismatch", "The actual application working directory differs from saved source and image defaults.")
    if config.get("User", "") != (definition.get("user") or image_config.get("User", "")):
        fail("runtime_user_mismatch", "The actual serving user differs from the reviewed source and image. Setup does not change the serving identity.")
    expected_networks = definition.get("networks", {"default": {}})
    expected_names = set()
    for alias in expected_networks:
        spec = deployment.rendered.get("networks", {}).get(alias, {})
        expected_names.add(spec.get("name") or (alias if spec.get("external") else deployment.project + "_" + alias))
    if definition.get("network_mode") not in (None, ""):
        if host.get("NetworkMode") != definition["network_mode"]:
            fail("runtime_network_mismatch", "The running network mode differs from saved source.")
    elif expected_names != set(metadata.get("NetworkSettings", {}).get("Networks", {})):
        fail("runtime_network_mismatch", "The running network attachment inventory differs from saved source.")
    bindings = host.get("PortBindings") or {}
    expected_ports = {}
    for port in definition.get("ports", []):
        if not isinstance(port, dict) or not port.get("published"):
            fail("runtime_port_unbound", "Dynamic or unresolved published ports need an explicit owner source before host attestation.")
        key = str(port["target"]) + "/" + port.get("protocol", "tcp")
        expected_ports.setdefault(key, []).append((port.get("host_ip", ""), str(port["published"])))
    actual_ports = {key: [(value.get("HostIp", ""), value.get("HostPort", "")) for value in values or []] for key, values in bindings.items()}
    if expected_ports != actual_ports:
        fail("runtime_port_mismatch", "The actual published port bindings differ from saved source.")


def duration_nanoseconds(value):
    if isinstance(value, (int, float)):
        return int(value)
    # This parses only the canonical duration emitted by Compose, never YAML.
    units = {"ns": 1, "us": 1000, "µs": 1000, "ms": 1000000, "s": 1000000000, "m": 60000000000, "h": 3600000000000}
    terms = re.findall(r"([0-9]+(?:\.[0-9]+)?)(ns|us|µs|ms|s|m|h)", value)
    if not terms or "".join(number + unit for number, unit in terms) != value:
        fail("runtime_healthcheck_duration", "A rendered healthcheck duration cannot be compared safely. Restore a supported canonical Compose duration.")
    return int(sum(float(number) * units[unit] for number, unit in terms))


class Deployment:
    def __init__(self, args, docker):
        self.args, self.docker = args, docker
        self.roles = {role: getattr(args, role.replace("-", "_") + "_service", None) or role for role in SERVICE_NAMES}
        self.intent = getattr(args, "intent", "verify-existing")
        self.legacy = self.intent == "legacy-initializer"
        self.container, self.metadata = (None, None)
        if args.container or not args.compose_file:
            self.container, self.metadata = choose_container(docker, args.container)
        labels = (self.metadata or {}).get("Config", {}).get("Labels", {}) or {}
        bundle = None
        bundle_path = Path(__file__).resolve().parent / "setup-release.json"
        if not self.metadata and not args.compose_file and bundle_path.is_file():
            try:
                bundle = json.loads(read_bytes(bundle_path))
            except (ValueError, UnicodeError):
                fail("release_metadata", "The extracted setup release metadata is invalid. Re-extract the verified deployment archive.")
            if (bundle.get("schemaVersion") != 1 or bundle.get("composeFile") != "docker-compose.release.yml"
                    or bundle.get("manager") != "release-compose" or bundle.get("projectName", "rateldesk") != "rateldesk"
                    or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?", bundle.get("version", ""))
                    or bundle.get("image") != f"ghcr.io/bostontechnologies/rateldesk-mcp-http:{bundle['version']}"):
                fail("release_metadata", "The extracted setup release metadata is unsupported. Re-extract the verified archive.")
            existing_project = docker.run(["ps", "--all", "--filter", "label=com.docker.compose.project=" + bundle.get("projectName", "rateldesk"),
                                           "--format", "{{json .ID}}"], maximum=8192)
            if existing_project.strip():
                fail("deployment_manager_unknown", "This project already exists without unambiguous RatelDesk source metadata. Supply its actual --container or ordered --compose-file/--project and deployment owner; the release bundle cannot replace an unknown owner's stack.")
        found_project = labels.get("com.docker.compose.project")
        if args.project and found_project and args.project != found_project:
            fail("project_mismatch", "The selected container and supplied project disagree. Select the actual saved deployment.")
        self.project = args.project or found_project or (bundle or {}).get("projectName", "rateldesk" if bundle else None)
        self.sources = args.compose_file or metadata_paths(labels.get("com.docker.compose.project.config_files")) or ([str(bundle_path.parent / bundle["composeFile"])] if bundle else [])
        if not self.sources or not self.project:
            fail("sources_missing", "No saved MCP deployment was found. Supply its ordered --compose-file paths and --project; use --image only when the saved service has no image.")
        if not re.fullmatch(r"[a-z0-9][a-z0-9_-]{0,127}", self.project):
            fail("project_invalid", "Supply the actual Docker Compose project name.")
        if len(self.sources) > 16 or len(args.env_file) > 16:
            fail("sources_oversized", "At most 16 ordered Compose and 16 interpolation files are supported.")
        self.sources = [Path(item) for item in self.sources]
        for path in self.sources:
            file_record(path)
        directory = args.project_directory or labels.get("com.docker.compose.project.working_dir") or str(self.sources[0].parent)
        self.directory = Path(safe_label(directory, 4096))
        if not self.directory.is_absolute() or not self.directory.is_dir():
            fail("working_directory", "Supply --project-directory with the existing saved working directory.")
        self.env_files = [Path(item) for item in (args.env_file or metadata_paths(labels.get("com.docker.compose.project.environment_file")))]
        if not self.env_files and (self.directory / ".env").is_file():
            self.env_files = [self.directory / ".env"]
        for path in self.env_files:
            try:
                file_record(path)
            except Prerequisite:
                fail("compose_env_file_unreadable", "A selected interpolation env file is missing, unsafe or unreadable. Restore its regular saved file and permissions before setup; no values were printed.")
        saved_manager = None
        checkpoint = Path(args.output_directory or self.directory / f".rateldesk-http-mcp-{self.project}") / "launcher-state.json"
        if checkpoint.is_file():
            saved = read_private_json(checkpoint)
            if args.verify or args.apply:
                self.intent = saved.get("intent", self.intent)
                self.legacy = self.intent == "legacy-initializer"
            if (args.verify or args.apply) and isinstance(docker, Docker):
                docker.check_selection(saved)
            if saved.get("project") == self.project and saved.get("manager") in ("release-compose", "external-compose"):
                saved_manager = saved["manager"]
        self.manager = args.manager or labels.get(MANAGER_LABEL) or saved_manager or (bundle or {}).get("manager")
        if self.manager not in ("release-compose", "external-compose"):
            if not sys.stdin.isatty() or not sys.stdout.isatty():
                fail("deployment_manager_unknown", "Supply --manager external-compose for a deployment manager, or --manager release-compose for an explicitly release-owned bundle. Unknown ownership cannot authorize apply.")
            print("Who applies this saved deployment?")
            print("  1) Existing deployment manager — retain saved source ownership")
            print("  2) Release-owned Compose bundle — review and apply on this host")
            choice = input("Deployment owner (1 or 2): ")
            self.manager = {"1": "external-compose", "2": "release-compose"}.get(choice)
            if self.manager is None:
                fail("deployment_manager_unknown", "Select the actual owner before setup.")
        self.output = private_directory(args.output_directory or self.directory / f".rateldesk-http-mcp-{self.project}")
        if bundle:
            launcher_env = self.output / "launcher.env"
            wanted = f"RATELDESK_VERSION={bundle['version']}\n".encode()
            if launcher_env.exists() and read_bytes(launcher_env) != wanted:
                fail("release_metadata", "The saved release selection changed. Review the existing private setup directory before using another release archive.")
            if not launcher_env.exists():
                write_private(launcher_env, wanted)
            self.env_files.append(launcher_env)
        state_path = self.output / "launcher-state.json"
        if (args.verify or args.apply) and not args.compose_file and state_path.is_file():
            saved = read_private_json(state_path)
            exported = self.output / "deployment.json"
            if self.sources == [exported] and saved.get("composeFiles"):
                if hashlib.sha256(read_bytes(exported, MAX_BYTES)).hexdigest() != saved.get("deploymentSha256"):
                    fail("approved_export_changed", "The applied complete deployment changed after approval. Run ordinary setup to review it.")
                self.sources = [Path(path) for path in saved["composeFiles"]]
                self.env_files = [Path(path) for path in saved.get("envFiles", [])]
                for path in self.sources + self.env_files:
                    file_record(path)
        selected_is_mcp = labels.get("com.docker.compose.service") == self.roles["mcp-http"] or "rateldesk-mcp-http" in (self.metadata or {}).get("Config", {}).get("Image", "")
        self.image = args.image or ((self.metadata or {}).get("Config", {}).get("Image") if selected_is_mcp else None) or (bundle or {}).get("image")
        approved_addition = read_private_json(state_path).get("mcpProposed", False) if state_path.is_file() else False
        self.foundation = bool(bundle or args.image or labels.get("com.docker.compose.service") == "web" or approved_addition)
        self.compose = ["compose", "--project-directory", str(self.directory), "--project-name", self.project]
        for path in self.env_files or [Path("/dev/null")]:
            self.compose += ["--env-file", str(path)]
        for path in self.sources:
            self.compose += ["-f", str(path)]

    def snapshot(self, verify=False):
        for path in self.sources:
            check_compose_dependencies(path)
        initial = [file_record(path) for path in self.sources + self.env_files]
        if self.args.ca_file:
            initial.append(file_record(self.args.ca_file))
        unresolved = self.docker.json(self.compose + ["config", "--no-env-resolution", "--format", "json"])
        source_document = self.docker.json(self.compose + ["config", "--no-env-resolution", "--no-interpolate", "--no-path-resolution", "--format", "json"])
        write_private(self.output / ".compose-source.json", source_document)
        source_paths = [Path(item["path"]) for item in initial]
        required_paths = set(source_paths)
        optional_paths = set()
        for service_name, service in unresolved.get("services", {}).items():
            for entry in service.get("env_file", []):
                path = Path(entry if isinstance(entry, str) else entry.get("path", ""))
                required = isinstance(entry, str) or entry.get("required", True)
                source_paths.append(path)
                if required:
                    required_paths.add(path)
                    optional_paths.discard(path)
                elif path not in required_paths:
                    optional_paths.add(path)
            for mount in service.get("volumes", []):
                target = mount.get("target", "") if isinstance(mount, dict) else ""
                if mount.get("type") == "bind" and (target.startswith("/app/appsettings")
                        or service_name == "mcp-config-init" and target == "/input/config.json"
                        or service_name == "mcp-http" and target.endswith(".json")):
                    source_paths.append(Path(mount.get("source", "")))
        # Source-preserving rendering is authoritative for provider references.
        # Some Compose builds resolve env files on the interpolated path even
        # with --no-env-resolution; never let that omit a required source hash.
        raw_providers = [(entry if isinstance(entry, str) else entry["path"],
                          isinstance(entry, str) or entry.get("required", True))
                         for service in source_document.get("services", {}).values()
                         for entry in service.get("env_file", [])]
        if len(raw_providers) > MAX_FILES:
            fail("sources_oversized", "Too many service source providers exist. Consolidate the saved deployment before setup.")
        if raw_providers:
            name = "rateldesk-source-paths-" + uuid.uuid4().hex[:12]
            probe = self.output / ".optional-source-paths.json"
            image = next((service.get("image") for service in unresolved.get("services", {}).values() if service.get("image")), None)
            if not image:
                fail("image_missing", "Build the saved serving images before effective source discovery.")
            write_private(probe, {"services": {name: {"image": image, "environment": {
                f"RATELDESK_PROVIDER_PATH_{index}": value for index, (value, _) in enumerate(raw_providers)}}}})
            try:
                resolved = self.docker.json(self.compose + ["-f", str(probe), "config", "--no-env-resolution", "--format", "json"])
                if not self.legacy:
                    resolved = compose_runtime_model(resolved)
                paths = environment_map(resolved["services"][name]["environment"])
                for index, (_, required) in enumerate(raw_providers):
                    value = paths.get(f"RATELDESK_PROVIDER_PATH_{index}")
                    if not value:
                        fail("optional_source_path", "An optional provider path resolves empty. Correct its winning interpolation source before setup.")
                    path = Path(os.path.abspath(self.directory / value))
                    source_paths.append(path)
                    if required:
                        required_paths.add(path)
                        optional_paths.discard(path)
                    elif path not in required_paths:
                        optional_paths.add(path)
            finally:
                probe.unlink(missing_ok=True)
        # Hash env_file and protected binds BEFORE the fully resolved render;
        # a change in the render window must not become an approved generation.
        provider_initial = [file_record(path, optional=path in optional_paths) for path in dict.fromkeys(source_paths)]
        proposal = self.docker.json(self.compose + ["config", "--format", "json"])
        if not self.legacy:
            proposal = compose_runtime_model(proposal)
        services = proposal.get("services", {})
        for role in SERVICE_NAMES:
            if self.roles[role] in services:
                continue
            explicit = getattr(self.args, role.replace("-", "_") + "_service", None)
            if explicit:
                fail("service_missing", "The explicitly selected service role is absent from the saved source.")
            family = {"api": "rateldesk-api", "web": "rateldesk-web", "mcp-http": "rateldesk-mcp-http"}[role]
            matches = [name for name, service in services.items() if family in service.get("image", "")]
            if len(matches) == 1:
                self.roles[role] = matches[0]
            elif not self.legacy:
                fail("service_role_ambiguous", "Select custom service names with --api-service, --web-service and --mcp-http-service; setup never renames saved services.")
        if len(set(self.roles.values())) != len(self.roles):
            fail("service_role_ambiguous", "API, Web and MCP must select distinct existing service names.")
        self.rendered = proposal
        if self.roles["mcp-http"] not in services:
            image = self.image
            if not image:
                api_image = services.get("api", {}).get("image", "")
                prefix = "ghcr.io/bostontechnologies/rateldesk-api:"
                if api_image.startswith(prefix):
                    image = "ghcr.io/bostontechnologies/rateldesk-mcp-http:" + api_image[len(prefix):]
                elif api_image.startswith("ghcr.io/bostontechnologies/rateldesk-api@sha256:"):
                    metadata = self.docker.json(["image", "inspect", "--format", "{{json .}}", api_image])
                    version = (metadata.get("Config", {}).get("Labels", {}) or {}).get("org.opencontainers.image.version", "")
                    if re.fullmatch(r"v?[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?", version):
                        image = "ghcr.io/bostontechnologies/rateldesk-mcp-http:" + version.removeprefix("v")
            if not self.foundation or not image:
                fail("mcp_service_missing", "The saved sources have no mcp-http service or exact release image. Use the verified matching deployment archive, or include the existing MCP source.")
            # This is an explicitly reviewed service addition, not a guessed
            # running configuration. The first Termina page still asks the URL;
            # no placeholder origin/resource is rendered or persisted.
            services[self.roles["mcp-http"]] = {"image": image, "environment": {}, "volumes": []}
            proposal["x-rateldesk-mcp-proposed"] = True
            self.image = image
        self.image = self.image or services[self.roles["mcp-http"]].get("image")
        if not self.image:
            fail("image_missing", "Build the existing MCP image, then supply --image with that exact local image. Setup never downloads packages or invents an image family.")
        if services[self.roles["mcp-http"]].get("image") != self.image:
            fail("image_source_mismatch", "The saved MCP image and selected container disagree. Restore the actual source before setup.")
        image_environment, image_ids, protected, image_entrypoints, image_commands, image_working_directories = {}, {}, {}, {}, {}, {}
        self.provider_source_records = {}
        for name in SERVICE_NAMES:
            service_name = self.roles[name]
            if service_name not in services:
                fail("service_missing", f"The saved deployment has no {name} service. Supply the complete saved deployment before host setup.")
            image = services[service_name].get("image")
            if not image:
                fail("image_missing", f"The saved {name} service needs a built local image reference before setup.")
            image_bytes = self.docker.run(["image", "inspect", "--format", "{{json .}}", image], optional=True)
            if image_bytes is None:
                official = any(re.fullmatch(re.escape(prefix) + r"(?::[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?|@sha256:[a-f0-9]{64})", image)
                               for prefix in ("ghcr.io/bostontechnologies/rateldesk-api", "ghcr.io/bostontechnologies/rateldesk-web",
                                              "ghcr.io/bostontechnologies/rateldesk-mcp-http"))
                if not official or self.args.preflight or not self.legacy:
                    fail("image_unavailable", "The exact serving image is not available locally. Build the saved image, or pull the verified exact release image before read-only preflight. Setup never substitutes another version.")
                self.docker.run(["pull", image], maximum=65536)
                image_bytes = self.docker.run(["image", "inspect", "--format", "{{json .}}", image])
            metadata = json.loads(image_bytes)
            image_ids[name] = metadata["Id"]
            image_environment[name] = relevant_environment(metadata.get("Config", {}).get("Env", []))
            image_entrypoints[name] = metadata.get("Config", {}).get("Entrypoint") or []
            image_commands[name] = metadata.get("Config", {}).get("Cmd") or []
            image_working_directories[name] = metadata.get("Config", {}).get("WorkingDir", "")
            existing_id, _ = (None, None) if self.legacy else unique_container(self.docker, self.project, service_name, running=True)
            if not existing_id and not self.legacy:
                fail("runtime_missing", "Existing deployment inspection needs its running serving containers. Restore them through their owner, or select an explicit Advanced provisioning workflow.")
            protected[name] = self.configuration_providers(name, services[service_name], image, source_paths, existing_id,
                                                           image_environment=image_environment[name], image_command=image_commands[name],
                                                           image_working_directory=image_working_directories[name])
        records = [file_record(path, optional=path in optional_paths) for path in dict.fromkeys(source_paths)]
        if len(records) > MAX_FILES:
            fail("sources_oversized", "The effective deployment has too many protected providers. Consolidate the saved sources before setup.")
        if any(file_record(item["path"], optional=item.get("optional", False)) != item for item in provider_initial):
            fail("sources_changed", "Saved sources changed during rendering. Rerun setup with stable sources.")
        if any(file_record(item["path"]) != item for item in self.provider_source_records.values()):
            fail("sources_changed", "A protected provider changed during inspection. Rerun setup with stable sources.")
        proposal["x-rateldesk-source-files"] = records
        proposal["x-rateldesk-service-roles"] = self.roles
        proposal["x-rateldesk-image-environment"] = image_environment
        proposal["x-rateldesk-image-ids"] = image_ids
        proposal["x-rateldesk-image-entrypoints"] = image_entrypoints
        proposal["x-rateldesk-image-commands"] = image_commands
        proposal["x-rateldesk-image-working-directories"] = image_working_directories
        proposal["x-rateldesk-protected-configuration"] = protected
        proposal["x-rateldesk-deployment-manager"] = self.manager
        proposal["x-rateldesk-project-directory"] = str(self.directory)
        proposal["x-rateldesk-project-name"] = self.project
        proposal["x-rateldesk-compose-files"] = [str(path) for path in self.sources]
        proposal["x-rateldesk-interpolation-files"] = [str(path) for path in self.env_files]
        proposal["x-rateldesk-docker-selection"] = getattr(self.docker, "selection", {})
        proposal["x-rateldesk-compose-version"] = getattr(self.docker, "compose_version", None)
        proposal["x-rateldesk-docker-daemon-identity"] = getattr(self.docker, "daemon_identity", None)
        if self.legacy:
            proposal["x-rateldesk-legacy-export-recipe"] = legacy_recipe_identity(self,
                hashlib.sha256(read_bytes(self.output / ".compose-source.json", MAX_BYTES)).hexdigest())
        proposal["x-rateldesk-mount-ancestor-checks"] = inspect_mount_ancestors(self.docker, proposal, self.image) if self.legacy else []
        self.capture_secret_mounts(proposal)
        self.validate_resume_sources(records, verify)
        if getattr(self, "imported_export_records", []):
            proposal["x-rateldesk-approved-export-files"] = self.imported_export_records
        write_private(self.output / "effective-deployment.json", proposal)
        write_private(self.output / "source-state.json", {"sources": records})
        if verify or not self.legacy:
            self.observe_runtime(proposal)
        if not self.legacy:
            _, payload_identity = native_executable(self.args)
            proposal["x-rateldesk-native-payload-identity"] = payload_identity
            inventory = read_private_json(self.output / "runtime-environment.json").get("x-rateldesk-runtime-inventory", {})
            proposal["x-rateldesk-runtime-baseline-sha256"] = hashlib.sha256(json.dumps(stable_runtime_inventory(inventory), sort_keys=True, separators=(",", ":")).encode()).hexdigest()
            write_private(self.output / "effective-deployment.json", proposal)
        self.proposal = proposal
        return proposal

    def capture_secret_mounts(self, proposal):
        references = selected_secret_references(proposal)
        if len(references) > 8:
            fail("secret_reference_count", "The MCP deployment exceeds the bounded protected-reference count. Consolidate its explicit identity sources before setup.")
        mounts = []
        for path in sorted(references):
            safe_label(path, 4096)
            if not path.startswith("/") or ".." in Path(path).parts or path.endswith("docker.sock"):
                fail("secret_reference", "Use an explicit absolute protected secret file inside the MCP container, without traversal or Docker access.")
            candidates = []
            for mount in proposal["services"][self.roles["mcp-http"]].get("volumes", []):
                target = normalized_container_path(mount.get("target", ""))
                if target and (path == target or path.startswith(target + "/")):
                    source = mount.get("source")
                    if mount.get("type") == "volume":
                        source = proposal.get("volumes", {}).get(source, {}).get("name") or source
                    candidates.append((len(target), {"type": mount.get("type"), "source": source, "target": target}))
            for secret in proposal["services"][self.roles["mcp-http"]].get("secrets", []):
                name = secret if isinstance(secret, str) else secret.get("source")
                target = container_file_target("secrets", secret)
                file = proposal.get("secrets", {}).get(name, {}).get("file")
                if path == target and file:
                    candidates.append((len(target), {"type": "bind", "source": file, "target": target}))
            if not candidates:
                fail("secret_reference_mount", "An adopted downstream secret reference has no readable persistent mount. Restore its saved MCP secret mount before approval.")
            descriptor = max(candidates, key=lambda item: item[0])[1]
            if descriptor["type"] not in ("bind", "volume") or descriptor["target"] in (HOST_TARGET, CLIENT_TARGET, "/", "/app"):
                fail("secret_reference_mount", "The downstream secret must use its own bounded protected mount. Host/client stores and whole application directories cannot be inherited as identities.")
            descriptor["path"] = path
            descriptor["sha256"] = self.secret_digest(descriptor)
            mounts.append(descriptor)
        self.secret_mounts = mounts
        proposal["x-rateldesk-secret-mounts"] = mounts

    def secret_digest(self, descriptor):
        if not self.legacy:
            identifier, _ = unique_container(self.docker, self.project, self.roles["mcp-http"], running=True)
            if not identifier:
                fail("runtime_missing", "The existing MCP container must be running to validate its protected file as its serving identity.")
            result = self.docker.run(["exec", identifier, "/bin/sh", "-ec",
                'test -f "$1"; test -r "$1"; test "$(wc -c < "$1")" -le 16384; sha256sum "$1"',
                "secret-hash", descriptor["path"]], maximum=8192)
            digest = result.decode().split(" ", 1)[0]
            if not re.fullmatch(r"[a-f0-9]{64}", digest):
                fail("secret_reference_hash", "The selected protected file is not readable by the actual serving identity.")
            return digest
        result = self.docker.run(["run", "--rm", "--pull", "never", "--network", "none", "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges", "--user", "10001:10001", "--mount", mount_argument(descriptor) + ",readonly",
            "--entrypoint", "/bin/sh", self.image, "-ec",
            'test -f "$1"; test "$(wc -c < "$1")" -le 16384; sha256sum "$1"', "secret-hash", descriptor["path"]], maximum=8192)
        digest = result.decode().split(" ", 1)[0]
        if not re.fullmatch(r"[a-f0-9]{64}", digest):
            fail("secret_reference_hash", "The protected secret could not be safely hashed as the serving identity. Restore its readable private file before approval.")
        return digest

    def configuration_providers(self, name, service, image, source_paths, container=None, image_environment=None, image_command=None, image_working_directory="/app"):
        environment = effective_environment(image_environment or [], service.get("environment", {}))
        deployment_environment = environment.get("dotnet_environment", environment.get("aspnetcore_environment", "Production"))
        command = command_configuration(service.get("command", image_command or []))
        validate_content_root(name, service, environment, command, image_working_directory)
        deployment_environment = command.get("environment", deployment_environment)
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", deployment_environment):
            fail("environment_name", "The saved .NET environment name is invalid. Restore the serving environment before setup.")
        replacements = {}
        protected_files = {}
        for kind in ("configs", "secrets"):
            for entry in service.get(kind, []):
                alias = entry if isinstance(entry, str) else entry.get("source")
                target = container_file_target(kind, entry)
                definition = getattr(self, "rendered", {}).get(kind, {}).get(alias, {})
                protected_files[target] = definition.get("file")
                if target.startswith("/app/appsettings") and target.endswith(".json"):
                    if not definition.get("file"):
                        fail("protected_provider_unreadable", "A selected appsettings config/secret lacks a readable saved file source. Restore/export that protected provider before setup.")
                    replacements[target] = Path(definition["file"])
        for mount in service.get("volumes", []):
            target = mount.get("target", "") if isinstance(mount, dict) else ""
            if target in ("/app", "/config"):
                fail("protected_provider", "A whole application/config directory is mounted. Export its explicit private appsettings files before setup.")
            if target.startswith("/app/appsettings") and target.endswith(".json"):
                if mount.get("type") != "bind":
                    fail("protected_provider", "An appsettings volume has no saved readable source. Export its private provider before setup.")
                replacements[target] = Path(mount.get("source", ""))
        # The MCP outbound provider can live in a named config volume. Its actual
        # serving bytes are observed rather than guessed from the initial input.
        provider_paths = ["/app/appsettings.json", f"/app/appsettings.{deployment_environment}.json"]
        mcp_config = None
        selected_json_path = None
        temporary = None
        if container is None and not self.legacy:
            fail("runtime_missing", "A running service is required for deployment-read-only provider inspection. No inspection container was created.")
        if container is None:
            identifier = self.docker.run(["create", "--network", "none", "--entrypoint", "/bin/true", image], maximum=256).decode().strip()
            if not re.fullmatch(r"[a-f0-9]{12,64}", identifier):
                fail("image_inspection", "The saved image could not be inspected safely. Restore its local image and retry.")
            temporary = container = identifier
        providers = []
        try:
            for index in range(3 if name == "mcp-http" else 2):
                if index == 2:
                    selected = command.get("helpdesk:mcp:configurationpath",
                        environment.get("helpdesk__mcp__configurationpath", selected_json_path))
                    mcp_config = selected if isinstance(selected, str) and selected.strip() else environment.get("rateldesk_mcp_config")
                    if not mcp_config:
                        continue
                    safe_label(mcp_config, 4096)
                    if not mcp_config.startswith("/"):
                        fail("outbound_provider", "The selected MCP configuration must have an explicit absolute container path. Restore its owning provider before setup.")
                    mcp_config = normalized_container_path(mcp_config)
                    provider_paths.append(mcp_config)
                    if mcp_config in protected_files:
                        if not protected_files[mcp_config]:
                            fail("protected_provider_unreadable", "The selected MCP config/secret lacks a readable saved file source. Restore/export its owning protected provider before approval; image/default content cannot replace it.")
                        replacements[mcp_config] = Path(protected_files[mcp_config])
                    for mount in service.get("volumes", []):
                        target = normalized_container_path(mount.get("target", ""))
                        if mount.get("type") == "bind" and target == mcp_config:
                            replacements[mcp_config] = Path(mount["source"])
                        elif mount.get("type") == "bind" and mcp_config.startswith(target + "/"):
                            relative = mcp_config[len(target) + 1:]
                            if ".." in Path(relative).parts:
                                fail("protected_provider", "A protected provider escapes its mount. Restore its explicit absolute configuration path.")
                            replacements[mcp_config] = Path(mount["source"]) / relative
                    initializer = getattr(self, "rendered", {}).get("services", {}).get("mcp-config-init", {})
                    for mount in initializer.get("volumes", []):
                        if mount.get("type") == "bind" and mount.get("target") == "/input/config.json":
                            replacements.setdefault(mcp_config, Path(mount["source"]))
                path = provider_paths[index]
                if index == 2 and not temporary and not self.legacy:
                    self.docker.run(["exec", container, "/bin/sh", "-ec", 'test -f "$1"; test -r "$1"; test "$(wc -c < "$1")" -le 16384', "provider-readability", path], maximum=1024)
                if path in replacements:
                    host_path = replacements[path]
                    try:
                        record = file_record(host_path)
                    except Prerequisite:
                        fail("protected_provider_unreadable", "A selected protected provider source is missing, unsafe or unreadable. Restore its regular saved file before approval; image/default content cannot replace it.")
                    source_paths.append(host_path)
                    content = read_bytes(host_path)
                    if hashlib.sha256(content).hexdigest() != record["sha256"]:
                        fail("sources_changed", "A protected provider changed while reading. Retry with stable sources.")
                    self.provider_source_records[str(host_path)] = record
                    origin = str(host_path)
                    if not self.legacy and not temporary:
                        # A bind can retain an old inode after an atomic host
                        # replacement. Inspect the actual serving bytes as well
                        # as the owner's current source, never substitute one
                        # for the other in a full-host verification receipt.
                        self.docker.run(["exec", container, "/bin/sh", "-ec",
                            'test -f "$1"; test -r "$1"', "provider-readability", path], maximum=1024)
                        observed_path = self.output / f".mounted-provider-{name}-{index}.json"
                        observed_path.unlink(missing_ok=True)
                        copied = self.docker.run(["cp", f"{container}:{path}", str(observed_path)], maximum=4096)
                        if copied is None or observed_path.is_symlink() or not observed_path.is_file():
                            fail("runtime_provider_unreadable", "The actual serving provider cannot be read safely. Restore the selected owner mapping before host review.")
                        observed_path.chmod(0o600)
                        actual_content = read_bytes(observed_path)
                        if hashlib.sha256(actual_content).hexdigest() != record["sha256"]:
                            fail("runtime_provider_mismatch", "The actual mounted configuration differs from the owner's saved file, including a possible stale bind inode. Reconcile that source/runtime generation through the owner, then review again; no service was changed.")
                else:
                    destination = self.output / f".provider-{name}-{index}.json"
                    destination.unlink(missing_ok=True)
                    if not self.legacy:
                        presence = self.docker.run(["exec", container, "/bin/sh", "-ec",
                            'if test -e "$1"; then if test -f "$1" && test -r "$1"; then printf present; else printf unreadable; fi; else printf absent; fi',
                            "provider-presence", path], maximum=64)
                        if presence == b"absent" and index < 2:
                            continue
                        if presence != b"present":
                            fail("runtime_provider_unreadable", "A selected serving provider is absent or unreadable by its actual user. Restore its source/mapping before review.")
                        copied = self.docker.run(["cp", f"{container}:{path}", str(destination)], maximum=4096)
                    else:
                        copied = self.docker.run(["cp", f"{container}:{path}", str(destination)], optional=True, maximum=4096)
                    if copied is None:
                        if name == "mcp-http" and path == mcp_config:
                            # Named config volume before first start: find its
                            # exact saved initializer bind; do not adopt a key
                            # from an unrelated deployment.
                            _, current = unique_container(self.docker, self.project, self.roles["mcp-http"])
                            current_id = (current or {}).get("Id")
                            if current_id and temporary:
                                copied = self.docker.run(["cp", f"{current_id}:{path}", str(destination)], optional=True, maximum=4096)
                            if copied is None:
                                fail("outbound_provider", "The protected MCP configuration is unavailable before startup. Restore its saved initializer input or start the existing stack, then rerun setup.")
                        else:
                            continue
                    if destination.is_symlink() or not destination.is_file():
                        fail("protected_provider", "A protected provider is not a regular file. Restore its saved source before setup.")
                    destination.chmod(0o600)
                    content = read_bytes(destination)
                    origin = f"{'runtime' if not temporary else 'image'}:{name}:{path}"
                try:
                    parsed = json.loads(content)
                    if not isinstance(parsed, dict):
                        raise ValueError()
                except (ValueError, UnicodeError):
                    fail("protected_provider", "A protected JSON provider is malformed. Correct its source before setup.")
                if index < 2:
                    selected = json_configuration_value(parsed, "Helpdesk:Mcp:ConfigurationPath")
                    if selected is not None:
                        selected_json_path = selected
                providers.append({"path": path, "content": content.decode("utf-8-sig"),
                                  "sha256": hashlib.sha256(content).hexdigest(), "source": origin})
        finally:
            if temporary:
                self.docker.run(["rm", temporary], maximum=256)
        return providers

    def observe_runtime(self, proposal):
        observed = {**json.loads(json.dumps(proposal)), "services": {}, "x-rateldesk-image-ids": {}, "x-rateldesk-protected-configuration": {},
                    "x-rateldesk-image-working-directories": proposal.get("x-rateldesk-image-working-directories", {}),
                    "x-rateldesk-observed-at-utc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")}
        for name in SERVICE_NAMES:
            service_name = self.roles[name]
            identifier, metadata = unique_container(self.docker, self.project, service_name, running=True)
            if not identifier:
                fail("runtime_missing", f"A running {name} container is required. Apply the reviewed deployment, then rerun --verify.")
            image = metadata.get("Image")
            if image != proposal["x-rateldesk-image-ids"][name]:
                fail("runtime_image_mismatch", f"The running {name} image differs from the reviewed deployment. Recreate that service with the approved image before verification.")
            service = json.loads(json.dumps(proposal["services"][service_name]))
            service.update({"environment": environment_map(relevant_environment(metadata.get("Config", {}).get("Env", []))),
                       "image": metadata.get("Config", {}).get("Image"),
                       "command": metadata.get("Config", {}).get("Cmd") or [],
                       "entrypoint": metadata.get("Config", {}).get("Entrypoint") or [],
                       "working_dir": metadata.get("Config", {}).get("WorkingDir", ""),
                       "volumes": [{"type": mount.get("Type"), "source": mount.get("Name") or mount.get("Source"),
                                    "target": mount.get("Destination"), "read_only": not mount.get("RW", False)}
                                   for mount in metadata.get("Mounts", [])]})
            # Keep source mount aliases while validating actual inspect mounts independently.
            if not self.legacy:
                actual_environment = service["environment"]
                service = json.loads(json.dumps(proposal["services"][service_name]))
                service["environment"] = actual_environment
            observed["services"][service_name] = service
            observed["x-rateldesk-image-ids"][name] = image
            observed["x-rateldesk-protected-configuration"][name] = self.configuration_providers(name, service, image, [], identifier,
                image_working_directory=metadata.get("Config", {}).get("WorkingDir", ""))
        if not self.legacy:
            observed["x-rateldesk-runtime-inventory"] = self.runtime_inventory(proposal)
            for name, definition in proposal["services"].items():
                if name not in observed["services"]:
                    observed["services"][name] = json.loads(json.dumps(definition))
            observed["x-rateldesk-runtime-topology-validated"] = True
            write_private(self.output / "runtime-environment.json", observed)
            return
        actual_mounts = observed["services"]["mcp-http"]["volumes"]
        storage = {}
        for purpose, target in (("host", HOST_TARGET), ("client", CLIENT_TARGET)):
            matching = [mount for mount in actual_mounts if mount.get("target") == target]
            if len(matching) != 1 or matching[0].get("read_only"):
                fail("runtime_storage_missing", "The applied MCP container lacks its reviewed writable persistent stores. Recreate it from the complete reviewed document before verification.")
            mount = matching[0]
            storage[purpose] = {"type": mount["type"], "source": mount["source"], "target": target,
                                "readOnly": False, "private": True, "uid": 10001}
        observed["x-rateldesk-storage"] = storage
        observed["x-rateldesk-mount-ancestor-checks"] = inspect_mount_ancestors(self.docker, observed, self.image)
        if (self.output / "launcher-state.json").is_file():
            expected_storage = read_private_json(self.output / "launcher-state.json").get("storage", {})
            if storage != expected_storage:
                fail("runtime_storage_mismatch", "The running MCP stores differ from the approved mounts. Recreate it from the reviewed complete document before verification.")
        write_private(self.output / "runtime-environment.json", observed)

    def runtime_inventory(self, proposal):
        inventory = {}
        selected_services = set(proposal.get("services", {}))
        if len(selected_services) > 64:
            fail("runtime_inventory_oversized", "The host inventory exceeds the bounded service limit; select a supported complete deployment before review.")
        actual_services = set()
        rows = self.docker.run(["ps", "--all", "--filter", "label=com.docker.compose.project=" + self.project,
            "--format", "{{json .ID}}"], maximum=8192)
        for row in rows.splitlines():
            identifier = json.loads(row)
            metadata = self.docker.inspect(identifier)
            name = (metadata.get("Config", {}).get("Labels", {}) or {}).get("com.docker.compose.service")
            if name:
                actual_services.add(name)
        if actual_services != selected_services:
            fail("runtime_service_inventory_mismatch", "The actual project service names/count differ from the complete saved source, including stopped/one-shot services. Review the owner inventory; no service was added or removed.")
        for name in proposal.get("services", {}):
            identifier, metadata = unique_container(self.docker, self.project, name, running=False)
            if not identifier:
                fail("runtime_missing", "Every defined service in the adopted deployment must have one running container; no service was started.")
            validate_runtime_topology(self, name, proposal["services"][name], metadata)
            config, state = metadata.get("Config", {}), metadata.get("State", {})
            inventory[name] = {"containerId": identifier, "startedAt": state.get("StartedAt"),
                "restartCount": metadata.get("RestartCount", 0), "state": state.get("Status"), "exitCode": state.get("ExitCode"),
                "healthStatus": state.get("Health", {}).get("Status", "absent"),
                "healthcheck": config.get("Healthcheck"), "labels": config.get("Labels", {}),
                # Engine builds this slice from an unordered mount map. Bind
                # every record field, without treating its iteration order as
                # a source/runtime change. Operator source order is untouched.
                "mounts": sorted(metadata.get("Mounts", []), key=lambda mount: json.dumps(mount, sort_keys=True, separators=(",", ":"))),
                "networks": metadata.get("NetworkSettings", {}).get("Networks", {}),
                "ports": metadata.get("HostConfig", {}).get("PortBindings", {}),
                "restartPolicy": metadata.get("HostConfig", {}).get("RestartPolicy", {}),
                "environmentSha256": hashlib.sha256(json.dumps(environment_map(config.get("Env", [])), sort_keys=True, separators=(",", ":")).encode()).hexdigest(),
                "security": {key: metadata.get("HostConfig", {}).get(key) for key in ("Privileged", "ReadonlyRootfs", "CapAdd", "CapDrop", "SecurityOpt", "GroupAdd", "PidMode", "IpcMode")},
                "user": config.get("User", ""), "workingDirectory": config.get("WorkingDir", ""),
                "runtimeOptions": validate_runtime_options(proposal["services"][name], metadata)}
        return inventory

    def validate_resume_sources(self, sources, verify):
        self.imported_export_records = []
        if not verify:
            return
        state_path = self.output / "launcher-state.json"
        if not state_path.is_file():
            fail("generation_missing", "No approved saved generation exists. Run ordinary setup first.")
        saved = read_private_json(state_path)
        if saved.get("sources") == sources:
            return
        if not self.legacy:
            candidates = saved.get("sourceCandidates", [])
            expected = {item["path"]: dict(item) for item in saved.get("sources", [])}
            for candidate in candidates:
                if candidate["source"] not in expected or expected[candidate["source"]]["sha256"] != candidate["originalSha256"]:
                    fail("approved_sources_changed", "The candidate source identity is not the reviewed baseline. Review a new proposal.")
                expected[candidate["source"]]["sha256"] = candidate["candidateSha256"]
            if candidates and expected == {item["path"]: item for item in sources}:
                return
            fail("approved_sources_changed", "Saved sources differ from the exact reviewed candidate. Reread and review them before verification; no saved hash was replaced.")
        # An external manager may persist exactly the one complete reviewed
        # document over its original single source. No arbitrary edit is allowed.
        if self.manager == "external-compose" and len(self.sources) == 1:
            old = saved.get("sources", [])
            changed = [item for item in sources if item not in old]
            if (len(sources) == len(old) and len(changed) == 1
                    and changed[0]["path"] == str(self.sources[0])
                    and changed[0]["sha256"] == saved.get("deploymentSha256")):
                return
            selected = next((item for item in sources if item["path"] == str(self.sources[0])), None)
            if selected and selected["sha256"] == saved.get("deploymentSha256"):
                normalized, exports = self.import_source_continuity(sources, saved)
                changed = [item for item in normalized if item not in old]
                if (len(normalized) == len(old) and len(changed) == 1
                        and changed[0] == selected and {item["path"] for item in normalized} == {item["path"] for item in old}):
                    # Keep the original source inventory for the independently
                    # enforced C# sole-complete-document transition guard. The
                    # generated provider is already an approval-bound export,
                    # and has its own throughout-verification hash fence.
                    sources[:] = normalized
                    self.imported_export_records = exports
                    return
        fail("approved_sources_changed", "Saved sources changed after approval. Run ordinary setup to review the current deployment; --verify cannot approve changed sources.")

    def import_source_continuity(self, sources, saved):
        check_saved_exports(self, saved)
        if (not getattr(self, "rendered", None) or resolved_deployment_hash(self.rendered) != saved.get("resolvedDeploymentSha256")):
            fail("approved_context_changed", "The imported complete document does not resolve to the reviewed deployment. Restore its approved interpolation context or run ordinary setup for new review.")
        frozen_path = self.output / "approved-effective-deployment.json"
        frozen_bytes = read_bytes(frozen_path, MAX_BYTES)
        if hashlib.sha256(frozen_bytes).hexdigest() != saved.get("sourceHash", "").casefold():
            fail("approved_source_snapshot_changed", "The frozen approved source snapshot changed or is unavailable. Run ordinary setup for a fresh review; migration dependencies cannot be inferred.")
        frozen = read_private_json(frozen_path)
        if frozen.get("x-rateldesk-source-files") != saved.get("sources"):
            fail("approved_source_snapshot_changed", "The frozen approved source inventory differs from the saved approval. Run ordinary setup for a fresh review.")
        patch = read_private_json(self.output / "compose.host.json")
        retained = migration_source_records(frozen, patch)
        old = {item["path"]: item for item in saved["sources"]}
        current = {item["path"]: item for item in sources}
        if len(old) != len(saved["sources"]) or len(current) != len(sources):
            fail("approved_sources_changed", "The imported source inventory contains duplicate paths. Run ordinary setup for new review.")
        exports = []
        outbound_path = str(self.output / "mcp-outbound.json")
        if outbound_path in current and outbound_path not in old:
            mounts = self.rendered.get("services", {}).get("mcp-config-init", {}).get("volumes", [])
            inputs = [mount for mount in mounts if isinstance(mount, dict) and mount.get("target") == "/input/config.json"]
            expected = saved.get("exportHashes", {}).get("mcp-outbound.json", "").casefold()
            if (len(inputs) != 1 or inputs[0].get("type") != "bind" or inputs[0].get("source") != outbound_path
                    or file_record(outbound_path)["sha256"] != expected):
                fail("approved_sources_changed", "The imported initializer provider is not the exact approved outbound export. Run ordinary setup for new review.")
            record = file_record(outbound_path)
            if outbound_path in current and current[outbound_path] != record:
                fail("approved_sources_changed", "The approved outbound provider changed during source capture. Restore its reviewed bytes or run ordinary setup for a fresh review.")
            current.pop(outbound_path)
            exports.append(record)
        for path, original in old.items():
            if path not in current:
                if retained.get(path) != original:
                    fail("approved_sources_changed", "An unapproved source dependency disappeared from the imported deployment. Restore its original source or run ordinary setup for new review.")
                try:
                    actual = file_record(path, optional=original.get("optional", False))
                except Prerequisite:
                    fail("approved_sources_changed", "The retained legacy provider is missing or unreadable. Restore its approved source before verification or run ordinary setup for a fresh review.")
                if actual != original:
                    fail("approved_sources_changed", "The retained legacy provider changed after approval. Restore its approved bytes or run ordinary setup for a fresh review.")
                current[path] = actual
        return list(current.values()), exports

    def check_sources(self):
        if any(file_record(item["path"], optional=item.get("optional", False)) != item for item in self.proposal["x-rateldesk-source-files"]):
            fail("sources_changed", "The saved deployment changed during setup. Nothing was applied. Rerun ordinary setup to review the current source.")
        if any(file_record(item["path"]) != item for item in getattr(self, "imported_export_records", [])):
            fail("sources_changed", "The approved outbound export changed during verification. Restore the reviewed bytes or run ordinary setup for new review.")
        for descriptor in getattr(self, "secret_mounts", []):
            if self.secret_digest(descriptor) != descriptor["sha256"]:
                fail("secret_source_changed", "A protected downstream secret changed during setup. Rerun ordinary setup to review the current generation; no identity was rotated.")

    def prepare_storage(self, provision=True):
        storage = {}
        if self.metadata is None:
            _, current = unique_container(self.docker, self.project, self.roles["mcp-http"])
            if not current:
                _, current = unique_container(self.docker, self.project, "web")
            if current:
                self.metadata = current
        existing_mounts = (self.metadata or {}).get("Mounts", [])
        saved_storage = {}
        if (self.output / "launcher-state.json").is_file():
            saved_storage = read_private_json(self.output / "launcher-state.json").get("storage", {})
        for purpose, target, suffix in (("host", HOST_TARGET, "http-host-setup"),
                                        ("client", CLIENT_TARGET, "http-client-data")):
            existing = [mount for mount in existing_mounts if mount.get("Destination") == target]
            if len(existing) > 1:
                fail("storage_ambiguous", "The serving MCP container has duplicate setup mounts. Restore its durable storage before setup.")
            if existing:
                mount = existing[0]
                if not mount.get("RW") or mount.get("Type") not in ("bind", "volume"):
                    fail("storage_read_only", "Existing setup storage must be a writable bind or named volume. Restore the manager-owned mount before setup.")
                descriptor = {"type": mount["Type"], "source": mount.get("Name") or mount.get("Source"),
                              "target": target, "readOnly": False, "private": True, "uid": 10001}
            elif purpose in saved_storage:
                descriptor = saved_storage[purpose]
                if descriptor.get("target") != target or descriptor.get("type") not in ("bind", "volume"):
                    fail("storage_metadata", "The approved persistent store metadata changed. Run ordinary setup to review current storage.")
            else:
                volume = f"{self.project}-{suffix}"
                metadata = self.docker.run(["volume", "inspect", "--format", "{{json .}}", volume], optional=True)
                if metadata is None:
                    if not provision:
                        fail("storage_not_provisioned", "Private setup storage is not provisioned. Run ordinary setup to select and preflight durable stores before approval.")
                    self.docker.run(["volume", "create", "--label", f"com.rateldesk.setup.project={self.project}",
                                     "--label", f"com.rateldesk.setup.purpose={purpose}", volume], maximum=4096)
                    # Only newly created task-owned stores are initialized. The
                    # serving/tooling entrypoint always remains UID 10001.
                    self.docker.run(["run", "--rm", "--pull", "never", "--network", "none", "--read-only",
                                     "--cap-drop", "ALL", "--cap-add", "CHOWN", "--security-opt", "no-new-privileges",
                                     "--user", "0:0", "--mount", f"type=volume,source={volume},target=/store",
                                     "--entrypoint", "/bin/sh", self.image, "-ec",
                                     "chmod 0700 /store; chown 10001:10001 /store"], maximum=4096)
                else:
                    try:
                        metadata = json.loads(metadata)
                    except ValueError:
                        fail("storage_metadata", "The named setup volume metadata is invalid. Restore that volume before setup.")
                    labels = metadata.get("Labels", {}) or {}
                    if labels.get("com.rateldesk.setup.project") != self.project or labels.get("com.rateldesk.setup.purpose") != purpose:
                        fail("storage_collision", "A proposed setup volume already belongs to another owner. Preserve it and select the existing deployment's actual persistent mounts.")
                descriptor = {"type": "volume", "source": volume, "target": target,
                              "readOnly": False, "private": True, "uid": 10001}
            storage[purpose] = descriptor
        if storage["host"]["source"] == storage["client"]["source"]:
            fail("storage_identity_isolation", "Host exports and personal client profiles must use separate stores. Restore their separate manager-owned mounts.")
        for mount in self.proposal["services"]["api"].get("volumes", []):
            source = mount.get("source") if isinstance(mount, dict) else None
            if source and source == storage["client"]["source"]:
                fail("storage_identity_isolation", "The API cannot mount personal MCP client secrets. Remove that API mount before setup.")
        for descriptor in storage.values():
            try:
                self.docker.run(["run", "--rm", "--pull", "never", "--network", "none", "--read-only", "--cap-drop", "ALL",
                                 "--security-opt", "no-new-privileges", "--user", "10001:10001",
                                 "--mount", mount_argument(descriptor, "/store"), "--entrypoint", "/bin/sh", self.image,
                                 "-ec", 'test "$(stat -c %u /store)" = 10001; test "$(stat -c %a /store)" = 700; umask 077; probe=$(mktemp /store/.preflight.XXXXXX); trap \'rm -f "$probe"\' EXIT; printf storage-preflight > "$probe"; test -r "$probe"'], maximum=4096)
            except Prerequisite:
                fail("storage_preflight", "A persistent setup mount is not privately readable/writable by UID 10001 with mode 0700. Restore that existing mount's ownership and permissions before approval; no existing data was changed.")
        self.storage = storage
        self.proposal["x-rateldesk-storage"] = storage
        write_private(self.output / "effective-deployment.json", self.proposal)


def mount_argument(descriptor, target=None):
    source = descriptor.get("source", "")
    if not isinstance(source, str) or not source or any(character in source for character in ",\n\r"):
        fail("storage_source", "The saved storage source cannot be represented safely by Docker --mount. Use its explicit regular bind path or named volume.")
    if descriptor["type"] == "volume" and not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,255}", source):
        fail("storage_source", "The saved named volume metadata is malformed. Restore its actual persistent mount.")
    return f"type={descriptor['type']},source={source},target={target or descriptor['target']}"


def tooling_arguments(deployment):
    command = ["run", "--pull", "never", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
               "--user", "10001:10001", "--tmpfs", "/tmp:rw,nosuid,nodev,size=16777216,mode=1777"]
    for descriptor in deployment.storage.values():
        command += ["--mount", mount_argument(descriptor)]
    inherited = set()
    for descriptor in getattr(deployment, "secret_mounts", []):
        if descriptor["target"] not in inherited:
            command += ["--mount", mount_argument(descriptor) + ",readonly"]
            inherited.add(descriptor["target"])
    command += ["--env", f"RATELDESK_MCP_HOST_DIRECTORY={HOST_TARGET}",
                "--env", f"RATELDESK_MCP_CLIENT_DIRECTORY={CLIENT_TARGET}",
                "--env", f"RATELDESK_SETUP_EFFECTIVE_COMPOSE_SOURCE={HOST_TARGET}/effective-deployment.json",
                "--env", f"RATELDESK_SETUP_RUNTIME_ENVIRONMENT_SOURCE={HOST_TARGET}/runtime-environment.json",
                "--env", f"RATELDESK_SETUP_DEPLOYMENT_MANAGER={deployment.manager}",
                "--env", f"RATELDESK_SETUP_HOST_INTENT={deployment.intent}"]
    for key, value in getattr(deployment, "legacy_compose_proof", {}).items():
        command += ["--env", key + "=" + value]
    services = getattr(deployment, "proposal", {}).get("services", {})
    exclusions = None
    for name in ("mcp-http", "web"):
        environment = environment_map(services.get(name, {}).get("environment", {}))
        exclusions = environment.get("no_proxy", environment.get("NO_PROXY"))
        if exclusions is not None:
            break
    if exclusions is not None:
        safe_label(exclusions, 8192)
        command += ["--env", "NO_PROXY=" + exclusions, "--env", "no_proxy=" + exclusions]
    if deployment.args.ca_file:
        certificate = str(Path(deployment.args.ca_file))
        command += ["--mount", mount_argument({"type": "bind", "source": certificate}, "/run/rateldesk-setup-ca.pem") + ",readonly",
                    "--env", "SSL_CERT_FILE=/run/rateldesk-setup-ca.pem"]
    # Reuse the existing MCP network solely for explicit private API backends.
    # Do not grant host networking or a Docker socket to the setup process.
    networks = (deployment.metadata or {}).get("NetworkSettings", {}).get("Networks", {})
    if len(networks) == 1:
        network = next(iter(networks))
        safe_label(network, 256)
        command += ["--network", network]
    return command


def stage_snapshot(deployment, name):
    content = read_bytes(deployment.output / name, MAX_BYTES)
    command = tooling_arguments(deployment) + ["--rm", "--interactive", "--entrypoint", "/bin/sh", deployment.image,
        "-ec", 'umask 077; file=$(mktemp /output/.stage.XXXXXX); trap \'rm -f "$file"\' EXIT; head -c 1048577 > "$file"; test "$(wc -c < "$file")" -le 1048576; chmod 0600 "$file"; mv -f "$file" "/output/$1"; trap - EXIT; sha256sum "/output/$1"',
        "setup-stage", name]
    returned = deployment.docker.run(command, input_data=content, maximum=4096).decode().split(" ", 1)[0]
    if returned != hashlib.sha256(content).hexdigest():
        fail("snapshot_stage", "The persistent effective snapshot failed its hash check. Retry with stable sources.")


def copy_from_tool(deployment, container, name, optional=False):
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,100}", name):
        fail("export_path", "The setup receipt contains an unsupported export path. Rerun ordinary setup.")
    destination = deployment.output / (".copy-" + uuid.uuid4().hex)
    try:
        copied = deployment.docker.run(["cp", f"{container}:{HOST_TARGET}/{name}", str(destination)],
                                       optional=optional, maximum=4096)
        if copied is None:
            return None
        if destination.is_symlink() or not destination.is_file():
            fail("export_path", "The setup export is not a regular private file. Rerun ordinary setup.")
        destination.chmod(0o600)
        return read_bytes(destination, MAX_BYTES)
    finally:
        destination.unlink(missing_ok=True)


def check_saved_exports(deployment, state):
    for name, digest in state.get("exportHashes", {}).items():
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,100}", name):
            fail("export_path", "A saved export path is invalid. Rerun ordinary setup.")
        if hashlib.sha256(read_bytes(deployment.output / name, MAX_BYTES)).hexdigest().casefold() != digest.casefold():
            fail("approved_export_changed", "A reviewed export changed after approval. Run ordinary setup to review a new generation.")
    if hashlib.sha256(read_bytes(deployment.output / "deployment.json", MAX_BYTES)).hexdigest() != state.get("deploymentSha256"):
        fail("approved_export_changed", "The complete reviewed deployment changed. Run ordinary setup before applying or verifying it.")


def check_effective_context(deployment, state):
    # Saved file hashes alone do not cover interpolation inherited from this host
    # process or updated image/provider defaults. Fence those unchanged-source
    # invocations with the original complete snapshot hash as well.
    if state.get("sources") == deployment.proposal["x-rateldesk-source-files"]:
        current = hashlib.sha256(read_bytes(deployment.output / "effective-deployment.json", MAX_BYTES)).hexdigest()
        if current != state.get("sourceHash", "").casefold():
            fail("approved_context_changed", "Effective interpolation, image defaults or protected providers changed after approval. Run ordinary setup to review the current effective deployment.")
    else:
        if state.get("secretMounts", []) != deployment.proposal.get("x-rateldesk-secret-mounts", []):
            fail("approved_secret_changed", "A selected protected downstream secret differs from the reviewed generation. Restore the approved secret or run ordinary setup to review the changed identity before applying or verifying.")
        if resolved_deployment_hash(deployment.proposal) != state.get("resolvedDeploymentSha256"):
            fail("approved_context_changed", "The imported complete deployment resolves differently from the reviewed export. Restore its approved interpolation context, or run ordinary setup to review the changed generation.")


def inspect_mount_ancestors(docker, complete, image):
    """Prove parent-volume children are directories without writing their data."""
    receipts = []
    for service in ("mcp-http", "mcp-config-init"):
        for mount in complete.get("services", {}).get(service, {}).get("volumes", []):
            if not isinstance(mount, dict) or mount.get("type") != "volume" or mount.get("read_only") or not mount.get("source"):
                continue
            target = posixpath.normpath(mount.get("target", ""))
            children = [root for root in ("/run/rateldesk-mcp", HOST_TARGET, CLIENT_TARGET)
                        if target != "/" and root.startswith(target + "/")]
            if not children:
                continue
            alias = mount["source"]
            definition = complete.get("volumes", {}).get(alias, {})
            source = definition.get("name") or (alias if definition.get("external") or not complete.get("name") else complete["name"] + "_" + alias)
            if docker.run(["volume", "inspect", source], optional=True, maximum=8192) is None:
                continue
            paths = {"/ancestor"}
            for child in children:
                current = "/ancestor"
                for segment in child[len(target) + 1:].split("/"):
                    current += "/" + segment
                    paths.add(current)
            command = ["run", "--rm", "--pull", "never", "--network", "none", "--read-only", "--cap-drop", "ALL",
                       "--security-opt", "no-new-privileges", "--user", "10001:10001", "--mount",
                       mount_argument({"type": "volume", "source": source}, "/ancestor") + ",readonly,volume-nocopy",
                       "--entrypoint", "/bin/sh", image, "-ec",
                       ANCESTOR_INSPECTION_SCRIPT,
                       "mount-ancestor-check", *sorted(paths, key=lambda path: (path.count("/"), path))]
            if docker.run(command, optional=True, maximum=4096) is not None:
                receipts.append({"service": service, "source": source, "target": target, "children": children})
    return receipts


def migrated_compose_source(source, rendered, patch):
    """Remove only the source-bound selected mapping in a private source copy.

    Compose's multi-file merge retains absent config entries. Apply the approved
    removal before that merge, preserving expressions and all unrelated entries.
    The paired Compose renders retain array order even when targets interpolate.
    """
    source = json.loads(json.dumps(source))
    migrations = patch.get("x-rateldesk-mount-migrations", [])
    if not isinstance(migrations, list) or len(migrations) > 1:
        fail("approved_mount_migration", "The approved mount replacement is invalid. Run ordinary setup for a fresh review.")
    for migration in migrations:
        if not isinstance(migration, dict) or migration.get("service") != "mcp-http" or migration.get("kind") != "configs" or migration.get("target") != "/run/rateldesk-mcp/config.json":
            fail("approved_mount_migration", "The approved mount replacement is unsupported. Resolve its owning mapping and run ordinary setup.")
        actual = rendered.get("services", {}).get("mcp-http", {}).get("configs", [])
        original = source.get("services", {}).get("mcp-http", {}).get("configs", [])
        matches = []
        for index, mapping in enumerate(actual):
            alias = mapping.get("source") if isinstance(mapping, dict) else mapping
            target = mapping.get("target", "/" + alias) if isinstance(mapping, dict) else "/" + alias
            parts = []
            for part in target.split("/"):
                if part == "..":
                    if parts:
                        parts.pop()
                elif part and part != ".":
                    parts.append(part)
            normalized = "/" + "/".join(parts)
            if alias == migration.get("source") and normalized == migration["target"]:
                matches.append(index)
        if len(matches) != 1 or len(original) != len(actual):
            fail("approved_mount_migration", "The selected legacy config mapping changed after review. Run ordinary setup for new approval.")
        del original[matches[0]]
    return source


def migration_source_records(frozen, patch):
    """Retain only captured providers retired by the approved storage replacement."""
    # Reuse the exact descriptor/target/source match checks; no blanket retirement
    # of missing config/secret/env dependencies is permitted.
    migrated_compose_source(frozen, frozen, patch)
    records = {}
    for migration in patch.get("x-rateldesk-mount-migrations", []):
        path = frozen.get("configs", {}).get(migration["source"], {}).get("file")
        original = [item for item in frozen.get("x-rateldesk-source-files", []) if item.get("path") == path]
        providers = [provider for provider in frozen.get("x-rateldesk-protected-configuration", {}).get("mcp-http", [])
            if normalized_container_path(provider.get("path", "")) == migration["target"] and provider.get("source") == path]
        if len(original) != 1 or len(providers) != 1 or providers[0].get("sha256") != original[0].get("sha256"):
            fail("approved_mount_migration", "The approved legacy removal lacks its exact captured protected source. Run ordinary setup for a new review; no dependency was retired.")
        records[path] = original[0]
    # The supported beta.1 already-migrated shape selected its outbound provider
    # through one initializer input bind. The reviewed initializer now reads the
    # same protected export from the host store. Keep that original selected
    # input source fenced as well; unrelated initializer inputs are never adopted.
    old_inputs = [mount for mount in frozen.get("services", {}).get("mcp-config-init", {}).get("volumes", [])
        if isinstance(mount, dict) and mount.get("type") == "bind" and mount.get("target") == "/input/config.json"]
    initializer = patch.get("services", {}).get("mcp-config-init", {})
    outputs = [mount for mount in initializer.get("volumes", [])
        if isinstance(mount, dict) and mount.get("target") == HOST_TARGET]
    host = frozen.get("x-rateldesk-storage", {}).get("host", {})
    command = initializer.get("command", [])
    if len(old_inputs) == 1 and len(outputs) == 1 and isinstance(command, list) and len(command) == 1 and isinstance(command[0], str) and "cp /output/mcp-outbound.json /run/rateldesk-mcp/config.json" in command[0]:
        output = outputs[0]
        definition = patch.get("volumes", {}).get(output.get("source"), {})
        output_source = definition.get("name") if output.get("type") == "volume" else output.get("source")
        if (host.get("target") == HOST_TARGET and host.get("private") is True
                and output.get("type") == host.get("type") and output_source == host.get("source")):
            path = old_inputs[0].get("source")
            original = [item for item in frozen.get("x-rateldesk-source-files", []) if item.get("path") == path]
            providers = frozen.get("x-rateldesk-protected-configuration", {}).get("mcp-http", [])
            selected = providers[-1] if providers else {}
            if (len(original) == 1 and selected.get("source") == path
                    and normalized_container_path(selected.get("path", "")) == "/run/rateldesk-mcp/config.json"
                    and selected.get("sha256") == original[0].get("sha256")):
                records[path] = original[0]
    return records


def legacy_recipe_identity(deployment, source_digest):
    deployment.docker.bind_daemon()
    owner_version = deployment.docker.run(["compose", "version", "--short"], owner_tool=True, maximum=1024).decode().strip()
    safe_label(owner_version, 256)
    return {"schemaVersion": 1, "unresolvedSourceSha256": source_digest,
        "project": deployment.project, "manager": deployment.manager, "projectDirectory": str(deployment.directory), "outputDirectory": str(deployment.output),
        "composeFiles": [str(path) for path in deployment.sources], "envFiles": [str(path) for path in deployment.env_files],
        "dockerSelection": dict(deployment.docker.selection), "dockerDaemonIdentity": deployment.docker.daemon_identity,
        "rendererVersion": deployment.docker.compose_version, "ownerVersion": owner_version,
        "rawFlags": ["config", "--no-interpolate", "--no-env-resolution", "--no-path-resolution", "--format", "json"],
        "projectionFlags": ["config", "--format", "json"]}


def private_observation_bytes(path):
    try:
        descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
        with os.fdopen(descriptor, "rb") as source:
            info = os.fstat(source.fileno())
            if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) & 0o077:
                raise OSError()
            content = source.read(MAX_BYTES + 1)
        if len(content) > MAX_BYTES:
            raise OSError()
        return content
    except OSError:
        fail("legacy_recipe_invalid", "Restore the bounded private approved files, or re-adopt the running deployment unchanged / review the explicit initializer alternative again. No existing resource was changed.")


def legacy_approved_inputs(deployment):
    receipt_bytes = private_observation_bytes(deployment.output / "host-approval.json")
    receipt = json.loads(receipt_bytes)
    hashes = receipt.get("exportHashes", {})
    if (receipt.get("approved") is not True or receipt.get("intent", 2) != 2 or not isinstance(hashes, dict)
            or not 1 <= len(hashes) <= 16 or not {"approved-proposal.json", "compose.host.json"}.issubset(hashes)):
        fail("legacy_recipe_invalid", "The initializer checkpoint lacks a complete approved recipe. Re-adopt the running deployment unchanged or review the explicit initializer alternative again.")
    files = {}
    for name, expected in hashes.items():
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,100}", name):
            fail("legacy_recipe_invalid", "The approved recipe has an invalid export path. Review a fresh initializer proposal.")
        content = private_observation_bytes(deployment.output / name)
        if hashlib.sha256(content).hexdigest() != str(expected).casefold():
            fail("legacy_recipe_invalid", "An approved recipe file changed. Restore its reviewed bytes or review a fresh proposal; no saved hash was repaired.")
        files[name] = content
    frozen = private_observation_bytes(deployment.output / "approved-effective-deployment.json")
    if hashlib.sha256(frozen).hexdigest() != str(receipt.get("sourceHash", "")).casefold():
        fail("legacy_recipe_invalid", "The approved source generation changed. Restore it or review a new proposal.")
    return receipt_bytes, files, json.loads(frozen)


def preserve_source_definitions(document, source):
    # Compose omits unused declarations from its effective model. Keep those
    # exact source-only definitions without replacing a serving definition.
    for kind in ("configs", "secrets", "volumes", "networks"):
        for name, definition in source.get(kind, {}).items():
            document.setdefault(kind, {}).setdefault(name, json.loads(json.dumps(definition)))
    return document


def legacy_compose_context(deployment):
    command = ["compose", "--project-directory", str(deployment.directory), "--project-name", deployment.project]
    for path in deployment.env_files or [Path("/dev/null")]:
        command += ["--env-file", str(path)]
    return command


def render_legacy_recipe(deployment, original, frozen, patch_bytes, paths):
    patch = json.loads(patch_bytes)
    source = migrated_compose_source(json.loads(original), frozen, patch)
    for mount in patch.get("services", {}).get("mcp-config-init", {}).get("volumes", []):
        if mount.get("target") == "/input/config.json":
            mount["source"] = str(deployment.output / "mcp-outbound.json")
    source_path, patch_path = paths
    source_bytes = (json.dumps(source, sort_keys=True, separators=(",", ":")) + "\n").encode()
    patch_content = (json.dumps(patch, sort_keys=True, separators=(",", ":")) + "\n").encode()
    write_private(source_path, source_bytes)
    write_private(patch_path, patch_content)
    command = legacy_compose_context(deployment) + ["-f", str(source_path), "-f", str(patch_path)]
    rendered = deployment.docker.json(command + ["config", "--no-interpolate", "--no-env-resolution", "--no-path-resolution", "--format", "json"])
    if private_observation_bytes(source_path) != source_bytes or private_observation_bytes(patch_path) != patch_content:
        fail("legacy_recipe_changed", "A private recipe input changed during its render. Retry with the unchanged approved sources; nothing was applied.")
    rendered = preserve_source_definitions(rendered, source)
    return (json.dumps(rendered, sort_keys=True, separators=(",", ":")) + "\n").encode()


@contextmanager
def legacy_compose_observation(deployment, actual_sources=None):
    """Fresh host observation with in-memory digests, never a saved baseline."""
    if not deployment.legacy:
        yield
        return
    prefix = ".legacy-observation-" + uuid.uuid4().hex
    names = [prefix + suffix for suffix in ("-source.json", "-patch.json", "-input.json", "-expected.json", "-actual.json")]
    paths = [deployment.output / name for name in names]
    staged = []
    try:
        before = legacy_approved_inputs(deployment)
        _, files, frozen = before
        recipe = frozen.get("x-rateldesk-legacy-export-recipe")
        original = private_observation_bytes(deployment.output / "approved-compose-source.json")
        original_digest = hashlib.sha256(original).hexdigest()
        if not isinstance(recipe, dict) or recipe != legacy_recipe_identity(deployment, original_digest):
            fail("legacy_recipe_invalid", "The original source recipe or selected owner/tool/context changed. Re-adopt the running deployment unchanged or review the initializer alternative again; no receipt was rebased.")
        deployment.check_sources()
        expected_raw = render_legacy_recipe(deployment, original, frozen, files["compose.host.json"], paths[:2])
        reviewed_path = deployment.output / "deployment.json"
        if private_observation_bytes(reviewed_path) != expected_raw:
            fail("legacy_recipe_changed", "The complete manager document differs from the exact approved source/patch recipe, including source-only declarations. Restore its reviewed bytes or obtain a new review; no state hash can renew approval.")
        sources = list(actual_sources or deployment.sources)
        actual_records = [file_record(path) for path in sources]
        if actual_sources is None:
            changed = [item for item in actual_records if item not in frozen.get("x-rateldesk-source-files", [])]
            if changed and (len(changed) != 1 or changed[0]["sha256"] != hashlib.sha256(expected_raw).hexdigest()):
                fail("legacy_recipe_changed", "The imported owner source differs from the exact approved complete document. Review the owner changes explicitly.")
        approved = json.loads(files["approved-proposal.json"])
        approved_input = (json.dumps({key: value for key, value in approved.items() if not key.startswith("x-rateldesk-")}, sort_keys=True, separators=(",", ":")) + "\n").encode()
        write_private(paths[2], approved_input)
        context = legacy_compose_context(deployment)
        # Both proof projections use the same verified setup renderer. The
        # original owner must independently render the exact same serving model.
        expected = deployment.docker.json(context + ["-f", str(paths[2]), "config", "--format", "json"])
        if private_observation_bytes(paths[2]) != approved_input:
            fail("legacy_recipe_changed", "The exact approved render input changed. No deployment approval was renewed.")
        actual_command = context.copy()
        for path in sources:
            actual_command += ["-f", str(path)]
        actual = deployment.docker.json(actual_command + ["config", "--format", "json"])
        owner_actual = json.loads(deployment.docker.run(actual_command + ["config", "--format", "json"], owner_tool=True))
        strip_metadata = lambda model: {key: value for key, value in model.items() if not key.startswith("x-rateldesk-")}
        if strip_metadata(owner_actual) != strip_metadata(actual):
            fail("legacy_owner_semantics", "The original deployment owner's Compose tool resolves a different serving model. Review that owner/tool/source context explicitly; no generated document was applied.")
        projection_bytes = []
        for path, model in zip(paths[3:], (expected, actual)):
            content = (json.dumps({key: value for key, value in model.items() if not key.startswith("x-rateldesk-")}, sort_keys=True, separators=(",", ":")) + "\n").encode()
            write_private(path, content)
            projection_bytes.append(content)
            staged.append(path.name)
            stage_snapshot(deployment, path.name)
        if (legacy_approved_inputs(deployment) != before or private_observation_bytes(deployment.output / "approved-compose-source.json") != original
                or recipe != legacy_recipe_identity(deployment, original_digest)
                or any(file_record(path) != record for path, record in zip(sources, actual_records))
                or private_observation_bytes(reviewed_path) != expected_raw):
            fail("legacy_recipe_changed", "The approved recipe, source, tool or context changed while rendering. Restart review; no deployment was applied.")
        deployment.check_sources()
        deployment.legacy_compose_proof = {
            "RATELDESK_SETUP_APPROVED_RAW_SHA256": hashlib.sha256(files["approved-proposal.json"]).hexdigest(),
            "RATELDESK_SETUP_APPROVED_OVERLAY_SHA256": hashlib.sha256(files["compose.host.json"]).hexdigest(),
            "RATELDESK_SETUP_REVIEWED_DEPLOYMENT_SHA256": hashlib.sha256(expected_raw).hexdigest(),
            "RATELDESK_SETUP_EXPECTED_COMPOSE_SOURCE": HOST_TARGET + "/" + paths[3].name,
            "RATELDESK_SETUP_EXPECTED_COMPOSE_SHA256": hashlib.sha256(projection_bytes[0]).hexdigest(),
            "RATELDESK_SETUP_ACTUAL_COMPOSE_SOURCE": HOST_TARGET + "/" + paths[4].name,
            "RATELDESK_SETUP_ACTUAL_COMPOSE_SHA256": hashlib.sha256(projection_bytes[1]).hexdigest()}
        def check_fresh_recipe():
            deployment.check_sources()
            if (legacy_approved_inputs(deployment) != before or private_observation_bytes(reviewed_path) != expected_raw
                    or private_observation_bytes(deployment.output / "approved-compose-source.json") != original
                    or recipe != legacy_recipe_identity(deployment, original_digest)):
                fail("legacy_recipe_changed", "The approved source recipe changed during validation. Restore it or review the changed source.")
        deployment.legacy_compose_fence = check_fresh_recipe
        yield actual
        check_fresh_recipe()
    finally:
        deployment.legacy_compose_proof = {}
        deployment.legacy_compose_fence = None
        for path in paths:
            path.unlink(missing_ok=True)
        if staged:
            deployment.docker.run(tooling_arguments(deployment) + ["--rm", "--entrypoint", "/bin/sh", deployment.image,
                "-ec", 'for name do rm -f "/output/$name"; done', "remove-observation", *staged], maximum=4096)


def export_complete_document(deployment, container, attempt):
    raw_receipt = copy_from_tool(deployment, container, "host-approval.json", optional=True)
    if raw_receipt is None:
        print("Setup exited without a new approved export. Existing private state was retained.")
        return False
    try:
        receipt = json.loads(raw_receipt)
    except (ValueError, UnicodeError):
        fail("approval_receipt", "The host approval receipt is malformed. Rerun ordinary setup; nothing was applied.")
    if receipt.get("launcherAttempt") != attempt or receipt.get("approved") is not True:
        print("Setup exited without a new host approval for this invocation. Existing private state was retained.")
        return False
    expected = hashlib.sha256(read_bytes(deployment.output / "effective-deployment.json", MAX_BYTES)).hexdigest()
    if receipt.get("sourceHash", "").casefold() != expected:
        fail("approval_source_mismatch", "The host approval belongs to another source generation. Rerun ordinary setup; nothing was applied.")
    export_hashes = receipt.get("exportHashes", {})
    if not isinstance(export_hashes, dict) or not {"compose.host.json", "host-settings.json", "mcp-outbound.json"}.issubset(export_hashes):
        fail("approval_receipt", "The host approval lacks complete reviewed export hashes. Rerun ordinary setup.")
    for name, expected in export_hashes.items():
        content = copy_from_tool(deployment, container, name)
        if hashlib.sha256(content).hexdigest().casefold() != str(expected).casefold():
            fail("export_hash_mismatch", "A saved export does not match the approved generation. Nothing was applied; rerun setup.")
        write_private(deployment.output / name, content)
    deployment.check_sources()
    # Retain the exact original unresolved input covered by administrator
    # SourceHash. A resume never replaces this copy with an imported document.
    _, approved_files, approved_source = legacy_approved_inputs(deployment)
    original = private_observation_bytes(deployment.output / ".compose-source.json")
    original_digest = hashlib.sha256(original).hexdigest()
    if approved_source.get("x-rateldesk-legacy-export-recipe") != legacy_recipe_identity(deployment, original_digest):
        fail("legacy_recipe_invalid", "The approved unresolved source/tool recipe is unavailable or changed. Re-adopt the running deployment unchanged or review the initializer alternative again.")
    write_private(deployment.output / "approved-compose-source.json", original)
    export_paths = [deployment.output / name for name in (".export-source.json", ".export-patch.json")]
    complete_bytes = render_legacy_recipe(deployment, original, approved_source, approved_files["compose.host.json"], export_paths)
    write_private(deployment.output / "deployment.json", complete_bytes)
    try:
        with legacy_compose_observation(deployment, [deployment.output / "deployment.json"]) as resolved_complete:
            write_private(deployment.output / ".resolved-export.json", resolved_complete)
            stage_snapshot(deployment, ".resolved-export.json")
            deployment.docker.run(tooling_arguments(deployment) + ["--rm", deployment.image,
                "host", "export-check", "--export-directory", HOST_TARGET], maximum=16384)
    except Prerequisite as error:
        rules = getattr(error, "export_validation_rules", [])
        diagnostic = " Validation rules: " + ", ".join(rules) + "." if rules else ""
        fail("complete_export_invalid", "The complete manager document failed the approved serving and mount validation."
            + diagnostic + " Nothing was applied. Resolve its owning sources and run ordinary setup.")
    deployment.check_sources()
    # Ordinary client setup and new verification replace effective-deployment.
    # Retain this exact receipt-bound source for dependency continuity after the
    # manager imports the approved migrated complete document.
    frozen = read_bytes(deployment.output / "effective-deployment.json", MAX_BYTES)
    if hashlib.sha256(frozen).hexdigest() != receipt["sourceHash"].casefold():
        fail("approval_source_mismatch", "The reviewed source snapshot changed during export. Nothing was applied; rerun ordinary setup for a fresh review.")
    write_private(deployment.output / "approved-effective-deployment.json", frozen)
    state = {"schemaVersion": 1, "project": deployment.project, "manager": deployment.manager,
             "sources": deployment.proposal["x-rateldesk-source-files"], "sourceHash": receipt["sourceHash"],
             "composeFiles": [str(path) for path in deployment.sources], "envFiles": [str(path) for path in deployment.env_files],
             "mcpProposed": deployment.proposal.get("x-rateldesk-mcp-proposed", False),
             "proposalHash": receipt.get("proposalHash"), "exportHashes": export_hashes,
             "deploymentSha256": hashlib.sha256(read_bytes(deployment.output / "deployment.json", MAX_BYTES)).hexdigest(),
             "resolvedDeploymentSha256": resolved_deployment_hash(resolved_complete),
             "secretMounts": approved_import_secret_mounts(deployment.proposal, resolved_complete,
                 read_bytes(deployment.output / "mcp-outbound.json", MAX_BYTES).decode("utf-8-sig")),
             "phase": "saved-apply-pending", "storage": deployment.storage,
             "dockerSelection": getattr(deployment.docker, "selection", {}),
             "dockerDaemonIdentity": getattr(deployment.docker, "daemon_identity", None)}
    write_private(deployment.output / "host-approval.json", raw_receipt)
    write_private(deployment.output / "launcher-state.json", state)
    print(f"Private complete deployment document: {deployment.output / 'deployment.json'}")
    if deployment.manager == "external-compose":
        print("Saved — apply pending. Review the complete document in the existing deployment manager, preserve the recorded interpolation files, then apply that same stack.")
    else:
        print("Saved — apply pending. Review the private document, then run on this Docker host:")
        print("  " + resume_command(deployment, "--apply"))
    print("After apply/recreation, start a NEW verification invocation:")
    print("  " + resume_command(deployment, "--verify"))
    return True


def native_executable(args):
    selected = args.native_executable
    if not selected:
        directory = Path(__file__).resolve().parent
        for candidate in (directory / "rateldesk", directory / "rateldesk-mcp"):
            if candidate.is_file():
                selected = str(candidate)
                break
    if not selected:
        fail("native_setup_required", "Extract the verified matching native CLI or stdio archive and supply --native-executable /absolute/path/rateldesk-mcp. This deployment-read-only path does not create a tooling container or install a serving mount.")
    selected = Path(selected).absolute()
    if not selected.is_file() or not os.access(selected, os.X_OK):
        fail("native_setup_unavailable", "Select an existing executable from the verified matching native archive with --native-executable.")
    # Native archives include managed dependencies. Pin the complete sibling
    # payload, including assemblies, so changing a DLL cannot reuse approval.
    records = []
    for candidate in sorted(selected.parent.iterdir()):
        if candidate.is_file() and (candidate == selected or candidate.suffix in (".dll", ".json", ".so")):
            digest = hashlib.sha256()
            with candidate.open("rb") as source:
                while chunk := source.read(65536):
                    digest.update(chunk)
            records.append([candidate.name, digest.hexdigest()])
    identity = hashlib.sha256(json.dumps(records, separators=(",", ":")).encode()).hexdigest()
    return str(selected), identity


def native_environment(deployment):
    environment = deployment.docker.process_environment()
    environment.update({"RATELDESK_MCP_HOST_DIRECTORY": str(deployment.output),
        "RATELDESK_SETUP_EFFECTIVE_COMPOSE_SOURCE": str(deployment.output / "effective-deployment.json"),
        "RATELDESK_SETUP_RUNTIME_ENVIRONMENT_SOURCE": str(deployment.output / "runtime-environment.json"),
        "RATELDESK_SETUP_DEPLOYMENT_MANAGER": deployment.manager,
        "RATELDESK_SETUP_HOST_INTENT": deployment.intent})
    if deployment.args.ca_file:
        environment["SSL_CERT_FILE"] = str(Path(deployment.args.ca_file).absolute())
    context = {"schemaVersion": 1, "compose": deployment.compose,
        "sources": [str(path) for path in deployment.sources], "projectDirectory": str(deployment.directory),
        "outputDirectory": str(deployment.output), "dockerSelection": deployment.docker.selection,
        "daemonIdentity": deployment.docker.daemon_identity,
        "effectiveSnapshot": str(deployment.output / "effective-deployment.json"),
        "manager": deployment.manager, "ownerComposeRequired": True}
    context_path = deployment.output / "source-review-context.json"
    write_private(context_path, context)
    editor = Path(__file__).resolve().parent / "source_review.py"
    environment["RATELDESK_SETUP_SOURCE_REVIEW_COMMAND"] = json.dumps([sys.executable, str(editor), "--context", str(context_path)])
    return environment


def native_command(deployment, arguments, *, interactive=False):
    executable, identity = native_executable(deployment.args)
    state_path = deployment.output / "launcher-state.json"
    if state_path.is_file() and (deployment.args.verify or deployment.args.apply):
        expected = read_private_json(state_path).get("nativeExecutableSha256")
        if expected and expected != identity:
            fail("native_setup_changed", "The selected native setup payload changed after review. Use the reviewed executable or explicitly review a new baseline/proposal.")
    environment = native_environment(deployment)
    if interactive:
        return subprocess.run([executable, *arguments], env=environment, check=False).returncode
    try:
        completed = subprocess.run([executable, *arguments], env=environment, check=False, capture_output=True, timeout=60)
    except subprocess.TimeoutExpired:
        fail("native_setup_timeout", "The bounded native checkpoint validation timed out. No deployment completion was recorded.")
    if completed.returncode:
        # Native commands deliberately emit safe diagnostics; arbitrary stdout
        # remains private because it may include protected source identities.
        safe = completed.stderr.decode("utf-8", errors="replace")[-8192:]
        if safe:
            print(safe, file=sys.stderr, end="")
        fail("native_checkpoint_validation", "The reviewed source/authority/runtime check did not pass. Restore the exact reviewed state or reopen a fresh review.")
    return 0


def save_native_checkpoint(deployment):
    receipt = read_private_json(deployment.output / "host-approval.json")
    approved = read_private_json(deployment.output / "approved-proposal.json")
    _, identity = native_executable(deployment.args)
    state = {"schemaVersion": 2, "phase": "adopted-verify-pending" if deployment.intent == "verify-existing" else "prepared-apply-pending",
        "intent": deployment.intent, "project": deployment.project, "manager": deployment.manager,
        "composeFiles": [str(path) for path in deployment.sources], "envFiles": [str(path) for path in deployment.env_files],
        "sources": deployment.proposal["x-rateldesk-source-files"], "sourceHash": receipt["sourceHash"],
        "proposalHash": receipt["proposalHash"], "exportHashes": receipt["exportHashes"],
        "sourceCandidates": approved.get("x-rateldesk-source-candidates", []),
        "baselineRuntimeInventory": read_private_json(deployment.output / "runtime-environment.json").get("x-rateldesk-runtime-inventory", {}),
        "changedServices": [name for name, definition in deployment.proposal["services"].items() if approved.get("services", {}).get(name) != definition],
        "dockerSelection": deployment.docker.selection, "dockerDaemonIdentity": deployment.docker.daemon_identity,
        "composeVersion": deployment.docker.compose_version, "nativeExecutableSha256": identity}
    write_private(deployment.output / "launcher-state.json", state)
    return state


def check_native_checkpoint(deployment, state):
    receipt = read_private_json(deployment.output / "host-approval.json")
    frozen = read_private_json(deployment.output / "approved-effective-deployment.json")
    approved = read_private_json(deployment.output / "approved-proposal.json")
    frozen_hash = hashlib.sha256(read_bytes(deployment.output / "approved-effective-deployment.json", MAX_BYTES)).hexdigest()
    if frozen_hash != str(receipt.get("sourceHash", "")).casefold() or any(state.get(key) != receipt.get(key) for key in ("sourceHash", "proposalHash", "exportHashes")):
        fail("approved_checkpoint_changed", "The launcher checkpoint differs from its reviewed administrator receipt. Reopen source review; no saved hash was repaired.")
    changed = [name for name, definition in frozen.get("services", {}).items() if approved.get("services", {}).get(name) != definition]
    expected_intent = {0: "verify-existing", 1: "prepare-changes", 2: "legacy-initializer"}.get(receipt.get("intent"))
    if (state.get("intent") != expected_intent or state.get("sources") != frozen.get("x-rateldesk-source-files")
            or state.get("sourceCandidates") != approved.get("x-rateldesk-source-candidates", [])
            or state.get("changedServices") != changed
            or state.get("dockerSelection") != frozen.get("x-rateldesk-docker-selection")
            or state.get("dockerDaemonIdentity") != frozen.get("x-rateldesk-docker-daemon-identity")
            or state.get("composeVersion") != frozen.get("x-rateldesk-compose-version")):
        fail("approved_checkpoint_changed", "Source candidates, service impact or Docker selection differ from the exact approved proposal. Review the current source instead of editing a checkpoint.")
    if (state.get("project") != frozen.get("x-rateldesk-project-name")
            or state.get("manager") != frozen.get("x-rateldesk-deployment-manager")
            or deployment.project != state.get("project") or deployment.manager != state.get("manager")
            or str(deployment.directory) != frozen.get("x-rateldesk-project-directory")
            or state.get("composeFiles") != frozen.get("x-rateldesk-compose-files")
            or state.get("envFiles") != frozen.get("x-rateldesk-interpolation-files")
            or [str(path) for path in deployment.sources] != state.get("composeFiles")
            or [str(path) for path in deployment.env_files] != state.get("envFiles")):
        fail("approved_owner_changed", "The deployment owner, project, working directory or ordered source selection changed after review. Re-read and review the current owner source.")
    _, identity = native_executable(deployment.args)
    if identity != frozen.get("x-rateldesk-native-payload-identity") or state.get("nativeExecutableSha256") != identity:
        fail("native_setup_changed", "Use the exact reviewed native payload or review a new baseline/proposal; the saved receipt cannot authorize a different executable.")
    inventory_hash = hashlib.sha256(json.dumps(stable_runtime_inventory(state.get("baselineRuntimeInventory", {})), sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    if inventory_hash != frozen.get("x-rateldesk-runtime-baseline-sha256"):
        fail("approved_runtime_changed", "The launcher runtime baseline differs from its reviewed source checkpoint. Re-read and review the actual deployment.")
    deployment.docker.check_selection(state)
    expected_document = frozen if deployment.args.apply else approved
    actual_document = {key: value for key, value in deployment.proposal.items() if not key.startswith("x-rateldesk-")}
    expected_document = {key: value for key, value in expected_document.items() if not key.startswith("x-rateldesk-")}
    if actual_document != expected_document:
        fail("approved_effective_model_changed", "The full resolved deployment differs from the reviewed generation, including unrelated service settings or inherited interpolation values. Re-read and review the exact owner source/context; no values were printed.")
    current_inventory = read_private_json(deployment.output / "runtime-environment.json").get("x-rateldesk-runtime-inventory", {})
    unchanged = set(frozen.get("services", {})) - set(state.get("changedServices", []))
    baseline = state.get("baselineRuntimeInventory", {})
    if any(stable_runtime_inventory({name: current_inventory.get(name, {})}) != stable_runtime_inventory({name: baseline.get(name, {})}) for name in unchanged):
        fail("approved_unaffected_runtime_changed", "An unchanged service's actual identity or runtime changed after review. Re-read/review the deployment instead of accepting an unrelated recreation or image change.")
    for name, digest in state.get("exportHashes", {}).items():
        if Path(name).name != name or hashlib.sha256(read_bytes(deployment.output / name, MAX_BYTES)).hexdigest() != digest.casefold():
            fail("approved_export_changed", "A private reviewed checkpoint changed. Reopen setup to review the actual source; no hash was repaired.")
    for item in state.get("sourceCandidates", []):
        if hashlib.sha256(read_bytes(item["candidate"], MAX_SOURCE_BYTES)).hexdigest() != item["candidateSha256"]:
            fail("approved_candidate_changed", "The reviewed source candidate changed. Prepare and approve a new proposal before owner apply.")


def stable_runtime_inventory(inventory):
    result = json.loads(json.dumps(inventory))
    for value in result.values():
        value.pop("state", None)
        value.pop("healthStatus", None)
    return result


def revalidate_apply_runtime(deployment, state):
    deployment.docker.bind_daemon()
    deployment.observe_runtime(deployment.proposal)
    current = read_private_json(deployment.output / "runtime-environment.json").get("x-rateldesk-runtime-inventory", {})
    expected = state.get("baselineRuntimeInventory")
    if not expected or stable_runtime_inventory(current) != stable_runtime_inventory(expected):
        fail("approved_runtime_changed", "The actual containers, images, mounts or owner topology changed after review. Re-read and review the current deployment before applying; no stale approval was used.")


def apply_native(deployment):
    if deployment.manager != "release-compose":
        fail("manager_apply_required", "Save and apply the private reviewed source candidates through the existing deployment manager, then run the printed --verify command against its actual saved sources. The launcher does not overwrite manager-owned YAML.")
    state = read_private_json(deployment.output / "launcher-state.json")
    if state.get("intent") != "prepare-changes":
        fail("adoption_has_no_apply", "An adopted baseline has no deployment changes. Use --verify; no service was applied or restarted.")
    check_native_checkpoint(deployment, state)
    deployment.validate_resume_sources(deployment.proposal["x-rateldesk-source-files"], True)
    native_command(deployment, ["host", "approval-check", "--export-directory", str(deployment.output)])
    candidates = state.get("sourceCandidates", [])
    changed_services = state.get("changedServices")
    if (not isinstance(changed_services, list) or not changed_services or len(set(changed_services)) != len(changed_services)
            or not set(changed_services).issubset(deployment.proposal.get("services", {}))):
        fail("approved_service_impact_invalid", "The exact approved affected-service list is missing or inconsistent. Review the proposal before owner apply; no broad apply was attempted.")
    if not candidates:
        fail("no_deployment_changes", "The reviewed proposal has no source changes. Adopt and verify the existing deployment instead.")
    deployment.check_sources()
    revalidate_apply_runtime(deployment, state)
    # Release-owned writes retain a private byte-for-byte backup and the source's
    # existing mode. External sources never enter this branch.
    for item in candidates:
        source = Path(item["source"])
        if file_record(source)["sha256"] != item["originalSha256"]:
            fail("approved_sources_changed", "The saved source changed before apply. Review it again; no stale proposal was applied.")
    staged = []
    try:
        for item in candidates:
            source = Path(item["source"])
            candidate_data = read_bytes(item["candidate"])
            if hashlib.sha256(candidate_data).hexdigest() != item["candidateSha256"]:
                fail("approved_candidate_changed", "The reviewed source candidate changed before staging. Prepare and approve a new proposal before owner apply; no changed candidate was saved.")
            backup = deployment.output / ("backup-" + item["originalSha256"] + "-" + source.name)
            if not backup.exists():
                original_data = read_bytes(source)
                if hashlib.sha256(original_data).hexdigest() != item["originalSha256"]:
                    fail("approved_sources_changed", "The original source changed while its private backup was read. Review the current owner source before apply.")
                write_private(backup, original_data)
            if file_record(backup)["sha256"] != item["originalSha256"]:
                fail("approved_backup_changed", "A retained private backup differs from the reviewed original source. Restore its exact reviewed bytes or review a new generation before owner apply; the backup was not overwritten.")
            mode = stat.S_IMODE(source.stat().st_mode)
            descriptor, temporary = tempfile.mkstemp(dir=source.parent, prefix=".rateldesk-reviewed-")
            staged.append((item, source, temporary, mode))
            with os.fdopen(descriptor, "wb") as target:
                os.fchmod(target.fileno(), 0o600)
                target.write(candidate_data)
                target.flush(); os.fsync(target.fileno())
        deployment.check_sources()
        revalidate_apply_runtime(deployment, state)
        check_native_checkpoint(deployment, state)
        # Validate every staged source before replacing any owner file. Each
        # exact payload is checked again at its own atomic replacement boundary.
        for item, _, temporary, _ in staged:
            if file_record(temporary)["sha256"] != item["candidateSha256"]:
                fail("approved_staged_source_changed", "A privately staged source differs from its reviewed candidate. Review the retained checkpoints before owner apply; no staged changes were saved.")
        state["phase"] = "source-save-in-progress"
        state["savedSources"] = []
        write_private(deployment.output / "launcher-state.json", state)
        for item, source, temporary, mode in staged:
            if file_record(source)["sha256"] != item["originalSha256"]:
                fail("approved_sources_changed", "A source changed during save. Private backups and source-save progress were retained; reread the owner's sources and review recovery before apply.")
            if file_record(temporary)["sha256"] != item["candidateSha256"]:
                fail("approved_staged_source_changed", "A staged source changed during save. Private backups and source-save progress were retained; review recovery before owner apply.")
            os.chmod(temporary, mode)
            os.replace(temporary, source)
            state["savedSources"].append(str(source))
            write_private(deployment.output / "launcher-state.json", state)
    finally:
        for _, _, temporary, _ in staged:
            Path(temporary).unlink(missing_ok=True)
    state["phase"] = "source-saved-apply-pending"
    write_private(deployment.output / "launcher-state.json", state)
    # Compose computes and recreates only the actually changed services from the
    # original ordered saved sources. No replacement generated document exists.
    deployment.docker.run(deployment.compose + ["up", "--pull", "never", "--no-build", "-d", "--no-deps", *changed_services], maximum=16384, owner_tool=True)
    state["phase"] = "applied-verify-pending"
    write_private(deployment.output / "launcher-state.json", state)
    print("Reviewed source saved and owner apply completed; fresh verification pending:")
    print("  " + resume_command(deployment, "--verify"))
    return 0


def run_native_setup(deployment, args):
    if args.apply:
        return apply_native(deployment)
    options = args.wizard_options[1:] if args.wizard_options[:1] == ["--"] else args.wizard_options
    environment = native_environment(deployment)
    if args.verify:
        state = read_private_json(deployment.output / "launcher-state.json")
        check_native_checkpoint(deployment, state)
        arguments = ["host", "verify", "--export-directory", str(deployment.output)]
    elif args.preflight:
        arguments = ["host", "preflight", "--export-directory", str(deployment.output)]
    else:
        arguments = ["setup", "--target", "http-host", "--export-directory", str(deployment.output)]
    deployment.check_sources()
    baseline_inventory = read_private_json(deployment.output / "runtime-environment.json").get("x-rateldesk-runtime-inventory", {}) if not args.verify and not args.preflight else None
    prior_receipt = hashlib.sha256(read_bytes(deployment.output / "host-approval.json")).hexdigest() if not args.verify and not args.preflight and (deployment.output / "host-approval.json").is_file() else None
    result = native_command(deployment, [*arguments, *options], interactive=True)
    deployment.check_sources()
    if result or args.preflight:
        return result
    if args.verify:
        deployment.observe_runtime(deployment.proposal)
        deployment.check_sources()
        native_command(deployment, ["host", "observation-check", "--export-directory", str(deployment.output)])
        state["phase"] = "existing-verified-unchanged" if deployment.intent == "verify-existing" else "end-to-end-verified"
        write_private(deployment.output / "launcher-state.json", state)
        print("Existing deployment verified unchanged." if deployment.intent == "verify-existing" else "End-to-end verified after approved owner apply.")
        return 0
    if not (deployment.output / "host-approval.json").is_file() or (prior_receipt is not None and hashlib.sha256(read_bytes(deployment.output / "host-approval.json")).hexdigest() == prior_receipt):
        return 0  # cancelled before approval: no completed generation is invented.
    if baseline_inventory is not None:
        deployment.observe_runtime(deployment.proposal)
        current_inventory = read_private_json(deployment.output / "runtime-environment.json").get("x-rateldesk-runtime-inventory", {})
        if stable_runtime_inventory(current_inventory) != stable_runtime_inventory(baseline_inventory):
            fail("reviewed_runtime_changed", "The running deployment changed during administrator review. Reopen a fresh baseline/proposal review; no deployment was applied.")
    state = save_native_checkpoint(deployment)
    if deployment.intent == "verify-existing":
        print("Existing baseline adopted; no deployment changes. Fresh verification pending:")
    else:
        print("Changes prepared, not applied. Save/apply each candidate through the source owner:")
        for item in state["sourceCandidates"]:
            print("  " + shlex.quote(item["candidate"]) + " -> " + shlex.quote(item["source"]))
    print("  " + resume_command(deployment, "--verify"))
    if deployment.intent == "prepare-changes" and deployment.manager == "release-compose":
        print("Explicit release-owner apply: " + resume_command(deployment, "--apply"))
    return 0


def run_native_client(args):
    executable, _ = native_executable(args)
    options = args.wizard_options[1:] if args.wizard_options[:1] == ["--"] else args.wizard_options
    # Native client setup has no Docker/Compose preflight or source dependency.
    return subprocess.run([executable, "setup", "--target", "http-client", *options], check=False).returncode


def resume_command(deployment, mode):
    # Display only shell-quoted arguments generated by this helper. The helper
    # itself executes argument arrays and never evaluates this string.
    wrapper = Path(__file__).resolve().with_suffix(".sh")
    command = [str(wrapper), "--project", deployment.project, "--project-directory", str(deployment.directory),
               "--output-directory", str(deployment.output), "--manager", deployment.manager]
    for path in deployment.sources:
        command += ["--compose-file", str(path)]
    for path in deployment.env_files:
        command += ["--env-file", str(path)]
    for role in SERVICE_NAMES:
        if getattr(deployment, "roles", {}).get(role, role) != role:
            command += ["--" + role + "-service", deployment.roles[role]]
    if deployment.args.ca_file:
        command += ["--ca-file", deployment.args.ca_file]
    command += ["--intent", deployment.intent]
    if deployment.args.native_executable:
        command += ["--native-executable", deployment.args.native_executable]
    command.append(mode)
    selection = getattr(deployment.docker, "selection", {})
    if selection:
        command = ["env", *(f"{key}={selection[key]}" for key in DOCKER_SELECTION_KEYS if key in selection), *command]
    return shlex.join(command)


def apply_saved(deployment):
    if deployment.manager != "release-compose":
        fail("manager_apply_required", "Apply the complete reviewed deployment in the existing deployment manager, then rerun --verify. The launcher never replaces a manager-owned stack.")
    state = read_private_json(deployment.output / "launcher-state.json")
    check_saved_exports(deployment, state)
    deployment.prepare_storage(provision=False)
    check_effective_context(deployment, state)
    deployment.check_sources()
    for name in ("effective-deployment.json", "launcher-state.json", "deployment.json"):
        stage_snapshot(deployment, name)
    with legacy_compose_observation(deployment):
        try:
            deployment.docker.run(tooling_arguments(deployment) + ["--rm", deployment.image,
                "host", "approval-check", "--export-directory", HOST_TARGET], maximum=16384)
        except Prerequisite:
            fail("approval_revalidation", "Current administrator approval/authority could not be reconfirmed. Check the trusted Web URL and approval checkpoint, then retry; rerun ordinary setup if authority or the approved generation changed. Nothing was applied.")
        deployment.legacy_compose_fence()
        deployment.check_sources()
        expected = deployment.legacy_compose_proof["RATELDESK_SETUP_REVIEWED_DEPLOYMENT_SHA256"]
        if hashlib.sha256(private_observation_bytes(deployment.output / "deployment.json")).hexdigest() != expected:
            fail("legacy_recipe_changed", "The reviewed manager document changed during the authority check. No owner apply was started; review the changed source explicitly.")
        command = legacy_compose_context(deployment) + ["-f", str(deployment.output / "deployment.json"), "up", "--pull", "never", "--no-build", "-d", "api", "web", "mcp-http"]
        deployment.docker.run(command, maximum=16384)
    state["phase"] = "applied-verify-pending"
    write_private(deployment.output / "launcher-state.json", state)
    print("Applied — verify pending. Recreate completed; end-to-end verification still requires a fresh invocation:")
    print("  " + resume_command(deployment, "--verify"))
    return 0


def begin_verification(deployment):
    if getattr(deployment, "verification_started", False):
        return
    path = deployment.output / "launcher-state.json"
    if not path.is_file():
        fail("generation_missing", "No approved saved generation exists. Run ordinary setup first.")
    state = read_private_json(path)
    state["phase"] = "adopted-verify-pending" if deployment.intent == "verify-existing" else "applied-verify-pending"
    state["verificationStartedAtUtc"] = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    write_private(path, state)
    deployment.verification_started = True


def run_setup(deployment, args):
    if not deployment.legacy:
        return run_native_setup(deployment, args)
    if args.apply:
        return apply_saved(deployment)
    if args.verify:
        begin_verification(deployment)
    deployment.prepare_storage(provision=not args.preflight)
    stage_snapshot(deployment, "effective-deployment.json")
    if args.verify:
        stage_snapshot(deployment, "runtime-environment.json")
        state = read_private_json(deployment.output / "launcher-state.json")
        check_saved_exports(deployment, state)
        check_effective_context(deployment, state)
        stage_snapshot(deployment, "launcher-state.json")
        stage_snapshot(deployment, "deployment.json")
    deployment.check_sources()
    if args.verify:
        with legacy_compose_observation(deployment):
            return run_legacy_tool(deployment, args, state)
    return run_legacy_tool(deployment, args)


def run_legacy_tool(deployment, args, state=None):
    attempt = uuid.uuid4().hex
    name = f"{deployment.project}-mcp-setup-{attempt[:12]}"
    command = tooling_arguments(deployment) + ["--name", name, "--env", f"RATELDESK_SETUP_ATTEMPT={attempt}"]
    interactive = sys.stdin.isatty() and sys.stdout.isatty()
    if interactive:
        command += ["--interactive", "--tty"]
    if args.verify:
        arguments = ["host", "verify", "--export-directory", HOST_TARGET]
    elif args.preflight:
        arguments = ["host", "preflight", "--export-directory", HOST_TARGET]
    else:
        arguments = ["setup", "--mode", "host" if args.target == "http-host" else "client", "--export-directory", HOST_TARGET]
    options = args.wizard_options[1:] if args.wizard_options[:1] == ["--"] else args.wizard_options
    command += [deployment.image, *arguments, *options]
    try:
        environment = deployment.docker.process_environment() if isinstance(deployment.docker, Docker) else None
        # With --tty Docker sends the application's complete terminal stream on
        # stdout. Its own stderr can contain private daemon/proxy/source errors.
        # Keep those bytes private, including for a failed pristine image launch.
        with tempfile.TemporaryFile() as docker_error:
            completed = subprocess.run(["docker", *command], check=False, env=environment, stderr=docker_error)
            if completed.returncode in (125, 126, 127):
                fail("docker_tooling_unavailable", "Docker could not start the selected setup image. Check the same daemon, saved image and protected mounts, then rerun; raw Docker errors remain private.")
            if completed.returncode and docker_error.tell():
                print("Setup stopped: docker_tooling_exit: The selected setup process did not complete. Check its safe setup diagnostics and retry with the same Docker selection; raw Docker stderr remains private.", file=sys.stderr)
        deployment.check_sources()
        if completed.returncode != 0:
            return completed.returncode
        if args.preflight or args.target == "http-client":
            return 0
        if args.verify:
            # Browser approval and protocol checks can take minutes. Re-inspect
            # Docker now, rather than revalidating the earlier snapshot bytes.
            deployment.observe_runtime(deployment.proposal)
            stage_snapshot(deployment, "runtime-environment.json")
            deployment.check_sources()
            try:
                with legacy_compose_observation(deployment):
                    deployment.docker.run(tooling_arguments(deployment) + ["--rm", deployment.image,
                        "host", "observation-check", "--export-directory", HOST_TARGET], maximum=16384)
            except Prerequisite:
                fail("runtime_revalidation", "The final fresh Docker observation did not match the approved generation. Verification remains pending. Restore/recreate the reviewed deployment, then invoke --verify again; unchanged sources need no new host provisioning consent.")
            deployment.check_sources()
            state["phase"] = "end-to-end-verified"
            write_private(deployment.output / "launcher-state.json", state)
            print("End-to-end verified. Applied configuration, real MCP catalog and API-backed status passed; temporary verifier cleanup completed.")
            return 0
        return 0 if export_complete_document(deployment, name, attempt) else 2
    finally:
        # Named stores and manager/source files are deliberately retained.
        deployment.docker.run(["rm", name], optional=True, maximum=4096)


def parser():
    command = argparse.ArgumentParser(description="Set up an existing RatelDesk HTTP MCP deployment on its Docker host.",
        epilog=f"Host source verification requires tested Compose capabilities (v{COMPOSE_FLOOR}+ reference). Native client setup/doctor has no Compose requirement. Missing features offer existing verified standalone selection, explicit private pinned preparation, or cancellation. Original Docker config/context/TLS/auth helpers remain selected; receipts retain the setup-only tool digest. Client setup: ./setup-http-mcp.sh --target http-client. Explicit setup before serving needs saved Compose sources and a built existing image.")
    command.add_argument("--compose-binary", help="Existing standalone Compose executable; requires independently expected --compose-sha256.")
    command.add_argument("--compose-sha256", help="Expected upstream SHA-256 of --compose-binary; checked before every execution.")
    command.add_argument("--prepare-compose", metavar="PRIVATE_DIRECTORY", help="Explicit consent to prepare/reuse pinned setup-only Compose v2.39.4 in this private directory. Does not change the normal Docker plugin/config/context.")
    command.add_argument("--compose-cache", metavar="VERIFIED_PLATFORM_FILE", help="Offline source for --prepare-compose; checked against the repository-owned platform digest.")
    command.add_argument("--intent", choices=("verify-existing", "prepare-changes", "legacy-initializer"), default="verify-existing", help="Verify an existing deployment unchanged (default), prepare minimal source changes, or explicitly select the Advanced legacy initializer topology.")
    command.add_argument("--native-executable", help="Existing native rateldesk-mcp executable from a verified matching CLI/stdio distribution. Host inspection creates no tooling container.")
    command.add_argument("--api-service", help="Existing custom API service name; inferred from the application image when unambiguous.")
    command.add_argument("--web-service", help="Existing custom Web service name.")
    command.add_argument("--mcp-http-service", help="Existing custom MCP service name.")
    command.add_argument("--container")
    command.add_argument("--compose-file", action="append", default=[])
    command.add_argument("--env-file", action="append", default=[])
    command.add_argument("--project")
    command.add_argument("--project-directory")
    command.add_argument("--output-directory")
    command.add_argument("--image")
    command.add_argument("--ca-file", help="Existing public CA bundle for an explicitly trusted private HTTPS instance; mounted read-only into tooling.")
    command.add_argument("--manager", choices=("release-compose", "external-compose"))
    command.add_argument("--target", choices=("http-host", "http-client"), default="http-host")
    command.add_argument("--verify", action="store_true", help="Resume the approved generation with fresh bounded verification consent.")
    command.add_argument("--apply", action="store_true", help="Apply the saved reviewed release-owned deployment on this host.")
    command.add_argument("--preflight", action="store_true", help="Validate effective configuration without approval or mutation.")
    command.add_argument("wizard_options", nargs=argparse.REMAINDER, help="Options after -- are forwarded as literal setup arguments.")
    return command


def main(argv=None):
    os.umask(0o077)
    args = parser().parse_args(argv)
    if sum((args.verify, args.apply, args.preflight)) > 1:
        fail("mode_conflict", "Choose only one of --verify, --apply or --preflight.")
    if args.target != "http-host" and (args.verify or args.apply or args.preflight):
        fail("mode_conflict", "Client setup verifies its own consumer. --verify, --apply and --preflight belong to host setup.")
    if args.target == "http-client":
        return run_native_client(args)
    docker = Docker()
    docker.configure(args)
    docker.preflight()
    deployment = Deployment(args, docker)
    if args.verify:
        begin_verification(deployment)
    proposal = deployment.snapshot(verify=args.verify)
    if args.apply:
        deployment.validate_resume_sources(proposal["x-rateldesk-source-files"], True)
    print(f"Using saved Compose project {deployment.project}. Deployment owner: {deployment.manager}.")
    print("Deployment source remains authoritative. Private host checkpoints are separate from personal client profiles.")
    return run_setup(deployment, args)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Prerequisite as error:
        print(f"Setup not completed: {error}", file=sys.stderr)
        sys.exit(78)
    except (KeyboardInterrupt, EOFError):
        print("Setup cancelled. Saved private state was retained; rerun to resume.", file=sys.stderr)
        sys.exit(130)
    except (OSError, ValueError, TypeError, KeyError, UnicodeError):
        print("Setup not completed: deployment_prerequisite: The private saved deployment could not be resolved. Restore its Compose sources, protected providers and local images, then retry. No environment or credential was printed.", file=sys.stderr)
        sys.exit(78)
