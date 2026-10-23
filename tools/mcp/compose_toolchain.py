"""Explicit, integrity-checked setup-only Compose selection.

Compose's supported standalone invocation uses the original Docker configuration,
context, TLS files and credential helpers. We never create a replacement
DOCKER_CONFIG, install a CLI plugin, or change the active Docker context.
"""
import hashlib
import os
from pathlib import Path
import platform
import re
import stat
import tempfile
import urllib.error
import urllib.request

VERSION = "2.39.4"
# Official v2.39.4 checksums.txt, retained here so verification is independent
# of the download connection. https://github.com/docker/compose/releases/tag/v2.39.4
PINNED = {
    ("Linux", "x86_64"): ("linux-x86_64", "7af95166a730b87e172d4fc9aefea8725d3c6c7327d59149267b452114ddb7d4"),
    ("Linux", "aarch64"): ("linux-aarch64", "49082844b87f03cdcd5f5bbef1ba8c9c897b7a2dfb80cea18d61ec8ca6117e0c"),
    ("Darwin", "x86_64"): ("darwin-x86_64", "ec56a48c47ff30db7626f980f36e48d9573114626c2beb6b76b47dd6ff4aa63c"),
    ("Darwin", "arm64"): ("darwin-aarch64", "718d09670f2017cdb7bfabccebdbf00b3f971a7e560afc13512b4334796e3cf0"),
}
MAX_BINARY_BYTES = 128 * 1024 * 1024
BINARY_KEY = "RATELDESK_SETUP_COMPOSE_BINARY"
DIGEST_KEY = "RATELDESK_SETUP_COMPOSE_SHA256"
VERSION_KEY = "RATELDESK_SETUP_COMPOSE_VERSION"
CONTEXT_KEY = "RATELDESK_SETUP_DOCKER_CONTEXT_SHA256"


def digest_file(path, fail):
    path = Path(path)
    try:
        # Do not follow a replaced final symlink between inspection and read.
        descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
        with os.fdopen(descriptor, "rb") as source:
            info = os.fstat(source.fileno())
            if not stat.S_ISREG(info.st_mode) or info.st_size > MAX_BINARY_BYTES:
                fail("compose_tool_unreadable", "Select a bounded regular Compose executable, not a symlink or directory.")
            digest = hashlib.sha256()
            total = 0
            while chunk := source.read(1024 * 1024):
                total += len(chunk)
                if total > MAX_BINARY_BYTES:
                    fail("compose_tool_oversized", "The selected Compose file exceeds its bounded size limit; it was not executed.")
                digest.update(chunk)
            return digest.hexdigest()
    except OSError:
        fail("compose_tool_unreadable", "The selected Compose executable is missing or unreadable. Restore that exact verified file, then rerun.")


class Selection:
    def __init__(self, path, expected, fail):
        self.path = Path(path).absolute()
        self.expected = expected.lower() if isinstance(expected, str) else ""
        self.fail = fail
        if not re.fullmatch(r"[a-f0-9]{64}", self.expected):
            fail("compose_tool_integrity_required", "Supply --compose-sha256 with the expected upstream SHA-256 before selecting --compose-binary; the executable will not run without integrity verification.")
        self.verify()

    def verify(self):
        if self.path.is_symlink() or digest_file(self.path, self.fail) != self.expected:
            self.fail("compose_tool_integrity", "The selected Compose executable does not match its approved SHA-256. Restore the verified file or explicitly select and review a different tool; it was not executed.")
        if not os.access(self.path, os.X_OK):
            self.fail("compose_tool_permission", "The verified Compose file is not executable. Grant execute permission to that file, then rerun the same selection.")

    def environment(self):
        return {BINARY_KEY: str(self.path), DIGEST_KEY: self.expected}

    def command(self, arguments):
        # Revalidate each invocation, including fresh verify/apply processes.
        self.verify()
        return [str(self.path), *arguments]


def platform_pin(fail):
    identity = (platform.system(), platform.machine().lower())
    aliases = {("Linux", "amd64"): ("Linux", "x86_64"), ("Linux", "arm64"): ("Linux", "aarch64")}
    identity = aliases.get(identity, identity)
    if identity not in PINNED:
        fail("compose_tool_platform", "Private preparation supports Linux x64/arm64 and macOS x64/arm64. Select an existing verified compatible Compose executable on this platform, or cancel and install Docker Compose v2.35+ through the host's supported package channel.")
    return PINNED[identity]


def prepare(directory, fail, *, cache=None):
    """Caller has obtained explicit consent by flag or interactive choice."""
    artifact, expected = platform_pin(fail)
    directory = Path(directory).absolute()
    try:
        if directory.is_symlink():
            fail("compose_tool_directory", "Choose a private setup-only tool directory, not a symlink.")
        directory.mkdir(mode=0o700, parents=True, exist_ok=True)
        info = directory.stat()
        if info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700:
            fail("compose_tool_directory", "The setup-only tool directory must be owned by this user with mode 0700. Select a new private directory or restore its protection.")
        target = directory / ("docker-compose-v" + VERSION + "-" + artifact)
        if target.exists() or target.is_symlink():
            return Selection(target, expected, fail)
        descriptor, temporary = tempfile.mkstemp(prefix=".compose-partial-", dir=directory)
        try:
            with os.fdopen(descriptor, "wb") as destination:
                os.fchmod(destination.fileno(), 0o600)
                if cache:
                    source_path = Path(cache)
                    if source_path.is_symlink() or digest_file(source_path, fail) != expected:
                        fail("compose_tool_integrity", "The offline Compose cache does not match the pinned upstream SHA-256. Obtain the matching v2.39.4 platform file; nothing was executed.")
                    source = source_path.open("rb")
                else:
                    request = urllib.request.Request(
                        f"https://github.com/docker/compose/releases/download/v{VERSION}/docker-compose-{artifact}",
                        headers={"User-Agent": "RatelDesk-explicit-private-Compose-setup"})
                    source = urllib.request.urlopen(request, timeout=30)
                with source:
                    total = 0
                    while chunk := source.read(1024 * 1024):
                        total += len(chunk)
                        if total > MAX_BINARY_BYTES:
                            fail("compose_tool_oversized", "The private Compose download exceeded its size limit. Use a verified offline platform file instead.")
                        destination.write(chunk)
                destination.flush()
                os.fsync(destination.fileno())
            if digest_file(temporary, fail) != expected:
                fail("compose_tool_integrity", "The private Compose download does not match the pinned upstream SHA-256. No downloaded file was executed; obtain the verified platform file and retry with --compose-cache.")
            os.chmod(temporary, 0o700)
            # Do not overwrite any concurrent or existing selection.
            os.link(temporary, target)
        finally:
            Path(temporary).unlink(missing_ok=True)
        return Selection(target, expected, fail)
    except (urllib.error.URLError, TimeoutError, ConnectionError):
        fail("compose_tool_network", "The pinned Compose download is unavailable. Retry --prepare-compose with --compose-cache pointing to the verified v2.39.4 platform file, or select an existing --compose-binary with --compose-sha256. The normal Docker plugin/configuration was unchanged.")
    except OSError:
        fail("compose_tool_storage", "The private Compose tool could not be prepared. Restore private directory permissions or use an existing verified executable; the normal Docker plugin/configuration was unchanged.")


def choose(fail, *, input_fn=input, output=None):
    """A short prerequisite recovery choice; cancellation creates nothing."""
    def say(value):
        print(value, file=output)
    say("Compose needs additional source-reading capabilities. Choose:")
    say("  1. Use an existing verified standalone Compose executable")
    say(f"  2. Prepare private pinned Compose v{VERSION} (explicit download consent)")
    say("  3. Cancel; install Docker Compose v2.35+ through the host's supported package channel")
    answer = input_fn("Choice [3]: ").strip()
    if answer == "1":
        path = input_fn("Existing Compose executable (absolute path): ").strip()
        expected = input_fn("Expected upstream SHA-256: ").strip()
        return Selection(path, expected, fail)
    if answer == "2":
        directory = input_fn("Private setup-only directory (absolute path): ").strip()
        if not directory or not Path(directory).is_absolute():
            fail("compose_tool_directory", "Choose an explicit absolute private directory, then rerun --prepare-compose with that directory.")
        return prepare(directory, fail)
    fail("compose_tool_cancelled", "Setup cancelled before consent or deployment changes. Install Docker Compose v2.35+ through the host's supported package channel, then rerun the same host command.")
