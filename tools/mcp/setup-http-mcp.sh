#!/usr/bin/env sh
# The same entrypoint is distributed at the deployment archive root.
set -eu
umask 077
command -v python3 >/dev/null 2>&1 || {
  printf '%s\n' 'Setup not started: Python 3 is required on the Docker host. Install Python 3, then rerun ./setup-http-mcp.sh.' >&2
  exit 78
}
launcher_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec python3 "$launcher_directory/setup-http-mcp.py" "$@"
