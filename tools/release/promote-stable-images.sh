#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <validated-promotion-plan.json>" >&2
  exit 64
fi

plan="$1"
docker_bin="${DOCKER_BIN:-docker}"
promoted=()

while IFS=$'\t' read -r image latest digest; do
  if ! "$docker_bin" buildx imagetools create --tag "$latest" "$image@$digest"; then
    printf 'Partial promotion; already updated: %s. Re-run this exact tag after correcting registry access; do not roll latest backwards.\n' "${promoted[*]:-none}" >&2
    exit 1
  fi
  updated=("${promoted[@]}" "$latest")
  if ! actual="$("$docker_bin" buildx imagetools inspect "$latest" --format '{{.Manifest.Digest}}')"; then
    printf 'Partial promotion; already updated: %s. Read-back of %s failed. Re-run this exact tag after investigating.\n' "${updated[*]}" "$latest" >&2
    exit 1
  fi
  if [[ "$actual" != "$digest" ]]; then
    printf 'Partial promotion; already updated: %s. %s read back as %s, expected %s. Re-run this exact tag after investigating.\n' "${updated[*]}" "$latest" "$actual" "$digest" >&2
    exit 1
  fi
  promoted=("${updated[@]}")
done < <(jq -r '.images[] | [.image, .latest, .digest] | @tsv' "$plan")
