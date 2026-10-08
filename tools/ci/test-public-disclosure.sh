#!/usr/bin/env bash
set -euo pipefail

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
test_root="$(mktemp -d)"
cleanup() { find "$test_root" -depth -delete 2>/dev/null || true; }
trap cleanup EXIT

new_fixture() {
  local fixture="$test_root/$1"
  mkdir -p "$fixture/tools/ci"
  cp "$source_root/tools/ci/check-public-disclosure.sh" "$fixture/tools/ci/check-public-disclosure.sh"
  git -C "$fixture" init --quiet
  printf '# Synthetic public fixture\n' > "$fixture/README.md"
  git -C "$fixture" add README.md
  printf '%s' "$fixture"
}

expect_rejected() {
  local fixture="$1"
  if (cd "$fixture" && bash tools/ci/check-public-disclosure.sh) >/dev/null 2>&1; then
    echo "Public disclosure gate accepted a private or near-match synthetic reference." >&2
    exit 1
  fi
}

accepted="$(new_fixture accepted)"
printf '%s\n' \
  'bostec.service-link.v1' \
  'bostec.service-link.incident-only.v1' \
  'bostec-service-link.incident-only.v1' \
  'bostec-service-link.incident-only.v1.md' \
  'bostec-service-link.incident-only.v1.json' \
  'bostec-service-link.incident-only.v1.fixtures.json' \
  'bostec-service-link.incident-only.v1.SHA256SUMS' >> "$accepted/README.md"
printf '\0%s\0%s\0' 'bostec.service-link.incident-only.v1' 'bostec-service-link.incident-only.v1.json' > "$accepted/public.bin"
git -C "$accepted" add README.md public.bin
(cd "$accepted" && bash tools/ci/check-public-disclosure.sh) >/dev/null

near_capability="$(new_fixture near-capability)"
printf '%s%s\n' 'bos' 'tec.service-link.incident-only.v10' >> "$near_capability/README.md"
git -C "$near_capability" add README.md
expect_rejected "$near_capability"

near_contract="$(new_fixture near-contract)"
printf '%s%s\n' 'bos' 'tec-service-link.incident-only.v1.private.json' >> "$near_contract/README.md"
git -C "$near_contract" add README.md
expect_rejected "$near_contract"

private_reference="$(new_fixture private-reference)"
printf '%s%s\n' 'bos' 'tec-private-infrastructure' >> "$private_reference/README.md"
git -C "$private_reference" add README.md
expect_rejected "$private_reference"

near_binary="$(new_fixture near-binary)"
printf '\0%s%s\0' 'bos' 'tec.service-link.incident-only.v1-internal' > "$near_binary/private.bin"
git -C "$near_binary" add private.bin
expect_rejected "$near_binary"

echo "Public disclosure gate synthetic tests passed."
