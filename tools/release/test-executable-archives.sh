#!/usr/bin/env bash
set -euo pipefail

# Executes the CLI and stdio MCP apphosts from a generated release archive.
# Invoke this on the matching platform (or a deliberate emulator); it never
# contacts a RatelDesk API and creates only a disposable local MCP config.
if [[ $# -ne 2 ]]; then
  echo "usage: $0 <asset-directory> <rid>" >&2
  exit 64
fi

asset_directory="$1"
rid="$2"
[[ -d "$asset_directory" ]] || { echo "Asset directory does not exist: $asset_directory" >&2; exit 1; }

case "$rid" in
  linux-x64|linux-arm64) extension=tar.gz ;;
  win-x64) extension=zip ;;
  *) echo "Unsupported RID: $rid" >&2; exit 64 ;;
esac

cli_archive="$(find "$asset_directory" -maxdepth 1 -type f -name "rateldesk-cli-*-$rid.$extension" -print -quit)"
mcp_archive="$(find "$asset_directory" -maxdepth 1 -type f -name "rateldesk-mcp-stdio-*-$rid.$extension" -print -quit)"
[[ -n "$cli_archive" && -n "$mcp_archive" ]] || { echo "Missing executable archive(s) for $rid." >&2; exit 1; }

temporary_directory="$(mktemp -d)"
trap 'rm -rf "$temporary_directory"' EXIT
python_bin="$(command -v python3 || command -v python)"

extract_archive() {
  local archive="$1" destination="$2"
  mkdir -p "$destination"
  if [[ "$archive" == *.zip ]]; then
    unzip -q "$archive" -d "$destination"
  else
    tar -xzf "$archive" -C "$destination"
  fi
}

extract_archive "$cli_archive" "$temporary_directory/cli"
extract_archive "$mcp_archive" "$temporary_directory/mcp"

cli_directory="$(find "$temporary_directory/cli" -mindepth 1 -maxdepth 1 -type d -print -quit)"
mcp_directory="$(find "$temporary_directory/mcp" -mindepth 1 -maxdepth 1 -type d -print -quit)"
[[ -n "$cli_directory" && -n "$mcp_directory" ]] || { echo "Archives must contain one package directory." >&2; exit 1; }

suffix=""
[[ "$rid" == win-x64 ]] && suffix=.exe
cli="$cli_directory/rateldesk$suffix"
mcp="$mcp_directory/rateldesk-mcp$suffix"
[[ -f "$cli" && -f "$mcp" ]] || { echo "Archive apphost is missing for $rid." >&2; exit 1; }
[[ "$rid" == win-x64 || -x "$cli" ]] || { echo "CLI apphost is not executable for $rid." >&2; exit 1; }
[[ "$rid" == win-x64 || -x "$mcp" ]] || { echo "MCP apphost is not executable for $rid." >&2; exit 1; }

"$cli" --version >"$temporary_directory/cli-version.txt"
"$cli" --help >"$temporary_directory/cli-help.txt"
"$mcp" --version >"$temporary_directory/mcp-version.txt"
"$mcp" --help >"$temporary_directory/mcp-help.txt"
grep -q '^rateldesk ' "$temporary_directory/cli-version.txt"
grep -q '^rateldesk-mcp ' "$temporary_directory/mcp-version.txt"
grep -q 'stdout is reserved for MCP protocol traffic' "$temporary_directory/mcp-help.txt"

# Verify setup is packaged and remains explicit/noninteractive before configuration.
# On Windows, exercise actual archived CLI private storage under a broad parent ACL.
"$python_bin" - "$cli" "$mcp" "$temporary_directory" "$rid" "$(dirname "${BASH_SOURCE[0]}")/check-windows-profile-privacy.ps1" <<'PY'
import os
from pathlib import Path
import subprocess
import sys

cli, mcp, temporary, rid, privacy_script = sys.argv[1:]
environment = {key: value for key, value in os.environ.items() if not key.startswith('RATELDESK_')}
for tool in [cli, mcp]:
    result = subprocess.run([tool, 'setup'], env=environment, capture_output=True, text=True, timeout=15)
    if result.returncode != 78 or result.stdout or 'requires a TTY' not in result.stderr:
        raise SystemExit('The archive setup entrypoint did not exit predictably without a TTY.')
if rid == 'win-x64':
    directory = str(Path(temporary) / 'windows-private-profile')
    command = ['powershell.exe', '-NoProfile', '-NonInteractive', '-File', privacy_script, '-DirectoryPath', directory]
    subprocess.run(command + ['-Prepare'], check=True, timeout=15)
    environment.update({
        'RATELDESK_API_BASE_URL': 'https://api.example.test/',
        'RATELDESK_CREDENTIAL_MODE': 'authentik',
        'RATELDESK_AUTHENTIK_TOKEN_URL': 'https://auth.example.test/token',
        'RATELDESK_AUTHENTIK_CLIENT_ID': 'archive-probe',
        'RATELDESK_AUTHENTIK_USERNAME': 'archive-probe',
        'RATELDESK_AUTHENTIK_APP_PASSWORD': 'synthetic-archive-private-fixture',
    })
    for _ in range(2):
        result = subprocess.run([cli, '--config', str(Path(directory) / 'cli.json'), 'auth', 'configure'],
                                env=environment, capture_output=True, text=True, timeout=15)
        if result.returncode or 'synthetic-archive-private-fixture' in result.stdout + result.stderr:
            raise SystemExit('The archive CLI private configuration probe failed or disclosed its fixture.')
        subprocess.run(command, check=True, timeout=15)
print('Archive explicit setup entrypoints passed for ' + rid)
PY

configuration="$temporary_directory/mcp-config.json"
cat > "$configuration" <<'EOF'
{
  "apiBaseUrl": "https://api.example.test",
  "credentialMode": "integration",
  "integrationCredential": "rdk_archive_probe"
}
EOF

"$python_bin" - "$mcp" "$configuration" "$temporary_directory/mcp-stdout.jsonl" "$temporary_directory/mcp-stderr.txt" <<'PY'
import os
import queue
import subprocess
import sys
import threading

mcp, configuration, stdout_path, stderr_path = sys.argv[1:]
environment = os.environ.copy()
environment.update({
    "RATELDESK_MCP_CONFIG": configuration,
    "RATELDESK_MCP_INSTANCE": "archivetest",
    "RATELDESK_MCP_ARCHIVETEST_API_BASE_URL": "https://api.example.test",
})
process = subprocess.Popen(
    [mcp],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True,
    encoding="utf-8",
    env=environment,
)
response = queue.Queue()
threading.Thread(target=lambda: response.put(process.stdout.readline()), daemon=True).start()
process.stdin.write('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"release-archive-probe","version":"1"}}}\n')
process.stdin.flush()
try:
    first_line = response.get(timeout=10)
except queue.Empty:
    process.kill()
    process.wait()
    stderr = process.stderr.read()
    open(stderr_path, "w", encoding="utf-8").write(stderr)
    raise SystemExit("The extracted MCP server did not respond to initialize within 10 seconds.")

process.stdin.close()
try:
    process.wait(timeout=10)
except subprocess.TimeoutExpired:
    process.kill()
    process.wait()
remaining_output = process.stdout.read()
stderr = process.stderr.read()
open(stdout_path, "w", encoding="utf-8").write(first_line + remaining_output)
open(stderr_path, "w", encoding="utf-8").write(stderr)
if process.returncode != 0:
    raise SystemExit(f"The extracted MCP server exited with code {process.returncode}.")
PY

"$python_bin" - "$temporary_directory/mcp-stdout.jsonl" <<'PY'
import json
import pathlib
import sys

lines = [line for line in pathlib.Path(sys.argv[1]).read_text(encoding="utf-8").splitlines() if line.strip()]
if len(lines) != 1:
    raise SystemExit(f"Expected exactly one protocol response on stdout, found {len(lines)} line(s).")
payload = json.loads(lines[0])
if payload.get("id") != 1 or "result" not in payload:
    raise SystemExit("MCP initialize did not return a successful JSON-RPC response.")
PY

echo "Verified extracted executable archives for $rid."
