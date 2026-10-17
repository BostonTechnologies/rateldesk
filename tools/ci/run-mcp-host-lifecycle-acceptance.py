#!/usr/bin/env python3
"""Disposable four-service host lane: actual launcher, browser and NEW verification.

Use already built current-source images. This never pulls, publishes, updates an
owner deployment, or removes persistent volumes outside its unique test project.
Default cases exercise unchanged adoption, a narrow reviewed source correction,
and retained legacy configuration with real PostgreSQL. The saved-source apply
adapter is explicitly separate from optional actual Portainer owner acceptance. Advanced
beta.2 initializer fixtures remain available as individually selected cases.
"""
import argparse
from contextlib import closing, ExitStack
from datetime import datetime
import errno
import fcntl
import hashlib
import http.cookiejar
import json
import os
from pathlib import Path
import pty
import re
import secrets
import select
import shlex
import signal
import socket
import sqlite3
import ssl
import struct
import subprocess
import tempfile
import tarfile
import termios
import threading
import time
import urllib.error
import urllib.request
import uuid
from terminal_test_support import raw_terminal_text, terminal_text

ROOT = Path(__file__).resolve().parents[2]
LAUNCHER = ROOT / "tools/mcp/setup-http-mcp.sh"


class CommandFailure(RuntimeError):
    def __init__(self, arguments, completed):
        super().__init__(f"Disposable {Path(arguments[0]).name} command failed (exit {completed.returncode}).")
        self.detail = completed.stdout + b"\n" + completed.stderr


def checked(arguments, *, cwd=None, input_data=None, timeout=240):
    completed = subprocess.run(arguments, cwd=cwd, input=input_data, capture_output=True, timeout=timeout)
    if completed.returncode:
        raise CommandFailure(arguments, completed)
    return completed.stdout


def port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


class Terminal:
    def __init__(self, arguments, directory, executable=LAUNCHER, environment=None):
        self.child, self.fd = pty.fork()
        if self.child == 0:
            os.chdir(directory)
            os.environ["TERM"] = "xterm-256color"
            if environment:
                os.environ.update(environment)
            os.execvp(str(executable), [str(executable), *arguments])
        fcntl.ioctl(self.fd, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 80, 0, 0))
        os.kill(self.child, signal.SIGWINCH)
        self.traffic = bytearray()
        self.status = None
        self.rows, self.columns = 24, 80

    def resize(self, rows, columns):
        fcntl.ioctl(self.fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, columns, 0, 0))
        os.kill(self.child, signal.SIGWINCH)
        self.rows, self.columns = rows, columns

    def send(self, value):
        os.write(self.fd, value)

    def text(self):
        return terminal_text(self.traffic, history=True)

    def raw_text(self):
        return raw_terminal_text(self.traffic)

    def screen(self):
        return terminal_text(self.traffic, rows=self.rows, columns=self.columns)

    def until(self, expression, seconds=120, since=0):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            # Termina emits cell deltas, so slicing the byte stream discards
            # unchanged label characters. Fresh waits reconstruct the complete
            # current screen after new traffic; callers exclude a former code.
            rendered = self.screen() if since else self.text()
            match = re.search(expression, rendered) if not since or len(self.traffic) > since else None
            if match:
                return match
            if len(self.traffic) > 4 * 1024 * 1024:
                raise RuntimeError("Disposable terminal output exceeded its bound")
            ready, _, _ = select.select([self.fd], [], [], 0.1)
            if ready:
                try:
                    self.traffic.extend(os.read(self.fd, 65536))
                except OSError as failure:
                    if failure.errno != errno.EIO:
                        raise
                    break
        # Expressions can contain a fresh device/human code. Keep failure
        # output scalar; the private redacted screen records the visible stage.
        raise RuntimeError("Disposable terminal did not reach the expected stage within its bound")

    def finish(self, expected=0, seconds=60):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            completed, status = os.waitpid(self.child, os.WNOHANG)
            if completed:
                self.status = os.waitstatus_to_exitcode(status)
                # The launcher prints its safely quoted resume command after
                # the wizard leaves the alternate screen, immediately before
                # exit. Drain already buffered PTY output before inspecting it.
                while select.select([self.fd], [], [], 0)[0]:
                    try:
                        data = os.read(self.fd, 65536)
                    except OSError as failure:
                        if failure.errno != errno.EIO:
                            raise
                        break
                    if not data:
                        break
                    self.traffic.extend(data)
                    if len(self.traffic) > 4 * 1024 * 1024:
                        raise RuntimeError("Disposable terminal output exceeded its bound")
                if self.status != expected:
                    raise RuntimeError(f"Disposable launcher exited {self.status}, expected {expected}")
                return
            ready, _, _ = select.select([self.fd], [], [], 0.1)
            if ready:
                try:
                    self.traffic.extend(os.read(self.fd, 65536))
                except OSError as failure:
                    if failure.errno != errno.EIO:
                        raise
        raise RuntimeError("Disposable launcher did not terminate within its bound")

    def close(self):
        if self.status is None:
            try:
                os.killpg(self.child, signal.SIGKILL)
                os.waitpid(self.child, 0)
            except (ProcessLookupError, ChildProcessError):
                pass
        os.close(self.fd)


class LiveDockerEvents:
    """Continuous bounded daemon evidence, fenced by real read-only execs."""

    def __init__(self, container):
        self.container = container
        self.condition = threading.Condition()
        self.events, self.bytes = [], 0
        self.error, self.closing, self.closed = None, False, False
        # Docker's CLI may connect after the first marker exec. Request only
        # this startup prefix; the window begins after its marker is observed
        # and thereafter uses the uninterrupted live subscription.
        self.process = subprocess.Popen(["docker", "events", "--since", str(time.time()), "--format", "{{json .}}"],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()

    def fail(self, message):
        with self.condition:
            self.error = self.error or message
            self.condition.notify_all()

    def read(self):
        try:
            while line := self.process.stdout.readline(65537):
                with self.condition:
                    self.bytes += len(line)
                    if self.bytes > 4 * 1024 * 1024 or len(self.events) >= 16384 or len(line) > 65536:
                        self.error = self.error or "Docker event collector exceeded its evidence bound"
                    elif not self.error:
                        try:
                            event = json.loads(line)
                            if not isinstance(event.get("timeNano"), int) or not isinstance(event.get("Type"), str) or not isinstance(event.get("Actor"), dict):
                                raise ValueError()
                            self.events.append(event)
                        except (ValueError, AttributeError):
                            self.error = "Docker event collector received incomplete or invalid evidence"
                    self.condition.notify_all()
            if not self.closing:
                self.fail("Docker event subscription ended before its closing handshake")
        except (OSError, ValueError):
            self.fail("Docker event collector could not consume its complete stream")

    def check(self):
        if self.error:
            raise RuntimeError(self.error)
        if not self.closing and self.process.poll() is not None:
            raise RuntimeError("Docker event subscription stopped unexpectedly")

    def handshake(self):
        self.check()
        marker = "rdk-event-read-barrier-" + uuid.uuid4().hex
        checked(["docker", "exec", self.container, "/bin/sh", "-c", "test -r /etc/hostname", marker], timeout=15)
        deadline = time.monotonic() + 15
        with self.condition:
            while time.monotonic() < deadline:
                self.check()
                execution = None
                for index, event in enumerate(self.events):
                    actor, action = event["Actor"], event.get("Action", event.get("status", ""))
                    if event["Type"] != "container" or actor.get("ID") != self.container:
                        continue
                    if action.startswith("exec_create:") and marker in action:
                        execution = actor.get("Attributes", {}).get("execID")
                    if action == "exec_die" and execution and actor.get("Attributes", {}).get("execID") == execution:
                        return index
                self.condition.wait(min(0.1, max(0, deadline - time.monotonic())))
        raise RuntimeError("Docker event subscription did not confirm its bounded read-only handshake")

    def begin(self):
        self.start = self.handshake() + 1

    def finish(self):
        try:
            end = self.handshake()
            with self.condition:
                captured = self.events[self.start:end + 1]
        finally:
            self.close()
        self.check()
        return captured

    def close(self):
        if self.closed:
            return
        if self.process.poll() is not None:
            self.fail("Docker event subscription exited before intentional stream shutdown")
        self.closing = True
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.fail("Docker event subscription required forced shutdown")
                self.process.kill()
                self.process.wait(timeout=5)
        self.reader.join(timeout=5)
        if self.reader.is_alive():
            self.fail("Docker event collector did not flush its complete stream")
        else:
            self.process.stdout.close()
        if self.process.returncode not in (0, -signal.SIGTERM, 128 + signal.SIGTERM):
            self.fail("Docker event collector reported an unsuccessful process exit")
        self.closed = True


class Fixture:
    def __init__(self, args, existing, legacy=False):
        self.args, self.existing, self.legacy = args, existing, legacy
        self.project = "rdk-host-fixture-" + uuid.uuid4().hex[:12]
        self.temporary = tempfile.TemporaryDirectory(prefix=self.project + "-")
        self.directory = Path(self.temporary.name)
        self.launcher = LAUNCHER
        if args.deployment_archive:
            with tarfile.open(args.deployment_archive) as archive:
                archive.extractall(self.directory / "extracted", filter="data")
            candidates = list((self.directory / "extracted").glob("*/setup-http-mcp.sh"))
            assert len(candidates) == 1, "Expected one extracted deployment launcher"
            self.launcher = candidates[0]
        self.api_port, self.web_port = port(), port()
        while self.api_port == self.web_port:
            self.web_port = port()
        self.origin = f"https://desk.fixture.test:{self.web_port}"
        self.api = f"http://127.0.0.1:{self.api_port}"
        self.email = "disposable-admin@example.test"
        self.organization_name = "Disposable acceptance organization"
        self.password = "Acceptance-" + secrets.token_urlsafe(24) + "!9aA"
        self.pfx_password = secrets.token_urlsafe(24)
        self.ca = self.directory / "fixture-ca.pem"
        self.source = self.directory / "compose.json"
        self.environment = self.directory / "fixture.env"
        self.output = self.directory / (".rateldesk-http-mcp-" + self.project)
        self.compose = ["docker", "compose", "--project-directory", str(self.directory), "--project-name", self.project,
                        "--env-file", str(self.environment), "-f", str(self.source)]
        self.cookies = http.cookiejar.CookieJar()
        self.http = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPCookieProcessor(self.cookies))
        self.terminals = []
        self.event_collectors = []
        self.onramp = None

    def write_model(self, model):
        self.source.write_text(json.dumps(model))

    def customize_model(self, model):
        return model

    def initialize(self):
        checked([*self.compose, "run", "--rm", "--no-deps", "api", "--initialize-unattended"], timeout=300)
        self.seed_incidents()

    def request(self, path, payload=None, expected=(200,), method=None):
        request = urllib.request.Request(self.api + path,
            data=json.dumps(payload).encode() if payload is not None else None,
            headers={"Content-Type": "application/json", "Accept": "application/json", "X-Requested-With": "XMLHttpRequest"}, method=method)
        try:
            with self.http.open(request, timeout=20) as response:
                status, content = response.status, response.read(1024 * 1024)
        except urllib.error.HTTPError as response:
            status, content = response.code, response.read(16384)
        if status not in expected:
            raise RuntimeError(f"Disposable API stage {path} returned HTTP {status}")
        if path.startswith("/health/"):
            return None  # The owning ASP.NET health endpoint returns plain text.
        return json.loads(content) if content else None

    def prepare(self):
        for image in (self.args.api_image, self.args.web_image, self.args.mcp_image):
            checked(["docker", "image", "inspect", image])
        web_image = self.args.web_image
        if not self.existing:
            # Match actual release discovery without publishing or pulling. A
            # generic local tag is intentionally not trusted as a RatelDesk Web
            # foundation by the launcher.
            self.fixture_web_image = "ghcr.io/bostontechnologies/rateldesk-web:fixture-" + self.project
            checked(["docker", "tag", web_image, self.fixture_web_image])
            web_image = self.fixture_web_image
        key, pfx = self.directory / "fixture.key", self.directory / "fixture.pfx"
        checked(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-keyout", str(key),
                 "-out", str(self.ca), "-subj", "/CN=desk.fixture.test", "-addext", "subjectAltName=DNS:desk.fixture.test,DNS:localhost,IP:127.0.0.1"])
        checked(["openssl", "pkcs12", "-export", "-out", str(pfx), "-inkey", str(key), "-in", str(self.ca), "-passout", "fd:0"],
                input_data=(self.pfx_password + "\n").encode())
        self.ca.chmod(0o644)  # Public CA, read-only inside non-root tooling.
        self.environment.write_text("FIXTURE_PFX_PASSWORD=" + self.pfx_password + "\nFIXTURE_ADMIN_PASSWORD=" + self.password + "\n")
        self.environment.chmod(0o600)
        api_environment = {"ASPNETCORE_ENVIRONMENT": "Production", "ASPNETCORE_URLS": "http://+:8222",
            "Authentication__Mode": "Local", "Authentication__AllowInsecureLocalhost": "true",
            "Bootstrap__StateDirectory": "/fixture/bootstrap", "Bootstrap__DataDirectory": "/fixture/data",
            "DataProtection__KeyRingPath": "/fixture/keys", "DataProtection__ApplicationName": "RatelDesk",
            "StorageOptions__RootPath": "/fixture/storage", "Bootstrap__Unattended__Provider": "Sqlite",
            "Bootstrap__Unattended__Email": self.email, "Bootstrap__Unattended__DisplayName": "Disposable acceptance administrator",
            "Bootstrap__Unattended__Password": "${FIXTURE_ADMIN_PASSWORD}",
            "Bootstrap__Unattended__OrganizationName": self.organization_name,
            "Bootstrap__Unattended__ApplicationName": "RatelDesk acceptance", "Bootstrap__Unattended__ApplicationUrl": self.origin,
            "IntegrationCredentials__Pairing__PublicOrigin": self.origin}
        services = {
            "api": {"image": self.args.api_image, "environment": api_environment, "volumes": ["state:/fixture"],
                    "ports": [f"127.0.0.1:{self.api_port}:8222"]},
            "web": {"image": web_image, "environment": {
                "ASPNETCORE_ENVIRONMENT": "Production", "ASPNETCORE_URLS": f"https://+:{self.web_port}",
                "ASPNETCORE_Kestrel__Certificates__Default__Path": "/fixture-cert/fixture.pfx",
                "ASPNETCORE_Kestrel__Certificates__Default__Password": "${FIXTURE_PFX_PASSWORD}",
                "ApiBaseUrl": "http://api:8222/", "WebGateway__PublicOrigin": self.origin,
                "NO_PROXY": "desk.fixture.test,api,localhost,127.0.0.1",
                "no_proxy": "desk.fixture.test,api,localhost,127.0.0.1",
                "Authentication__Mode": "Local", "Authentication__AllowInsecureLocalhost": "true",
                "DataProtection__KeyRingPath": "/fixture/keys", "DataProtection__ApplicationName": "RatelDesk"},
                "volumes": ["state:/fixture", {"type": "bind", "source": str(pfx), "target": "/fixture-cert/fixture.pfx", "read_only": True}],
                "ports": [f"127.0.0.1:{self.web_port}:{self.web_port}"],
                "networks": {"default": {"aliases": ["desk.fixture.test"]}}},
            "untouched": {"image": self.args.mcp_image, "entrypoint": ["/bin/sh", "-ec"], "command": ["sleep 3600"],
                          "environment": {"PRESERVE_ME": "existing-manager-content"}, "volumes": ["unrelated:/unrelated"]}}
        if self.existing:
            services["web"]["environment"].update({"WebGateway__Mcp__Enabled": "true", "WebGateway__Mcp__Destination": "http://mcp-http:8223/", "WebGateway__Mcp__AuthenticationMode": "gateway"})
            services["mcp-http"] = {"image": self.args.mcp_image, "user": "10001:10001", "environment": {
                "ASPNETCORE_ENVIRONMENT": "Production", "ASPNETCORE_URLS": "http://+:8223",
                "NO_PROXY": "desk.fixture.test,api,localhost,127.0.0.1",
                "no_proxy": "desk.fixture.test,api,localhost,127.0.0.1",
                "Helpdesk__Mcp__Instance": "synthetic-private-west", "Helpdesk__Mcp__ExpectedApiBaseUrl": "http://api:8222/",
                "Helpdesk__Mcp__PublicResourceUri": self.origin + "/mcp", "Helpdesk__Mcp__AllowedOrigins__0": self.origin,
                "Helpdesk__Mcp__AuthenticationMode": "gateway", "Helpdesk__Mcp__Outbound__ApiBaseUrl": "http://api:8222/",
                "Helpdesk__Mcp__Outbound__CredentialMode": "gateway"}}
            if self.legacy:
                self.legacy_config = self.directory / "legacy-outbound.json"
                self.legacy_config.write_text(json.dumps({"apiBaseUrl": "http://api:8222/", "credentialMode": "gateway"}))
                self.legacy_config.chmod(0o600)
                checked(["docker", "run", "--rm", "--user", "0:0", "--mount",
                    f"type=bind,src={self.legacy_config},dst=/fixture-config", "--entrypoint", "/bin/sh",
                    self.args.mcp_image, "-ec", f"chown {os.getuid()}:10001 /fixture-config"])
                self.legacy_config.chmod(0o640)
                services["mcp-http"]["environment"]["RATELDESK_MCP_CONFIG"] = "/run/rateldesk-mcp/config.json"
                services["mcp-http"]["configs"] = [{"source": "legacy-outbound", "target": "/run/rateldesk-mcp/config.json"}]
        else:
            # Match the shipped release recipe's ownership declaration. Fresh
            # setup discovers its actual Web container and saved source labels.
            for name in ("api", "web"):
                services[name]["labels"] = {"com.rateldesk.deployment.manager": "release-compose"}
        model = {"name": self.project, "services": services, "volumes": {"state": {}, "unrelated": {}}}
        if self.legacy:
            model["configs"] = {"legacy-outbound": {"file": str(self.legacy_config)}}
        model = self.customize_model(model)
        # Isolate this loopback/private-network fixture from Docker CLI proxy
        # defaults. BusyBox wget may ignore NO_PROXY; retain its exact inherited
        # healthcheck and prevent that check from reaching a cloud proxy.
        for service in model["services"].values():
            service.setdefault("environment", {}).update({key: "" for key in
                ("HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy", "ALL_PROXY", "all_proxy")})
        self.write_model(model)
        self.source.chmod(0o600)
        if self.legacy:
            self.reproduce_old_collision(model)
        self.initialize()
        checked([*self.compose, "up", "--pull", "never", "-d"], timeout=180)
        deadline = time.monotonic() + 150
        while time.monotonic() < deadline:
            try:
                self.request("/health/ready")
                break
            except (OSError, RuntimeError, urllib.error.URLError):
                time.sleep(1)
        else:
            raise RuntimeError("Disposable API readiness timeout")
        self.request("/api/v1/local-auth/login", {"email": self.email, "password": self.password, "rememberMe": False}, expected=(204,))
        account = self.request("/api/v1/auth/me")
        self.organization = account["primaryOrganizationId"]
        assert self.organization == self.seed_organization, "The seeded fixture must belong to its authenticated bootstrap owner"
        created = self.request("/api/v1/integration-credentials/", {"name": "Identity preservation fixture", "purpose": "api",
            "organizationId": self.organization, "permissions": ["Incident.Read"], "lifetimeDays": 1}, expected=(201,))
        self.ordinary_id = created["id"]
        persisted = next(item for item in self.request("/api/v1/integration-credentials/") if item["id"] == self.ordinary_id)
        assert persisted.get("revokedAtUtc") is None
        created_expiry = datetime.fromisoformat(created["expiresAtUtc"].replace("Z", "+00:00"))
        persisted_expiry = datetime.fromisoformat(persisted["expiresAtUtc"].replace("Z", "+00:00"))
        assert abs((created_expiry - persisted_expiry).total_seconds()) <= 0.000001, "The first persisted expiry must match creation within database timestamp precision"
        self.ordinary_expiry = persisted["expiresAtUtc"]
        print("PASS: pre-host ordinary identity baseline read from persisted metadata; creation/persisted expiry text differs=" +
            str(created["expiresAtUtc"] != persisted["expiresAtUtc"]).lower(), flush=True)
        self.ordinary_secret = created["secret"]

    def reproduce_old_collision(self, model):
        # Isolated negative control for the beta.1 exported shape, before any
        # approved fixture generation exists. The actual fixture is never reset.
        service = json.loads(json.dumps(model["services"]["mcp-http"]))
        service["volumes"] = [{"type": "volume", "source": "collision", "target": "/run/rateldesk-mcp", "read_only": True}]
        source = self.directory / "old-collision.json"
        source.write_text(json.dumps({"services": {"mcp-http": service}, "configs": model["configs"], "volumes": {"collision": {}}}))
        source.chmod(0o600)
        compose = ["docker", "compose", "--project-name", self.project + "-negative", "-f", str(source)]
        try:
            result = subprocess.run([*compose, "up", "--pull", "never", "-d"], capture_output=True, timeout=90)
            assert result.returncode != 0 and b"read-only file system" in result.stderr, "Negative control must reproduce the original runtime mount collision"
        finally:
            checked([*compose, "down", "--volumes", "--remove-orphans"], timeout=90)
        print("PASS: isolated old complete-document shape reproduces Docker read-only-filesystem collision", flush=True)

    def seed_incidents(self):
        # Seed the fixture's offline SQLite file before any serving process or
        # workers start. No ticket creation endpoint, mail or workflow runs.
        helper = checked(["docker", "create", "--volume", self.project + "_state:/fixture",
            "--entrypoint", "/bin/sh", self.args.api_image]).decode().strip()
        self.seed_helper = helper
        database = self.directory / "seed.sqlite"
        checked(["docker", "cp", helper + ":/fixture/data/rateldesk.db", str(database)])
        self.incident_ids = [str(uuid.uuid4()), str(uuid.uuid4())]
        with closing(sqlite3.connect(database)) as connection:
            organizations = connection.execute(
                'SELECT o."Id" FROM "Organizations" o JOIN "Users" u ON u."OrganizationId" = o."Id" '
                'WHERE u."Email" = ? COLLATE NOCASE AND o."Name" = ?',
                (self.email, self.organization_name)).fetchall()
            assert len(organizations) == 1, "Expected one organization linked to the synthetic bootstrap owner"
            self.seed_organization = organization = organizations[0][0]
            columns = connection.execute('PRAGMA table_info("Tickets")').fetchall()
            assert columns, "Expected the actual SQLite ticket hierarchy table"
            for index, identifier in enumerate(self.incident_ids):
                values = {name: (0 if any(part in kind.upper() for part in ["INT", "REAL", "NUM"]) else "")
                          for _, name, kind, required, default, _ in columns if required and default is None}
                values.update({"Id": identifier, "Discriminator": "Incident", "Title": "Beta2 needle" if index == 0 else "Beta2 baseline",
                    "Description": "Synthetic disposable acceptance fixture", "OrganizationId": organization,
                    "CreatedAt": "2026-10-17 00:00:00", "TrackingId": "INC-BETA2-" + str(index),
                    "LinkedAssetIds": "[]", "Attachments": "[]", "CcRecipientsJson": "[]"})
                names = list(values)
                connection.execute('INSERT INTO "Tickets" (' + ','.join('"' + name + '"' for name in names) +
                    ') VALUES (' + ','.join('?' for _ in names) + ')', [values[name] for name in names])
            connection.commit()
            # The initialized fixture uses WAL. Commit alone leaves these rows
            # in the sidecar, while docker cp transfers only the main file.
            # No serving process is running: checkpoint and close before copy.
            assert connection.execute('PRAGMA wal_checkpoint(TRUNCATE)').fetchone()[0] == 0, "Offline fixture seed checkpoint must complete"
        try:
            checked(["docker", "cp", str(database), helper + ":/fixture/data/rateldesk.db"])
        finally:
            checked(["docker", "rm", helper])
            self.seed_helper = None

    def launcher_arguments(self):
        if not self.existing:
            return ["--ca-file", str(self.ca), "--image", self.args.mcp_image, "--intent", "legacy-initializer"]
        return ["--project", self.project, "--project-directory", str(self.directory), "--compose-file", str(self.source),
                "--env-file", str(self.environment), "--manager", "external-compose" if self.existing else "release-compose",
                "--image", self.args.mcp_image, "--ca-file", str(self.ca), "--intent", "legacy-initializer"]

    def terminal(self, arguments, executable=LAUNCHER):
        if self.onramp and executable == LAUNCHER:
            terminal = Terminal(self.onramp.terminal_prefix(arguments, executable=self.launcher), self.directory, "env")
            self.terminals.append(terminal)
            self.onramp.select_existing(terminal)
            return terminal
        if executable == LAUNCHER:
            executable = self.launcher
        environment = {"PATH": self.onramp.environment["PATH"]} if self.onramp else None
        terminal = Terminal(arguments, self.directory, executable, environment)
        self.terminals.append(terminal)
        return terminal

    def approve_host(self, code):
        review = self.request("/api/v1/integration-credentials/host-proposals/review", {"humanCode": code})
        proposal = review["proposal"]
        self.request("/api/v1/integration-credentials/host-proposals/approve", {"humanCode": code,
            "proposalHash": proposal["proposalHash"], "sourceHash": proposal["sourceHash"], "consent": True}, expected=(204,))

    def wait_console_endpoint(self):
        trusted = urllib.request.build_opener(urllib.request.ProxyHandler({}),
            urllib.request.HTTPSHandler(context=ssl.create_default_context(cafile=str(self.ca))))
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            try:
                request = urllib.request.Request(f"https://127.0.0.1:{self.web_port}/api/v1/integration-credentials/pairing/capabilities",
                    headers={"Host": self.origin.split("://", 1)[1], "Accept": "application/json"})
                with trusted.open(request, timeout=5) as response:
                    capabilities = json.loads(response.read(16384))
                    if response.status == 200 and capabilities.get("pairingSupported") and capabilities.get("publicOrigin", "").rstrip("/") == self.origin:
                        return
            except (OSError, ValueError, urllib.error.URLError):
                pass
            time.sleep(0.5)
        raise RuntimeError("Disposable public Web pairing endpoint readiness timeout")

    def console_arguments(self, identifier, *arguments, interactive=False):
        return ["exec", *(["--interactive", "--tty"] if interactive else []),
            "--env", "SSL_CERT_FILE=/tmp/rateldesk-client-fixture-ca.pem",
            "--env", "NO_PROXY=desk.fixture.test,api,localhost,127.0.0.1",
            "--env", "no_proxy=desk.fixture.test,api,localhost,127.0.0.1",
            identifier, "dotnet", "/app/Helpdesk.Mcp.Http.dll", *arguments]

    def prepare_console(self, identifier):
        # The disposable private-CA certificate is public. Trust is scoped to
        # these exec processes; serving settings and personal secrets are untouched.
        checked(["docker", "cp", str(self.ca), identifier + ":/tmp/rateldesk-client-fixture-ca.pem"])
        metadata = json.loads(checked(["docker", "inspect", "--format", "{{json .}}", identifier]))
        environment = dict(item.split("=", 1) for item in metadata["Config"].get("Env", []) if "=" in item)
        root = environment.get("RATELDESK_MCP_CLIENT_DIRECTORY")
        assert root == "/var/lib/rateldesk/http-client", "Actual console must select its durable personal client store"
        assert any(mount.get("Destination") == root and mount.get("Name") == self.project + "-http-client-data" and mount.get("RW")
                   for mount in metadata.get("Mounts", [])), "Personal client store must be a persistent writable volume"
        assert checked(["docker", "exec", identifier, "id", "-u"]).strip() == b"10001", "Console setup must run as the serving non-root user"
        return root + "/mcp/incident-reader.json"

    def console_profile(self, identifier):
        raw = checked(["docker", "exec", identifier, "cat", self.console_profile_path])
        assert len(raw) <= 65536, "Personal profile must remain bounded"
        profile = json.loads(raw)
        assert profile.get("clientTransport") == "http" and profile.get("credentialPurpose") == "mcp"
        assert profile.get("credentialMode") == "integration" and profile.get("credentialTarget") == self.origin
        assert profile.get("mcpResourceUri") == self.origin + "/mcp"
        assert profile.get("credentialOrganizationId") == self.organization and profile.get("credentialPermissions") == ["Incident.Read"]
        assert not profile.get("integrationCredential") and not profile.get("authentikAppPassword")
        reference = profile.get("integrationCredentialFile", "")
        assert reference.startswith("/var/lib/rateldesk/http-client/mcp/secrets/") and reference.endswith(".secret")
        privacy = checked(["docker", "exec", identifier, "stat", "-c", "%a:%u:%g", self.console_profile_path, reference]).decode().splitlines()
        assert privacy == ["600:10001:10001", "600:10001:10001"], "Actual profile and referenced credential must remain owner-only"
        directories = checked(["docker", "exec", identifier, "stat", "-c", "%a:%u:%g", str(Path(self.console_profile_path).parent), str(Path(reference).parent)]).decode().splitlines()
        assert directories == ["700:10001:10001", "700:10001:10001"], "Actual client directories must remain private"
        digest = checked(["docker", "exec", identifier, "sha256sum", reference]).decode().split()[0]
        assert re.fullmatch(r"[a-f0-9]{64}", digest)
        return raw, profile, reference, digest

    def complete_console_client(self, identifier):
        self.wait_console_endpoint()
        self.console_profile_path = self.prepare_console(identifier)
        before = {item["id"] for item in self.request("/api/v1/integration-credentials/")}
        console = self.terminal(self.console_arguments(identifier, "setup", "--mode", "client", "--url", self.origin,
            "--monochrome", interactive=True), executable="docker")
        console.until("RatelDesk Web URL")
        console.send(b"\x03")
        console.finish()
        setup = self.terminal(self.console_arguments(identifier, "setup", "--mode", "client", "--profile", "incident-reader",
            "--url", self.origin, "--monochrome", interactive=True), executable="docker")
        setup.until("RatelDesk Web URL")
        setup.send(b"\x0e")
        setup.until("Continue to browser approval")
        setup.send(b"\x0e")
        code = setup.until(r"Short code:\s*([A-Z2-9]{4}-[A-Z2-9]{4})").group(1)
        review = self.request("/api/v1/integration-credentials/pairing/review", {"humanCode": code})
        assert review["grantKind"] == "ordinary" and review["purpose"] == "mcp" and review["mcpResourceUri"] == self.origin + "/mcp"
        organization = next(item for item in review["organizations"] if item["id"] == self.organization)
        assert "Incident.Read" in organization["permissions"]
        self.request("/api/v1/integration-credentials/pairing/approve", {"humanCode": code,
            "organizationId": self.organization, "permissions": ["Incident.Read"], "lifetimeDays": 1, "consent": True}, expected=(204,))
        setup.until(r"Approved organization:\s*" + re.escape(self.organization))
        setup.send(b"\x0e")
        setup.until("Client profile saved. Review transport checks and launch instructions")
        setup.send(b"\x0e")
        setup.finish()
        raw, profile, reference, digest = self.console_profile(identifier)
        created = [item for item in self.request("/api/v1/integration-credentials/") if item["id"] not in before]
        assert len(created) == 1 and created[0]["purpose"] == "mcp" and created[0]["organizationId"] == self.organization
        assert created[0]["permissions"] == ["Incident.Read"] and created[0].get("revokedAtUtc") is None
        self.console_credential_id = created[0]["id"]
        self.console_credential_expiry = created[0]["expiresAtUtc"]
        self.console_profile_bytes, self.console_secret_reference, self.console_secret_digest = raw, reference, digest
        api = checked([*self.applied, "ps", "--quiet", "api"]).decode().strip()
        api_metadata = json.loads(checked(["docker", "inspect", "--format", "{{json .}}", api]))
        assert all(mount.get("Name") != self.project + "-http-client-data" for mount in api_metadata.get("Mounts", [])), "API must not receive personal client storage"
        print("PASS: actual Docker-console client wizard, explicit ordinary one-organization/one-permission consent, real catalog/API-backed checks and owner-only durable profile", flush=True)

    def reload_console_client(self, identifier):
        assert self.prepare_console(identifier) == self.console_profile_path
        raw, profile, reference, digest = self.console_profile(identifier)
        assert (raw, reference, digest) == (self.console_profile_bytes, self.console_secret_reference, self.console_secret_digest), "Full recreation must retain the exact profile and ordinary credential"
        status = json.loads(checked(["docker", *self.console_arguments(identifier, "--config", self.console_profile_path, "status")]))
        assert status["profilePath"] == self.console_profile_path and status["configuration"]["credentialPurpose"] == "mcp"
        diagnostic = json.loads(checked(["docker", *self.console_arguments(identifier, "--config", self.console_profile_path,
            "doctor", "--network", "--authenticate")], timeout=90))
        checks = {item["name"]: item["status"] for item in diagnostic["checks"]}
        assert all(checks.get(name) == "passed" for name in ("initialize", "catalog", "authentication", "tool-result")), "A new console process must authenticate the retained ordinary credential"
        assert "failed" not in checks.values()
        credentials = self.request("/api/v1/integration-credentials/")
        credential = next(item for item in credentials if item["id"] == self.console_credential_id)
        assert credential.get("revokedAtUtc") is None and credential["expiresAtUtc"] == self.console_credential_expiry
        before = {item["id"] for item in credentials}
        reentry = self.terminal(self.console_arguments(identifier, "setup", "--mode", "client", "--url", self.origin,
            "--profile", "incident-reader", "--monochrome", interactive=True), executable="docker")
        reentry.until("RatelDesk Web URL")
        reentry.send(b"\x0e")
        reentry.until("Your existing identity stays bound to this reviewed target")
        reentry.send(b"\x0e")
        reentry.until("Review the target, actual grants and storage destination")
        reentry.send(b"\x03")
        reentry.finish()
        assert "Short code:" not in reentry.text(), "Known client identity must not request a replacement grant"
        assert before == {item["id"] for item in self.request("/api/v1/integration-credentials/")}
        assert self.console_profile(identifier)[0] == self.console_profile_bytes
        self.probe_incident_search(identifier)
        print("PASS: full container recreation retained ordinary identity/expiry; NEW explicit-profile status/doctor and existing-identity wizard reload passed without replacement consent", flush=True)

    def probe_incident_search(self, identifier=None, secret=None):
        secret = secret or checked(["docker", "exec", identifier, "cat", self.console_secret_reference]).decode().strip()
        trusted = urllib.request.build_opener(urllib.request.ProxyHandler({}),
            urllib.request.HTTPSHandler(context=ssl.create_default_context(cafile=str(self.ca))))
        def rpc(method, parameters):
            request = urllib.request.Request(f"https://127.0.0.1:{self.web_port}/mcp",
                data=json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": parameters}).encode(),
                headers={"Host": self.origin.split("://", 1)[1], "Content-Type": "application/json",
                    "Authorization": "Bearer " + secret, "Accept": "application/json, text/event-stream",
                    "MCP-Protocol-Version": "2025-03-26"})
            with trusted.open(request, timeout=30) as response:
                content = response.read(1024 * 1024).decode()
            if not content.lstrip().startswith("{"):
                content = '\n'.join(line[5:].lstrip() for line in content.splitlines() if line.startswith("data:"))
            payload = json.loads(content)
            assert "error" not in payload, "Real HTTP MCP returned a protocol error"
            return payload["result"]
        initialized = rpc("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
            "clientInfo": {"name": "beta2-disposable-acceptance", "version": "1"}})
        assert initialized.get("protocolVersion")
        tools = rpc("tools/list", {})["tools"]
        assert len(tools) == 28, "The actual complete supported catalog must remain 28 tools"
        assert {tool["name"] for tool in tools} >= {"helpdesk_incidents", "helpdesk_requests", "helpdesk_auth"}
        def call(name, operation, request=None):
            arguments = {"operation": operation}
            if request is not None:
                arguments["request"] = request
            return rpc("tools/call", {"name": name, "arguments": arguments})["structuredContent"]
        assert call("helpdesk_auth", "status")["success"]
        baseline = call("helpdesk_incidents", "list", {"includeTotal": True})
        data = baseline.get("data") if isinstance(baseline.get("data"), dict) else {}
        items = data.get("items") if isinstance(data.get("items"), list) else []
        total = data.get("totalCount") if isinstance(data.get("totalCount"), int) else None
        upstream = baseline.get("failure", {}).get("upstreamStatus") if isinstance(baseline.get("failure"), dict) else None
        print("OBSERVE: HTTP MCP incident baseline " + json.dumps({"success": baseline.get("success") is True,
            "totalCount": total, "itemCount": len(items), "itemsMatchSeed": {item.get("id") for item in items} == set(self.incident_ids),
            "upstreamStatus": upstream if isinstance(upstream, int) else None}), flush=True)
        assert baseline["success"] and baseline["data"]["totalCount"] == 2
        assert {item["id"] for item in baseline["data"]["items"]} == set(self.incident_ids)
        matched = call("helpdesk_incidents", "list", {"query": "Beta2 needle", "includeTotal": True})
        assert matched["success"] and matched["data"]["totalCount"] == 1
        assert [item["id"] for item in matched["data"]["items"]] == self.incident_ids[:1]
        absent = call("helpdesk_incidents", "list", {"query": "Impossible beta2 " + uuid.uuid4().hex, "includeTotal": True})
        assert absent["success"] and absent["data"]["items"] == [] and absent["data"]["totalCount"] == 0
        fetched = call("helpdesk_incidents", "get", {"incidentId": self.incident_ids[0]})
        assert fetched["success"] and fetched["data"]["id"] == self.incident_ids[0]
        denied = call("helpdesk_requests", "list", {})
        assert not denied["success"] and denied["failure"]["upstreamStatus"] == 403
        print("PASS: real HTTP MCP initialize/catalog/status, incident baseline=2/match=1/no-match=0 with filtered totals, permitted get and Request.Read HTTP 403", flush=True)

    def run(self):
        print("RUN: explicitly CLI-selected Advanced initializer compatibility; separate from four-service adoption", flush=True)
        self.prepare()
        normal = self.launcher_arguments() + ["--", "--url", self.origin, "--instance", "synthetic-private-west", "--monochrome"]
        cancelled = self.terminal(normal)
        cancelled.until("RatelDesk Web URL")
        cancelled.send(b"\x03")
        # This retained image-host launcher maps a native Cancel0 to2 when no
        # new approved export exists. The normal native adoption path uses0.
        cancelled.finish(expected=2)
        assert "Cancelled. Existing profiles are unchanged." in cancelled.text()
        assert "Setup exited without a new approved export." in cancelled.raw_text()
        assert not (self.output / "host-approval.json").exists(), "Cancel must not export approval"
        setup = self.terminal(normal)
        setup.until("RatelDesk Web URL")
        offset = len(setup.traffic)
        setup.send(b"\x0e")
        setup.until(r"(?s)^(?=.*HTTP MCP hosting)(?=.*Advanced initializer topology selected)(?=.*Server setup · personal client identities are separate)", since=offset)
        offset = len(setup.traffic)
        setup.send(b"\x0e")
        setup.until("Host deployment approval pending")
        code = setup.until(r"[Cc]ode:\s*([A-Z2-9]{4}-[A-Z2-9]{4})").group(1)
        setup.until("(?s)^" + "".join("(?=.*" + re.escape(value) + ")" for value in
            (code, "Open browser", "Copy URL and code", "Details", "Back", "Cancel")), since=offset)
        primary = setup.screen()
        assert all(value in primary for value in [code, "Open browser", "Copy URL and code", "Details", "Back", "Cancel"]), "Host approval URL/code/actions must be immediately available at 80x24"
        self.approve_host(code)
        setup.until("Administrator approved the reviewed changes")
        setup.send(b"\x0e")
        setup.until("Changes prepared")
        setup.send(b"\x0e")
        setup.finish()
        printed = [line.strip() for line in setup.raw_text().splitlines()
            if line.strip().startswith("env ") and line.strip().endswith("--verify")]
        assert len(printed) == 1, "The reviewed initializer export must print one safely quoted NEW verification command"
        self.resume_command = shlex.split(printed[0])
        assert self.resume_command[0] == "env" and str(self.launcher) in self.resume_command and self.resume_command[-1] == "--verify"
        complete = self.output / "deployment.json"
        document = json.loads(complete.read_bytes())
        assert "untouched" in document["services"] and "unrelated" in document["volumes"]
        assert "PRESERVE_ME=existing-manager-content" in str(document["services"]["untouched"]["environment"]) or document["services"]["untouched"]["environment"].get("PRESERVE_ME") == "existing-manager-content"
        if self.legacy:
            assert not document["services"]["mcp-http"].get("configs"), "Reviewed complete export must remove the legacy child mount"
            assert "legacy-outbound" in document.get("configs", {}), "Shared top-level source definitions must be retained"
            assert self.legacy_config.read_text() == json.dumps({"apiBaseUrl": "http://api:8222/", "credentialMode": "gateway"})
        if self.existing:
            # Simulate the existing manager's reviewed complete import at the
            # ORIGINAL persisted source path, not a transient additional overlay.
            self.source.write_bytes(complete.read_bytes())
            applied = self.compose
        else:
            checked([str(self.launcher), *self.launcher_arguments(), "--apply"], cwd=self.directory)
            applied = self.compose[:-2] + ["-f", str(complete)]
        self.applied = applied
        # Apply the manager-owned complete document first, so both first-time
        # and existing stacks have the reviewed protected stores before console setup.
        checked([*applied, "up", "--pull", "never", "-d"], timeout=180)
        identifier = checked([*applied, "ps", "--quiet", "mcp-http"]).decode().strip()
        assert checked(["docker", "exec", identifier, "stat", "-c", "%u:%g:%a", "/run/rateldesk-mcp/config.json"]).strip() == b"10001:10001:400", "Protected runtime config must remain readable by its intended serving owner only"
        mounts = json.loads(checked(["docker", "inspect", "--format", "{{json .Mounts}}", identifier]))
        assert any(mount.get("Destination") == "/run/rateldesk-mcp" and not mount.get("RW") for mount in mounts)
        assert not any(mount.get("Destination") == "/run/rateldesk-mcp/config.json" for mount in mounts), "Actual recreated serving container must have no obsolete child-file mount"
        self.complete_console_client(identifier)
        checked([*applied, "down"], timeout=180)  # Deliberately preserve data/stores.
        checked([*applied, "up", "--pull", "never", "-d"], timeout=180)
        time.sleep(5)
        identifier = checked([*applied, "ps", "--quiet", "mcp-http"]).decode().strip()
        before_verification = {item["id"] for item in self.request("/api/v1/integration-credentials/")}
        verify = self.terminal(self.resume_command[1:], executable=self.resume_command[0])
        code = verify.until(r"[Cc]ode:\s*([A-Z2-9]{4}-[A-Z2-9]{4})").group(1)
        # Read only this attempt's private cancellation capability, before approval.
        # It never enters a command argument, assertion message or evidence output.
        journal_read = subprocess.run(["docker", "exec", identifier, "head", "-c", "8193", "/output/verifier-cleanup.json"],
            capture_output=True, timeout=30)
        if journal_read.returncode or len(journal_read.stdout) > 8192:
            raise RuntimeError("This run's bounded private verifier cleanup journal is unavailable.")
        journal = json.loads(journal_read.stdout)
        self.verifier_device_code = journal.get("deviceCode")
        assert journal.get("origin") == self.origin and re.fullmatch(r"[A-Fa-f0-9]{64}", self.verifier_device_code or "")
        review = self.request("/api/v1/integration-credentials/pairing/review", {"humanCode": code})
        assert review["grantKind"] == "setup-verification"
        self.request("/api/v1/integration-credentials/pairing/approve", {"humanCode": code,
            "organizationId": self.organization, "permissions": [], "lifetimeDays": None, "consent": True}, expected=(204,))
        verify.until("End-to-end verified")
        verify.finish(seconds=120)
        self.reload_console_client(identifier)
        credentials = self.request("/api/v1/integration-credentials/")
        # Scalar-only evidence distinguishes unexpected fixture identities from a
        # retained verifier record without publishing account names, IDs or grants.
        print("OBSERVE: post-verification credential cleanup " + json.dumps({"count": len(credentials), "records": [
            {"kind": "setup-verification" if item.get("purpose") == "mcp-setup" else "ordinary",
             "purpose": item.get("purpose") if item.get("purpose") in ("api", "mcp", "mcp-setup") else "other",
             "revoked": item.get("revokedAtUtc") is not None,
             "expectedApi": item.get("id") == self.ordinary_id,
             "expectedMcp": item.get("id") == self.console_credential_id,
             "permissionsEmpty": not item.get("permissions")}
            for item in credentials]}), flush=True)
        verifier_records = [item for item in credentials if item["id"] not in before_verification]
        assert len(verifier_records) == 1, "Verification must issue exactly one separately owned temporary record"
        verifier_record = verifier_records[0]
        assert verifier_record["purpose"] == "mcp-setup" and verifier_record.get("revokedAtUtc") is not None
        assert verifier_record.get("permissions") == []
        assert verifier_record["id"] not in (self.ordinary_id, self.console_credential_id)
        cancelled = self.request("/api/v1/integration-credentials/pairing/poll", {"deviceCode": self.verifier_device_code})
        assert cancelled["status"] == "cancelled" and cancelled.get("credential") is None
        checked(["docker", "exec", identifier, "test", "!", "-e", "/output/verifier-cleanup.json"])
        print("PASS: automatic verifier cleanup revoked only this run's temporary status identity, removed its private cleanup journal and rejected protected-delivery retrieval", flush=True)
        # The API intentionally retains revoked credential history. Match the
        # beta.1 owner cleanup through its existing authenticated record-delete
        # endpoint, selecting only this attempt's already-revoked status record.
        try:
            self.request("/api/v1/integration-credentials/" + verifier_record["id"] + "/record", expected=(204,), method="DELETE")
        except RuntimeError:
            raise RuntimeError("Explicit fixture-owner temporary record deletion failed.") from None
        credentials = self.request("/api/v1/integration-credentials/")
        assert {item["id"] for item in credentials} == {self.ordinary_id, self.console_credential_id}, "Explicit fixture-owner deletion must durably remove only the temporary verifier record"
        ordinary = next(item for item in credentials if item["id"] == self.ordinary_id)
        console_credential = next(item for item in credentials if item["id"] == self.console_credential_id)
        assert ordinary.get("revokedAtUtc") is None and ordinary["expiresAtUtc"] == self.ordinary_expiry
        assert console_credential.get("revokedAtUtc") is None and console_credential["expiresAtUtc"] == self.console_credential_expiry
        cancelled_again = self.request("/api/v1/integration-credentials/pairing/poll", {"deviceCode": self.verifier_device_code})
        assert cancelled_again["status"] == "cancelled" and cancelled_again.get("credential") is None
        assert {item["id"] for item in self.request("/api/v1/integration-credentials/")} == before_verification
        print("PASS: explicit fixture-owner deletion durably removed this run's revoked verifier record; both ordinary identities and expiries remain active and delivery remains unavailable", flush=True)
        changed = json.loads(self.source.read_bytes())
        changed["services"]["untouched"]["environment"] = {"PRESERVE_ME": "separate changed-source fixture"}
        self.source.write_text(json.dumps(changed))
        environment = os.environ.copy()
        if self.onramp:
            environment["PATH"] = self.onramp.environment["PATH"]
        rejected = subprocess.run(self.resume_command, cwd=self.directory, env=environment, capture_output=True, timeout=180)
        assert rejected.returncode == 78 and b"approved_sources_changed" in rejected.stderr
        assert json.loads((self.output / "launcher-state.json").read_bytes())["phase"] == "applied-verify-pending", "A failed NEW verification attempt must clear the prior verified phase"
        for terminal in self.terminals:
            assert self.password not in terminal.raw_text() and self.ordinary_secret not in terminal.raw_text() and self.verifier_device_code not in terminal.raw_text()
        if self.onramp:
            selection = json.loads((self.output / "launcher-state.json").read_bytes())["dockerSelection"]
            assert selection["DOCKER_CONTEXT"] == self.onramp.context
            assert selection["RATELDESK_SETUP_COMPOSE_BINARY"] == str(self.onramp.tool.path)
            assert selection["RATELDESK_SETUP_COMPOSE_VERSION"] == "2.39.4"
            self.onramp.assert_unchanged()
            print("PASS: actual incompatible 2.26.1 choice and verified private 2.39.4 completed the explicit initializer export, exact printed NEW verification and source-change rejection; original plugin/config/context unchanged", flush=True)
        print("PASS: " + ("existing external-manager" if self.existing else "fresh release-owned") + " actual launcher cancel/reopen, source-bound administrator approval, complete preserved export/apply, full recreation + NEW verified invocation, API-backed status/catalog, identity preservation and separate changed-source rejection", flush=True)

    def close(self):
        for collector in self.event_collectors:
            collector.close()
        for terminal in self.terminals:
            terminal.close()
        if getattr(self, "seed_helper", None):
            subprocess.run(["docker", "rm", self.seed_helper], capture_output=True, timeout=30)
        containers = subprocess.run(["docker", "ps", "--all", "--format", "{{.Names}}"], capture_output=True, timeout=30)
        for name in containers.stdout.decode().splitlines():
            if name.startswith(self.project + "-mcp-setup-"):
                subprocess.run(["docker", "rm", "--force", name], capture_output=True, timeout=30)
        # Cleanup only the unique fixture project and its own setup volumes.
        for source in (self.source, self.output / "deployment.json"):
            if source.is_file():
                subprocess.run([*self.compose[:-2], "-f", str(source), "down", "--volumes", "--remove-orphans"], capture_output=True, timeout=120)
        for suffix in ("http-host-setup", "http-client-data"):
            name = self.project + "-" + suffix
            subprocess.run(["docker", "volume", "rm", name], capture_output=True, timeout=30)
        if getattr(self, "fixture_web_image", None):
            subprocess.run(["docker", "image", "rm", self.fixture_web_image], capture_output=True, timeout=30)
        self.temporary.cleanup()

    def record_failure(self, failure):
        evidence = ROOT / "work" / "mcp-host-lifecycle" / self.project
        evidence.mkdir(mode=0o700, parents=True, exist_ok=True)
        evidence.chmod(0o700)
        def save(name, content):
            text = content.decode("utf-8", errors="replace") if isinstance(content, bytes) else content
            for secret in (self.password, self.pfx_password, getattr(self, "ordinary_secret", ""), getattr(self, "verifier_device_code", ""),
                    getattr(self, "database_password", ""), getattr(self, "client_secret", ""), getattr(self, "guard_secret", ""),
                    *getattr(self, "device_capabilities", [])):
                if secret:
                    text = text.replace(secret, "[redacted]")
            text = re.sub(r"rdk_[A-Za-z0-9_.-]{10,}", "[redacted credential]", text)
            text = re.sub(r"\b[A-Z2-9]{4}-[A-Z2-9]{4}\b", "[redacted code]", text)
            target = evidence / name
            descriptor = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
            with os.fdopen(descriptor, "w") as stream:
                stream.write(text[:4 * 1024 * 1024])
        save("failure.txt", str(failure))
        if isinstance(failure, CommandFailure):
            save("command.txt", failure.detail)
        for index, terminal in enumerate(self.terminals):
            save(f"terminal-{index + 1}.txt", terminal.text())
        def differences(before, after, prefix=""):
            if isinstance(before, dict) and isinstance(after, dict):
                return [path for key in sorted(before.keys() | after.keys()) for path in
                    (differences(before[key], after[key], prefix + "." + key) if key in before and key in after else [prefix + "." + key])][:64]
            if isinstance(before, list) and isinstance(after, list) and len(before) == len(after):
                return [path for index, (old, new) in enumerate(zip(before, after)) for path in differences(old, new, prefix + f"[{index}]")][:64]
            return [prefix] if before != after else []
        approved_runtime, current_runtime = self.output / "approved-runtime.json", self.output / "runtime-environment.json"
        if approved_runtime.is_file() and current_runtime.is_file():
            try:
                old = json.loads(approved_runtime.read_bytes()).get("x-rateldesk-runtime-inventory", {})
                new = json.loads(current_runtime.read_bytes()).get("x-rateldesk-runtime-inventory", {})
                raw_paths = differences(old, new)
                for inventory in (old, new):
                    for observed in inventory.values():
                        observed.pop("state", None)
                        observed.pop("healthStatus", None)
                        observed["mounts"] = sorted(observed.get("mounts", []), key=lambda mount: json.dumps(mount, sort_keys=True))
                save("runtime-differences.json", json.dumps({"rawDifferingKeyPaths": raw_paths,
                    "stableDifferingKeyPaths": differences(old, new), "valuesRecorded": False}, indent=2))
            except (OSError, ValueError, TypeError):
                save("runtime-differences.json", json.dumps({"comparisonUnavailable": True, "valuesRecorded": False}))
        approved_export, resolved_export = self.output / "approved-proposal.json", self.output / ".resolved-export.json"
        if approved_export.is_file() and resolved_export.is_file():
            try:
                old, new = json.loads(approved_export.read_bytes()), json.loads(resolved_export.read_bytes())
                for model in (old, new):
                    for service in model.get("services", {}).values():
                        service.pop("environment", None)
                    for key in list(model):
                        if key.startswith("x-rateldesk-"):
                            model.pop(key)
                save("export-topology-differences.json", json.dumps({"differingKeyPaths": differences(old, new),
                    "environmentAndMetadataOmitted": True, "valuesRecorded": False}, indent=2))
            except (OSError, ValueError, TypeError, AttributeError):
                save("export-topology-differences.json", json.dumps({"comparisonUnavailable": True, "valuesRecorded": False}))
        logs = subprocess.run([*self.compose, "logs", "--no-color", "--tail", "120"], capture_output=True, timeout=30)
        save("services.txt", logs.stdout + logs.stderr)
        print(f"FAIL: private redacted acceptance evidence: {evidence}", flush=True)


class PreservedFixture(Fixture):
    """Actual four-service PostgreSQL host journey, without setup serving mounts.

    The default owner is a saved-source adapter. --external-manager performs
    actual Portainer source save/deploy/read-back for the change case.
    """

    def __init__(self, args, changed=False, legacy=False):
        super().__init__(args, existing=True, legacy=legacy)
        self.changed = changed
        self.origin = f"https://localhost:{self.web_port}"
        self.source = self.directory / "compose.yml"
        self.compose[-1] = str(self.source)
        self.native = args.native_executable.resolve()
        self.client_directory = self.directory / "private-client"
        self.client_environment = {"RATELDESK_MCP_CLIENT_DIRECTORY": str(self.client_directory),
            "SSL_CERT_FILE": str(self.ca), "NO_PROXY": "localhost,127.0.0.1", "no_proxy": "localhost,127.0.0.1"}
        self.manager = None
        self.onramp = None
        self.device_capabilities = []
        self.original_source = self.source

    def prepare(self):
        super().prepare()
        if self.changed and self.args.external_manager:
            from mcp_external_manager_acceptance import PortainerOwner
            self.manager = PortainerOwner(self.directory / "external-owner", mounts=[self.directory])
            self.manager.start()
            self.manager_stack = self.manager.create(self.project, self.source, self.environment)
            self.source = Path(self.manager_stack["source"])
            self.compose[-1] = str(self.source)
            self.compose[self.compose.index("--project-directory") + 1] = str(self.source.parent)
            assert self.source.read_bytes() == self.original_source.read_bytes()
        # Adoption reviews an already working deployment. Wait for healthchecks
        # that already exist before freezing its baseline; absence stays absent.
        deadline = time.monotonic() + 150
        while time.monotonic() < deadline:
            identifiers = checked([*self.compose, "ps", "--all", "--quiet"]).decode().split()
            observed = json.loads(checked(["docker", "inspect", *identifiers])) if identifiers else []
            readiness = {item["Config"]["Labels"]["com.docker.compose.service"]:
                {"running": item["State"]["Running"], "health": item["State"].get("Health", {}).get("Status", "absent")} for item in observed}
            if len(readiness) == 4 and all(item["running"] and item["health"] in ("healthy", "absent") for item in readiness.values()):
                print("PASS: pristine four-service fixture readiness " + json.dumps(readiness, sort_keys=True), flush=True)
                return
            time.sleep(0.5)
        raise RuntimeError("Disposable baseline did not become ready using its existing healthchecks: " + json.dumps(readiness, sort_keys=True))

    def customize_model(self, model):
        services = model["services"]
        services.pop("untouched")
        model["volumes"].pop("unrelated")
        model["volumes"]["database"] = {}
        services["postgres"] = {"image": self.args.database_image, "environment": {
            "POSTGRES_DB": "rateldesk", "POSTGRES_USER": "rateldesk", "POSTGRES_PASSWORD": "${FIXTURE_DATABASE_PASSWORD}"},
            "volumes": ["database:/var/lib/postgresql/data"], "healthcheck": {
                "test": ["CMD-SHELL", "pg_isready -U $$POSTGRES_USER -d $$POSTGRES_DB"], "interval": "2s", "timeout": "3s", "retries": 30}}
        services["api"]["environment"].update({"Bootstrap__Unattended__Provider": "PostgreSql",
            "Bootstrap__Unattended__PostgreSqlConnectionString": "Host=postgres;Port=5432;Database=rateldesk;Username=rateldesk;Password=${FIXTURE_DATABASE_PASSWORD}"})
        services["api"]["depends_on"] = {"postgres": {"condition": "service_healthy"}}
        if self.changed:
            services["web"]["environment"]["WebGateway__Mcp__Enabled"] = "false"
        self.database_password = secrets.token_urlsafe(24)
        with self.environment.open("a") as stream:
            stream.write("FIXTURE_DATABASE_PASSWORD=" + self.database_password + "\n")
        for service in services.values():
            service["pull_policy"] = "never"
            service["labels"] = {"com.rateldesk.deployment.manager": "external-compose", "fixture.preserve": "operator-owned"}
        return model

    def write_model(self, model):
        # A fixture writer, never an operator-source editor. The tested product
        # must preserve these comments, anchors, interpolation and block scalar.
        def scalar(value):
            return json.dumps(value, ensure_ascii=False)
        def render(value, indent=0):
            prefix = " " * indent
            lines = []
            if isinstance(value, dict):
                for key, item in value.items():
                    if key == "labels":
                        lines.append(prefix + "labels: *operator-labels")
                    elif isinstance(item, (dict, list)) and item:
                        lines.append(prefix + scalar(key) + ":")
                        lines.extend(render(item, indent + 2))
                    else:
                        lines.append(prefix + scalar(key) + ": " + scalar(item))
            else:
                for item in value:
                    if isinstance(item, (dict, list)) and item:
                        lines.append(prefix + "-")
                        lines.extend(render(item, indent + 2))
                    else:
                        lines.append(prefix + "- " + scalar(item))
            return lines
        preamble = ("# Disposable operator YAML: preserve comments, order and quoting.\n"
            "x-operator-labels: &operator-labels\n"
            "  com.rateldesk.deployment.manager: external-compose\n"
            "  fixture.preserve: operator-owned\n"
            "x-operator-notes: |\n"
            "  Preserve this readable block verbatim.\n"
            "  Only explicitly reviewed gateway settings may change.\n")
        self.source.write_text(preamble + "\n".join(render(model)) + "\n")

    def sql(self, statement):
        return checked([*self.compose, "exec", "-T", "postgres", "psql", "-U", "rateldesk", "-d", "rateldesk",
            "--no-psqlrc", "--tuples-only", "--no-align", "--set", "ON_ERROR_STOP=1"], input_data=statement.encode(), timeout=60).decode().strip()

    def initialize(self):
        checked(["docker", "image", "inspect", self.args.database_image])
        checked([*self.compose, "up", "--pull", "never", "-d", "postgres"], timeout=120)
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            result = subprocess.run([*self.compose, "exec", "-T", "postgres", "pg_isready", "-U", "rateldesk", "-d", "rateldesk"], capture_output=True, timeout=10)
            if result.returncode == 0:
                break
            time.sleep(0.5)
        else:
            raise RuntimeError("Disposable PostgreSQL readiness timeout")
        checked([*self.compose, "run", "--rm", "--no-deps", "api", "--initialize-unattended"], timeout=300)
        self.seed_incidents()

    def seed_incidents(self):
        def literal(value):
            return "'" + str(value).replace("'", "''") + "'"
        organization = self.sql('SELECT o."Id" FROM "Organizations" o JOIN "Users" u ON u."OrganizationId" = o."Id" '
            'WHERE u."Email" = ' + literal(self.email) + ' AND o."Name" = ' + literal(self.organization_name) + ';').splitlines()
        assert len(organization) == 1, "Expected exactly one synthetic fixture organization"
        self.seed_organization = organization[0]
        columns = [json.loads(line) for line in self.sql("SELECT row_to_json(c) FROM (SELECT column_name, data_type, is_nullable, column_default "
            "FROM information_schema.columns WHERE table_name = 'Tickets' ORDER BY ordinal_position) c;").splitlines()]
        assert columns, "Expected actual PostgreSQL ticket hierarchy table"
        self.incident_ids = [str(uuid.uuid4()), str(uuid.uuid4())]
        for index, identifier in enumerate(self.incident_ids):
            values = {column["column_name"]: (0 if column["data_type"] in ("integer", "double precision", "numeric", "bigint") else
                "false" if column["data_type"] == "boolean" else "{}" if column["data_type"] == "ARRAY" else "")
                for column in columns if column["is_nullable"] == "NO" and column["column_default"] is None}
            values.update({"Id": identifier, "Discriminator": "Incident", "Title": "Beta2 needle" if index == 0 else "Beta2 baseline",
                "Description": "Synthetic disposable acceptance fixture", "OrganizationId": self.seed_organization,
                "CreatedAt": "2026-10-17 00:00:00", "TrackingId": "INC-BETA3-" + str(index),
                "LinkedAssetIds": "{}", "Attachments": "{}", "CcRecipientsJson": "[]"})
            names = list(values)
            self.sql('INSERT INTO "Tickets" (' + ','.join('"' + name + '"' for name in names) + ') VALUES (' +
                ','.join(literal(values[name]) for name in names) + ');')

    def launcher_arguments(self):
        # The preserved path deliberately excludes the separately retained
        # historical initializer fixture's explicit Advanced intent.
        arguments = super().launcher_arguments()[:-2]
        arguments[arguments.index("--project-directory") + 1] = str(self.source.parent)
        return [*arguments, "--native-executable", str(self.native), "--output-directory", str(self.output)]

    def terminal(self, arguments, executable=LAUNCHER):
        if self.onramp and executable == LAUNCHER:
            selected_arguments = self.onramp.terminal_prefix(arguments, executable=self.launcher)
            terminal = Terminal(selected_arguments, self.directory, "env", self.client_environment)
            self.terminals.append(terminal)
            if "http-client" not in arguments:
                self.onramp.select_existing(terminal)
            return terminal
        if executable == LAUNCHER:
            executable = self.launcher
        terminal = Terminal(arguments, self.directory, executable, self.client_environment)
        self.terminals.append(terminal)
        return terminal

    def browser_consent(self, code, kind):
        request = {"origin": self.origin, "email": self.email, "password": self.password, "kind": kind,
            "code": code, "organizationId": self.organization, "permissions": ["Incident.Read"]}
        if kind == "host":
            request["summaries"] = ["Defined services: 4 → 4", "postgres"]
            if self.changed:
                request["summaries"] += ["Source diff:", "WebGateway__Mcp__Enabled", "false", "true"]
                request["sourceDiff"] = True
        response = json.loads(checked(["node", str(ROOT / "tools/ci/mcp-host-browser-consent.mjs")],
            input_data=json.dumps(request).encode(), timeout=120))
        assert response == {"approved": True, "kind": kind, "browser": "chromium", "interactiveConsent": True}

    def inventory(self):
        containers = checked(["docker", "ps", "--all", "--quiet", "--filter", "label=com.docker.compose.project=" + self.project]).decode().split()
        metadata = json.loads(checked(["docker", "inspect", *containers])) if containers else []
        return {"containers": sorted(({"id": item["Id"], "name": item["Name"], "started": item["State"]["StartedAt"],
            "restarts": item["RestartCount"], "running": item["State"]["Running"], "mounts": sorted(item["Mounts"], key=lambda mount: json.dumps(mount, sort_keys=True)), "healthcheck": item["Config"].get("Healthcheck"),
            "labels": item["Config"].get("Labels"), "image": item["Image"]} for item in metadata), key=lambda item: item["id"]),
            "volumes": sorted(checked(["docker", "volume", "ls", "--quiet", "--filter", "label=com.docker.compose.project=" + self.project]).decode().split()),
            "networks": sorted(checked(["docker", "network", "ls", "--quiet", "--filter", "label=com.docker.compose.project=" + self.project]).decode().split()),
            "allDaemonObjects": {
                "containers": sorted(checked(["docker", "ps", "--all", "--quiet", "--no-trunc"]).decode().split()),
                "volumes": sorted(checked(["docker", "volume", "ls", "--quiet"]).decode().split()),
                "networks": sorted(checked(["docker", "network", "ls", "--quiet", "--no-trunc"]).decode().split())}}

    def begin_event_window(self, baseline):
        container = next(item["id"] for item in baseline["containers"] if item["labels"]["com.docker.compose.service"] == "mcp-http")
        collector = LiveDockerEvents(container)
        self.event_collectors.append(collector)
        collector.begin()
        return collector

    def assert_no_resource_events(self, collector, baseline):
        events = collector.finish()
        prohibited = {"create", "destroy", "start", "restart", "stop", "kill", "die", "connect", "disconnect",
            "pause", "unpause", "update", "rename", "checkpoint", "restore", "commit", "extract-to-dir"}
        existing_containers = {item["id"] for item in baseline["containers"]}
        existing_pairs = {(mount["Name"], item["id"]) for item in baseline["containers"] for mount in item["mounts"]
            if mount.get("Type") == "volume" and mount.get("Name")}
        references, archive_reads, balanced_pairs = {}, 0, 0
        for event in events:
            kind, action = event.get("Type"), event.get("Action", event.get("status"))
            actor = event.get("Actor", {})
            assert not (kind in ("container", "network", "volume") and action in prohibited), "Deployment-read-only setup must not create, remove, restart, write or attach any Docker resources, including unlabeled transient helpers"
            if kind == "volume" and action in ("mount", "unmount"):
                pair = (actor.get("ID"), actor.get("Attributes", {}).get("container"))
                assert pair in existing_pairs, "Read-only volume driver references must belong to an existing serving mount and baseline container"
                if action == "mount":
                    references.setdefault(pair, []).append(False)
                else:
                    assert references.get(pair) and references[pair].pop(), "Volume driver references must balance around a read-only archive operation"
                    balanced_pairs += 1
            elif kind == "container" and action == "archive-path" and actor.get("ID") in existing_containers:
                archive_reads += 1
                for pair, active in references.items():
                    if pair[1] == actor["ID"]:
                        active[:] = [True] * len(active)
        assert not any(references.values()), "Read-only archive volume references must all be released"
        print("PASS: global Docker lifecycle has zero resource creation/removal, serving mount changes or starts/restarts; " +
            str(archive_reads) + " read-only existing-container archives and " + str(balanced_pairs) + " balanced existing-volume driver references; " +
            str(len(events)) + " continuous events fenced by startup/end read-only exec handshakes", flush=True)

    def native_profile(self):
        path = self.client_directory / "mcp/incident-reader.json"
        raw = path.read_bytes()
        assert len(raw) <= 65536
        profile = json.loads(raw)
        assert profile["clientTransport"] == "http" and profile["credentialPurpose"] == "mcp"
        assert profile["credentialTarget"] == self.origin and profile["mcpResourceUri"] == self.origin + "/mcp"
        assert profile["credentialOrganizationId"] == self.organization and profile["credentialPermissions"] == ["Incident.Read"]
        assert not profile.get("integrationCredential") and not profile.get("authentikAppPassword")
        secret = Path(profile["integrationCredentialFile"])
        assert secret.is_relative_to(self.client_directory) and secret.suffix == ".secret"
        for private in (path, secret):
            assert private.stat().st_mode & 0o777 == 0o600, "Native profile and referenced credential must remain owner-only"
        for private in (path.parent, secret.parent):
            assert private.stat().st_mode & 0o777 == 0o700, "Native client directories must remain private"
        return path, raw, secret.read_bytes()

    def complete_native_client(self):
        self.wait_console_endpoint()
        print("RUN: native client terminal, actual Back and explicit browser consent", flush=True)
        before = {item["id"] for item in self.request("/api/v1/integration-credentials/")}
        setup = self.terminal([*self.launcher_arguments(), "--target", "http-client", "--", "--profile", "incident-reader",
            "--url", self.origin, "--monochrome"])
        setup.until("RatelDesk Web URL")
        setup.send(b"\x0e")
        setup.until("Continue to browser approval")
        setup.send(b"\x0e")
        code = setup.until(r"Short code:\s*([A-Z2-9]{4}-[A-Z2-9]{4})").group(1)
        previous_code = code
        offset = len(setup.traffic)
        setup.send(b"\x02")
        setup.until("Inputs are preserved", since=offset)
        offset = len(setup.traffic)
        setup.send(b"\x0e")
        code = setup.until(r"Short code:\s*(?!" + re.escape(previous_code) + r")([A-Z2-9]{4}-[A-Z2-9]{4})", since=offset).group(1)
        assert code != previous_code, "Ordinary-client Back must retain client authentication and fence the former request"
        self.browser_consent(code, "ordinary")
        setup.until("Approved organization:")
        setup.send(b"\x0e")
        setup.until("Client profile saved. Review transport checks and launch instructions")
        setup.send(b"\x0e")
        setup.finish()
        self.profile_snapshot = self.native_profile()
        self.client_secret = self.profile_snapshot[2].decode().strip()
        credentials = [item for item in self.request("/api/v1/integration-credentials/") if item["id"] not in before]
        assert len(credentials) == 1 and credentials[0]["purpose"] == "mcp" and credentials[0]["permissions"] == ["Incident.Read"]
        self.client_credential = credentials[0]
        remaining = datetime.fromisoformat(self.client_credential["expiresAtUtc"].replace("Z", "+00:00")).timestamp() - time.time()
        assert 23 * 3600 < remaining <= 24 * 3600 + 60, "The actual ordinary client grant must have the explicitly approved one-day lifetime"
        print("PASS: actual native client terminal and browser consent; one-day Incident.Read grant and protected profile", flush=True)

    def reload_native_client(self):
        assert self.native_profile() == self.profile_snapshot, "Fresh invocation must retain exact ordinary profile and secret"
        path, _, secret = self.profile_snapshot
        environment = os.environ.copy()
        environment.update(self.client_environment)
        def native(*arguments):
            result = subprocess.run([str(self.native), "--config", str(path), *arguments], env=environment,
                capture_output=True, timeout=90)
            if result.returncode:
                raise CommandFailure([str(self.native)], result)
            return json.loads(result.stdout)
        assert native("status")["profilePath"] == str(path)
        checks = {item["name"]: item["status"] for item in native("doctor", "--network", "--authenticate")["checks"]}
        assert all(checks.get(name) == "passed" for name in ("initialize", "catalog", "authentication", "tool-result"))
        assert "failed" not in checks.values()
        credential = next(item for item in self.request("/api/v1/integration-credentials/") if item["id"] == self.client_credential["id"])
        assert credential.get("revokedAtUtc") is None and credential["expiresAtUtc"] == self.client_credential["expiresAtUtc"]
        self.probe_incident_search(secret=secret.decode().strip())

    def review_host(self, intent):
        print("RUN: host " + intent + " review, actual Back, 80x24/wide terminal and browser source consent", flush=True)
        normal = [*self.launcher_arguments(), "--intent", intent, "--", "--url", self.origin, "--instance", "synthetic-private-west", "--monochrome"]
        before_cancel = {item["id"] for item in self.request("/api/v1/integration-credentials/")}
        cancelled = self.terminal(normal)
        cancelled.until("RatelDesk Web URL")
        cancelled.send(b"\x03")
        cancelled.finish()
        assert "Cancelled. Existing profiles are unchanged." in cancelled.text(), "Initial host Cancel must explicitly finish as a canceled draft"
        assert all(not (self.output / name).exists() for name in ("host-approval.json", "host-approval-session.json", "launcher-state.json", "verifier-cleanup.json")), "Canceled draft must not issue consent, save an approved generation or create temporary verification state"
        assert "Fresh verification pending:" not in cancelled.raw_text() and "--verify" not in cancelled.raw_text(), "Canceled draft must not advertise a completed checkpoint or verification command"
        assert {item["id"] for item in self.request("/api/v1/integration-credentials/")} == before_cancel
        setup = self.terminal(normal)
        setup.until("RatelDesk Web URL")
        offset = len(setup.traffic)
        setup.send(b"\x0e")
        hosting_body = "Verify existing deployment unchanged" if intent == "verify-existing" else "Prepare explicit changes."
        setup.until("(?s)^(?=.*HTTP MCP hosting)(?=.*" + re.escape(hosting_body) +
            ")(?=.*Server setup · personal client identities are separate)", since=offset)
        setup.send(b"\x0e")
        code = setup.until(r"[Cc]ode:\s*([A-Z2-9]{4}-[A-Z2-9]{4})").group(1)
        print("PASS: actual host approval request reached; exercising Back before browser consent", flush=True)
        # Back cancels this pending proposal and retains the actual host draft.
        previous_code = code
        offset = len(setup.traffic)
        setup.send(b"\x02")
        setup.until("Host draft retained", since=offset)
        offset = len(setup.traffic)
        setup.send(b"\x0e")
        code = setup.until(r"[Cc]ode:\s*(?!" + re.escape(previous_code) + r")([A-Z2-9]{4}-[A-Z2-9]{4})", since=offset).group(1)
        assert code != previous_code, "Back must fence/cancel the former host proposal before a fresh request"
        required = (code, "4 → 4", "Open browser", "Copy URL and code", "Details", "Back", "Cancel")
        complete_frame = "(?s)^" + "".join("(?=.*" + re.escape(value) + ")" for value in required)
        # One PTY read can end midway through an ANSI sequence or visible row.
        # Require every primary item in the current frame before inspecting it.
        setup.until(complete_frame, since=offset)
        screen = setup.screen()
        assert all(value in screen for value in (code, "Open browser", "Copy URL and code", "Details", "Back", "Cancel")), "Host approval actions/code must fit 80x24"
        assert "4 → 4" in screen, "The host review must show the actual unchanged defined-service count at 80x24"
        assert (re.search(r"No (?:deployment )?changes", screen) if intent == "verify-existing" else "change" in screen), "The host review must state its change/no-change purpose"
        offset = len(setup.traffic)
        setup.resize(36, 120)
        # This sidebar exists only at >=104 columns. It proves a fresh wide
        # layout without assuming the bordered form spans the sidebar's width.
        setup.until(complete_frame + r"(?=.*\bHosting\b)(?=.*\bReview\b)(?=.*\bDone\b)", since=offset)
        assert all(value in setup.screen() for value in (code, "4 → 4", "Open browser", "Details", "Back", "Cancel")), "Wide host review must retain count/code/actions after real terminal resize"
        print("RUN: Chromium administrator " + ("source-diff" if self.changed else "unchanged baseline") + " consent", flush=True)
        self.browser_consent(code, "host")
        setup.until("Administrator reviewed the unchanged baseline|Administrator approved the reviewed changes")
        setup.send(b"\x0e")
        setup.until("Existing baseline adopted|Changes prepared", seconds=60)
        setup.send(b"\x0e")
        setup.finish()
        print("PASS: explicit administrator browser consent and private " + ("prepared candidate" if self.changed else "adopted baseline") + " checkpoint; fresh verification pending", flush=True)
        printed = [line.strip() for line in setup.raw_text().splitlines()
            if line.strip().startswith("env ") and line.strip().endswith("--verify")]
        assert len(printed) == 1, "The completed host path must print one safely quoted NEW-process verification command"
        self.resume_command = shlex.split(printed[0])
        assert self.resume_command[0] == "env" and str(self.launcher) in self.resume_command and self.resume_command[-1] == "--verify"

    def verify_fresh(self):
        print("RUN: exact printed NEW-process host verification command and verifier Back/consent/cleanup", flush=True)
        before = {item["id"] for item in self.request("/api/v1/integration-credentials/")}
        # Execute the exact advertised argument vector, without shell evaluation.
        verify = self.terminal(self.resume_command[1:], executable=self.resume_command[0])
        code = verify.until(r"[Cc]ode:\s*([A-Z2-9]{4}-[A-Z2-9]{4})").group(1)
        journal = json.loads((self.output / "verifier-cleanup.json").read_bytes())
        previous_code, previous_device = code, journal["deviceCode"]
        self.device_capabilities.append(previous_device)
        reviewed_checkpoint = (self.output / "host-approval.json").read_bytes()
        offset = len(verify.traffic)
        verify.send(b"\x02")
        verify.until("Temporary consent cancelled", since=offset)
        offset = len(verify.traffic)
        verify.send(b"\x0e")
        code = verify.until(r"[Cc]ode:\s*(?!" + re.escape(previous_code) + r")([A-Z2-9]{4}-[A-Z2-9]{4})", since=offset).group(1)
        assert code != previous_code and (self.output / "host-approval.json").read_bytes() == reviewed_checkpoint, "Verifier Back must retain the reviewed host checkpoint and fence its former temporary request"
        cancelled = self.request("/api/v1/integration-credentials/pairing/poll", {"deviceCode": previous_device})
        assert cancelled["status"] == "cancelled" and cancelled.get("credential") is None
        journal = json.loads((self.output / "verifier-cleanup.json").read_bytes())
        device_code = journal.get("deviceCode")
        self.device_capabilities.append(device_code)
        assert re.fullmatch(r"[A-Fa-f0-9]{64}", device_code or "")
        self.browser_consent(code, "verifier")
        verify.until("End-to-end verified|Existing deployment verified unchanged", seconds=120)
        verify.finish(seconds=120)
        credentials = self.request("/api/v1/integration-credentials/")
        created = [item for item in credentials if item["id"] not in before]
        assert len(created) == 1 and created[0]["purpose"] == "mcp-setup" and not created[0]["permissions"]
        assert created[0].get("revokedAtUtc") is not None
        assert not (self.output / "verifier-cleanup.json").exists()
        poll = self.request("/api/v1/integration-credentials/pairing/poll", {"deviceCode": device_code})
        assert poll["status"] == "cancelled" and poll.get("credential") is None
        self.request("/api/v1/integration-credentials/" + created[0]["id"] + "/record", expected=(204,), method="DELETE")
        assert {item["id"] for item in self.request("/api/v1/integration-credentials/")} == before
        ordinary = next(item for item in self.request("/api/v1/integration-credentials/") if item["id"] == self.ordinary_id)
        if ordinary.get("revokedAtUtc") is not None or ordinary["expiresAtUtc"] != self.ordinary_expiry:
            print("OBSERVE: ordinary preservation " + json.dumps({"revoked": ordinary.get("revokedAtUtc") is not None,
                "persistedExpiryTextChanged": ordinary["expiresAtUtc"] != self.ordinary_expiry}), flush=True)
        assert ordinary.get("revokedAtUtc") is None and ordinary["expiresAtUtc"] == self.ordinary_expiry
        print("PASS: NEW host verification process, real browser status-only consent, complete catalog/status and revoked/deleted temporary identity", flush=True)

    def probe_temporary_guard(self):
        """Exercise the real older-protocol denial result and former-bearer 401.

        The owning instrumented HTTP tests separately prove zero downstream and
        handler invocations; a public transport response alone cannot count them.
        """
        trusted = urllib.request.build_opener(urllib.request.ProxyHandler({}),
            urllib.request.HTTPSHandler(context=ssl.create_default_context(cafile=str(self.ca))))
        def rpc(secret, method, parameters, expected=200):
            request = urllib.request.Request(f"https://127.0.0.1:{self.web_port}/mcp",
                data=json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": parameters}).encode(),
                headers={"Host": self.origin.split("://", 1)[1], "Content-Type": "application/json", "Authorization": "Bearer " + secret,
                    "Accept": "application/json, text/event-stream", "MCP-Protocol-Version": "2025-03-26"})
            try:
                with trusted.open(request, timeout=30) as response:
                    status, content = response.status, response.read(1024 * 1024 + 1)
            except urllib.error.HTTPError as response:
                status, content = response.code, response.read(16384)
            assert status == expected, "Temporary verifier transport returned an unexpected status"
            assert len(content) <= 1024 * 1024
            if expected != 200:
                return None
            text = content.decode()
            if not text.lstrip().startswith("{"):
                text = '\n'.join(line[5:].lstrip() for line in text.splitlines() if line.startswith("data:"))
            payload = json.loads(text)
            assert "error" not in payload, "Known verifier denial must retain its safe tool-result contract"
            return payload["result"]
        before = {item["id"] for item in self.request("/api/v1/integration-credentials/")}
        session = self.request("/api/v1/integration-credentials/pairing/start", {"name": "Synthetic status-only boundary probe",
            "purpose": "mcp", "mcpResourceUri": self.origin + "/mcp", "grantKind": "setup-verification"})
        device_code = session["deviceCode"]
        self.device_capabilities.append(device_code)
        secret = None
        try:
            self.browser_consent(session["humanCode"], "verifier")
            delivered = self.request("/api/v1/integration-credentials/pairing/poll", {"deviceCode": device_code})
            credential = delivered["credential"]
            assert credential["grantKind"] == "setup-verification" and credential["permissions"] == []
            remaining = datetime.fromisoformat(credential["expiresAtUtc"].replace("Z", "+00:00")).timestamp() - time.time()
            assert 0 < remaining <= 310, "Temporary verifier lifetime must remain bounded to five minutes"
            secret = credential["secret"]
            self.guard_secret = secret
            assert rpc(secret, "initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                "clientInfo": {"name": "disposable-verifier-boundary", "version": "1"}})["protocolVersion"] == "2025-03-26"
            assert len(rpc(secret, "tools/list", {})["tools"]) == 28
            status = rpc(secret, "tools/call", {"name": "helpdesk_auth", "arguments": {"operation": "status"}})
            assert status.get("isError") is not True and status["structuredContent"]["success"]
            denied = rpc(secret, "tools/call", {"name": "helpdesk_incidents", "arguments": {"operation": "list", "request": {}}})
            assert denied.get("isError") is True
            contract = denied.get("structuredContent") or json.loads(next(item["text"] for item in denied["content"] if item.get("type") == "text"))
            assert contract["success"] is False and contract["failure"]["code"] == "setup_verification_operation_denied"
            assert not contract.get("data") and contract["nextActions"]
            self.request("/api/v1/integration-credentials/pairing/cancel", {"deviceCode": device_code}, expected=(204,))
            rpc(secret, "tools/list", {}, expected=401)
        finally:
            self.request("/api/v1/integration-credentials/pairing/cancel", {"deviceCode": device_code}, expected=(204,))
            created = [item for item in self.request("/api/v1/integration-credentials/") if item["id"] not in before]
            assert len(created) == 1 and created[0].get("revokedAtUtc") is not None and not created[0]["permissions"]
            self.request("/api/v1/integration-credentials/" + created[0]["id"] + "/record", expected=(204,), method="DELETE")
        assert {item["id"] for item in self.request("/api/v1/integration-credentials/")} == before
        print("PASS: actual 2025-03-26 status-only verifier returns stable isError denial with no business data; canceled bearer returns 401 and revoked record is deleted", flush=True)

    def reject_interrupted_manager_apply(self, record):
        source, candidate = Path(record["source"]), Path(record["candidate"])
        original, prepared = source.read_bytes(), candidate.read_bytes()
        before_runtime = self.inventory()
        before_credentials = {item["id"] for item in self.request("/api/v1/integration-credentials/")}
        unchanged = {path: path.read_bytes() for path in (candidate, self.output / "host-approval.json",
            self.output / "host-approval-session.json", self.output / "approved-proposal.json",
            self.output / "approved-effective-deployment.json")}
        state_before = json.loads((self.output / "launcher-state.json").read_bytes())
        events = self.begin_event_window(before_runtime)
        interrupted = self.manager.interrupt_apply(self.manager_stack, candidate, record["originalSha256"])
        assert interrupted["status"] == 400 and interrupted["baselinePreserved"], "The actual truncated manager request must be rejected before any source save/deploy"
        assert source.read_bytes() == original and candidate.read_bytes() == prepared
        environment = os.environ.copy()
        environment.update(self.client_environment)
        rejected = subprocess.run(self.resume_command, cwd=self.directory, env=environment, capture_output=True, timeout=120)
        print("OBSERVE: fresh verification after interrupted actual manager request " + json.dumps({
            "exit": rejected.returncode, "runtimeMismatch": b"HOST_RUNTIME_MISMATCH" in rejected.stderr}), flush=True)
        assert rejected.returncode == 78 and b"HOST_RUNTIME_MISMATCH" in rejected.stderr, "Unapplied approved settings must stop fresh verification at the observed runtime mismatch"
        assert b"End-to-end verified" not in rejected.stdout and b"Existing deployment verified unchanged" not in rejected.stdout
        assert {item["id"] for item in self.request("/api/v1/integration-credentials/")} == before_credentials
        assert not (self.output / "verifier-cleanup.json").exists(), "The unapplied generation must fail before any temporary verifier issuance"
        assert all(path.read_bytes() == content for path, content in unchanged.items()), "An interrupted owner request must not renew approval, repair hashes or replace its exact reviewed candidate"
        state_after = json.loads((self.output / "launcher-state.json").read_bytes())
        assert state_after["phase"] == "applied-verify-pending"
        assert set(state_after) == set(state_before) | {"verificationStartedAtUtc"}
        assert all(state_after[key] == value for key, value in state_before.items() if key != "phase"), "Failed verification may mark pending but cannot rebase any reviewed checkpoint field"
        assert source.read_bytes() == original and self.inventory() == before_runtime
        self.assert_no_resource_events(events, before_runtime)
        print("PASS: actual Portainer truncated PUT returned 400 before save/deploy; exact source/runtime and approved candidate retained; NEW verification returned 78/pending without a verifier or hash renewal; explicit recovery uses the same approved candidate", flush=True)

    def apply_saved_source_adapter(self):
        candidates = json.loads((self.output / "launcher-state.json").read_bytes())["sourceCandidates"]
        assert len(candidates) == 1, "The narrow gateway correction must touch only its authoritative YAML source"
        record = candidates[0]
        source, candidate = Path(record["source"]), Path(record["candidate"])
        assert source == self.source and candidate.is_relative_to(self.output)
        original, prepared = source.read_bytes(), candidate.read_bytes()
        assert hashlib.sha256(original).hexdigest() == record["originalSha256"]
        assert hashlib.sha256(prepared).hexdigest() == record["candidateSha256"]
        before_lines, after_lines = original.splitlines(), prepared.splitlines()
        print("OBSERVE: reviewed candidate source shape " + json.dumps({"beforeLineCount": len(before_lines),
            "afterLineCount": len(after_lines), "redactedKeyActions": record.get("safeDiff", "")}), flush=True)
        assert len(before_lines) == len(after_lines)
        changes = [(old, new) for old, new in zip(before_lines, after_lines) if old != new]
        assert len(changes) == 1 and b"WebGateway__Mcp__Enabled" in changes[0][0]
        assert b"false" in changes[0][0] and b"true" in changes[0][1]
        assert b"# Disposable operator YAML" in prepared and b"*operator-labels" in prepared and b"x-operator-notes: |" in prepared
        self.applied = self.compose
        if self.manager:
            self.reject_interrupted_manager_apply(record)
            result = self.manager.apply(self.manager_stack, candidate, record["originalSha256"])
            assert result["readBackExact"] and source.read_bytes() == prepared
        else:
            source.write_bytes(prepared)  # Explicit disposable saved-source owner adapter.
            checked([*self.compose, "up", "--pull", "never", "-d"], timeout=180)
        self.wait_console_endpoint()
        print("PASS: " + ("actual Portainer CE save/deploy/read-back applied one exact readable YAML setting" if self.manager else
            "explicit saved-source owner adapter applied one readable YAML setting; this is not live external-manager acceptance"), flush=True)

    def close(self):
        try:
            if self.manager:
                self.manager.close()
        finally:
            self.source = self.original_source
            self.compose[-1] = str(self.source)
            self.compose[self.compose.index("--project-directory") + 1] = str(self.directory)
            super().close()

    def reject_changed_source(self):
        before = {item["id"] for item in self.request("/api/v1/integration-credentials/")}
        original = self.source.read_bytes()
        try:
            self.source.write_bytes(original + b"\n# Separate concurrent-owner edit: renewed source review required.\n")
            environment = os.environ.copy()
            environment.update(self.client_environment)
            result = subprocess.run(self.resume_command,
                cwd=self.directory, env=environment, capture_output=True, timeout=120)
            assert result.returncode == 78 and b"approved_sources_changed" in result.stderr, "Concurrent owner source changes must stop fresh verification before consent"
            assert {item["id"] for item in self.request("/api/v1/integration-credentials/")} == before
            state = json.loads((self.output / "launcher-state.json").read_bytes())
            assert state["phase"] not in ("end-to-end-verified", "existing-verified-unchanged"), "Rejected verification must invalidate the prior completed phase"
        finally:
            self.source.write_bytes(original)
        print("PASS: actual saved-source concurrent edit is rejected before new verifier issuance; no checkpoint hash is repaired", flush=True)

    def run(self):
        self.prepare()
        self.applied = self.compose
        baseline_source, baseline_env = self.source.read_bytes(), self.environment.read_bytes()
        dependencies = [self.environment, self.directory / "fixture.pfx", self.ca]
        if self.legacy:
            dependencies.append(self.legacy_config)
        baseline_dependencies = {path: path.read_bytes() for path in dependencies}
        before = self.inventory()
        assert len(before["containers"]) == 4
        assert all(item["running"] for item in before["containers"]), "The pristine baseline must have four actual running services"
        assert all(item["restarts"] == 0 for item in before["containers"])
        web = next(item for item in before["containers"] if item["labels"].get("com.docker.compose.service") == "web")
        assert not web["healthcheck"], "The Web fixture deliberately has no Docker healthcheck"
        adoption_events = self.begin_event_window(before)
        if not self.changed:
            self.complete_native_client()
        self.review_host("prepare-changes" if self.changed else "verify-existing")
        assert self.source.read_bytes() == baseline_source and self.environment.read_bytes() == baseline_env
        assert self.inventory() == before, "Review/adoption must create no Docker resources or restart/recreate serving containers"
        self.assert_no_resource_events(adoption_events, before)
        if self.changed:
            self.apply_saved_source_adapter()
            after = self.inventory()
            assert len(after["containers"]) == 4 and len(after["volumes"]) == len(before["volumes"])
            assert all(item["running"] for item in after["containers"])
            for previous in before["containers"]:
                service = previous["labels"]["com.docker.compose.service"]
                current = next(item for item in after["containers"] if item["labels"]["com.docker.compose.service"] == service)
                if service != "web":
                    assert current == previous, "Only the explicitly corrected Web service may be recreated"
                else:
                    assert current["id"] != previous["id"] and current["started"] != previous["started"], "The approved real Web setting correction must be applied to the running service"
            print("PASS: defined services api/web/mcp-http/postgres remain 4 → 4; exact source delta WebGateway__Mcp__Enabled false → true; only Web was recreated", flush=True)
            self.complete_native_client()
            verified_source, verified_env, verified_runtime = self.source.read_bytes(), self.environment.read_bytes(), after
        else:
            assert not (self.output / "deployment.json").exists(), "No-change adoption must not require a generated deployment document"
            verified_source, verified_env, verified_runtime = baseline_source, baseline_env, before
            print("PASS: unchanged four-service source/config bytes, global object IDs and serving mounts/starts; zero creation/removal/recreation/restarts; Web has no Docker healthcheck", flush=True)
        verification_events = self.begin_event_window(verified_runtime)
        self.verify_fresh()
        self.reload_native_client()
        if not self.changed and not self.legacy:
            self.probe_temporary_guard()
        # Repeat from another NEW process; the unchanged baseline is retained.
        self.verify_fresh()
        assert self.source.read_bytes() == verified_source and self.environment.read_bytes() == verified_env
        assert all(path.read_bytes() == content for path, content in baseline_dependencies.items()), "Host adoption/apply/verification must preserve unrelated source/configuration dependencies byte-for-byte"
        assert self.native_profile() == self.profile_snapshot, "Repeated host verification must not replace the ordinary client identity"
        assert self.inventory() == verified_runtime, "Fresh verification must leave Docker objects, starts/restarts, mounts, healthchecks and labels unchanged"
        self.assert_no_resource_events(verification_events, verified_runtime)
        if self.legacy:
            assert self.legacy_config.read_text() == json.dumps({"apiBaseUrl": "http://api:8222/", "credentialMode": "gateway"})
        self.reject_changed_source()
        host_session = json.loads((self.output / "host-approval-session.json").read_bytes())
        self.device_capabilities.append(host_session["deviceCode"])
        private_values = [self.password, self.pfx_password, self.database_password, self.ordinary_secret,
            self.client_secret, getattr(self, "guard_secret", ""), *self.device_capabilities]
        assert all(not value or all(value not in terminal.raw_text() for terminal in self.terminals) for value in private_values), "Actual terminal rendering must not expose credentials or device capabilities"
        if self.onramp:
            state = json.loads((self.output / "launcher-state.json").read_bytes())
            assert state["composeVersion"] == "2.39.4"
            assert state["dockerSelection"]["DOCKER_CONTEXT"] == self.onramp.context
            self.onramp.assert_unchanged()
            print("PASS: actual incompatible 2.26.1 choice reached native client, four-service host/browser review and exact printed NEW verification commands with verified private 2.39.4; original plugin/config/context unchanged", flush=True)
        print("PASS: four-service " + ("necessary-change" if self.changed else "unchanged adoption") + " host lifecycle; source/dependency bytes, full Docker inventory and ordinary identity/expiry preserved", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-image", required=True)
    parser.add_argument("--web-image", required=True)
    parser.add_argument("--mcp-image", required=True)
    parser.add_argument("--case", choices=("adopt", "change", "legacy-retain", "fresh", "existing", "legacy", "all"), default="all")
    parser.add_argument("--native-executable", type=Path, help="Actual extracted native rateldesk-mcp executable; required for preserved host cases.")
    parser.add_argument("--database-image", default="postgres:16", help="Already present PostgreSQL fixture image; never pulled by this lane.")
    parser.add_argument("--external-manager", action="store_true", help="Use the actual disposable Portainer owner for the change case; requires the pinned image already present.")
    parser.add_argument("--legacy-compose-onramp", action="store_true", help="Start adoption or explicit initializer compatibility with a real private 2.26.1 plugin and exercise the deliberate compatible-tool choice.")
    parser.add_argument("--compose-fixture-cache", type=Path, help="Offline verified 2.39.4 platform fixture for the onramp.")
    parser.add_argument("--legacy-compose-fixture-cache", type=Path, help="Offline verified 2.26.1 platform fixture for the onramp.")
    parser.add_argument("--download-compose-fixtures", action="store_true", help="Explicitly allow pinned disposable Compose fixture downloads for --legacy-compose-onramp.")
    parser.add_argument("--deployment-archive", type=Path, help="Exercise launchers from this extracted deployment archive.")
    args = parser.parse_args()
    cases = ["adopt", "change", "legacy-retain"] if args.case == "all" else [args.case]
    if any(case in ("adopt", "change", "legacy-retain") for case in cases) and (not args.native_executable or not args.native_executable.is_file()):
        parser.error("Preserved host acceptance requires --native-executable pointing to the actual extracted rateldesk-mcp executable")
    for case in cases:
        with ExitStack() as resources:
            fixture = (PreservedFixture(args, changed=case == "change", legacy=case == "legacy-retain") if case in ("adopt", "change", "legacy-retain")
                else Fixture(args, case != "fresh", case == "legacy"))
            if case in ("adopt", "legacy") and args.legacy_compose_onramp:
                from private_compose_fixture import legacy_compose_fixture
                fixture.onramp = resources.enter_context(legacy_compose_fixture(compose_cache=args.compose_fixture_cache,
                    legacy_cache=args.legacy_compose_fixture_cache, download=args.download_compose_fixtures))
                # Reproduce the original synthetic shell PATH for exact printed
                # resume invocations; its credential helper remains selectable.
                if isinstance(fixture, PreservedFixture):
                    fixture.client_environment["PATH"] = fixture.onramp.environment["PATH"]
            try:
                fixture.run()
            except Exception as failure:
                fixture.record_failure(failure)
                raise
            finally:
                fixture.close()


if __name__ == "__main__":
    main()
