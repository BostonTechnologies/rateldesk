#!/usr/bin/env python3
"""Exercise exact archived client apphosts against disposable synthetic fixtures."""

from contextlib import contextmanager
import argparse
import ctypes
import hashlib
import http.server
import importlib.util
import json
import os
from pathlib import Path
import platform
import queue
import re
import select
import shutil
import socket
import ssl
import statistics
import subprocess
import sys
import threading
import time
from urllib.parse import parse_qs, urlsplit

ROOT = Path(__file__).resolve().parents[2]
IDENTIFIER = "11111111-1111-1111-1111-111111111111"
SECRET = "rdk_synthetic_campaign_client"


class BehaviorFailure(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise BehaviorFailure(message)


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    value = importlib.util.module_from_spec(spec)
    sys.modules[name] = value
    spec.loader.exec_module(value)
    return value


batch = module("behavior_batch", "client-batch.py")
discovery = module("behavior_discovery", "client-discovery.py")
closure = module("behavior_closure", "check-client-publish.py")


def write(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def environment():
    return {key: value for key, value in os.environ.items()
            if not key.upper().startswith(("RATELDESK_", "HELPDESK_", "HELPDESK:"))}


def memory(process):
    if os.name != "nt":
        values = dict(line.split(":", 1) for line in Path(f"/proc/{process.pid}/status").read_text().splitlines() if ":" in line)
        if "VmRSS" not in values or "VmHWM" not in values:
            raise ProcessLookupError("Process memory is no longer resident")
        return {"residentBytes": int(values["VmRSS"].split()[0]) * 1024,
                "peakResidentBytes": int(values["VmHWM"].split()[0]) * 1024,
                "source": "Linux per-process VmRSS/VmHWM"}

    class Counters(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_ulong), ("PageFaultCount", ctypes.c_ulong),
                    *[(name, ctypes.c_size_t) for name in ("PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
                        "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage", "PrivateUsage")]]

    counters = Counters()
    counters.cb = ctypes.sizeof(counters)
    function = ctypes.windll.psapi.GetProcessMemoryInfo
    function.argtypes = [ctypes.c_void_p, ctypes.POINTER(Counters), ctypes.c_ulong]
    function.restype = ctypes.c_int
    require(function(int(process._handle), ctypes.byref(counters), counters.cb), "Cannot read Windows process memory counters")
    return {"residentBytes": counters.WorkingSetSize, "peakResidentBytes": counters.PeakWorkingSetSize,
            "source": "Windows GetProcessMemoryInfo working set"}


def run_measured(command, directory, env):
    process = subprocess.Popen(command, cwd=directory, env=env, stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
    stopped = threading.Event()
    samples = []
    errors = []

    def observe():
        while not stopped.is_set():
            try:
                samples.append(memory(process))
            except (FileNotFoundError, ProcessLookupError):
                # communicate() may reap it before Popen publishes returncode.
                return
            except Exception as error:
                errors.append(str(error))
                return
            stopped.wait(0.005)

    observer = threading.Thread(target=observe, daemon=True)
    observer.start()
    try:
        stdout, stderr = process.communicate(input="", timeout=20)
    except subprocess.TimeoutExpired:
        process.kill()
        process.communicate()
        raise
    finally:
        stopped.set()
        observer.join(timeout=2)
    require(samples and not errors, "CLI memory sampling failed: " + "; ".join(errors))
    return subprocess.CompletedProcess(command, process.returncode, stdout, stderr), {
        "maxObservedKernelPeakResidentBytes": max(item["peakResidentBytes"] for item in samples),
        "sampleCount": len(samples), "sampleIntervalMilliseconds": 5,
        "scope": "Maximum observed kernel high-water mark during 5ms sampling; final interval can be missed"}


@contextmanager
def backend(tls_context=None):
    state = {"requests": [], "deny": False, "block": False,
             "entered": threading.Event(), "release": threading.Event(), "cancelled": threading.Event()}

    class Handler(http.server.BaseHTTPRequestHandler):
        def handle(self):
            try:
                super().handle()
            except (ssl.SSLError, BrokenPipeError, ConnectionResetError) as error:
                state.setdefault("transportDisconnects", []).append(type(error).__name__)

        def do_GET(self):
            parsed = urlsplit(self.path)
            auth = self.headers.get("Authorization")
            authenticated = auth == "Bearer " + SECRET
            state["requests"].append({"method": "GET", "path": parsed.path, "query": parse_qs(parsed.query),
                                      "authenticated": authenticated})
            status, data = 200, {}
            if state["deny"]:
                status, data = 403, {"error": "synthetic_permission_denied"}
            elif parsed.path.startswith("/api/") and not authenticated:
                status, data = 401, {"error": "synthetic_identity_required"}
            elif parsed.path in ("/health/live", "/health/ready"):
                data = {"status": "Healthy"}
            elif parsed.path == "/api/v1/system/version":
                if state["block"]:
                    state["entered"].set()
                    deadline = time.monotonic() + 20
                    while not state["release"].is_set() and time.monotonic() < deadline:
                        if select.select([self.connection], [], [], 0.05)[0] and self.connection.recv(1, socket.MSG_PEEK) == b"":
                            state["cancelled"].set()
                            return
                data = {"version": "0.0.0-synthetic", "displayVersion": "café Δ"}
            elif parsed.path in ("/api/v1/auth/me", "/api/v1/auth/ai-agent/status"):
                data = {"id": IDENTIFIER, "email": "agent@example.test", "permissions": ["Incident.Read"], "enabled": True}
            elif parsed.path.rstrip("/") == "/api/v1/incidents":
                data = {"items": [{"id": IDENTIFIER, "title": "café Δ", "state": 0, "priority": 1}], "total": 1}
            elif parsed.path.startswith(("/api/v1/incidents/", "/api/v1/customers/", "/api/v1/organizations/")):
                data = {"id": IDENTIFIER, "title": "café Δ", "name": "Fixture", "organizationId": IDENTIFIER, "enabled": True}
            else:
                status, data = 404, {"error": "unexpected_fixture_route"}
                state["unexpectedRoute"] = parsed.path
            body = json.dumps(data, ensure_ascii=False).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                # A cancelled client request intentionally closes this socket.
                state["cancelledConnectionClosed"] = True

        def do_POST(self):
            state["requests"].append({"method": self.command, "path": self.path})
            self.send_error(500, "Mutations are forbidden in this fixture")

        do_PUT = do_PATCH = do_DELETE = do_POST

        def log_message(self, *_):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    if tls_context is not None:
        server.socket = tls_context.wrap_socket(server.socket, server_side=True)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"{'https' if tls_context else 'http'}://127.0.0.1:{server.server_port}", state
    finally:
        state["release"].set()
        server.shutdown()
        server.server_close()
        thread.join(timeout=3)


def configured(path, origin):
    write(path, {"apiBaseUrl": origin, "credentialMode": "integration", "integrationCredential": SECRET})
    if os.name != "nt":
        path.chmod(0o600)


def cli_cases(executable, directory, origin, state):
    directory.mkdir()
    config = directory / "legacy profile café.json"
    configured(config, origin)
    observations = []

    def invoke(arguments, expected=0, as_json=False):
        index = len(observations)
        started = time.perf_counter()
        result, observed_memory = run_measured([str(executable), "--config", str(config), *arguments], directory, environment())
        duration = round((time.perf_counter() - started) * 1000, 3)
        (directory / f"{index:03d}.stdout").write_text(result.stdout, encoding="utf-8")
        (directory / f"{index:03d}.stderr").write_text(result.stderr, encoding="utf-8")
        observations.append({"arguments": arguments, "exitCode": result.returncode, "elapsedMilliseconds": duration,
                             "memory": observed_memory})
        write(directory / "cases.json", observations)
        require(result.returncode == expected, f"CLI {arguments} exited {result.returncode}, expected {expected}; see case {index}")
        require(SECRET not in result.stdout + result.stderr, "CLI disclosed its synthetic credential")
        return json.loads(result.stdout) if as_json else result

    def children(help_text):
        section = help_text.split("\nCommands:\n", 1)
        return re.findall(r"^  ([a-z][a-z-]+)(?:\s|$)", section[1], re.MULTILINE) if len(section) == 2 else []

    expected = set("setup host status config-validate doctor profile auth config health logs incidents requests changes request-tasks tickets organizations customers users roles categories services request-forms self-service notifications search connectivity system capabilities schema enums examples raw".split())
    root_commands = children(invoke(["--help"]).stdout)
    require(set(root_commands) == expected, "CLI root command surface changed")
    pending = [[name] for name in root_commands]
    while pending:
        path = pending.pop(0)
        require(len(path) <= 6, "CLI command help recursion exceeded its bound")
        pending.extend([*path, child] for child in children(invoke([*path, "--help"]).stdout))
    schema = invoke(["schema", "--json"], as_json=True)
    require({group["name"] for group in schema["groups"]} ==
            set("categories changes connectivity customers incidents notifications organizations raw request-forms request-tasks requests roles services users".split()),
            "CLI introspection schema groups changed")
    enum_names = invoke(["enums", "--json"], as_json=True)["enums"]
    require(enum_names, "CLI enum registry is empty")
    for name in enum_names:
        require(invoke(["enums", name, "--json"], as_json=True)["values"], "CLI enum lost its values")
    invoke(["config", "show", "--json"], as_json=True)
    invoke(["config-validate", "--json"], as_json=True)
    result = invoke(["incidents", "list", "--page", "2", "--page-size", "3", "--query", "café Δ", "--state", "New", "--json"], as_json=True)
    require("café Δ" in json.dumps(result, ensure_ascii=False), "CLI lost Unicode response data")
    require(state["requests"][-1]["query"] == {"page": ["2"], "pageSize": ["3"], "q": ["café Δ"], "state": ["New"]},
            "CLI changed paging, search or enum request semantics")
    invoke(["incidents", "get", IDENTIFIER, "--json"], as_json=True)
    before = len(state["requests"])
    preview = invoke(["incidents", "create", "--customer-id", IDENTIFIER, "--organization-id", IDENTIFIER,
                      "--title", "café Δ", "--description", "Synthetic preview", "--priority", "Medium", "--dry-run", "--json"], as_json=True)
    require(preview.get("dryRun") is True and not any(item["method"] != "GET" for item in state["requests"][before:]), "CLI dry-run performed a mutation")
    invoke(["incidents", "list", "--page-size", "not-an-integer", "--json"], expected=1)
    state["deny"] = True
    try:
        invoke(["incidents", "get", IDENTIFIER, "--json"], expected=2)
    finally:
        state["deny"] = False
    samples = [invoke(["--help"]).returncode for _ in range(5)]
    require(samples == [0] * 5, "CLI startup sampling failed")
    help_samples = [item["elapsedMilliseconds"] for item in observations[-5:]]
    write(directory / "cases.json", observations)
    return {"status": "PASS", "executed": len(observations), "schemaSha256": hashlib.sha256(json.dumps(schema, sort_keys=True).encode()).hexdigest(),
            "helpStartupMilliseconds": {"samples": help_samples, "median": statistics.median(help_samples)},
            "maximumObservedPeakResidentBytes": max(item["memory"]["maxObservedKernelPeakResidentBytes"] for item in observations),
            "scope": "Native archived command/help/enum/JSON/legacy-profile/read/query/denial/dry-run checks; fixture API only"}


def start_mcp(executable, directory, origin, instance):
    directory.mkdir()
    config = directory / "mcp profile café.json"
    configured(config, origin)
    env = environment()
    env.update(RATELDESK_MCP_CONFIG=str(config), RATELDESK_MCP_INSTANCE=instance,
               RATELDESK_MCP_EXPECTED_API_BASE_URL=origin, RATELDESK_API_BASE_URL="https://wrong.example.test",
               RATELDESK_INTEGRATION_CREDENTIAL="rdk_wrong_ambient_cli")
    started = time.perf_counter()
    session = discovery.Session([str(executable)], directory, env)
    session.call("initialize", {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "campaign-behavior", "version": "1"}})
    elapsed = round((time.perf_counter() - started) * 1000, 3)
    session.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
    return session, elapsed


def response(session, identifier, ignored_ids=()):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        try:
            message = session.messages.get(timeout=max(0.01, deadline - time.monotonic()))
        except queue.Empty as error:
            raise BehaviorFailure("MCP response deadline expired") from error
        require(isinstance(message, dict), "MCP exited or emitted an invalid frame")
        if "id" not in message:
            continue
        if message["id"] in ignored_ids:
            # MCP clients disregard optional late responses to cancelled requests.
            continue
        require(message["id"] == identifier, "MCP returned an unexpected response identifier")
        return message
    raise BehaviorFailure("MCP response deadline expired")


def mcp_cases(executable, directory, origin, state):
    directory.mkdir()
    session, initialize_ms = start_mcp(executable, directory / "primary", origin, "campaign-blue")
    other = None
    checks, workload = [], []

    def call(tool, operation, request=None, confirm=False):
        args = {"operation": operation, "confirm": confirm}
        if request is not None:
            args["request"] = request
        value = session.call("tools/call", {"name": tool, "arguments": args})
        require("structuredContent" in value, "MCP tool result lost its structured envelope")
        return value["structuredContent"]

    try:
        snapshot = {}
        for method, key, identity in discovery.LISTS:
            snapshot[key], _ = discovery.collect_pages(session.call, method, key, identity)
        require([len(snapshot[key]) for key in ("tools", "resources", "resourceTemplates", "prompts")] == [37, 4, 0, 4], "MCP catalog is incomplete")
        checks.append("complete paged discovery")
        for resource in snapshot["resources"]:
            data = session.call("resources/read", {"uri": resource["uri"]})
            require(data.get("contents"), "Resource returned no contents")
            for content in data["contents"]:
                json.loads(content["text"])
                require(SECRET not in content["text"], "Resource disclosed its synthetic credential")
            checks.append("read " + resource["uri"])
        for prompt in snapshot["prompts"]:
            value = session.call("prompts/get", {"name": prompt["name"], "arguments": {arg["name"]: IDENTIFIER for arg in prompt.get("arguments", [])}})
            require(value.get("messages"), "Prompt returned no messages")
            checks.append("get prompt " + prompt["name"])
        require(call("helpdesk_system", "version")["data"]["displayVersion"] == "café Δ", "MCP lost fixture Unicode data")
        require(call("helpdesk_auth", "access")["success"] is True, "MCP authenticated read failed")
        checks += ["Unicode API read", "explicit identity read"]
        before = len(state["requests"])
        invalid = session.call("tools/call", {"name": "helpdesk_capabilities", "arguments": {"operation": "get", "unsupported": True}})
        require(invalid["structuredContent"]["success"] is False, "Unknown envelope field was accepted")
        session.identifier += 1
        identifier = session.identifier
        raw = '{"jsonrpc":"2.0","id":' + str(identifier) + ',"method":"tools/call","params":{"name":"helpdesk_capabilities","arguments":{"operation":"get","operation":"get"}}}\n'
        session.process.stdin.write(raw.encode())
        session.process.stdin.flush()
        duplicated = response(session, identifier)
        require(duplicated.get("result", {}).get("structuredContent", {}).get("success") is False, "Duplicate envelope was accepted")
        preview = call("helpdesk_incidents", "delete", {"incidentId": IDENTIFIER})
        require(preview["status"] == "confirmation_required", "Mutation did not require explicit confirmation")
        require(len(state["requests"]) == before, "Invalid or unconfirmed tool performed backend I/O")
        checks += ["unknown envelope rejected", "duplicate envelope rejected", "mutation confirmation prevents I/O"]
        state["deny"] = True
        try:
            require(call("helpdesk_system", "version")["success"] is False, "MCP ignored upstream permission denial")
        finally:
            state["deny"] = False
        checks.append("upstream permission denial")
        other, _ = start_mcp(executable, directory / "isolated", origin, "campaign-green")
        for active, instance in ((session, "campaign-blue"), (other, "campaign-green")):
            value = active.call("tools/call", {"name": "helpdesk_capabilities", "arguments": {"operation": "get"}})["structuredContent"]["data"]
            require(value["instance"] == instance and value["apiBaseUrl"] == origin, "Concurrent MCP identities crossed or imported CLI settings")
        require(other.finish() == 0, "Isolated MCP did not exit naturally on EOF")
        other.close()
        other = None
        checks.append("concurrent isolated instances ignore ambient CLI identity")
        for _ in range(20):
            started = time.perf_counter()
            require(call("helpdesk_system", "version")["success"] is True, "Read workload failed")
            workload.append(round((time.perf_counter() - started) * 1000, 3))
        observed_memory = memory(session.process)
        state["block"] = True
        session.identifier += 1
        identifier = session.identifier
        session.send({"jsonrpc": "2.0", "id": identifier, "method": "tools/call", "params": {"name": "helpdesk_system", "arguments": {"operation": "version"}}})
        require(state["entered"].wait(timeout=10), "Cancellation fixture never received its request")
        session.send({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": identifier, "reason": "synthetic cancellation"}})
        require(state["cancelled"].wait(timeout=10), "MCP did not propagate caller cancellation to the backend connection")
        session.identifier += 1
        session.send({"jsonrpc": "2.0", "id": session.identifier, "method": "tools/call", "params": {"name": "helpdesk_capabilities", "arguments": {"operation": "get"}}})
        continued = response(session, session.identifier, ignored_ids=(identifier,))
        require(continued.get("result", {}).get("structuredContent", {}).get("success") is True, "Cancellation broke the MCP session")
        state["release"].set()
        checks.append("caller cancellation")
        require(session.finish() == 0, "MCP did not exit naturally on EOF")
        checks.append("natural EOF")
    finally:
        state["release"].set()
        state["block"] = False
        if other is not None:
            other.close()
        session.close()
    require(SECRET not in (directory / "primary/stdout.jsonl").read_text(), "MCP stdout disclosed its synthetic credential")
    samples = [initialize_ms]
    for index in range(4):
        active, elapsed = start_mcp(executable, directory / f"startup-{index}", origin, "campaign-sample")
        try:
            require(active.finish() == 0, "MCP startup sample did not exit naturally")
            samples.append(elapsed)
        finally:
            active.close()
    write(directory / "cases.json", checks)
    return {"status": "PASS", "executed": len(checks), "checks": checks,
            "initializeMilliseconds": {"samples": samples, "median": statistics.median(samples)},
            "residentAfter20Reads": observed_memory,
            "syntheticReadMilliseconds": {"samples": workload, "median": statistics.median(workload), "maximum": max(workload)},
            "scope": "Native archived protocol/resource/prompt/read/validation/confirmation/isolation/cancellation behavior; synthetic API only"}


def tls_cases(executables, directory):
    directory.mkdir()
    openssl = shutil.which("openssl")
    require(openssl, "OpenSSL is required to generate the disposable untrusted TLS fixture")
    key, certificate = directory / "fixture.key", directory / "fixture.crt"
    generated = subprocess.run([openssl, "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
        "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1",
        "-keyout", str(key), "-out", str(certificate)], capture_output=True, text=True, timeout=20)
    require(generated.returncode == 0, "Could not generate the disposable TLS fixture")
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(certificate, key)
    with backend(context) as (origin, state):
        config = directory / "tls-profile.json"
        configured(config, origin)
        result = subprocess.run([str(executables["cli"]), "--config", str(config), "system", "version", "--json"],
                                env=environment(), capture_output=True, text=True, encoding="utf-8", timeout=20)
        (directory / "cli.stdout").write_text(result.stdout, encoding="utf-8")
        (directory / "cli.stderr").write_text(result.stderr, encoding="utf-8")
        require(result.returncode == 3, "CLI accepted an untrusted fixture certificate or lost its safe failure exit")
        session, _ = start_mcp(executables["stdio"], directory / "stdio", origin, "campaign-tls")
        try:
            value = session.call("tools/call", {"name": "helpdesk_system", "arguments": {"operation": "version"}})
            require(value["structuredContent"]["success"] is False, "MCP accepted an untrusted fixture certificate")
            require(session.finish() == 0, "MCP TLS rejection did not preserve natural EOF")
        finally:
            session.close()
        require(not state["requests"], "An HTTP fixture request bypassed TLS trust validation")
    return {"status": "PASS", "executed": 2, "transportDisconnects": state.get("transportDisconnects", []),
            "scope": "CLI and stdio reject an untrusted local certificate without backend requests"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archives", type=Path, required=True)
    parser.add_argument("--rid", choices=("linux-x64", "linux-arm64", "win-x64"), required=True)
    parser.add_argument("--source-revision", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"status": "FAIL", "sourceRevision": args.source_revision, "rid": args.rid,
              "platform": platform.platform(), "machine": platform.machine(), "archives": {}, "releaseEligible": False}
    state = None
    try:
        require(batch.native_rid() == args.rid, "Selected RID does not match the native execution host")
        require(re.fullmatch(r"[0-9a-f]{40}", args.source_revision), "Expected a complete source revision")
        manifest = json.loads((args.archives / "release-manifest.json").read_text())
        require(manifest["sourceRevision"] == args.source_revision, "Archive source differs from the requested candidate")
        executables = {}
        for target, prefix, app in (("cli", "rateldesk-cli", "rateldesk"), ("stdio", "rateldesk-mcp-stdio", "rateldesk-mcp")):
            extension = ".zip" if args.rid == "win-x64" else ".tar.gz"
            archive = args.archives / f"{prefix}-{manifest['version']}-{args.rid}{extension}"
            digest = hashlib.sha256(archive.read_bytes()).hexdigest()
            require(manifest["assets"].get(archive.name) == digest, "Archive does not match its manifest")
            root = batch.extract_archive(archive, output / (target + "-extracted"))
            executables[target] = root / (app + (".exe" if args.rid == "win-x64" else ""))
            require(executables[target].is_file(), "Archive is missing its native executable")
            published = closure.check_publish(root, target, args.rid)
            require(published["status"] == "PASS", "Archive lost its self-contained JIT dependency contract: " + "; ".join(published["errors"]))
            files = {path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                     for path in sorted(root.rglob("*")) if path.is_file()}
            write(output / (target + "-executed-files.json"), files)
            report["archives"][target] = {"name": archive.name, "sha256": digest, "closure": published,
                "executedFileManifestSha256": hashlib.sha256((output / (target + "-executed-files.json")).read_bytes()).hexdigest()}
            trace_path = output / (target + "-host-resolution.log")
            traced = environment()
            traced.update(DOTNET_ROOT=str(output / "absent-global-runtime"), DOTNET_HOST_TRACE="1", DOTNET_HOST_TRACEFILE=str(trace_path))
            version = subprocess.run([str(executables[target]), "--version"], env=traced, capture_output=True, text=True, timeout=15)
            coreclr = root / ("coreclr.dll" if args.rid == "win-x64" else "libcoreclr.so")
            require(version.returncode == 0 and trace_path.is_file(), "Standalone runtime-resolution probe failed")
            matches = [line.replace("\\", "/").casefold() for line in trace_path.read_text(encoding="utf-8-sig").splitlines()
                       if line.startswith("CoreCLR path = ")]
            require(len(matches) == 1 and str(coreclr).replace("\\", "/").casefold() in matches[0], "Apphost loaded a global runtime instead of its archive runtime")
            report["archives"][target]["runtimeResolution"] = {"status": "PASS", "coreclr": coreclr.name,
                "trace": trace_path.name, "scope": "Loaded the archive's CoreCLR with DOTNET_ROOT pointing to an absent directory; SDK-equipped host"}
        with backend() as (origin, state):
            report["cli"] = cli_cases(executables["cli"], output / "cli", origin, state)
            report["stdio"] = mcp_cases(executables["stdio"], output / "stdio", origin, state)
            require(all(item["method"] == "GET" for item in state["requests"]), "Fixture observed a mutation")
            require(not state.get("unexpectedRoute"), "Fixture observed an unexpected route")
            # Every API call must use the explicit private profile identity.
            require(all(item["authenticated"] for item in state["requests"] if item["path"].startswith("/api/")), "API caller imported another identity")
        report["tls"] = tls_cases(executables, output / "tls")
        report["status"] = "PASS"
    except Exception as error:
        report["error"] = str(error)
    finally:
        if state is not None:
            write(output / "backend-requests.json", state["requests"])
        write(output / "report.json", report)
    print(json.dumps({"status": report["status"], "rid": args.rid, "error": report.get("error")}))
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
