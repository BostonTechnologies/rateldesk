#!/usr/bin/env python3
"""Actual disposable Portainer owner API for source save/deploy/read-back tests.

Only use with a unique synthetic project on the managed local Docker socket.
The manager's real stored YAML becomes the fixture source before host review;
this helper never labels a saved-source copy or direct Compose call as manager
acceptance. Passwords/tokens and response bodies are never printed.
"""
import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import urllib.parse
import uuid

PORTAINER_IMAGE = "portainer/portainer-ce@sha256:4786931dc7c588ff1c242696fe1eb3f7f9c5dafb136b6c713aff7745dd5bd407"
MAX_BYTES = 2 * 1024 * 1024


def checked(arguments, timeout=120):
    completed = subprocess.run(arguments, capture_output=True, timeout=timeout)
    if completed.returncode:
        raise RuntimeError("Disposable external-manager Docker operation failed; private process output withheld")
    return completed.stdout


def file_sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def environment_entries(path):
    entries = []
    for line in Path(path).read_text().splitlines():
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        key, separator, value = line.partition("=")
        if not separator or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key):
            raise RuntimeError("Manager fixture requires explicit simple synthetic interpolation entries")
        entries.append({"name": key, "value": value})
    return entries


class PortainerOwner:
    def __init__(self, directory, mounts=()):
        self.directory = Path(directory).resolve()
        self.data = self.directory / "manager-data"
        self.name = "rdk-manager-" + uuid.uuid4().hex[:12]
        self.token = None
        self.endpoint = None
        self.stack_ids = []
        self.stack_environment = {}
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        self.data.mkdir(mode=0o700, parents=True)
        (self.data / "home").mkdir(mode=0o700)
        self.mounts = list(mounts)
        self.base_url = None
        self.started = False
        self.last_error = None

    def request(self, path, body=None, method=None, multipart=False):
        headers = {"Accept": "application/json"}
        if self.token:
            headers["Authorization"] = "Bearer " + self.token
        if multipart:
            boundary = "rdk-manager-" + secrets.token_hex(12)
            data = b""
            for key, value in body.items():
                data += ("--" + boundary + '\r\nContent-Disposition: form-data; name="' + key + '"\r\n\r\n' + str(value) + "\r\n").encode()
            data += ("--" + boundary + "--\r\n").encode()
            headers["Content-Type"] = "multipart/form-data; boundary=" + boundary
        elif body is not None:
            data = json.dumps(body).encode()
            headers["Content-Type"] = "application/json"
        else:
            data = None
        request = urllib.request.Request(self.base_url + path, data=data, method=method, headers=headers)
        try:
            with self.opener.open(request, timeout=120) as response:
                content = response.read(MAX_BYTES + 1)
        except urllib.error.HTTPError as error:
            self.last_error = error.read(MAX_BYTES).decode(errors="replace")
            raise RuntimeError("Actual Portainer API operation failed with HTTP " + str(error.code) + "; response kept private") from None
        if len(content) > MAX_BYTES:
            raise RuntimeError("Actual manager response exceeded the fixture bound")
        return json.loads(content) if content else None

    def start(self):
        # Refuse redirected Docker selection; this fixture's manager socket is
        # explicitly the managed local socket and must control that same daemon.
        selected = subprocess.run(["docker", "info", "--format", "{{json .ID}}"], capture_output=True, timeout=30)
        local = subprocess.run(["docker", "--host=unix:///var/run/docker.sock", "info", "--format", "{{json .ID}}"],
                               env={key: value for key, value in os.environ.items() if key not in ("DOCKER_CONTEXT", "DOCKER_HOST", "DOCKER_TLS", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH")}, capture_output=True, timeout=30)
        if selected.returncode or local.returncode or selected.stdout != local.stdout:
            raise RuntimeError("Manager fixture requires the selected managed local daemon; it never changes Docker context")
        checked(["docker", "image", "inspect", PORTAINER_IMAGE])
        command = ["docker", "run", "--detach", "--name", self.name, "--user", str(os.getuid()) + ":" + str(os.getgid()),
                   "--group-add", str(Path("/var/run/docker.sock").stat().st_gid), "--env", "HOME=" + str(self.data / "home"), "--label", "com.rateldesk.acceptance=external-manager",
                   "--publish", "127.0.0.1::9000", "--mount", "type=bind,src=/var/run/docker.sock,dst=/var/run/docker.sock",
                   "--mount", "type=bind,src=" + str(self.data) + ",dst=" + str(self.data)]
        for mount in self.mounts:
            resolved = Path(mount).resolve()
            command += ["--mount", "type=bind,src=" + str(resolved) + ",dst=" + str(resolved) + ",readonly"]
        command += [PORTAINER_IMAGE, "--data", str(self.data), "--http-enabled", "--no-analytics", "--snapshot-interval", "1h"]
        checked(command)
        self.started = True
        ports = json.loads(checked(["docker", "inspect", "--format", "{{json .NetworkSettings.Ports}}", self.name]))
        self.base_url = "http://127.0.0.1:" + ports["9000/tcp"][0]["HostPort"]
        deadline = time.monotonic() + 45
        while True:
            try:
                self.request("/api/status")
                break
            except (OSError, urllib.error.URLError):
                if time.monotonic() > deadline:
                    raise RuntimeError("Actual Portainer did not become ready within the bounded startup") from None
                time.sleep(0.25)
        password = secrets.token_urlsafe(36)
        self.request("/api/users/admin/init", {"Username": "fixture-administrator", "Password": password})
        authenticated = self.request("/api/auth", {"Username": "fixture-administrator", "Password": password})
        self.token = authenticated["jwt"]
        endpoint = self.request("/api/endpoints", {"Name": "managed-local-disposable", "EndpointCreationType": "1", "URL": "unix:///var/run/docker.sock"}, multipart=True)
        self.endpoint = endpoint["Id"]
        return self

    def create(self, project, source, env_file):
        if not re.fullmatch(r"rdk-[a-z0-9_-]+", project):
            raise RuntimeError("External manager acceptance requires an explicit unique rdk- synthetic project")
        before = Path(source).read_bytes()
        environment = environment_entries(env_file)
        stack = self.request("/api/stacks/create/standalone/string?endpointId=" + str(self.endpoint),
                             {"Name": project, "StackFileContent": before.decode(), "Env": environment, "FromAppTemplate": False})
        identifier = stack["Id"]
        self.stack_ids.append(identifier)
        self.stack_environment[identifier] = environment
        managed_source = Path(stack["ProjectPath"]) / stack["EntryPoint"]
        readback = self.request("/api/stacks/" + str(identifier) + "/file")["StackFileContent"].encode()
        if readback != before or managed_source.read_bytes() != before or Path(source).read_bytes() != before:
            raise RuntimeError("Actual manager altered the original YAML; source review must restart from its actual stored bytes")
        return {"id": identifier, "source": managed_source, "project": project}

    def reviewed_update(self, stack, candidate, expected_original):
        identifier = stack["id"]
        source = Path(stack["source"])
        before = source.read_bytes()
        current = self.request("/api/stacks/" + str(identifier) + "/file")["StackFileContent"].encode()
        if before != current or hashlib.sha256(before).hexdigest() != expected_original:
            raise RuntimeError("Actual manager source changed after review; candidate was not saved/deployed")
        content = Path(candidate).read_bytes()
        if len(content) > MAX_BYTES:
            raise RuntimeError("Actual manager candidate exceeded the fixture bound")
        payload = {"StackFileContent": content.decode(), "Env": self.stack_environment[identifier], "Prune": False, "PullImage": False}
        return identifier, source, before, content, payload

    def interrupt_apply(self, stack, candidate, expected_original):
        """End an actual authenticated update body early, before valid JSON.

        A 400 alone is insufficient: both the manager API and its actual stored
        file must still contain the reviewed baseline before this negative
        probe can be reported as safely interrupted.
        """
        identifier, source, before, _, payload = self.reviewed_update(stack, candidate, expected_original)
        parsed = urllib.parse.urlsplit(self.base_url)
        if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or parsed.username or parsed.password or parsed.path or parsed.query or parsed.fragment or not parsed.port:
            raise RuntimeError("Interrupted manager probe requires its disposable loopback HTTP endpoint")
        data = json.dumps(payload).encode()
        if not 1 < len(data) <= MAX_BYTES:
            raise RuntimeError("Interrupted manager update exceeded the fixture request bound")
        connection = http.client.HTTPConnection(parsed.hostname, parsed.port, timeout=10)
        try:
            connection.putrequest("PUT", "/api/stacks/" + str(identifier) + "?endpointId=" + str(self.endpoint))
            connection.putheader("Authorization", "Bearer " + self.token)
            connection.putheader("Content-Type", "application/json")
            connection.putheader("Content-Length", str(len(data)))
            connection.putheader("Connection", "close")
            connection.endheaders()
            connection.send(data[:1])
            connection.sock.shutdown(socket.SHUT_WR)
            response = connection.getresponse()
            status = response.status
            if len(response.read(16385)) > 16384:
                raise RuntimeError("Interrupted manager response exceeded the fixture bound")
        except (OSError, http.client.HTTPException):
            raise RuntimeError("Interrupted manager update did not produce a bounded confirmed response; no completion was recorded") from None
        finally:
            connection.close()
        if status != 400:
            raise RuntimeError("Interrupted manager update did not return the required HTTP 400; no completion was recorded")
        readback = self.request("/api/stacks/" + str(identifier) + "/file")["StackFileContent"].encode()
        if readback != before or source.read_bytes() != before:
            raise RuntimeError("Interrupted manager update changed the reviewed baseline; fresh source review is required")
        return {"interrupted": True, "status": 400, "readBackExact": True, "baselinePreserved": True}

    def apply(self, stack, candidate, expected_original):
        identifier, source, _, content, payload = self.reviewed_update(stack, candidate, expected_original)
        self.request("/api/stacks/" + str(identifier) + "?endpointId=" + str(self.endpoint), payload, method="PUT")
        readback = self.request("/api/stacks/" + str(identifier) + "/file")["StackFileContent"].encode()
        if readback != content or source.read_bytes() != content:
            raise RuntimeError("Actual manager read-back differs from the approved candidate; fresh source review is required")
        return {"manager": "Portainer CE 2.33.6", "sourceSha256": hashlib.sha256(content).hexdigest(), "readBackExact": True}

    def close(self):
        failures = []
        if self.token and self.endpoint:
            for identifier in reversed(self.stack_ids):
                try:
                    self.request("/api/stacks/" + str(identifier) + "?endpointId=" + str(self.endpoint), method="DELETE")
                except Exception:
                    failures.append("stack")
        if self.started:
            try:
                checked(["docker", "rm", "--force", self.name])
            except Exception:
                failures.append("manager")
        if failures:
            raise RuntimeError("Disposable external-manager cleanup requires attention: " + ", ".join(failures))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="rdk-manager-smoke-", dir=args.workspace) as temporary:
        directory = Path(temporary)
        source = directory / "owner.yml"
        environment = directory / "owner.env"
        environment.write_text("SYNTHETIC_TIMEOUT=20\n")
        environment.chmod(0o600)
        source.write_text('# Actual disposable manager-owned source\nservices:\n' + ''.join(
            '  ' + name + ':\n    image: postgres:16\n    pull_policy: never\n    command: ["sleep", "infinity"]\n    environment:\n      SYNTHETIC_TIMEOUT: "${SYNTHETIC_TIMEOUT}" # preserve\n'
            for name in ("api", "web", "mcp-http", "db")))
        manager = PortainerOwner(directory, mounts=[directory])
        project = "rdk-manager-smoke-" + uuid.uuid4().hex[:10]
        try:
            manager.start()
            stack = manager.create(project, source, environment)
            original = stack["source"].read_bytes()
            assert original == source.read_bytes()
            before = [json.loads(line) for line in checked(["docker", "ps", "--filter", "label=com.docker.compose.project=" + project, "--format", "json"]).decode().splitlines()]
            assert len(before) == 4
            candidate = directory / "candidate.yml"
            candidate.write_bytes(original.replace(b'SYNTHETIC_TIMEOUT: "${SYNTHETIC_TIMEOUT}"', b'SYNTHETIC_TIMEOUT: "21"', 1))
            result = manager.apply(stack, candidate, hashlib.sha256(original).hexdigest())
            containers = checked(["docker", "ps", "--filter", "label=com.docker.compose.project=" + project, "--format", "{{.ID}}"]).decode().splitlines()
            assert len(containers) == 4
            environments = [json.loads(checked(["docker", "inspect", "--format", "{{json .Config.Env}}", item])) for item in containers]
            assert sum("SYNTHETIC_TIMEOUT=21" in item for item in environments) == 1
            assert sum("SYNTHETIC_TIMEOUT=20" in item for item in environments) == 3
            assert result["readBackExact"]
            assert source.read_bytes() == original
            print("PASS: actual Portainer CE manager created four services, saved/deployed one exact YAML scalar candidate and read it back; seven assertions; synthetic shell workloads only", flush=True)
        finally:
            manager.close()


if __name__ == "__main__":
    main()
