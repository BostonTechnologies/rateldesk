#!/usr/bin/env python3
"""Explicit disposable full-host lane: real launcher, approval, recreation, NEW verify.

Use already built current-source images. This never pulls, publishes, updates an
owner deployment, or removes persistent volumes outside its unique test project.
API consent exercises the actual administrator/organization authorization path;
browser layout and public distribution integrity are separate acceptance lanes.
"""
import argparse
import errno
import fcntl
import http.cookiejar
import json
import os
from pathlib import Path
import pty
import re
import secrets
import select
import signal
import socket
import ssl
import struct
import subprocess
import tempfile
import termios
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
    def __init__(self, arguments, directory, executable=LAUNCHER):
        self.child, self.fd = pty.fork()
        if self.child == 0:
            os.chdir(directory)
            os.environ["TERM"] = "xterm-256color"
            os.execvp(str(executable), [str(executable), *arguments])
        fcntl.ioctl(self.fd, termios.TIOCSWINSZ, struct.pack("HHHH", 80, 120, 0, 0))
        os.kill(self.child, signal.SIGWINCH)
        self.traffic = bytearray()
        self.status = None

    def send(self, value):
        os.write(self.fd, value)

    def text(self):
        return terminal_text(self.traffic, history=True)

    def raw_text(self):
        return raw_terminal_text(self.traffic)

    def until(self, expression, seconds=120):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            match = re.search(expression, self.text())
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
        raise RuntimeError("Disposable terminal did not reach expected stage: " + expression)

    def finish(self, expected=0, seconds=60):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            completed, status = os.waitpid(self.child, os.WNOHANG)
            if completed:
                self.status = os.waitstatus_to_exitcode(status)
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
                os.kill(self.child, signal.SIGKILL)
                os.waitpid(self.child, 0)
            except (ProcessLookupError, ChildProcessError):
                pass
        os.close(self.fd)


class Fixture:
    def __init__(self, args, existing):
        self.args, self.existing = args, existing
        self.project = "rdk-host-fixture-" + uuid.uuid4().hex[:12]
        self.temporary = tempfile.TemporaryDirectory(prefix=self.project + "-")
        self.directory = Path(self.temporary.name)
        self.api_port, self.web_port = port(), port()
        while self.api_port == self.web_port:
            self.web_port = port()
        self.origin = f"https://desk.fixture.test:{self.web_port}"
        self.api = f"http://127.0.0.1:{self.api_port}"
        self.email = "disposable-admin@example.test"
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
            "Bootstrap__Unattended__OrganizationName": "Disposable acceptance organization",
            "Bootstrap__Unattended__ApplicationName": "RatelDesk acceptance", "Bootstrap__Unattended__ApplicationUrl": self.origin,
            "IntegrationCredentials__Pairing__PublicOrigin": self.origin}
        services = {
            "api": {"image": self.args.api_image, "environment": api_environment, "volumes": ["state:/fixture"],
                    "ports": [f"127.0.0.1:{self.api_port}:8222"]},
            "web": {"image": self.args.web_image, "environment": {
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
        else:
            # Match the shipped release recipe's ownership declaration. Fresh
            # setup discovers its actual Web container and saved source labels.
            for name in ("api", "web"):
                services[name]["labels"] = {"com.rateldesk.deployment.manager": "release-compose"}
        self.source.write_text(json.dumps({"name": self.project, "services": services, "volumes": {"state": {}, "unrelated": {}}}))
        self.source.chmod(0o600)
        checked([*self.compose, "run", "--rm", "--no-deps", "api", "--initialize-unattended"], timeout=300)
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
        created = self.request("/api/v1/integration-credentials/", {"name": "Identity preservation fixture", "purpose": "api",
            "organizationId": self.organization, "permissions": ["Incident.Read"], "lifetimeDays": 1}, expected=(201,))
        self.ordinary_id = created["id"]
        self.ordinary_secret = created["secret"]

    def launcher_arguments(self):
        if not self.existing:
            return ["--ca-file", str(self.ca)]
        return ["--project", self.project, "--project-directory", str(self.directory), "--compose-file", str(self.source),
                "--env-file", str(self.environment), "--manager", "external-compose" if self.existing else "release-compose",
                "--image", self.args.mcp_image, "--ca-file", str(self.ca)]

    def terminal(self, arguments, executable=LAUNCHER):
        terminal = Terminal(arguments, self.directory, executable)
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
                    headers={"Host": f"desk.fixture.test:{self.web_port}", "Accept": "application/json"})
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
        return root + "/mcp/default.json"

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
        setup = self.terminal(self.console_arguments(identifier, "setup", "--mode", "client", "--url", self.origin,
            "--monochrome", interactive=True), executable="docker")
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
        status = json.loads(checked(["docker", *self.console_arguments(identifier, "status")]))
        assert status["profilePath"] == self.console_profile_path and status["configuration"]["credentialPurpose"] == "mcp"
        diagnostic = json.loads(checked(["docker", *self.console_arguments(identifier, "doctor", "--authenticate")], timeout=90))
        checks = {item["name"]: item["status"] for item in diagnostic["checks"]}
        assert all(checks.get(name) == "passed" for name in ("initialize", "catalog", "authentication", "tool-result")), "A new console process must authenticate the retained ordinary credential"
        assert "failed" not in checks.values()
        credentials = self.request("/api/v1/integration-credentials/")
        credential = next(item for item in credentials if item["id"] == self.console_credential_id)
        assert credential.get("revokedAtUtc") is None and credential["expiresAtUtc"] == self.console_credential_expiry
        before = {item["id"] for item in credentials}
        reentry = self.terminal(self.console_arguments(identifier, "setup", "--mode", "client", "--url", self.origin,
            "--monochrome", interactive=True), executable="docker")
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
        print("PASS: full container recreation retained ordinary identity/expiry; NEW default-profile status/doctor and existing-identity wizard reload passed without replacement consent", flush=True)

    def run(self):
        self.prepare()
        normal = self.launcher_arguments() + ["--", "--url", self.origin, "--instance", "synthetic-private-west", "--monochrome"]
        cancelled = self.terminal(normal)
        cancelled.until("RatelDesk Web URL")
        cancelled.send(b"\x03")
        cancelled.finish(expected=2)
        assert not (self.output / "host-approval.json").exists(), "Cancel must not export approval"
        setup = self.terminal(normal)
        setup.until("RatelDesk Web URL")
        setup.send(b"\x0e")
        setup.until("Review the server settings")
        setup.send(b"\x0e")
        setup.until("Host deployment approval pending")
        setup.send(b"\x1b[6~")
        code = setup.until(r"Code:\s*([A-Z2-9]{4}-[A-Z2-9]{4})").group(1)
        self.approve_host(code)
        setup.until("Administrator approved the reviewed generation")
        setup.send(b"\x0e")
        setup.until("Saved.apply pending")
        setup.send(b"\x0e")
        setup.finish()
        complete = self.output / "deployment.json"
        document = json.loads(complete.read_bytes())
        assert "untouched" in document["services"] and "unrelated" in document["volumes"]
        assert "PRESERVE_ME=existing-manager-content" in str(document["services"]["untouched"]["environment"]) or document["services"]["untouched"]["environment"].get("PRESERVE_ME") == "existing-manager-content"
        if self.existing:
            # Simulate the existing manager's reviewed complete import at the
            # ORIGINAL persisted source path, not a transient additional overlay.
            self.source.write_bytes(complete.read_bytes())
            applied = self.compose
        else:
            checked([str(LAUNCHER), *self.launcher_arguments(), "--apply"], cwd=self.directory)
            applied = self.compose[:-2] + ["-f", str(complete)]
        self.applied = applied
        # Apply the manager-owned complete document first, so both first-time
        # and existing stacks have the reviewed protected stores before console setup.
        checked([*applied, "up", "--pull", "never", "-d"], timeout=180)
        identifier = checked([*applied, "ps", "--quiet", "mcp-http"]).decode().strip()
        self.complete_console_client(identifier)
        checked([*applied, "down"], timeout=180)  # Deliberately preserve data/stores.
        checked([*applied, "up", "--pull", "never", "-d"], timeout=180)
        time.sleep(5)
        identifier = checked([*applied, "ps", "--quiet", "mcp-http"]).decode().strip()
        verify = self.terminal(["--container", identifier, "--ca-file", str(self.ca), "--verify"])
        code = verify.until(r"Code:\s*([A-Z2-9]{4}-[A-Z2-9]{4})").group(1)
        review = self.request("/api/v1/integration-credentials/pairing/review", {"humanCode": code})
        assert review["grantKind"] == "setup-verification"
        self.request("/api/v1/integration-credentials/pairing/approve", {"humanCode": code,
            "organizationId": self.organization, "permissions": [], "lifetimeDays": None, "consent": True}, expected=(204,))
        verify.until("End-to-end verified")
        verify.finish(seconds=120)
        self.reload_console_client(identifier)
        credentials = self.request("/api/v1/integration-credentials/")
        assert any(item["id"] == self.ordinary_id and item.get("revokedAtUtc") is None for item in credentials)
        changed = json.loads(self.source.read_bytes())
        changed["services"]["untouched"]["environment"] = {"PRESERVE_ME": "separate changed-source fixture"}
        self.source.write_text(json.dumps(changed))
        rejected = subprocess.run([str(LAUNCHER), "--container", identifier, "--ca-file", str(self.ca), "--verify"],
            cwd=self.directory, capture_output=True, timeout=180)
        assert rejected.returncode == 78 and b"approved_sources_changed" in rejected.stderr
        assert json.loads((self.output / "launcher-state.json").read_bytes())["phase"] == "applied-verify-pending", "A failed NEW verification attempt must clear the prior verified phase"
        for terminal in self.terminals:
            assert self.password not in terminal.raw_text() and self.ordinary_secret not in terminal.raw_text()
        print("PASS: " + ("existing external-manager" if self.existing else "fresh release-owned") + " actual launcher cancel/reopen, source-bound administrator approval, complete preserved export/apply, full recreation + NEW verified invocation, API-backed status/catalog, identity preservation and separate changed-source rejection", flush=True)

    def close(self):
        for terminal in self.terminals:
            terminal.close()
        # Cleanup only the unique fixture project and its own setup volumes.
        for source in (self.source, self.output / "deployment.json"):
            if source.is_file():
                subprocess.run([*self.compose[:-2], "-f", str(source), "down", "--volumes", "--remove-orphans"], capture_output=True, timeout=120)
        for suffix in ("http-host-setup", "http-client-data"):
            name = self.project + "-" + suffix
            subprocess.run(["docker", "volume", "rm", name], capture_output=True, timeout=30)
        self.temporary.cleanup()

    def record_failure(self, failure):
        evidence = ROOT / "work" / "mcp-host-lifecycle" / self.project
        evidence.mkdir(mode=0o700, parents=True, exist_ok=True)
        evidence.chmod(0o700)
        def save(name, content):
            text = content.decode("utf-8", errors="replace") if isinstance(content, bytes) else content
            for secret in (self.password, self.pfx_password, getattr(self, "ordinary_secret", "")):
                if secret:
                    text = text.replace(secret, "[redacted]")
            text = re.sub(r"rdk_[A-Za-z0-9_.-]{10,}", "[redacted credential]", text)
            target = evidence / name
            descriptor = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
            with os.fdopen(descriptor, "w") as stream:
                stream.write(text[:4 * 1024 * 1024])
        save("failure.txt", str(failure))
        if isinstance(failure, CommandFailure):
            save("command.txt", failure.detail)
        for index, terminal in enumerate(self.terminals):
            save(f"terminal-{index + 1}.txt", terminal.text())
        logs = subprocess.run([*self.compose, "logs", "--no-color", "--tail", "120"], capture_output=True, timeout=30)
        save("services.txt", logs.stdout + logs.stderr)
        print(f"FAIL: private redacted acceptance evidence: {evidence}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-image", required=True)
    parser.add_argument("--web-image", required=True)
    parser.add_argument("--mcp-image", required=True)
    parser.add_argument("--case", choices=("fresh", "existing", "all"), default="all")
    args = parser.parse_args()
    for existing in ([False, True] if args.case == "all" else [args.case == "existing"]):
        fixture = Fixture(args, existing)
        try:
            fixture.run()
        except Exception as failure:
            fixture.record_failure(failure)
            raise
        finally:
            fixture.close()


if __name__ == "__main__":
    main()
