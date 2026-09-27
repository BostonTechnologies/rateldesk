#!/usr/bin/env bash
set -euo pipefail

# Update release metadata without changing whether the release is a draft.
if [[ $# -ne 4 ]]; then
  echo "usage: $0 <tag> <version> <prerelease:true|false> <release-notes>" >&2
  exit 64
fi

tag="$1"
version="$2"
prerelease="$3"
release_notes="$4"
gh_bin="${GH_BIN:-gh}"

if [[ "$prerelease" != true && "$prerelease" != false ]]; then
  echo "Prerelease value must be true or false: $prerelease" >&2
  exit 64
fi

"$gh_bin" release edit "$tag" \
  --title "RatelDesk $version" \
  "--prerelease=$prerelease" \
  --notes "$release_notes"
