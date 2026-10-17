"""Private real legacy/standalone selections for explicit host PTY acceptance."""
from contextlib import contextmanager
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from types import SimpleNamespace
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("private_compose_launcher", ROOT / "tools/mcp/setup-http-mcp.py")
launcher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(launcher)
LEGACY_DIGEST = "2f61856d1b8c9de29ffdaedaa1c6d0a5fc5c79da45068f1f4310feed8d3a3f61"


def checked(arguments, *, environment=None):
    completed = subprocess.run(arguments, env=environment, capture_output=True, timeout=45)
    if completed.returncode:
        raise RuntimeError("Private Compose acceptance prerequisite failed; raw Docker output withheld")
    return completed.stdout


@contextmanager
def legacy_compose_fixture(*, compose_cache=None, legacy_cache=None, download=False, host=None):
    """Never copies real Docker config/auth/TLS or changes the active context.

    `download=True` is the explicit disposable-fixture download opt-in. Otherwise
    both offline fixture files are required. The returned terminal_prefix uses
    env's supported unset/assignment arguments to isolate only each child.
    """
    if os.uname().machine != "x86_64":
        raise RuntimeError("The real legacy Compose fixture is pinned for Linux x86_64")
    if not download and (not compose_cache or not legacy_cache):
        raise RuntimeError("Supply both verified fixture caches or explicitly opt into disposable downloads")
    normal_version = checked(["docker", "compose", "version", "--short"])
    normal_context = checked(["docker", "context", "show"])
    host = host or checked(["docker", "context", "inspect", "--format", "{{.Endpoints.docker.Host}}"] ).decode().strip()
    if not host.startswith("unix://"):
        raise RuntimeError("Use a local Unix Docker daemon for this private acceptance fixture")
    with tempfile.TemporaryDirectory(prefix="rateldesk host private compose ") as temporary:
        directory = Path(temporary)
        tool = launcher.compose_toolchain.prepare(directory / "verified setup renderer", launcher.fail, cache=compose_cache)
        config = directory / "original Docker configuration"
        plugins = config / "cli-plugins"
        plugins.mkdir(parents=True)
        legacy = plugins / "docker-compose"
        if legacy_cache:
            shutil.copyfile(legacy_cache, legacy)
        else:
            request = urllib.request.Request("https://github.com/docker/compose/releases/download/v2.26.1/docker-compose-linux-x86_64",
                headers={"User-Agent": "RatelDesk-explicit-host-private-Compose-acceptance"})
            with urllib.request.urlopen(request, timeout=30) as source, legacy.open("wb") as target:
                total = 0
                while chunk := source.read(1024 * 1024):
                    total += len(chunk)
                    if total > 128 * 1024 * 1024:
                        raise RuntimeError("Legacy Compose fixture exceeded size bound")
                    target.write(chunk)
        if hashlib.sha256(legacy.read_bytes()).hexdigest() != LEGACY_DIGEST:
            raise RuntimeError("Legacy Compose fixture integrity mismatch; file was not executed")
        legacy.chmod(0o700)
        environment = {key: value for key, value in os.environ.items() if key not in launcher.DOCKER_SELECTION_KEYS}
        context = "host-onramp-" + uuid.uuid4().hex[:12]
        environment.update(DOCKER_CONFIG=str(config), DOCKER_CONTEXT=context)
        checked(["docker", "context", "create", context, "--docker", "host=" + host], environment=environment)
        helper_directory = directory / "original credential helpers"
        helper_directory.mkdir()
        helper = helper_directory / "docker-credential-rateldesk-onramp-fixture"
        helper.write_text("#!/bin/sh\nprintf '%s\\n' 'Synthetic fixture has no registry credentials' >&2\nexit 1\n")
        helper.chmod(0o700)
        environment["PATH"] = str(helper_directory) + os.pathsep + environment.get("PATH", "")
        config_bytes = json.dumps({"currentContext": context,
            "credHelpers": {"fixture.invalid": "rateldesk-onramp-fixture"}}).encode()
        (config / "config.json").write_bytes(config_bytes)

        def terminal_prefix(arguments, *, selected=False, executable=None):
            prefix = [item for key in launcher.DOCKER_SELECTION_KEYS for item in ("-u", key)]
            prefix += ["DOCKER_CONFIG=" + str(config), "DOCKER_CONTEXT=" + context, "PATH=" + environment["PATH"]]
            if selected:
                prefix += [launcher.compose_toolchain.BINARY_KEY + "=" + str(tool.path),
                    launcher.compose_toolchain.DIGEST_KEY + "=" + tool.expected]
            return [*prefix, str(executable or ROOT / "tools/mcp/setup-http-mcp.sh"), *arguments]

        def select_existing(terminal):
            terminal.until(r"Choice \[3\]:", seconds=60)
            terminal.send(b"1\n")
            terminal.until("Existing Compose executable", seconds=30)
            terminal.send((str(tool.path) + "\n").encode())
            terminal.until("Expected upstream SHA-256", seconds=30)
            terminal.send((tool.expected + "\n").encode())

        def assert_unchanged():
            if hashlib.sha256(legacy.read_bytes()).hexdigest() != LEGACY_DIGEST or (config / "config.json").read_bytes() != config_bytes:
                raise AssertionError("The original private plugin/configuration changed during host acceptance")
            if checked(["docker", "compose", "version", "--short"], environment=environment).strip() != b"2.26.1":
                raise AssertionError("The original private plugin was replaced")
            if checked(["docker", "compose", "version", "--short"]) != normal_version or checked(["docker", "context", "show"]) != normal_context:
                raise AssertionError("The normal Docker plugin or active context changed")

        fixture = SimpleNamespace(directory=directory, environment=environment, context=context, config=config, tool=tool,
            terminal_prefix=terminal_prefix, select_existing=select_existing, assert_unchanged=assert_unchanged)
        try:
            yield fixture
        finally:
            assert_unchanged()
