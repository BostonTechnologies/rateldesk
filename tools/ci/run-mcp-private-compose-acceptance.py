#!/usr/bin/env python3
"""Opt-in real standalone/context/auth-helper onramp; no deployment mutation.

Supply verified offline fixture files or explicitly opt in to pinned downloads.
Only private temporary Docker configuration/context files are created. No engine
resources are created, and no registry credentials or TLS private keys are copied.
"""
import argparse
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import urllib.request
from unittest.mock import patch

from runpy import run_path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("mcp_launcher", ROOT / "tools/mcp/setup-http-mcp.py")
launcher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(launcher)
terminal_support = run_path(str(ROOT / "tools/ci/run-mcp-host-lifecycle-acceptance.py"))
Terminal = terminal_support["Terminal"]
LEGACY_DIGEST = "2f61856d1b8c9de29ffdaedaa1c6d0a5fc5c79da45068f1f4310feed8d3a3f61"


def checked(arguments, **kwargs):
    completed = subprocess.run(arguments, capture_output=True, timeout=45, **kwargs)
    if completed.returncode:
        raise RuntimeError("Private Compose fixture command failed; raw Docker output withheld")
    return completed.stdout


def legacy_fixture(destination, args):
    if args.legacy_cache:
        shutil.copyfile(args.legacy_cache, destination)
    elif args.download:
        request = urllib.request.Request("https://github.com/docker/compose/releases/download/v2.26.1/docker-compose-linux-x86_64",
            headers={"User-Agent": "RatelDesk-explicit-private-Compose-acceptance"})
        with urllib.request.urlopen(request, timeout=30) as source, destination.open("wb") as target:
            total = 0
            while chunk := source.read(1024 * 1024):
                total += len(chunk)
                assert total < 128 * 1024 * 1024, "Legacy fixture exceeded size bound"
                target.write(chunk)
    else:
        raise RuntimeError("Supply --legacy-cache or explicitly --download")
    assert hashlib.sha256(destination.read_bytes()).hexdigest() == LEGACY_DIGEST
    destination.chmod(0o700)


class Registry(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(401)
        self.send_header("WWW-Authenticate", 'Basic realm="synthetic-private-compose-fixture"')
        self.end_headers()

    def log_message(self, *arguments):
        pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--download", action="store_true")
    parser.add_argument("--compose-cache", type=Path)
    parser.add_argument("--legacy-cache", type=Path)
    args = parser.parse_args()
    if os.uname().machine != "x86_64":
        parser.error("The legacy capability fixture is pinned for Linux x86_64")
    if not args.download and not args.compose_cache:
        parser.error("Supply --compose-cache or explicitly --download")
    normal_version = checked(["docker", "compose", "version", "--short"])
    normal_context = checked(["docker", "context", "show"])
    host = checked(["docker", "context", "inspect", "--format", "{{.Endpoints.docker.Host}}"] ).decode().strip()
    if not host.startswith("unix://"):
        parser.error("Use a local Unix Docker daemon for this disposable fixture")
    with tempfile.TemporaryDirectory(prefix="rateldesk private compose acceptance ") as temporary:
        directory = Path(temporary)
        tool = launcher.compose_toolchain.prepare(directory / "private setup tool", launcher.fail, cache=args.compose_cache)
        config = directory / "original Docker config"
        plugins = config / "cli-plugins"
        plugins.mkdir(parents=True)
        legacy = plugins / "docker-compose"
        legacy_fixture(legacy, args)
        environment = os.environ.copy()
        for key in launcher.DOCKER_SELECTION_KEYS:
            environment.pop(key, None)
        environment.update(DOCKER_CONFIG=str(config), DOCKER_CONTEXT="private-fixture-context")
        checked(["docker", "context", "create", "private-fixture-context", "--docker", "host=" + host], env=environment)
        registry = ThreadingHTTPServer(("127.0.0.1", 0), Registry)
        thread = threading.Thread(target=registry.serve_forever, daemon=True)
        thread.start()
        try:
            authority = "127.0.0.1:" + str(registry.server_port)
            helper_directory = directory / "original credential helpers"
            helper_directory.mkdir()
            marker = directory / "credential helper invoked"
            helper = helper_directory / "docker-credential-rateldesk-private-fixture"
            helper.write_text("#!/usr/bin/env python3\nimport pathlib,sys\n"
                f"pathlib.Path({str(marker)!r}).write_text(sys.argv[1])\n"
                "sys.stdin.read()\nprint('{\"Username\":\"synthetic\",\"Secret\":\"synthetic\"}')\n")
            helper.chmod(0o700)
            config_bytes = json.dumps({"currentContext": "private-fixture-context",
                "credHelpers": {authority: "rateldesk-private-fixture"}}).encode()
            (config / "config.json").write_bytes(config_bytes)
            environment["PATH"] = str(helper_directory) + os.pathsep + environment.get("PATH", "")
            with patch.dict(os.environ, environment, clear=True):
                before = launcher.Docker()
                try:
                    before.preflight()
                    raise AssertionError("Legacy plugin unexpectedly passed required capabilities")
                except launcher.Prerequisite as failure:
                    assert str(failure).startswith("compose_feature_unsupported:")
                # Real terminal reaches the production choice before source read
                # or browser consent, then the verified existing tool is used.
                terminal = Terminal(["--compose-file", "/missing-source-must-not-be-read-first", "--project", "fixture",
                    "--manager", "external-compose", "--preflight"], directory, environment=environment)
                try:
                    terminal.until(r"Choice \[3\]:")
                    terminal.send(b"1\n")
                    terminal.until("Existing Compose executable")
                    terminal.send((str(tool.path) + "\n").encode())
                    terminal.until("Expected upstream SHA-256")
                    terminal.send((tool.expected + "\n").encode())
                    terminal.finish(expected=78)
                    assert "source_unreadable:" in terminal.text(), "Selected modern tool did not complete prerequisite recovery"
                    assert "Short code:" not in terminal.text(), "Consent started before source validation"
                finally:
                    terminal.close()
                selected = launcher.Docker()
                selected.configure(launcher.parser().parse_args(["--compose-binary", str(tool.path), "--compose-sha256", tool.expected]))
                selected.preflight()
                assert selected.compose_version == "2.39.4"
                assert selected.selection["DOCKER_CONTEXT"] == "private-fixture-context"
                assert selected.process_environment()["DOCKER_CONFIG"] == str(config)
                # Ordered source and interpolation files with spaces are rendered
                # by both setup and the actual older deployment-owner plugin.
                first, second = directory / "first env", directory / "second env"
                first.write_text("FIXTURE_ORDER=first\n")
                second.write_text("FIXTURE_ORDER=last\n")
                base, override = directory / "operator source.yml", directory / "operator override.yml"
                base.write_text("# retained operator comment\nservices:\n  probe:\n    image: postgres:16\n    environment:\n      ORDER: ${FIXTURE_ORDER}\n")
                override.write_text("services:\n  probe:\n    labels:\n      fixture: preserved\n")
                source_bytes = (base.read_bytes(), override.read_bytes())
                arguments = ["compose", "--project-directory", str(directory), "--project-name", "private-onramp-fixture",
                    "--env-file", str(first), "--env-file", str(second), "-f", str(base), "-f", str(override), "config", "--format", "json"]
                modern = selected.json(arguments)
                owner = json.loads(selected.run(arguments, owner_tool=True))
                assert modern["services"] == owner["services"]
                assert modern["services"]["probe"]["environment"]["ORDER"] == "last"
                assert source_bytes == (base.read_bytes(), override.read_bytes())
                # Supported standalone mode resolves the same real named context
                # and rejects a missing context rather than falling back locally.
                assert selected.json(["compose", "ls", "--all", "--format", "json"]) == json.loads(before.run(["compose", "ls", "--all", "--format", "json"]))
                missing = environment.copy()
                missing["DOCKER_CONTEXT"] = "missing-context-must-not-fallback"
                failed = subprocess.run([str(tool.path), "ls", "--format", "json"], env=missing, capture_output=True, timeout=30)
                assert failed.returncode != 0
                # Force an actual registry-auth path. The original credential
                # helper must be invoked; the synthetic pull is expected to fail.
                pull_source = directory / "credential helper fixture.yml"
                pull_source.write_text("services:\n  fixture:\n    image: " + authority + "/synthetic/never-published:fixture\n")
                selected.run(["compose", "-f", str(pull_source), "pull"], maximum=16384, optional=True)
                assert marker.read_text() == "get", "Standalone selection did not retain original credential helper"
                receipt = {"dockerSelection": selected.selection, "dockerDaemonIdentity": selected.daemon_identity}
                with patch.dict(os.environ, selected.selection, clear=False):
                    fresh = launcher.Docker()
                    fresh.preflight()
                    fresh.check_selection(receipt)
                assert before.run(["compose", "version", "--short"]).strip() == b"2.26.1"
                assert hashlib.sha256(legacy.read_bytes()).hexdigest() == LEGACY_DIGEST
                assert (config / "config.json").read_bytes() == config_bytes
                print("PASS real private Compose: legacy terminal choice; verified existing/private offline selection; original named context, daemon and registry helper; owner-compatible ordered sources; fresh-process receipt; unchanged normal plugin/config")
        finally:
            registry.shutdown()
            registry.server_close()
            thread.join(timeout=5)
    assert checked(["docker", "compose", "version", "--short"]) == normal_version
    assert checked(["docker", "context", "show"]) == normal_context


if __name__ == "__main__":
    main()
