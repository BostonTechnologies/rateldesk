#!/usr/bin/env python3
"""Bounded host-side Docker discovery, private storage, apply and resume.

Docker Compose owns source parsing, interpolation and merging. This helper never
interprets a shell command from deployment metadata and never prints a rendered
deployment, container environment, or Docker error containing its input.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import stat
import subprocess
import sys
import tempfile
import uuid
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


class Prerequisite(Exception):
    """An actionable error whose text is safe to show to the operator."""


def fail(code, action):
    raise Prerequisite(f"{code}: {action}")


def safe_label(value, maximum=256):
    if not isinstance(value, str) or len(value) > maximum or any(ord(c) < 32 or ord(c) == 127 for c in value):
        fail("deployment_metadata", "Saved Docker metadata is malformed. Restore its actual source or supply the explicit source options.")
    return value


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


def file_record(path):
    path = Path(path)
    if not path.is_absolute() or path.is_symlink() or not path.is_file():
        fail("source_unreadable", "Use an existing absolute regular source file. Restore missing files; symlink sources require their actual resolved path.")
    return {"path": str(path), "sha256": hashlib.sha256(read_bytes(path)).hexdigest()}


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

    def run(self, arguments, *, input_data=None, maximum=MAX_BYTES, optional=False):
        # Temporary files bound memory even when an untrusted daemon/source emits
        # huge output. Stderr is deliberately never copied into a public error.
        with tempfile.TemporaryFile() as result, tempfile.TemporaryFile() as error:
            try:
                completed = subprocess.run(["docker", *arguments], input=input_data,
                                           stdout=result, stderr=error, timeout=45, check=False,
                                           preexec_fn=bound_child_files if resource else None)
            except (OSError, subprocess.TimeoutExpired):
                fail("docker_unavailable", "Docker did not finish the bounded host operation. Check host Docker access and retry.")
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
                fail("docker_operation", "Docker could not resolve the saved deployment. Check its Compose sources, interpolation files and local images, then retry. No raw environment was printed.")
            result.seek(0)
            return result.read()

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
        if normalized in canonical:
            fail("environment_duplicate", "A relevant Docker environment key is duplicated. Remove the ambiguous source before setup.")
        canonical.add(normalized)
        result[key] = content
    return result


def relevant_environment(values):
    environment = environment_map(values)
    return [f"{key}={value}" for key, value in environment.items()
            if key.casefold() == "apibaseurl" or key.casefold().startswith(RELEVANT_PREFIXES)]


def effective_environment(image, inline):
    result = {key.replace(":", "__").casefold(): value for key, value in environment_map(image).items()}
    for key, value in environment_map(inline).items():
        canonical = key.replace(":", "__").casefold()
        if value is None:
            result.pop(canonical, None)
        else:
            result[canonical] = value
    return result


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
    environment = effective_environment(proposal["x-rateldesk-image-environment"]["mcp-http"], proposal["services"]["mcp-http"].get("environment", {}))
    suffixes = {"CredentialMode": "credential_mode", "IntegrationCredential": "integration_credential", "IntegrationCredentialFile": "integration_credential_file",
                "AuthentikAppPassword": "authentik_app_password", "AuthentikAppPasswordFile": "authentik_app_password_file"}
    apply({field: environment["rateldesk_mcp_" + suffix].strip() for field, suffix in suffixes.items() if "rateldesk_mcp_" + suffix in environment})
    apply({field: environment["helpdesk__mcp__outbound__" + field.casefold()].strip() for field in fields
           if "helpdesk__mcp__outbound__" + field.casefold() in environment})
    command = proposal["services"]["mcp-http"].get("command", proposal.get("x-rateldesk-image-commands", {}).get("mcp-http", []))
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
        if foundation and not str(metadata.get("Config", {}).get("Image", "")).startswith("ghcr.io/bostontechnologies/rateldesk-web:"):
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


class Deployment:
    def __init__(self, args, docker):
        self.args, self.docker = args, docker
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
            file_record(path)
        self.manager = args.manager or labels.get(MANAGER_LABEL) or (bundle or {}).get("manager")
        if self.manager not in ("release-compose", "external-compose"):
            if not sys.stdin.isatty() or not sys.stdout.isatty():
                fail("deployment_manager_unknown", "Supply --manager external-compose for a deployment manager, or --manager release-compose for an explicitly release-owned bundle. Unknown ownership cannot authorize apply.")
            print("Who applies this saved deployment?")
            print("  1) Existing deployment manager — export the complete document")
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
        selected_is_mcp = labels.get("com.docker.compose.service") == "mcp-http"
        self.image = args.image or ((self.metadata or {}).get("Config", {}).get("Image") if selected_is_mcp else None) or (bundle or {}).get("image")
        approved_addition = read_private_json(state_path).get("mcpProposed", False) if state_path.is_file() else False
        self.foundation = bool(bundle or args.image or labels.get("com.docker.compose.service") == "web" or approved_addition)
        self.compose = ["compose", "--project-directory", str(self.directory), "--project-name", self.project]
        for path in self.env_files or [Path("/dev/null")]:
            self.compose += ["--env-file", str(path)]
        for path in self.sources:
            self.compose += ["-f", str(path)]

    def snapshot(self, verify=False):
        initial = [file_record(path) for path in self.sources + self.env_files]
        if self.args.ca_file:
            initial.append(file_record(self.args.ca_file))
        unresolved = self.docker.json(self.compose + ["config", "--no-env-resolution", "--format", "json"])
        source_document = self.docker.json(self.compose + ["config", "--no-env-resolution", "--no-interpolate", "--format", "json"])
        write_private(self.output / ".compose-source.json", source_document)
        source_paths = [Path(item["path"]) for item in initial]
        for service_name, service in unresolved.get("services", {}).items():
            for entry in service.get("env_file", []):
                path = Path(entry if isinstance(entry, str) else entry.get("path", ""))
                required = isinstance(entry, str) or entry.get("required", True)
                if required or path.exists():
                    source_paths.append(path)
            for mount in service.get("volumes", []):
                target = mount.get("target", "") if isinstance(mount, dict) else ""
                if mount.get("type") == "bind" and (target.startswith("/app/appsettings")
                        or service_name == "mcp-config-init" and target == "/input/config.json"
                        or service_name == "mcp-http" and target.endswith(".json")):
                    source_paths.append(Path(mount.get("source", "")))
        # Hash env_file and protected binds BEFORE the fully resolved render;
        # a change in the render window must not become an approved generation.
        provider_initial = [file_record(path) for path in dict.fromkeys(source_paths)]
        proposal = self.docker.json(self.compose + ["config", "--format", "json"])
        services = proposal.get("services", {})
        self.rendered = proposal
        if "mcp-http" not in services:
            image = self.image
            if not image:
                api_image = services.get("api", {}).get("image", "")
                prefix = "ghcr.io/bostontechnologies/rateldesk-api:"
                if api_image.startswith(prefix):
                    image = "ghcr.io/bostontechnologies/rateldesk-mcp-http:" + api_image[len(prefix):]
            if not self.foundation or not image:
                fail("mcp_service_missing", "The saved sources have no mcp-http service or exact release image. Use the verified matching deployment archive, or include the existing MCP source.")
            # This is an explicitly reviewed service addition, not a guessed
            # running configuration. The first Termina page still asks the URL;
            # no placeholder origin/resource is rendered or persisted.
            services["mcp-http"] = {"image": image, "environment": {}, "volumes": []}
            proposal["x-rateldesk-mcp-proposed"] = True
            self.image = image
        self.image = self.image or services["mcp-http"].get("image")
        if not self.image:
            fail("image_missing", "Build the existing MCP image, then supply --image with that exact local image. Setup never downloads packages or invents an image family.")
        if self.metadata and not self.foundation and services["mcp-http"].get("image") != self.image:
            fail("image_source_mismatch", "The saved MCP image and selected container disagree. Restore the actual source before setup.")
        image_environment, image_ids, protected, image_entrypoints, image_commands = {}, {}, {}, {}, {}
        self.provider_source_records = {}
        for name in SERVICE_NAMES:
            if name not in services:
                fail("service_missing", f"The saved deployment has no {name} service. Supply the complete saved deployment before host setup.")
            image = services[name].get("image")
            if not image:
                fail("image_missing", f"The saved {name} service needs a built local image reference before setup.")
            metadata = self.docker.json(["image", "inspect", "--format", "{{json .}}", image])
            image_ids[name] = metadata["Id"]
            image_environment[name] = relevant_environment(metadata.get("Config", {}).get("Env", []))
            image_entrypoints[name] = metadata.get("Config", {}).get("Entrypoint") or []
            image_commands[name] = metadata.get("Config", {}).get("Cmd") or []
            protected[name] = self.configuration_providers(name, services[name], image, source_paths,
                                                           image_environment=image_environment[name], image_command=image_commands[name])
        records = [file_record(path) for path in dict.fromkeys(source_paths)]
        if len(records) > MAX_FILES:
            fail("sources_oversized", "The effective deployment has too many protected providers. Consolidate the saved sources before setup.")
        if any(file_record(item["path"]) != item for item in provider_initial):
            fail("sources_changed", "Saved sources changed during rendering. Rerun setup with stable sources.")
        if any(file_record(item["path"]) != item for item in self.provider_source_records.values()):
            fail("sources_changed", "A protected provider changed during inspection. Rerun setup with stable sources.")
        proposal["x-rateldesk-source-files"] = records
        proposal["x-rateldesk-image-environment"] = image_environment
        proposal["x-rateldesk-image-ids"] = image_ids
        proposal["x-rateldesk-image-entrypoints"] = image_entrypoints
        proposal["x-rateldesk-image-commands"] = image_commands
        proposal["x-rateldesk-protected-configuration"] = protected
        proposal["x-rateldesk-deployment-manager"] = self.manager
        proposal["x-rateldesk-project-directory"] = str(self.directory)
        proposal["x-rateldesk-project-name"] = self.project
        self.capture_secret_mounts(proposal)
        self.validate_resume_sources(records, verify)
        write_private(self.output / "effective-deployment.json", proposal)
        write_private(self.output / "source-state.json", {"sources": records})
        if verify:
            self.observe_runtime(proposal)
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
            for mount in proposal["services"]["mcp-http"].get("volumes", []):
                target = mount.get("target", "")
                if target and (path == target or path.startswith(target + "/")):
                    source = mount.get("source")
                    if mount.get("type") == "volume":
                        source = proposal.get("volumes", {}).get(source, {}).get("name") or source
                    candidates.append((len(target), {"type": mount.get("type"), "source": source, "target": target}))
            for secret in proposal["services"]["mcp-http"].get("secrets", []):
                name = secret if isinstance(secret, str) else secret.get("source")
                target = "/run/secrets/" + (secret.get("target", name) if isinstance(secret, dict) else name)
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
        result = self.docker.run(["run", "--rm", "--pull", "never", "--network", "none", "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges", "--user", "10001:10001", "--mount", mount_argument(descriptor) + ",readonly",
            "--entrypoint", "/bin/sh", self.image, "-ec",
            'test -f "$1"; test "$(wc -c < "$1")" -le 16384; sha256sum "$1"', "secret-hash", descriptor["path"]], maximum=8192)
        digest = result.decode().split(" ", 1)[0]
        if not re.fullmatch(r"[a-f0-9]{64}", digest):
            fail("secret_reference_hash", "The protected secret could not be safely hashed as the serving identity. Restore its readable private file before approval.")
        return digest

    def configuration_providers(self, name, service, image, source_paths, container=None, image_environment=None, image_command=None):
        environment = effective_environment(image_environment or [], service.get("environment", {}))
        deployment_environment = environment.get("dotnet_environment", environment.get("aspnetcore_environment", "Production"))
        command = service.get("command", image_command or [])
        if isinstance(command, list):
            for index, argument in enumerate(command):
                if not isinstance(argument, str):
                    fail("command_invalid", "The saved service command must contain bounded literal string arguments.")
                key, separator, value = argument.lstrip("-/").partition("=")
                if key.casefold() == "environment":
                    deployment_environment = value if separator else command[index + 1] if index + 1 < len(command) else ""
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", deployment_environment):
            fail("environment_name", "The saved .NET environment name is invalid. Restore the serving environment before setup.")
        replacements = {}
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
        if container is None:
            identifier = self.docker.run(["create", "--network", "none", "--entrypoint", "/bin/true", image], maximum=256).decode().strip()
            if not re.fullmatch(r"[a-f0-9]{12,64}", identifier):
                fail("image_inspection", "The saved image could not be inspected safely. Restore its local image and retry.")
            temporary = container = identifier
        providers = []
        try:
            for index in range(3 if name == "mcp-http" else 2):
                if index == 2:
                    selected = environment.get("helpdesk__mcp__configurationpath", selected_json_path)
                    mcp_config = selected if isinstance(selected, str) and selected.strip() else environment.get("rateldesk_mcp_config")
                    if not mcp_config:
                        continue
                    safe_label(mcp_config, 4096)
                    if not mcp_config.startswith("/"):
                        fail("outbound_provider", "The selected MCP configuration must have an explicit absolute container path. Restore its owning provider before setup.")
                    provider_paths.append(mcp_config)
                    for mount in service.get("volumes", []):
                        if mount.get("type") == "bind" and mount.get("target") == mcp_config:
                            replacements[mcp_config] = Path(mount["source"])
                        elif mount.get("type") == "bind" and mcp_config.startswith(mount.get("target", "") + "/"):
                            relative = mcp_config[len(mount["target"]) + 1:]
                            if ".." in Path(relative).parts:
                                fail("protected_provider", "A protected provider escapes its mount. Restore its explicit absolute configuration path.")
                            replacements[mcp_config] = Path(mount["source"]) / relative
                    initializer = getattr(self, "rendered", {}).get("services", {}).get("mcp-config-init", {})
                    for mount in initializer.get("volumes", []):
                        if mount.get("type") == "bind" and mount.get("target") == "/input/config.json":
                            replacements.setdefault(mcp_config, Path(mount["source"]))
                path = provider_paths[index]
                if path in replacements and temporary:
                    host_path = replacements[path]
                    record = file_record(host_path)
                    source_paths.append(host_path)
                    content = read_bytes(host_path)
                    if hashlib.sha256(content).hexdigest() != record["sha256"]:
                        fail("sources_changed", "A protected provider changed while reading. Retry with stable sources.")
                    self.provider_source_records[str(host_path)] = record
                    origin = str(host_path)
                else:
                    destination = self.output / f".provider-{name}-{index}.json"
                    destination.unlink(missing_ok=True)
                    copied = self.docker.run(["cp", f"{container}:{path}", str(destination)], optional=True, maximum=4096)
                    if copied is None:
                        if name == "mcp-http" and path == mcp_config:
                            # Named config volume before first start: find its
                            # exact saved initializer bind; do not adopt a key
                            # from an unrelated deployment.
                            _, current = unique_container(self.docker, self.project, "mcp-http")
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
        observed = {"services": {}, "x-rateldesk-image-ids": {}, "x-rateldesk-protected-configuration": {}}
        for name in SERVICE_NAMES:
            identifier, metadata = unique_container(self.docker, self.project, name, running=True)
            if not identifier:
                fail("runtime_missing", f"A running {name} container is required. Apply the reviewed deployment, then rerun --verify.")
            image = metadata.get("Image")
            if image != proposal["x-rateldesk-image-ids"][name]:
                fail("runtime_image_mismatch", f"The running {name} image differs from the reviewed deployment. Recreate that service with the approved image before verification.")
            service = {"environment": environment_map(relevant_environment(metadata.get("Config", {}).get("Env", []))),
                       "image": metadata.get("Config", {}).get("Image"),
                       "command": metadata.get("Config", {}).get("Cmd") or [],
                       "entrypoint": metadata.get("Config", {}).get("Entrypoint") or [],
                       "volumes": [{"type": mount.get("Type"), "source": mount.get("Name") or mount.get("Source"),
                                    "target": mount.get("Destination"), "read_only": not mount.get("RW", False)}
                                   for mount in metadata.get("Mounts", [])]}
            observed["services"][name] = service
            observed["x-rateldesk-image-ids"][name] = image
            observed["x-rateldesk-protected-configuration"][name] = self.configuration_providers(name, service, image, [], identifier)
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
        if (self.output / "launcher-state.json").is_file():
            expected_storage = read_private_json(self.output / "launcher-state.json").get("storage", {})
            if storage != expected_storage:
                fail("runtime_storage_mismatch", "The running MCP stores differ from the approved mounts. Recreate it from the reviewed complete document before verification.")
        write_private(self.output / "runtime-environment.json", observed)

    def validate_resume_sources(self, sources, verify):
        if not verify:
            return
        state_path = self.output / "launcher-state.json"
        if not state_path.is_file():
            fail("generation_missing", "No approved saved generation exists. Run ordinary setup first.")
        saved = read_private_json(state_path)
        if saved.get("sources") == sources:
            return
        # An external manager may persist exactly the one complete reviewed
        # document over its original single source. No arbitrary edit is allowed.
        if self.manager == "external-compose" and len(self.sources) == 1:
            old = saved.get("sources", [])
            changed = [item for item in sources if item not in old]
            if (len(sources) == len(old) and len(changed) == 1
                    and changed[0]["path"] == str(self.sources[0])
                    and changed[0]["sha256"] == saved.get("deploymentSha256")):
                return
        fail("approved_sources_changed", "Saved sources changed after approval. Run ordinary setup to review the current deployment; --verify cannot approve changed sources.")

    def check_sources(self):
        if any(file_record(item["path"]) != item for item in self.proposal["x-rateldesk-source-files"]):
            fail("sources_changed", "The saved deployment changed during setup. Nothing was applied. Rerun ordinary setup to review the current source.")
        for descriptor in getattr(self, "secret_mounts", []):
            if self.secret_digest(descriptor) != descriptor["sha256"]:
                fail("secret_source_changed", "A protected downstream secret changed during setup. Rerun ordinary setup to review the current generation; no identity was rotated.")

    def prepare_storage(self, provision=True):
        storage = {}
        if self.metadata is None:
            _, current = unique_container(self.docker, self.project, "mcp-http")
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
                "--env", f"RATELDESK_SETUP_DEPLOYMENT_MANAGER={deployment.manager}"]
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
    # Compose performs the merge, including list/key semantics. Keep env_file
    # declarations and interpolation expressions in a complete manager document;
    # do not flatten them into an ephemeral values-only overlay.
    patch = read_private_json(deployment.output / "compose.host.json")
    for mount in patch.get("services", {}).get("mcp-config-init", {}).get("volumes", []):
        if mount.get("target") == "/input/config.json":
            mount["source"] = str(deployment.output / "mcp-outbound.json")
    write_private(deployment.output / ".export-patch.json", patch)
    command = ["compose", "--project-directory", str(deployment.directory), "--project-name", deployment.project]
    for path in deployment.env_files or [Path("/dev/null")]:
        command += ["--env-file", str(path)]
    command += ["-f", str(deployment.output / ".compose-source.json"), "-f", str(deployment.output / ".export-patch.json"),
                "config", "--no-interpolate", "--no-env-resolution", "--format", "json"]
    complete = deployment.docker.json(command)
    write_private(deployment.output / "deployment.json", complete)
    deployment.check_sources()
    state = {"schemaVersion": 1, "project": deployment.project, "manager": deployment.manager,
             "sources": deployment.proposal["x-rateldesk-source-files"], "sourceHash": receipt["sourceHash"],
             "composeFiles": [str(path) for path in deployment.sources], "envFiles": [str(path) for path in deployment.env_files],
             "mcpProposed": deployment.proposal.get("x-rateldesk-mcp-proposed", False),
             "proposalHash": receipt.get("proposalHash"), "exportHashes": export_hashes,
             "deploymentSha256": hashlib.sha256(read_bytes(deployment.output / "deployment.json", MAX_BYTES)).hexdigest(),
             "phase": "saved-apply-pending", "storage": deployment.storage}
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
    if deployment.args.ca_file:
        command += ["--ca-file", deployment.args.ca_file]
    command.append(mode)
    return shlex.join(command)


def apply_saved(deployment):
    if deployment.manager != "release-compose":
        fail("manager_apply_required", "Apply the complete reviewed deployment in the existing deployment manager, then rerun --verify. The launcher never replaces a manager-owned stack.")
    state = read_private_json(deployment.output / "launcher-state.json")
    check_saved_exports(deployment, state)
    deployment.proposal["x-rateldesk-storage"] = state.get("storage", {})
    write_private(deployment.output / "effective-deployment.json", deployment.proposal)
    check_effective_context(deployment, state)
    deployment.check_sources()
    command = ["compose", "--project-directory", str(deployment.directory), "--project-name", deployment.project]
    for path in deployment.env_files or [Path("/dev/null")]:
        command += ["--env-file", str(path)]
    command += ["-f", str(deployment.output / "deployment.json"), "up", "-d", "api", "web", "mcp-http"]
    deployment.docker.run(command, maximum=16384)
    state["phase"] = "applied-verify-pending"
    write_private(deployment.output / "launcher-state.json", state)
    print("Applied — verify pending. Recreate completed; end-to-end verification still requires a fresh invocation:")
    print("  " + resume_command(deployment, "--verify"))
    return 0


def run_setup(deployment, args):
    if args.apply:
        return apply_saved(deployment)
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
        completed = subprocess.run(["docker", *command], check=False)
        deployment.check_sources()
        if completed.returncode != 0:
            return completed.returncode
        if args.preflight or args.target == "http-client":
            return 0
        if args.verify:
            # The host verify command returns success only after observed runtime,
            # authenticated initialize/catalog/API status and verifier cleanup.
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
        epilog="Client setup: ./setup-http-mcp.sh --target http-client. Explicit setup before serving is valid needs only the saved Compose sources and a built existing image.")
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
    docker = Docker()
    deployment = Deployment(args, docker)
    proposal = deployment.snapshot(verify=args.verify)
    if args.apply:
        deployment.validate_resume_sources(proposal["x-rateldesk-source-files"], True)
    print(f"Using saved Compose project {deployment.project}. Deployment owner: {deployment.manager}.")
    print("Private server setup and personal client stores are separate and persistent.")
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
