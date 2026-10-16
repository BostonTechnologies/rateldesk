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

ROOT = Path(__file__).resolve().parents[2]
LAUNCHER = ROOT / "tools/mcp/setup-http-mcp.sh"


def checked(arguments, *, cwd=None, input_data=None, timeout=240):
    completed = subprocess.run(arguments, cwd=cwd, input=input_data, capture_output=True, timeout=timeout)
    if completed.returncode:
        raise RuntimeError(f"Disposable host acceptance command failed (exit {completed.returncode}); private fixture retained only for process cleanup.")
    return completed.stdout


def port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


class Terminal:
    def __init__(self, arguments, directory):
        self.child, self.fd = pty.fork()
        if self.child == 0:
            os.chdir(directory)
            os.environ["TERM"] = "xterm-256color"
            os.execv(str(LAUNCHER), [str(LAUNCHER), *arguments])
        fcntl.ioctl(self.fd, termios.TIOCSWINSZ, struct.pack("HHHH", 80, 120, 0, 0))
        os.kill(self.child, signal.SIGWINCH)
        self.traffic = bytearray()
        self.status = None

    def send(self, value):
        os.write(self.fd, value)

    def text(self):
        return re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", self.traffic.decode("utf-8", errors="replace"))

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
        return ["--project", self.project, "--project-directory", str(self.directory), "--compose-file", str(self.source),
                "--env-file", str(self.environment), "--manager", "external-compose" if self.existing else "release-compose",
                "--image", self.args.mcp_image, "--ca-file", str(self.ca)]

    def terminal(self, arguments):
        terminal = Terminal(arguments, self.directory)
        self.terminals.append(terminal)
        return terminal

    def approve_host(self, code):
        review = self.request("/api/v1/integration-credentials/host-proposals/review", {"humanCode": code})
        proposal = review["proposal"]
        self.request("/api/v1/integration-credentials/host-proposals/approve", {"humanCode": code,
            "proposalHash": proposal["proposalHash"], "sourceHash": proposal["sourceHash"], "consent": True}, expected=(204,))

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
        credentials = self.request("/api/v1/integration-credentials/")
        assert any(item["id"] == self.ordinary_id and item.get("revokedAtUtc") is None for item in credentials)
        changed = json.loads(self.source.read_bytes())
        changed["services"]["untouched"]["environment"] = {"PRESERVE_ME": "separate changed-source fixture"}
        self.source.write_text(json.dumps(changed))
        rejected = subprocess.run([str(LAUNCHER), "--container", identifier, "--ca-file", str(self.ca), "--verify"],
            cwd=self.directory, capture_output=True, timeout=180)
        assert rejected.returncode == 78 and b"approved_sources_changed" in rejected.stderr
        for terminal in self.terminals:
            assert self.password not in terminal.text() and self.ordinary_secret not in terminal.text()
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
        finally:
            fixture.close()


if __name__ == "__main__":
    main()
