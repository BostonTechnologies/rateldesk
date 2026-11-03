#!/usr/bin/env python3
"""Backend-free, bounded discovery of the exact archived stdio MCP apphost."""

from __future__ import annotations

from contextlib import contextmanager
import http.server
import json
import os
from pathlib import Path
import queue
import signal
import subprocess
import threading
import time


class DiscoveryFailure(RuntimeError):
    pass


LISTS = (
    ("tools/list", "tools", "name"),
    ("resources/list", "resources", "uri"),
    ("resources/templates/list", "resourceTemplates", "uriTemplate"),
    ("prompts/list", "prompts", "name"),
)
MAX_FRAME_BYTES = 16 * 1024 * 1024
MAX_STDOUT_BYTES = 96 * 1024 * 1024
MAX_STDERR_BYTES = 8 * 1024 * 1024
MAX_PAGES = 256


def collect_pages(call, method: str, key: str, identity: str, max_pages: int = MAX_PAGES):
    """Follow opaque cursors, including the empty string; keep every item field."""
    items, identities, cursors = [], set(), set()
    parameters = {}
    for page in range(1, max_pages + 1):
        result = call(method, parameters)
        values = result.get(key)
        if not isinstance(values, list):
            raise DiscoveryFailure(f"{method} did not return an array named {key}")
        for item in values:
            if not isinstance(item, dict) or not isinstance(item.get(identity), str) or not item[identity]:
                raise DiscoveryFailure(f"{method} returned an item without {identity}")
            if item[identity] in identities:
                raise DiscoveryFailure(f"{method} repeated an item identity")
            identities.add(item[identity])
            items.append(item)
        cursor = result.get("nextCursor")
        if cursor is None:
            return sorted(items, key=lambda item: item[identity]), page
        if not isinstance(cursor, str):
            raise DiscoveryFailure(f"{method} returned a non-string cursor")
        if cursor in cursors:
            raise DiscoveryFailure(f"{method} repeated its cursor")
        cursors.add(cursor)
        parameters = {"cursor": cursor}
    raise DiscoveryFailure(f"{method} exceeded {max_pages} pages")


def compare_snapshots(control: dict, candidate: dict) -> dict:
    # Object ordering and outer discovery collection ordering have no semantic
    # significance. Nested arrays and every description/schema/annotation remain.
    invalid = []
    for side, snapshot in (("control", control), ("candidate", candidate)):
        if not isinstance(snapshot.get("initialize"), dict) or any(not isinstance(snapshot.get(key), list) for _, key, _ in LISTS):
            invalid.append(side)
        elif not snapshot["tools"] or not snapshot["prompts"] or not (snapshot["resources"] or snapshot["resourceTemplates"]):
            invalid.append(side)
    if invalid:
        return {"status": "FAIL", "invalidSnapshots": invalid, "differentSections": []}
    different = [key for key in ("initialize", *(item[1] for item in LISTS))
                 if control.get(key) != candidate.get(key)]
    return {"status": "FAIL" if different else "PASS", "differentSections": different,
            "counts": {key: len(candidate.get(key, [])) for _, key, _ in LISTS}}


@contextmanager
def backend_tripwire():
    """A disposable local API target: any unexpected request fails the batch."""
    requests = []

    class Handler(http.server.BaseHTTPRequestHandler):
        def parse_request(self):
            valid = super().parse_request()
            if valid:
                requests.append({"method": self.command, "path": self.path})
            return valid

        def do_GET(self):
            self.send_response(503)
            self.send_header("Content-Length", "0")
            self.end_headers()

        do_POST = do_PUT = do_PATCH = do_DELETE = do_OPTIONS = do_HEAD = do_GET

        def log_message(self, format, *args):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield {"origin": f"http://127.0.0.1:{server.server_port}", "requests": requests}
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


class Session:
    def __init__(self, command: list[str], directory: Path, environment: dict, timeout: float = 30):
        self.timeout = timeout
        self.deadline = time.monotonic() + 180
        self.identifier = 0
        self.writer = None
        self.messages = queue.Queue(maxsize=512)
        self.reader_error = None
        self.stdout_bytes = 0
        self.stderr_bytes = 0
        self.stdout_log = (directory / "stdout.jsonl").open("wb")
        self.stderr_log = (directory / "stderr.log").open("wb")
        try:
            self.process = subprocess.Popen(command, cwd=directory, stdin=subprocess.PIPE,
                                            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                            env=environment, start_new_session=os.name != "nt")
        except BaseException:
            self.stdout_log.close()
            self.stderr_log.close()
            raise
        self.reader = threading.Thread(target=self._read_stdout, daemon=True)
        self.reader.start()
        self.stderr_reader = threading.Thread(target=self._read_stderr, daemon=True)
        self.stderr_reader.start()

    def _emit(self, value):
        try:
            self.messages.put(value, timeout=1)
        except queue.Full as error:
            raise DiscoveryFailure("MCP emitted too many queued protocol messages") from error

    def _failure(self, error):
        self.reader_error = error
        try:
            self.messages.put_nowait(error)
        except queue.Full:
            # The caller checks reader_error before consuming another message.
            return

    def _read_stderr(self):
        try:
            while True:
                chunk = self.process.stderr.read(65536)
                if not chunk:
                    return
                self.stderr_bytes += len(chunk)
                if self.stderr_bytes > MAX_STDERR_BYTES:
                    raise DiscoveryFailure("MCP stderr exceeded its byte bound")
                self.stderr_log.write(chunk)
                self.stderr_log.flush()
        except BaseException as error:
            self._failure(error)

    def _read_stdout(self):
        try:
            while True:
                line = self.process.stdout.readline(MAX_FRAME_BYTES + 1)
                if not line:
                    self._emit(None)
                    return
                self.stdout_bytes += len(line)
                if len(line) > MAX_FRAME_BYTES or self.stdout_bytes > MAX_STDOUT_BYTES:
                    raise DiscoveryFailure("MCP stdout exceeded its byte bound")
                self.stdout_log.write(line)
                self.stdout_log.flush()
                try:
                    message = json.loads(line.decode("utf-8"))
                except (UnicodeError, ValueError) as error:
                    raise DiscoveryFailure("MCP stdout contains non-JSON protocol traffic") from error
                if not isinstance(message, dict) or message.get("jsonrpc") != "2.0":
                    raise DiscoveryFailure("MCP stdout contains an invalid JSON-RPC envelope")
                if "method" in message:
                    if not isinstance(message["method"], str) or not message["method"] or "result" in message or "error" in message:
                        raise DiscoveryFailure("MCP stdout contains an invalid request/notification")
                elif "id" not in message or (("result" in message) == ("error" in message)):
                    raise DiscoveryFailure("MCP stdout contains an invalid response")
                self._emit(message)
        except BaseException as error:
            self._failure(error)

    def send(self, message: dict):
        data = (json.dumps(message, separators=(",", ":")) + "\n").encode("utf-8")
        if len(data) > MAX_FRAME_BYTES:
            raise DiscoveryFailure("MCP request exceeded its byte bound")
        done, errors = threading.Event(), []
        def write():
            try:
                self.process.stdin.write(data)
                self.process.stdin.flush()
            except (BrokenPipeError, OSError) as error:
                errors.append(error)
            finally:
                done.set()
        self.writer = threading.Thread(target=write, daemon=True)
        self.writer.start()
        remaining = min(self.timeout, max(0, self.deadline - time.monotonic()))
        if not done.wait(timeout=remaining):
            raise DiscoveryFailure("Timed out writing an MCP request")
        self.writer.join(timeout=1)
        if errors:
            raise DiscoveryFailure("MCP process closed stdin before completing discovery") from errors[0]

    def call(self, method: str, parameters: dict):
        self.identifier += 1
        identifier = self.identifier
        self.send({"jsonrpc": "2.0", "id": identifier, "method": method, "params": parameters})
        deadline = min(time.monotonic() + self.timeout, self.deadline)
        while True:
            if self.reader_error is not None:
                raise DiscoveryFailure(str(self.reader_error)) from self.reader_error
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise DiscoveryFailure(f"Timed out waiting for {method}")
            try:
                message = self.messages.get(timeout=remaining)
            except queue.Empty as error:
                raise DiscoveryFailure(f"Timed out waiting for {method}") from error
            if isinstance(message, BaseException):
                raise DiscoveryFailure(str(message)) from message
            if message is None:
                raise DiscoveryFailure(f"MCP process exited before answering {method}")
            if "id" not in message and isinstance(message.get("method"), str):
                # Valid server notifications are retained in stdout.jsonl. Their
                # timing is not part of discovery definition parity.
                continue
            if type(message.get("id")) is not int or message["id"] != identifier or "method" in message:
                raise DiscoveryFailure(f"Unexpected JSON-RPC response/request during {method}")
            if "error" in message or not isinstance(message.get("result"), dict):
                raise DiscoveryFailure(f"{method} did not return a successful result")
            return message["result"]

    def finish(self):
        self.process.stdin.close()
        try:
            code = self.process.wait(timeout=10)
        except subprocess.TimeoutExpired as error:
            raise DiscoveryFailure("MCP did not exit naturally after stdin EOF") from error
        self.reader.join(timeout=2)
        self.stderr_reader.join(timeout=2)
        if self.reader.is_alive() or self.stderr_reader.is_alive():
            raise DiscoveryFailure("MCP stdout did not reach EOF")
        if self.reader_error is not None:
            raise DiscoveryFailure(str(self.reader_error)) from self.reader_error
        if code != 0:
            raise DiscoveryFailure(f"MCP exited with code {code}")
        while not self.messages.empty():
            message = self.messages.get_nowait()
            if message is not None and not (isinstance(message, dict) and "id" not in message and isinstance(message.get("method"), str)):
                raise DiscoveryFailure("Unexpected response after discovery completed")
        return code

    def close(self):
        if self.process.poll() is None:
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(self.process.pid), "/T", "/F"],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10, check=False)
            else:
                try:
                    os.killpg(self.process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    # The process exited between poll and termination.
                    pass
            self.process.wait(timeout=10)
        if self.writer is not None:
            self.writer.join(timeout=2)
        self.reader.join(timeout=2)
        self.stderr_reader.join(timeout=2)
        if self.writer is not None and self.writer.is_alive():
            raise DiscoveryFailure("MCP writer did not stop after process termination")
        if self.process.stdin and not self.process.stdin.closed:
            self.process.stdin.close()
        self.process.stdout.close()
        self.process.stderr.close()
        self.stdout_log.close()
        self.stderr_log.close()


def capture_discovery(executable: Path, directory: Path, api_origin: str) -> dict:
    directory.mkdir(parents=True, exist_ok=True)
    configuration = directory / "mcp-config.json"
    # Synthetic identity only. No tools/call or resources/read is sent; even an
    # accidental API request reaches only the caller's local failing tripwire.
    configuration.write_text(json.dumps({"apiBaseUrl": api_origin, "credentialMode": "integration",
                                         "integrationCredential": "rdk_size_batch_probe"}), encoding="utf-8")
    if os.name != "nt":
        configuration.chmod(0o600)
    environment = {key: value for key, value in os.environ.items()
                   if not key.upper().startswith(("RATELDESK_", "HELPDESK_", "HELPDESK:"))}
    environment.update({"RATELDESK_MCP_CONFIG": str(configuration.resolve()),
                        "RATELDESK_MCP_INSTANCE": "sizebatch",
                        "RATELDESK_MCP_EXPECTED_API_BASE_URL": api_origin})
    session = None
    status = {"status": "FAIL", "methods": [], "pageCounts": {}, "exitCode": None}
    try:
        session = Session([str(executable.resolve())], directory.resolve(), environment)
        initialized = session.call("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                                  "clientInfo": {"name": "rateldesk-size-discovery", "version": "1"}})
        if initialized.get("protocolVersion") != "2025-03-26":
            raise DiscoveryFailure("MCP negotiated an unexpected protocol version")
        server_info = initialized.get("serverInfo", {})
        if not isinstance(server_info, dict) or not all(isinstance(server_info.get(key), str) and server_info[key] for key in ("name", "version")):
            raise DiscoveryFailure("MCP initialization omitted its server identity")
        capabilities = initialized.get("capabilities", {})
        if not isinstance(capabilities, dict) or any(key not in capabilities for key in ("tools", "resources", "prompts")):
            raise DiscoveryFailure("MCP initialization omitted a required discovery capability")
        session.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        snapshot = {"initialize": initialized}
        for method, key, identity in LISTS:
            snapshot[key], pages = collect_pages(session.call, method, key, identity)
            status["methods"].append(method)
            status["pageCounts"][key] = pages
        if not snapshot["tools"] or not snapshot["prompts"] or not (snapshot["resources"] or snapshot["resourceTemplates"]):
            raise DiscoveryFailure("MCP discovery unexpectedly returned an empty product catalog")
        status["exitCode"] = session.finish()
        status["status"] = "PASS"
        status["counts"] = {key: len(snapshot[key]) for _, key, _ in LISTS}
        (directory / "snapshot.json").write_text(json.dumps(snapshot, sort_keys=True, indent=2) + "\n", encoding="utf-8")
        return snapshot
    except BaseException as error:
        status["error"] = str(error)
        raise
    finally:
        if session is not None:
            session.close()
            status["exitCode"] = session.process.returncode
            status["stdoutBytes"] = session.stdout_bytes
            status["stderrBytes"] = session.stderr_bytes
        (directory / "status.json").write_text(json.dumps(status, indent=2) + "\n", encoding="utf-8")
