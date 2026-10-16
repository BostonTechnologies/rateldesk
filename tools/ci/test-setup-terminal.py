#!/usr/bin/env python3
"""Explicit, bounded real-PTY setup acceptance: pass the executable command after --."""
import argparse
import errno
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


if __name__ == "__main__":
    main()
