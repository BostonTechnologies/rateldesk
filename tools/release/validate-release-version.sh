#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <vMAJOR.MINOR.PATCH[-prerelease]|RatelDesk-MAJOR.MINOR.PATCH[-prerelease]>" >&2
  exit 64
fi

tag="$1"
case "$tag" in
  v*) version="${tag#v}" ;;
  RatelDesk-*) version="${tag#RatelDesk-}" ;;
  *)
    echo "Tag '$tag' must start with 'v' or 'RatelDesk-'." >&2
    exit 1
    ;;
esac

python3 - "$tag" "$version" <<'PY'
import re
import sys

tag, version = sys.argv[1:]
identifier = r"(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)"
pattern = re.compile(
    rf"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-({identifier}(?:\.{identifier})*))?$")
match = pattern.fullmatch(version)
if match is None:
    raise SystemExit(f"Tag '{tag}' contains an unsupported SemVer release version.")

print(f"version={version}")
print(f"prerelease={'true' if match.group(4) else 'false'}")
PY
