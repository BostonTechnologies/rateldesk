#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
validator="$repository_root/tools/release/validate-release-version.sh"

for tag in v0.1.1 v0.1.1-beta.1 v0.1.1-beta.10 v1.2.3-rc.2 RatelDesk-0.1.1-beta.3 RatelDesk-1.2.3-rc.2; do
  bash "$validator" "$tag" >/dev/null
done

for tag in v01.1.1 v0.01.1 v0.1.01 v0.1.1-beta.01 v0.1.1-01 v0.1.1- v0.1 v0.1.1+build RatelDesk-v0.1.1 RatelDesk-0.01.1 RatelDesk-0.1.1-beta.01 RatelDesk-0.1.1+build; do
  if bash "$validator" "$tag" >/dev/null 2>&1; then
    echo "Validator accepted malformed tag '$tag'." >&2
    exit 1
  fi
done
