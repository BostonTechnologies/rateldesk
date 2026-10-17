#!/usr/bin/env python3
"""Explicit, bounded real-PTY setup acceptance: pass the executable command after --."""
import argparse
import errno
import datetime
import http.server
import json
import ssl
import subprocess
import threading
import fcntl
import os
import pty
import select
import signal
import struct
import tempfile
import termios
import time
from pathlib import Path
from terminal_test_support import terminal_text


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--approval", action="store_true", help="Also exercise approval using a disposable HTTPS pairing fixture.")
    parser.add_argument("--transcript", type=Path, help="Optional synthetic PTY render evidence file.")
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("pass an executable, or dotnet and its assembly, after --")
    with tempfile.TemporaryDirectory(prefix="rateldesk-setup-pty-") as temporary:
        profile = Path(temporary) / "private" / "client.json"
        child, terminal = pty.fork()
        if child == 0:
            os.environ["TERM"] = "xterm-256color"
            os.execvp(command[0], command + ["setup", "--config", str(profile), "--url", "https://seed.example.test/mcp", "--monochrome"])
        traffic = bytearray()

        def resize(columns, rows):
            fcntl.ioctl(terminal, termios.TIOCSWINSZ, struct.pack("HHHH", rows, columns, 0, 0))
            os.kill(child, signal.SIGWINCH)

        def rendered():
            return terminal_text(traffic, rows=40)

        def until(predicate, description, seconds=10):
            deadline = time.monotonic() + seconds
            while time.monotonic() < deadline:
                if predicate(rendered()):
                    return
                readable, _, _ = select.select([terminal], [], [], 0.1)
                if readable:
                    try:
                        traffic.extend(os.read(terminal, 65536))
                    except OSError as error:
                        if error.errno != errno.EIO:
                            raise
                        break
            raise RuntimeError("PTY acceptance failed: " + description)

        try:
            resize(80, 24)
            until(lambda text: "RatelDesk Web URL" in text and "Advanced" in text, "first 80x24 URL screen")
            until(lambda text: "https://seed.example.test/mcp" in text, "actual --url option seeds the focused first field")
            if "Profile name" in rendered() or "Export directory" in rendered():
                raise RuntimeError("PTY acceptance failed: extra first-page questions")
            # End/Backspace clears the seeded value using the actual terminal decoder.
            os.write(terminal, b"\x1b[F" + b"\x7f" * len("https://seed.example.test/mcp") +
                     b"\x1b[200~https://desk.example.test/prefix/mcp\x1b[201~\r")
            until(lambda text: "Application path prefixes are not supported" in text, "focused URL paste/Enter field error")
            os.write(terminal, b"\x1bOQ")  # F2: discoverable Advanced also has a visible first-page action.
            until(lambda text: "Profile name" in text, "F2 Advanced")
            resize(120, 32)
            until(lambda text: b"\x1b[3;120H" in traffic and "https://desk.example.test/prefix/mcp" in text, "resize retains input")
            os.write(terminal, b"\x02")  # Ctrl+B keeps the URL and returns from the first page safely.
            os.write(terminal, b"\x03")
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                completed, status = os.waitpid(child, os.WNOHANG)
                if completed:
                    if os.waitstatus_to_exitcode(status) != 0:
                        raise RuntimeError("PTY acceptance failed: cancellation exit")
                    break
                readable, _, _ = select.select([terminal], [], [], 0.1)
                if readable:
                    try:
                        traffic.extend(os.read(terminal, 65536))
                    except OSError as error:
                        if error.errno != errno.EIO:
                            raise
            else:
                raise RuntimeError("PTY acceptance failed: cancellation timed out")
            if profile.exists():
                raise RuntimeError("PTY acceptance failed: cancellation saved a profile")
            print("PASS: real PTY URL focus/paste/Enter, Advanced, resize, Back and cancellation (80x24 and 120x32)")
        finally:
            if args.transcript:
                args.transcript.write_bytes(traffic)
            try:
                os.kill(child, signal.SIGKILL)
                os.waitpid(child, 0)
            except ProcessLookupError:
                pass
            except ChildProcessError:
                pass
            os.close(terminal)
    if args.approval:
        approval_acceptance(command, args.transcript)


def approval_acceptance(command, transcript):
    """Actual Termina render/input, with synthetic protocol responses and no issued credential."""
    with tempfile.TemporaryDirectory(prefix="rateldesk-approval-pty-") as temporary:
        directory = Path(temporary)
        certificate, key = directory / "ca.pem", directory / "key.pem"
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                        "-keyout", str(key), "-out", str(certificate), "-subj", "/CN=RatelDesk synthetic PTY fixture",
                        "-addext", "subjectAltName=IP:127.0.0.1"], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        os.chmod(key, 0o600)
        state = {"status": "pending", "starts": 0, "cancels": 0, "requests": []}

        class Pairing(http.server.BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def reply(self, body):
                data = json.dumps(body).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def do_GET(self):
                state["requests"].append(("GET", self.path))
                if not self.path.endswith("/pairing/capabilities"):
                    self.send_error(404)
                    return
                self.reply(dict(product="RatelDesk", protocolVersion=1, pairingSupported=True,
                                publicOrigin=origin, verificationUri=approval_url,
                                defaultLifetimeDays=30, maximumLifetimeDays=90))

            def do_POST(self):
                state["requests"].append(("POST", self.path))
                length = int(self.headers.get("Content-Length", "0"))
                if length > 8192:
                    self.send_error(413)
                    return
                if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
                    consumed = 0
                    while True:
                        size = int(self.rfile.readline(128).strip().split(b";", 1)[0], 16)
                        consumed += size
                        if consumed > 8192:
                            self.send_error(413)
                            return
                        if size == 0:
                            self.rfile.readline(128)
                            break
                        self.rfile.read(size)
                        self.rfile.read(2)
                else:
                    self.rfile.read(length)
                if self.path.endswith("/pairing/start"):
                    state["starts"] += 1
                    self.reply(dict(deviceCode="a" * 64, humanCode="TRUST-CODE", verificationUri=approval_url,
                                    expiresAtUtc=(datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(minutes=5)).isoformat(),
                                    pollIntervalSeconds=1))
                elif self.path.endswith("/pairing/poll"):
                    self.reply(dict(status=state["status"], pollIntervalSeconds=1, credential=None))
                elif self.path.endswith("/pairing/cancel"):
                    state["cancels"] += 1
                    self.reply({})
                else:
                    self.send_error(404)

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Pairing)
        origin = "https://127.0.0.1:" + str(server.server_port)
        approval_url = origin + "/account/integration-credentials/approve"
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(certificate, key)
        server.socket = context.wrap_socket(server.socket, server_side=True)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        profile = directory / "private" / "client.json"
        child, terminal = pty.fork()
        if child == 0:
            os.environ["TERM"] = "xterm-256color"
            os.environ["SSL_CERT_FILE"] = str(certificate)
            os.environ["NO_PROXY"] = "127.0.0.1,localhost"
            os.execvp(command[0], command + ["setup", "--config", str(profile), "--url", origin, "--monochrome"])
        traffic = bytearray()
        rows, columns = 24, 80

        def rendered():
            return terminal_text(traffic, rows=rows, columns=columns)

        def until(predicate, description):
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                if predicate(rendered()):
                    return
                readable, _, _ = select.select([terminal], [], [], 0.1)
                if readable:
                    try:
                        traffic.extend(os.read(terminal, 65536))
                    except OSError as error:
                        if error.errno != errno.EIO:
                            raise
                        break
            raise RuntimeError("PTY approval acceptance failed: " + description + "\n" + repr(state) + "\n" + rendered())

        def primary(text):
            return all(value in text for value in ("Client access approval", approval_url, "TRUST-CODE", "Open browser", "Copy URL and code", "Details", "Back", "Cancel"))

        try:
            fcntl.ioctl(terminal, termios.TIOCSWINSZ, struct.pack("HHHH", rows, columns, 0, 0))
            os.kill(child, signal.SIGWINCH)
            until(lambda text: "RatelDesk Web URL" in text, "initial URL")
            os.write(terminal, b"\r")
            until(lambda text: "Choose authentication for this client profile" in text, "real HTTPS discovery")
            os.write(terminal, b"\x0e")
            until(primary, "URL/code/actions visible immediately at 80x24 without PageDown")
            os.write(terminal, b"\r\t\x1b[Z\x04")  # Enter copy; Tab/Shift+Tab; explicit Details.
            until(lambda text: primary(text) and "Esc closes" in text, "Details retains primary controls")
            os.write(terminal, b"\x1b")
            until(lambda text: primary(text) and "Esc closes" not in text, "Escape closes only Details")
            os.write(terminal, b"\x19")
            until(lambda text: primary(text) and ("copied" in text.lower() or "clipboard unavailable" in text.lower()), "deliberate clipboard feedback preserves text")
            os.write(terminal, b"\x02")
            until(lambda text: "Authentication" in text and "Inputs are preserved" in text, "client Back reaches Authentication")
            os.write(terminal, b"\x0e")
            until(primary, "renewed client approval")
            state["status"] = "denied"
            until(lambda text: primary(text) and "Approval denied" in text, "denied state preserves actions")
            os.write(terminal, b"\x02")
            until(lambda text: "Authentication" in text and "Inputs are preserved" in text, "denied Back")
            state["status"] = "expired"
            os.write(terminal, b"\x0e")
            until(lambda text: primary(text) and "Approval expired" in text, "expired state preserves actions")
            rows, columns = 32, 120
            fcntl.ioctl(terminal, termios.TIOCSWINSZ, struct.pack("HHHH", rows, columns, 0, 0))
            os.kill(child, signal.SIGWINCH)
            until(primary, "wide resize preserves approval")
            os.write(terminal, b"\x03")
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                completed, status = os.waitpid(child, os.WNOHANG)
                if completed:
                    if os.waitstatus_to_exitcode(status) != 0:
                        raise RuntimeError("PTY approval cancellation failed")
                    break
                readable, _, _ = select.select([terminal], [], [], 0.05)
                if readable:
                    try:
                        traffic.extend(os.read(terminal, 65536))
                    except OSError as error:
                        if error.errno != errno.EIO:
                            raise
            else:
                raise RuntimeError("PTY approval cancellation exceeded its bound")
            if profile.exists() or state["starts"] != 3 or state["cancels"] != 3:
                raise RuntimeError("PTY approval failed profile isolation or bounded cancellation")
            if "a" * 64 in traffic.decode(errors="replace"):
                raise RuntimeError("PTY approval exposed a device capability")
            print("PASS: real HTTPS approval PTY URL/code/actions, Details, focus/copy, Back, denied/expired, resize and cleanup (80x24 and 120x32)")
        finally:
            if transcript:
                transcript.with_name(transcript.stem + "-approval" + transcript.suffix).write_bytes(traffic)
            try:
                os.kill(child, signal.SIGKILL)
                os.waitpid(child, 0)
            except (ProcessLookupError, ChildProcessError):
                pass
            os.close(terminal)
            server.shutdown()
            server.server_close()
            worker.join(timeout=2)


if __name__ == "__main__":
    main()
