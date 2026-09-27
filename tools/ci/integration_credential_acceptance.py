#!/usr/bin/env python3
"""Run a disposable protected-operation journey against local Release publishes."""

from __future__ import annotations

import argparse
import hashlib
import http.cookiejar
import http.client
import json
import os
from pathlib import Path, PurePosixPath
import re
import secrets
import select
import shutil
import signal
import socket
import ssl
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from typing import Any


class AcceptanceFailure(Exception):
    def __init__(self, stage: str, detail: str = "") -> None:
        super().__init__(stage)
        self.stage = stage
        self.detail = detail


def emit(message: str) -> None:
    print(message, flush=True)


def reserve_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def inside(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


class NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(
        self,
        request: urllib.request.Request,
        response: Any,
        code: int,
        message: str,
        headers: http.client.HTTPMessage,
        new_url: str,
    ) -> None:
        return None


class JsonHttpClient:
    def __init__(self, ca_file: Path, cookie_jar: http.cookiejar.CookieJar | None = None) -> None:
        self.context = ssl.create_default_context(cafile=str(ca_file))
        self.is_cookie_session = cookie_jar is not None
        handlers: list[urllib.request.BaseHandler] = [
            urllib.request.ProxyHandler({}),
            NoRedirectHandler(),
            urllib.request.HTTPSHandler(context=self.context),
        ]
        if cookie_jar is not None:
            handlers.append(urllib.request.HTTPCookieProcessor(cookie_jar))
        self.opener = urllib.request.build_opener(*handlers)

    def send(
        self,
        url: str,
        method: str = "GET",
        payload: Any | None = None,
        headers: dict[str, str] | None = None,
        timeout: float = 20,
    ) -> tuple[int, bytes, http.client.HTTPMessage]:
        request_headers = {"Accept": "application/json"}
        if self.is_cookie_session:
            request_headers["X-Requested-With"] = "XMLHttpRequest"
        if headers:
            request_headers.update(headers)
        data = None
        if payload is not None:
            data = json.dumps(payload, separators=(",", ":")).encode("utf-8")
            request_headers.setdefault("Content-Type", "application/json")
        request = urllib.request.Request(url, data=data, headers=request_headers, method=method)
        try:
            with self.opener.open(request, timeout=timeout) as response:
                return response.status, response.read(), response.headers
        except urllib.error.HTTPError as response:
            return response.code, response.read(), response.headers
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            raise AcceptanceFailure("local HTTPS request") from error


class StdioMcpProcess:
    def __init__(self, executable: Path, env: dict[str, str]) -> None:
        self.process = subprocess.Popen(
            [str(executable)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, cwd=executable.parent, env=env, bufsize=0,
        )
        if self.process.stdin is None or self.process.stdout is None:
            raise AcceptanceFailure("stdio MCP process startup")
        self.stdout_fd = self.process.stdout.fileno()
        self.buffer = bytearray()
        self.next_id = 1

    def _write(self, message: dict[str, Any]) -> None:
        if self.process.poll() is not None:
            raise AcceptanceFailure("stdio MCP process exited")
        assert self.process.stdin is not None
        self.process.stdin.write(json.dumps(message, separators=(",", ":")).encode("utf-8") + b"\n")
        self.process.stdin.flush()

    def _response(self, expected_id: int, timeout: float = 20) -> dict[str, Any]:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if b"\n" in self.buffer:
                line, _, remainder = self.buffer.partition(b"\n")
                self.buffer = bytearray(remainder)
                if not line.strip():
                    continue
                try:
                    message = json.loads(line)
                except json.JSONDecodeError as error:
                    raise AcceptanceFailure("stdio MCP protocol framing") from error
                if not isinstance(message, dict):
                    raise AcceptanceFailure("stdio MCP protocol response")
                if message.get("id") == expected_id:
                    return message
                if "id" in message:
                    raise AcceptanceFailure("stdio MCP response id")
                continue
            remaining = max(0.01, deadline - time.monotonic())
            ready, _, _ = select.select([self.stdout_fd], [], [], remaining)
            if not ready:
                break
            chunk = os.read(self.stdout_fd, 4096)
            if not chunk:
                raise AcceptanceFailure("stdio MCP process closed stdout")
            self.buffer.extend(chunk)
            if len(self.buffer) > 4 * 1024 * 1024:
                raise AcceptanceFailure("stdio MCP response exceeded the frame limit")
        raise AcceptanceFailure("stdio MCP response timeout")

    def call(self, method: str, parameters: dict[str, Any]) -> dict[str, Any]:
        call_id = self.next_id
        self.next_id += 1
        self._write({"jsonrpc": "2.0", "id": call_id, "method": method, "params": parameters})
        return self._response(call_id)

    def initialize(self) -> None:
        response = self.call(
            "initialize",
            {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "rateldesk-acceptance", "version": "1"},
            },
        )
        if "error" in response or "result" not in response:
            raise AcceptanceFailure("stdio MCP initialize")
        self._write({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def tool_call(self, tool: str, arguments: dict[str, Any]) -> dict[str, Any]:
        return self.call("tools/call", {"name": tool, "arguments": arguments})

    def close(self) -> None:
        try:
            if self.process.stdin is not None and not self.process.stdin.closed:
                self.process.stdin.close()
        except OSError:
            pass
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            try:
                self.process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=3)
        self.process.stdout.close() if self.process.stdout is not None else None


class AcceptanceRunner:
    def __init__(self, args: argparse.Namespace) -> None:
        self.root = Path(__file__).resolve().parents[2]
        self.api_publish = Path(args.api_publish_dir).resolve(strict=True)
        self.web_publish = Path(args.web_publish_dir).resolve(strict=True)
        self.mcp_publish = Path(args.mcp_http_publish_dir).resolve(strict=True)
        self.assets = Path(args.release_assets).resolve(strict=True)
        self.request_timeout = args.request_timeout
        self.setup_timeout = args.setup_timeout
        self.temp = Path(tempfile.mkdtemp(prefix="rateldesk-credential-acceptance-"))
        os.chmod(self.temp, 0o700)
        self.project = f"rdk-accept-{uuid.uuid4().hex[:18]}"
        self.compose = ["docker", "compose", "-p", self.project, "-f", str(self.root / "docker/docker-compose.e2e.yml")]
        self.pg_port = reserve_port()
        self.api_port = reserve_port()
        self.web_port = reserve_port()
        self.mcp_port = reserve_port()
        while len({self.pg_port, self.api_port, self.web_port, self.mcp_port}) != 4:
            self.pg_port, self.api_port, self.web_port, self.mcp_port = (reserve_port() for _ in range(4))
        self.api_url = f"https://localhost:{self.api_port}/"
        self.web_url = f"https://localhost:{self.web_port}/"
        self.mcp_url = f"https://localhost:{self.mcp_port}/mcp"
        self.processes: dict[str, subprocess.Popen[Any]] = {}
        self.stdio: StdioMcpProcess | None = None
        self.secret_values: list[str] = []
        self.compose_started = False
        self.compose_env = os.environ.copy()
        self.compose_env["RATELDESK_POSTGRES_PORT"] = str(self.pg_port)
        self.ca_file = self.temp / "acceptance-ca.pem"
        self.server_pfx = self.temp / "localhost.pfx"
        self.certificate_password = secrets.token_urlsafe(32)
        self.postgres_password = "rateldesk"
        self.secret_values.extend([self.certificate_password, self.postgres_password])
        self.api_state = self.temp / "bootstrap"
        self.api_data = self.temp / "data"
        self.keyring = self.temp / "keys"
        self.storage = self.temp / "storage"
        self.http_mcp_config = self.temp / "http-mcp-config.json"
        self.cli_config = self.temp / "cli-config.json"
        self.stdio_config = self.temp / "stdio-config.json"
        self.archive_root = self.temp / "archives"
        self.archive_version = ""
        self.cli_executable: Path | None = None
        self.stdio_executable: Path | None = None
        self.admin_email = f"acceptance-{uuid.uuid4().hex[:12]}@example.test"
        self.admin_password = f"Acceptance-{secrets.token_urlsafe(30)}!9aA"
        self.secret_values.append(self.admin_password)

    def run(self) -> None:
        self._check_prerequisites()
        self._verify_release_archives()
        self._create_local_certificate()
        self._start_postgres()
        self._initialize_unattended_postgres()
        self._start_api()
        self._start_web()
        self._start_http_mcp()
        self._create_synthetic_fixture_and_credentials()
        self._exercise_cli_archive()
        self._exercise_stdio_archive()
        self._exercise_http_mcp()
        self._assert_incident_unchanged()
        self._revoke_api_credential()
        self._assert_cli_revocation()
        self._assert_stdio_revocation()
        self._revoke_mcp_credential()
        self._assert_http_mcp_revocation()

    def cleanup(self) -> bool:
        cleanup_failed = False
        if self.stdio is not None:
            try:
                self.stdio.close()
            except Exception:
                cleanup_failed = True
            self.stdio = None
        for name, process in list(self.processes.items()):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        cleanup_failed = True
            try:
                process.stdout.close() if process.stdout is not None else None
            except OSError:
                cleanup_failed = True
        self.processes.clear()
        if self.compose_started:
            try:
                result = subprocess.run(
                    [*self.compose, "down", "--volumes", "--remove-orphans", "--timeout", "10"],
                    cwd=self.root,
                    env=self.compose_env,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    timeout=30,
                    check=False,
                )
                cleanup_failed = cleanup_failed or result.returncode != 0
            except (OSError, subprocess.TimeoutExpired):
                cleanup_failed = True
        try:
            shutil.rmtree(self.temp)
        except OSError:
            cleanup_failed = True
        return cleanup_failed

    def _check_prerequisites(self) -> None:
        if not sys.platform.startswith("linux") or os.uname().machine.lower() not in {"x86_64", "amd64"}:
            raise AcceptanceFailure("Linux x64 host required")
        for executable in ("python3", "dotnet", "openssl", "docker", "git"):
            if shutil.which(executable) is None:
                raise AcceptanceFailure(f"missing prerequisite: {executable}")
        self._run_capture(["docker", "compose", "version"], "Docker Compose check", timeout=10, env=self.compose_env)
        if os.environ.get("DOCKER_HOST", "").strip() and not os.environ["DOCKER_HOST"].startswith("unix://"):
            raise AcceptanceFailure("Docker engine must be a local Unix socket")
        context_host = self._run_capture(
            ["docker", "context", "inspect", "--format", "{{.Endpoints.docker.Host}}"],
            "local Docker context check",
            timeout=10,
            env=self.compose_env,
        ).stdout.strip()
        if not context_host.startswith("unix://"):
            raise AcceptanceFailure("Docker context must use a local Unix socket")
        self._run_capture(
            ["docker", "image", "inspect", "postgres:16"],
            "PostgreSQL 16 image must be available locally",
            timeout=15,
            env=self.compose_env,
        )
        self._require_publish_output(self.api_publish, "Helpdesk.API.dll")
        self._require_publish_output(self.web_publish, "HelpDesk.NewWeb.dll")
        self._require_publish_output(self.mcp_publish, "Helpdesk.Mcp.Http.dll")
        self._require_publish_output(self.api_publish, "Helpdesk.API.runtimeconfig.json")
        self._require_publish_output(self.web_publish, "HelpDesk.NewWeb.runtimeconfig.json")
        self._require_publish_output(self.mcp_publish, "Helpdesk.Mcp.Http.runtimeconfig.json")

    @staticmethod
    def _require_publish_output(directory: Path, filename: str) -> None:
        if not directory.is_dir() or not (directory / filename).is_file():
            raise AcceptanceFailure("missing Release publish output")

    def _verify_release_archives(self) -> None:
        manifest_path = self.assets / "release-manifest.json"
        if not manifest_path.is_file():
            raise AcceptanceFailure("release archive manifest missing")
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            raise AcceptanceFailure("release archive manifest invalid") from error
        source_revision = manifest.get("sourceRevision")
        if not isinstance(source_revision, str) or not re.fullmatch(r"[0-9a-fA-F]{40,64}", source_revision):
            raise AcceptanceFailure("release archive source revision invalid")
        current_revision = self._run_capture(["git", "rev-parse", "HEAD"], "source revision check", timeout=10).stdout.strip()
        ancestry = self._run_capture(
            ["git", "merge-base", "--is-ancestor", source_revision, current_revision],
            "source revision ancestry check",
            timeout=10,
            check=False,
        )
        tracked_changes = self._run_capture(
            ["git", "diff", "--name-only", source_revision, "--"],
            "source changeset check",
            timeout=10,
        ).stdout.splitlines()
        untracked_changes = self._run_capture(
            ["git", "ls-files", "--others", "--exclude-standard"],
            "untracked source check",
            timeout=10,
        ).stdout.splitlines()
        allowed_changes = {
            "tools/ci/integration_credential_acceptance.py",
            "tools/ci/run-integration-credential-acceptance.sh",
            "docs/implementation/beta4-integration-progress.md",
        }
        changed_paths = {path for path in tracked_changes + untracked_changes if path}
        if ancestry.returncode != 0 or changed_paths - allowed_changes:
            raise AcceptanceFailure("release archive source differs outside acceptance-only changes")
        version = manifest.get("version")
        if not isinstance(version, str) or not re.fullmatch(r"[0-9A-Za-z.+-]+", version):
            raise AcceptanceFailure("release archive version invalid")
        self.archive_version = version
        expected = {
            f"rateldesk-cli-{version}-linux-x64.tar.gz": "cli",
            f"rateldesk-mcp-stdio-{version}-linux-x64.tar.gz": "stdio MCP",
        }
        hashes = manifest.get("assets")
        if not isinstance(hashes, dict):
            raise AcceptanceFailure("release archive checksums missing")
        for filename, label in expected.items():
            archive = self.assets / filename
            if not archive.is_file():
                raise AcceptanceFailure(f"{label} release archive missing")
            digest = hashlib.sha256(archive.read_bytes()).hexdigest()
            if hashes.get(filename) != digest:
                raise AcceptanceFailure(f"{label} release archive checksum mismatch")
            destination = self.archive_root / label.replace(" ", "-")
            destination.mkdir(parents=True, exist_ok=True)
            self._safe_extract_tar(archive, destination)
            package_directory = destination / filename.removesuffix(".tar.gz")
            executable_name = "rateldesk" if label == "cli" else "rateldesk-mcp"
            executable = package_directory / executable_name
            if not executable.is_file() or not os.access(executable, os.X_OK):
                raise AcceptanceFailure(f"{label} apphost missing or not executable")
            if label == "cli":
                self.cli_executable = executable
            else:
                self.stdio_executable = executable
        if self.cli_executable is None or self.stdio_executable is None:
            raise AcceptanceFailure("release apphosts unavailable")
        emit("PASS: manifest-matched CLI and stdio MCP archives verified and extracted")

    @staticmethod
    def _safe_extract_tar(archive: Path, destination: Path) -> None:
        destination_root = destination.resolve()
        try:
            with tarfile.open(archive, "r:gz") as package:
                members = package.getmembers()
                if not members:
                    raise AcceptanceFailure("release archive is empty")
                for member in members:
                    relative = PurePosixPath(member.name)
                    if relative.is_absolute() or ".." in relative.parts or not (member.isdir() or member.isfile()):
                        raise AcceptanceFailure("release archive contains an unsafe entry")
                    target = (destination / Path(*relative.parts)).resolve()
                    if not inside(target, destination_root):
                        raise AcceptanceFailure("release archive contains an unsafe path")
                for member in members:
                    target = destination / Path(*PurePosixPath(member.name).parts)
                    if member.isdir():
                        target.mkdir(parents=True, exist_ok=True)
                        continue
                    target.parent.mkdir(parents=True, exist_ok=True)
                    source = package.extractfile(member)
                    if source is None:
                        raise AcceptanceFailure("release archive file could not be read")
                    with source, target.open("wb") as output:
                        shutil.copyfileobj(source, output)
                    os.chmod(target, member.mode & 0o777)
        except (OSError, tarfile.TarError) as error:
            raise AcceptanceFailure("release archive extraction failed") from error

    def _create_local_certificate(self) -> None:
        server_key = self.temp / "localhost.key"
        self._run_capture(
            [
                "openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-sha256", "-days", "2",
                "-keyout", str(server_key), "-out", str(self.ca_file), "-subj", "/CN=localhost",
                "-addext", "basicConstraints=critical,CA:TRUE",
                "-addext", "keyUsage=critical,digitalSignature,keyEncipherment,keyCertSign,cRLSign",
                "-addext", "extendedKeyUsage=serverAuth",
                "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1",
            ],
            "self-signed localhost certificate generation",
            timeout=30,
        )
        self._run_capture(
            [
                "openssl", "pkcs12", "-export", "-out", str(self.server_pfx), "-inkey", str(server_key),
                "-in", str(self.ca_file), "-passout", "fd:0",
            ],
            "Kestrel certificate packaging",
            timeout=30,
            input_text=self.certificate_password + "\n",
        )
        self._run_capture(
            ["openssl", "verify", "-CAfile", str(self.ca_file), "-verify_hostname", "localhost", str(self.ca_file)],
            "localhost certificate trust probe",
            timeout=10,
        )
        emit("PASS: isolated localhost TLS certificate prepared")

    def _start_postgres(self) -> None:
        # Compose may create the project container/volume before returning an
        # error. Cleanup is scoped to this unique project name, so enable it
        # before invoking `up` to cover partial startup failures as well.
        self.compose_started = True
        self._run_capture(
            [*self.compose, "up", "--pull", "never", "--detach", "postgres"],
            "disposable PostgreSQL startup",
            timeout=180,
            env=self.compose_env,
        )
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            result = subprocess.run(
                [*self.compose, "exec", "-T", "postgres", "pg_isready", "-U", "rateldesk", "-d", "rateldesk"],
                cwd=self.root,
                env=self.compose_env,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=8,
                check=False,
            )
            if result.returncode == 0:
                emit("PASS: isolated PostgreSQL 16 sidecar ready")
                return
            time.sleep(2)
        raise AcceptanceFailure("disposable PostgreSQL readiness timeout")

    def _initialize_unattended_postgres(self) -> None:
        env = self._base_process_environment()
        env.update(
            {
                "ASPNETCORE_ENVIRONMENT": "Production",
                "Bootstrap__StateDirectory": str(self.api_state),
                "Bootstrap__DataDirectory": str(self.api_data),
                "Bootstrap__Unattended__Provider": "PostgreSql",
                "Bootstrap__Unattended__PostgreSqlConnectionString": self._postgres_connection_string(),
                "Bootstrap__Unattended__Email": self.admin_email,
                "Bootstrap__Unattended__DisplayName": "Disposable Acceptance Administrator",
                "Bootstrap__Unattended__Password": self.admin_password,
                "Bootstrap__Unattended__OrganizationName": "Disposable Acceptance Organization",
                "Bootstrap__Unattended__ApplicationName": "RatelDesk Acceptance",
                "Bootstrap__Unattended__ApplicationUrl": self.web_url.rstrip("/"),
                "Bootstrap__Unattended__TimeZoneId": "UTC",
                "DataProtection__KeyRingPath": str(self.keyring),
                "DataProtection__ApplicationName": "RatelDesk",
            }
        )
        self._run_capture(
            ["dotnet", "Helpdesk.API.dll", "--initialize-unattended"],
            "synthetic PostgreSQL setup and administrator creation",
            timeout=self.setup_timeout,
            cwd=self.api_publish,
            env=env,
        )
        emit("PASS: synthetic PostgreSQL setup, administrator, and organization created")

    def _start_api(self) -> None:
        self._launch_app("api", self.api_publish, "Helpdesk.API.dll", self.api_port, self._api_environment())
        self._wait_for_status(self.api_url + "health/ready", {200}, 150, "normal API readiness")
        self._wait_for_status(self.api_url + "api/v1/auth/me", {401}, 30, "normal API authentication readiness")
        emit("PASS: normal PostgreSQL-backed API restarted and ready")

    def _start_web(self) -> None:
        env = self._base_process_environment()
        env.update(
            {
                "ASPNETCORE_ENVIRONMENT": "Production",
                "ASPNETCORE_URLS": f"https://localhost:{self.web_port}",
                "ASPNETCORE_Kestrel__Certificates__Default__Path": str(self.server_pfx),
                "ASPNETCORE_Kestrel__Certificates__Default__Password": self.certificate_password,
                "ApiBaseUrl": self.api_url,
                "ReverseProxy__Clusters__apiCluster__Destinations__api1__Address": self.api_url,
                "DataProtection__KeyRingPath": str(self.keyring),
                "DataProtection__ApplicationName": "RatelDesk",
                "Authentication__Mode": "Local",
                "StorageOptions__RootPath": str(self.storage),
            }
        )
        self._launch_app("web", self.web_publish, "HelpDesk.NewWeb.dll", self.web_port, env)
        self._wait_for_status(self.web_url, {200, 302, 303}, 90, "Web readiness")
        emit("PASS: published Web host serves over trusted loopback TLS")

    def _start_http_mcp(self) -> None:
        self._write_private_json(
            self.http_mcp_config,
            {"apiBaseUrl": self.api_url, "credentialMode": "gateway"},
        )
        env = self._base_process_environment()
        env.update(
            {
                "ASPNETCORE_ENVIRONMENT": "Production",
                "ASPNETCORE_URLS": f"https://localhost:{self.mcp_port}",
                "ASPNETCORE_Kestrel__Certificates__Default__Path": str(self.server_pfx),
                "ASPNETCORE_Kestrel__Certificates__Default__Password": self.certificate_password,
                "RATELDESK_MCP_CONFIG": str(self.http_mcp_config),
                "Helpdesk__Mcp__Instance": "acceptance",
                "Helpdesk__Mcp__ExpectedApiBaseUrl": self.api_url,
                "Helpdesk__Mcp__PublicResourceUri": self.mcp_url,
                "Helpdesk__Mcp__AllowedOrigins__0": f"https://localhost:{self.mcp_port}",
                "Helpdesk__Mcp__AuthenticationMode": "gateway",
            }
        )
        self._launch_app("http-mcp", self.mcp_publish, "Helpdesk.Mcp.Http.dll", self.mcp_port, env)
        self._wait_for_status(self.mcp_url.replace("/mcp", "/health/ready"), {200}, 90, "HTTP MCP readiness")
        emit("PASS: published HTTP MCP gateway serves the configured HTTPS resource")

    def _create_synthetic_fixture_and_credentials(self) -> None:
        cookies = http.cookiejar.CookieJar()
        admin = JsonHttpClient(self.ca_file, cookies)
        login = self._json_request(
            self.api_url + "api/v1/local-auth/login",
            "POST",
            {"email": self.admin_email, "password": self.admin_password, "rememberMe": False},
            expected={204},
            client=admin,
            headers={"X-Requested-With": "XMLHttpRequest"},
            stage="synthetic administrator login",
        )
        del login
        profile = self._json_request(
            self.api_url + "api/v1/auth/me",
            expected={200},
            client=admin,
            headers={"X-Requested-With": "XMLHttpRequest"},
            stage="synthetic administrator profile",
        )
        admin_organization_id = profile.get("primaryOrganizationId")
        if not isinstance(admin_organization_id, str) or not admin_organization_id:
            raise AcceptanceFailure("synthetic administrator organization")
        captcha = self._json_request(
            self.api_url + "api/v1/captcha/",
            expected={200},
            stage="synthetic incident CAPTCHA",
        )
        captcha_id = captcha.get("id")
        captcha_answer = captcha.get("challenge")
        if not isinstance(captcha_id, str) or not isinstance(captcha_answer, str) or not captcha_answer:
            raise AcceptanceFailure("synthetic incident CAPTCHA")
        requester_email = f"acceptance-{uuid.uuid4().hex[:12]}@example.test"
        submitted = self._json_request(
            self.api_url + "api/v1/public/tickets/submit",
            "POST",
            {
                "name": "Disposable Acceptance Customer",
                "email": requester_email,
                "subject": "Disposable integration credential acceptance incident",
                "message": "Synthetic protected-operation fixture.",
                "captchaId": captcha_id,
                "captchaAnswer": captcha_answer,
            },
            expected={200},
            stage="synthetic incident creation",
        )
        self.incident_id = submitted.get("id")
        if not isinstance(self.incident_id, str) or not self.incident_id:
            raise AcceptanceFailure("synthetic incident creation")
        incident = self._json_request(
            self.api_url + f"api/v1/incidents/{self.incident_id}",
            expected={200},
            client=admin,
            stage="synthetic incident verification",
        )
        organization_id = incident.get("organizationId")
        self.incident_priority = incident.get("priority")
        if not isinstance(organization_id, str) or not organization_id:
            raise AcceptanceFailure("synthetic incident organization")
        if incident.get("customerId") is None:
            raise AcceptanceFailure("synthetic incident customer")
        self.admin_client = admin
        self.organization_id = organization_id

        api_credential = self._json_request(
            self.api_url + "api/v1/integration-credentials/",
            "POST",
            {
                "name": "Disposable CLI and stdio acceptance",
                "purpose": "api",
                "organizationId": organization_id,
                "permissions": ["Incident.Read"],
                "lifetimeDays": 1,
            },
            expected={201},
            client=admin,
            stage="scoped API credential creation",
        )
        self.api_credential_id = api_credential.get("id")
        self.api_credential = api_credential.get("secret")
        if not isinstance(self.api_credential_id, str) or not isinstance(self.api_credential, str):
            raise AcceptanceFailure("scoped API credential creation")
        self.secret_values.append(self.api_credential)

        mcp_credential = self._json_request(
            self.api_url + "api/v1/integration-credentials/",
            "POST",
            {
                "name": "Disposable HTTP MCP acceptance",
                "purpose": "mcp",
                "organizationId": organization_id,
                "permissions": ["Incident.Read"],
                "lifetimeDays": 1,
                "mcpResourceUri": self.mcp_url,
            },
            expected={201},
            client=admin,
            stage="resource-bound MCP credential creation",
        )
        self.mcp_credential_id = mcp_credential.get("id")
        self.mcp_credential = mcp_credential.get("secret")
        if not isinstance(self.mcp_credential_id, str) or not isinstance(self.mcp_credential, str):
            raise AcceptanceFailure("resource-bound MCP credential creation")
        if mcp_credential.get("mcpResourceUri") != self.mcp_url:
            raise AcceptanceFailure("MCP resource URI pairing")
        self.secret_values.append(self.mcp_credential)

        self._write_private_json(
            self.cli_config,
            {
                "apiBaseUrl": self.api_url,
                "credentialMode": "integration",
                "integrationCredential": self.api_credential,
            },
        )
        self._write_private_json(
            self.stdio_config,
            {
                "apiBaseUrl": self.api_url,
                "credentialMode": "integration",
                "integrationCredential": self.api_credential,
            },
        )
        emit("PASS: synthetic incident and least-privilege API/MCP credentials created")

    def _exercise_cli_archive(self) -> None:
        assert self.cli_executable is not None
        env = self._base_process_environment()
        result = self._run_capture(
            [
                str(self.cli_executable), "--config", str(self.cli_config), "--output", "json",
                "incidents", "list", "--page-size", "10",
            ],
            "CLI protected read",
            timeout=30,
            env=env,
            check=False,
        )
        combined_output = result.stdout + result.stderr
        self._assert_no_secret(combined_output, "CLI read output")
        incident_present = self.incident_id in result.stdout
        if result.returncode != 0:
            status = re.search(r"\bHTTP\s+([1-5][0-9]{2})\b", combined_output)
            lower_output = combined_output.lower()
            tls_failure = any(
                marker in lower_output
                for marker in (
                    "ssl connection could not be established",
                    "certificate verify failed",
                    "remote certificate is invalid",
                    "certificate has expired",
                )
            )
            diagnostic = (
                f"CLI protected read: exit={result.returncode} "
                f"incident_present={str(incident_present).lower()}"
            )
            if status:
                diagnostic += f" http_status={status.group(1)}"
            elif tls_failure:
                diagnostic += " tls_failure=true"
            raise AcceptanceFailure(diagnostic)
        if not incident_present:
            raise AcceptanceFailure("CLI protected read: exit=0 incident_present=false")
        denied = self._run_capture(
            [
                str(self.cli_executable), "--config", str(self.cli_config), "incidents", "update", self.incident_id,
                "--body", '{"priority":1}',
            ],
            "CLI protected write denial",
            timeout=30,
            env=env,
            check=False,
        )
        self._assert_cli_denial(denied, 403, "CLI protected write denial")
        self.cli_environment = env
        emit("PASS: extracted CLI read succeeds and scoped write is forbidden")

    def _exercise_stdio_archive(self) -> None:
        assert self.stdio_executable is not None
        env = self._base_process_environment()
        env.update(
            {
                "RATELDESK_MCP_CONFIG": str(self.stdio_config),
                "RATELDESK_MCP_INSTANCE": "acceptance",
                "RATELDESK_MCP_ACCEPTANCE_API_BASE_URL": self.api_url,
            }
        )
        self.stdio = StdioMcpProcess(self.stdio_executable, env)
        self.stdio.initialize()
        read = self.stdio.tool_call("helpdesk_incidents", {"operation": "list", "request": {"pageSize": 10}})
        read_content = self._stdio_structured(read, "stdio protected read")
        self._assert_no_secret(json.dumps(read_content), "stdio read output")
        if read_content.get("status") != "completed" or self.incident_id not in json.dumps(read_content):
            raise AcceptanceFailure("stdio protected read")
        denied = self.stdio.tool_call(
            "helpdesk_incidents",
            {"operation": "update", "request": {"incidentId": self.incident_id, "priority": 1}, "confirm": True},
        )
        self._assert_tool_denial(self._stdio_structured(denied, "stdio protected write denial"), 403, "stdio protected write denial")
        emit("PASS: extracted stdio MCP read succeeds and scoped write is forbidden")

    def _exercise_http_mcp(self) -> None:
        read = self._http_tool_call(
            self.mcp_credential,
            {"operation": "list", "request": {"pageSize": 10}},
            expected_status=200,
            stage="HTTP MCP protected read",
        )
        if read.get("status") != "completed" or self.incident_id not in json.dumps(read):
            raise AcceptanceFailure("HTTP MCP protected read")
        denied = self._http_tool_call(
            self.mcp_credential,
            {"operation": "update", "request": {"incidentId": self.incident_id, "priority": 1}, "confirm": True},
            expected_status=200,
            stage="HTTP MCP protected write denial",
        )
        self._assert_tool_denial(denied, 403, "HTTP MCP protected write denial")
        emit("PASS: HTTP MCP protected read succeeds and scoped write is forbidden")

    def _assert_incident_unchanged(self) -> None:
        current = self._json_request(
            self.api_url + f"api/v1/incidents/{self.incident_id}",
            expected={200},
            client=self.admin_client,
            stage="denied write persistence check",
        )
        if current.get("priority") != self.incident_priority:
            raise AcceptanceFailure("denied writes changed the synthetic incident")

    def _revoke_api_credential(self) -> None:
        self._revoke(self.api_credential_id, "API credential revocation")

    def _revoke_mcp_credential(self) -> None:
        self._revoke(self.mcp_credential_id, "HTTP MCP credential revocation")

    def _revoke(self, credential_id: str, stage: str) -> None:
        status, _, _ = self.admin_client.send(
            self.api_url + f"api/v1/integration-credentials/{credential_id}",
            method="DELETE",
            timeout=self.request_timeout,
        )
        if status != 204:
            raise AcceptanceFailure(stage)
        credentials = self._json_request(
            self.api_url + "api/v1/integration-credentials/",
            expected={200},
            client=self.admin_client,
            stage=f"{stage} verification",
        )
        if not isinstance(credentials, list):
            raise AcceptanceFailure(f"{stage} verification")
        revoked = next((item for item in credentials if item.get("id", "").replace("-", "").lower() == credential_id.replace("-", "").lower()), None)
        if revoked is None or revoked.get("revokedAtUtc") is None:
            raise AcceptanceFailure(f"{stage} verification")
        emit(f"PASS: {stage.lower()} persisted")

    def _assert_cli_revocation(self) -> None:
        assert self.cli_executable is not None
        result = self._run_capture(
            [
                str(self.cli_executable), "--config", str(self.cli_config), "incidents", "list", "--page-size", "10",
            ],
            "CLI post-revocation denial",
            timeout=30,
            env=self.cli_environment,
            check=False,
        )
        self._assert_cli_denial(result, 401, "CLI post-revocation denial")
        emit("PASS: revoked CLI credential is rejected by the API")

    def _assert_stdio_revocation(self) -> None:
        assert self.stdio is not None
        response = self.stdio.tool_call("helpdesk_incidents", {"operation": "list", "request": {"pageSize": 10}})
        content = self._stdio_structured(response, "stdio post-revocation denial")
        self._assert_no_secret(json.dumps(content), "stdio revocation response")
        self._assert_tool_denial(content, 401, "stdio post-revocation denial")
        emit("PASS: revoked stdio MCP credential is rejected by the API")

    def _assert_http_mcp_revocation(self) -> None:
        status, body, headers = self._send_http_mcp(
            self.mcp_credential,
            {"operation": "list", "request": {"pageSize": 10}},
            stage="HTTP MCP post-revocation denial",
        )
        if status != 401:
            raise AcceptanceFailure("HTTP MCP post-revocation denial")
        self._assert_no_secret(body.decode("utf-8", errors="replace") + headers.get("WWW-Authenticate", ""), "HTTP MCP denial response")
        emit("PASS: revoked HTTP MCP credential is rejected by the gateway")

    def _api_environment(self) -> dict[str, str]:
        env = self._base_process_environment()
        env.update(
            {
                "ASPNETCORE_ENVIRONMENT": "Production",
                "ASPNETCORE_URLS": f"https://localhost:{self.api_port}",
                "ASPNETCORE_Kestrel__Certificates__Default__Path": str(self.server_pfx),
                "ASPNETCORE_Kestrel__Certificates__Default__Password": self.certificate_password,
                "Authentication__Mode": "Local",
                "DataProtection__KeyRingPath": str(self.keyring),
                "DataProtection__ApplicationName": "RatelDesk",
                "Bootstrap__StateDirectory": str(self.api_state),
                "Bootstrap__DataDirectory": str(self.api_data),
                "ConnectionStrings__HelpdeskDb": self._postgres_connection_string(),
                "Database__Provider": "PostgreSql",
                "StorageOptions__RootPath": str(self.storage),
                "EmailIngestion__Enabled": "false",
            }
        )
        return env

    def _postgres_connection_string(self) -> str:
        return (
            "Host=127.0.0.1;"
            f"Port={self.pg_port};"
            "Database=rateldesk;Username=rateldesk;"
            f"Password={self.postgres_password}"
        )

    def _base_process_environment(self) -> dict[str, str]:
        env = {name: os.environ[name] for name in ("PATH", "DOTNET_ROOT", "LANG", "LC_ALL") if os.environ.get(name)}
        env.update(
            {
                "TMPDIR": str(self.temp),
                "DOTNET_CLI_HOME": str(self.temp),
                "DOTNET_NOLOGO": "1",
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                "SSL_CERT_FILE": str(self.ca_file),
            }
        )
        return env

    def _launch_app(self, name: str, directory: Path, assembly: str, port: int, env: dict[str, str]) -> None:
        if name in self.processes and self.processes[name].poll() is None:
            raise AcceptanceFailure(f"{name} process already running")
        command = ["dotnet", assembly]
        try:
            process = subprocess.Popen(
                command,
                cwd=directory,
                env=env,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                close_fds=True,
            )
        except OSError as error:
            raise AcceptanceFailure(f"{name} process startup") from error
        self.processes[name] = process

    def _wait_for_status(self, url: str, accepted: set[int], timeout: float, stage: str) -> None:
        deadline = time.monotonic() + timeout
        client = JsonHttpClient(self.ca_file)
        while time.monotonic() < deadline:
            process = self._process_for_url(url)
            if process is not None and process.poll() is not None:
                raise AcceptanceFailure(f"{stage}: host process exited")
            try:
                status, _, _ = client.send(url, timeout=5)
                if status in accepted:
                    return
            except AcceptanceFailure:
                pass
            time.sleep(1)
        raise AcceptanceFailure(f"{stage} timeout")

    def _process_for_url(self, url: str) -> subprocess.Popen[Any] | None:
        if f"{self.api_port}/" in url:
            return self.processes.get("api")
        if f"{self.web_port}/" in url:
            return self.processes.get("web")
        if f"{self.mcp_port}/" in url:
            return self.processes.get("http-mcp")
        return None

    def _json_request(
        self,
        url: str,
        method: str = "GET",
        payload: Any | None = None,
        expected: set[int] | None = None,
        client: JsonHttpClient | None = None,
        headers: dict[str, str] | None = None,
        timeout: float | None = None,
        stage: str = "API request",
    ) -> Any:
        response_client = client or JsonHttpClient(self.ca_file)
        status, body, _ = response_client.send(url, method, payload, headers, timeout or self.request_timeout)
        if expected is not None and status not in expected:
            raise AcceptanceFailure(stage, f"HTTP {status}")
        if not body:
            if status in {204, 205}:
                return None
            return {}
        try:
            return json.loads(body)
        except (json.JSONDecodeError, UnicodeDecodeError) as error:
            raise AcceptanceFailure(f"{stage} response format", f"HTTP {status}") from error

    def _send_http_mcp(
        self,
        credential: str,
        arguments: dict[str, Any],
        stage: str,
    ) -> tuple[int, bytes, http.client.HTTPMessage]:
        body = {
            "jsonrpc": "2.0",
            "id": 7,
            "method": "tools/call",
            "params": {"name": "helpdesk_incidents", "arguments": arguments},
        }
        status, response_body, headers = JsonHttpClient(self.ca_file).send(
            self.mcp_url,
            "POST",
            body,
            {
                "Authorization": f"Bearer {credential}",
                "Accept": "application/json, text/event-stream",
            },
            timeout=self.request_timeout,
        )
        self._assert_no_secret(response_body.decode("utf-8", errors="replace"), stage)
        return status, response_body, headers

    def _http_tool_call(self, credential: str, arguments: dict[str, Any], expected_status: int, stage: str) -> dict[str, Any]:
        status, body, _ = self._send_http_mcp(credential, arguments, stage)
        if status != expected_status:
            raise AcceptanceFailure(stage, f"HTTP {status}")
        rpc = self._parse_mcp_response(body, stage)
        structured = self._structured_content(rpc, stage)
        return structured

    @staticmethod
    def _parse_mcp_response(body: bytes, stage: str) -> dict[str, Any]:
        text = body.decode("utf-8", errors="strict")
        if text.lstrip().startswith("data:") or "\ndata:" in text:
            data_lines = [line[5:].strip() for line in text.splitlines() if line.startswith("data:")]
            if not data_lines:
                raise AcceptanceFailure(f"{stage} MCP event stream")
            text = data_lines[-1]
        try:
            response = json.loads(text)
        except json.JSONDecodeError as error:
            raise AcceptanceFailure(f"{stage} MCP response format") from error
        if not isinstance(response, dict) or response.get("id") != 7 or "error" in response:
            raise AcceptanceFailure(f"{stage} JSON-RPC response")
        return response

    @classmethod
    def _structured_content(cls, response: dict[str, Any], stage: str) -> dict[str, Any]:
        result = response.get("result")
        if not isinstance(result, dict):
            raise AcceptanceFailure(f"{stage} result missing")
        structured = result.get("structuredContent")
        if isinstance(structured, dict):
            return structured
        content = result.get("content")
        if isinstance(content, list):
            for item in content:
                if isinstance(item, dict) and isinstance(item.get("text"), str):
                    try:
                        value = json.loads(item["text"])
                    except json.JSONDecodeError:
                        continue
                    if isinstance(value, dict):
                        return value
        raise AcceptanceFailure(f"{stage} structured content missing")

    @classmethod
    def _stdio_structured(cls, response: dict[str, Any], stage: str) -> dict[str, Any]:
        if "error" in response:
            raise AcceptanceFailure(f"{stage} JSON-RPC error")
        return cls._structured_content(response, stage)

    @staticmethod
    def _assert_tool_denial(content: dict[str, Any], status: int, stage: str) -> None:
        failure = content.get("failure")
        if content.get("success") is not False:
            raise AcceptanceFailure(stage)
        expected_status = "forbidden" if status == 403 else "unauthorized"
        if content.get("status") != expected_status:
            raise AcceptanceFailure(stage)
        if not isinstance(failure, dict) or failure.get("upstreamStatus") != status:
            raise AcceptanceFailure(stage)

    def _assert_cli_denial(self, result: subprocess.CompletedProcess[str], status: int, stage: str) -> None:
        combined = result.stdout + result.stderr
        self._assert_no_secret(combined, stage)
        if result.returncode != 2 or not re.search(rf"HTTP {status}(?:\D|$)", combined):
            raise AcceptanceFailure(stage)

    def _assert_no_secret(self, value: str, stage: str) -> None:
        if any(secret and secret in value for secret in self.secret_values):
            raise AcceptanceFailure(f"{stage}: credential material leaked")

    @staticmethod
    def _write_private_json(path: Path, payload: dict[str, Any]) -> None:
        path.write_text(json.dumps(payload, separators=(",", ":")) + "\n", encoding="utf-8")
        os.chmod(path, 0o600)

    def _run_capture(
        self,
        command: list[str],
        stage: str,
        timeout: float,
        cwd: Path | None = None,
        env: dict[str, str] | None = None,
        input_text: str | None = None,
        check: bool = True,
    ) -> subprocess.CompletedProcess[str]:
        try:
            result = subprocess.run(
                command,
                cwd=cwd or self.root,
                env=env if env is not None else self._base_process_environment(),
                input=input_text,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=timeout,
                check=False,
            )
        except (OSError, subprocess.TimeoutExpired) as error:
            raise AcceptanceFailure(stage) from error
        if check and result.returncode != 0:
            raise AcceptanceFailure(stage, f"exit {result.returncode}")
        return result


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Exercise scoped API and HTTP MCP credentials using isolated local Release publishes."
    )
    parser.add_argument("--api-publish-dir", required=True, help="Release publish directory containing Helpdesk.API.dll")
    parser.add_argument("--web-publish-dir", required=True, help="Release publish directory containing HelpDesk.NewWeb.dll")
    parser.add_argument("--mcp-http-publish-dir", required=True, help="Release publish directory containing Helpdesk.Mcp.Http.dll")
    parser.add_argument("--release-assets", required=True, help="Directory with the current-HEAD release manifest and Linux x64 CLI/stdio archives")
    parser.add_argument("--request-timeout", type=float, default=20, help=argparse.SUPPRESS)
    parser.add_argument("--setup-timeout", type=float, default=240, help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    if args.request_timeout < 1 or args.request_timeout > 120 or args.setup_timeout < 30 or args.setup_timeout > 600:
        parser.error("timeouts are outside the supported bounds")
    return args


def main(argv: list[str]) -> int:
    os.umask(0o077)
    def terminate_as_interrupt(_signum: int, _frame: Any) -> None:
        raise KeyboardInterrupt

    signal.signal(signal.SIGTERM, terminate_as_interrupt)
    runner: AcceptanceRunner | None = None
    succeeded = False
    try:
        runner = AcceptanceRunner(parse_args(argv))
        runner.run()
        succeeded = True
    except AcceptanceFailure as error:
        detail = f" ({error.detail})" if error.detail and re.fullmatch(r"(?:HTTP|exit) \d+", error.detail) else ""
        emit(f"FAIL: {error.stage}{detail}")
    except KeyboardInterrupt:
        emit("FAIL: interrupted")
    except Exception:
        emit("FAIL: unexpected acceptance-runner error")
    cleanup_failed = False
    if runner is not None:
        cleanup_failed = runner.cleanup()
    if cleanup_failed:
        emit("FAIL: disposable-resource cleanup did not complete")
        return 1
    if succeeded:
        emit("PASS: protected reads, scoped write denials, and revocation verified through CLI, stdio MCP, and HTTP MCP")
        return 0
    return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
