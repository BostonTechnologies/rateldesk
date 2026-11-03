#!/usr/bin/env bash

set -euo pipefail

for dependency in git rg file strings; do
  if ! command -v "$dependency" >/dev/null 2>&1; then
    printf 'public disclosure gate: required tool is missing: %s\n' "$dependency" >&2
    exit 1
  fi
done

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

# NetRatel and the exact public pairing/historical protocol identifiers are
# intentional public integration terms. Other organization, infrastructure,
# and operator identifiers remain blocked.
blocked_text='boston|bostec|proxicon|komodo|openbao|spacetimeorchestrator|camelot|konrad|jeremi|hd-dev|@boston\.net\.za'
blocked_files='(^|/)(\.env|appsettings\.Development\.local\.json)$|\.(pfx|pem|key)$|(^|/)(id_rsa|id_ed25519)$'

failed=false

# Repository skills are public instructions. Scan their hidden descendants too,
# including tracked files matched by ignore rules, without exposing other hidden
# workflow directories through a broader allowlist.
scan_public_files() {
  rg "$@" . || true
  if [[ -d .agents/skills ]]; then
    rg --hidden --no-ignore "$@" .agents/skills || true
  fi
}

disallowed_matches=$(scan_public_files -n -i -e "$blocked_text" \
  --glob '!LICENSE' \
  --glob '!tools/ci/check-public-disclosure.sh' \
  --glob '!**/bin/**' \
  --glob '!**/obj/**' \
  | sed \
    -e 's#https://github.com/BostonTechnologies/RatelDesk##g' \
    -e 's#ghcr.io/bostontechnologies/rateldesk-web##g' \
    -e 's#ghcr.io/bostontechnologies/rateldesk-api##g' \
    -e 's#ghcr.io/bostontechnologies/rateldesk-mcp-http##g' \
    -e 's#orgs/BostonTechnologies/packages/container##g' \
    -e 's#BostonTechnologies/RatelDesk##g' \
    -e 's#\(^\|[^[:alnum:]_.-]\)bostec\.pairing\.v1\($\|[^[:alnum:]_.-]\)#\1\2#g' \
    -e 's#\(^\|[^[:alnum:]_.-]\)bostec-pairing\.v1\.md\($\|[^[:alnum:]_.-]\)#\1\2#g' \
    -e 's#bostec\.service-link\.hash\.v1##g' \
    -e 's#\(^\|[^[:alnum:]_.-]\)bostec\.service-link\.incident-only\.v1\($\|[^[:alnum:]_.-]\)#\1\2#g' \
    -e 's#\(^\|[^[:alnum:]_.-]\)bostec-service-link\.incident-only\.v1\(\.fixtures\.json\|\.SHA256SUMS\|\.json\|\.md\)\{0,1\}\($\|[^[:alnum:]_.-]\)#\1\3#g' \
    -e 's#bostec\.service-link\.v1##g' \
    -e 's#bostec\.service-link\.control##g' \
    -e 's#bostec\.service-link\.verify##g' \
    -e 's#bostec-service-link\.v1##g' \
  | rg -n -i -e "$blocked_text" || true)

if [[ -n "$disallowed_matches" ]]; then
  printf '%s\n' "$disallowed_matches"
  printf 'public disclosure gate: private organization or system reference found\n' >&2
  failed=true
fi

if scan_public_files --files -g '!**/bin/**' -g '!**/obj/**' | rg -n -i "$blocked_files"; then
  printf 'public disclosure gate: prohibited secret-bearing file is tracked\n' >&2
  failed=true
fi

while IFS= read -r candidate_file; do
  [[ -f "$candidate_file" ]] || continue
  mime_type=$(file --brief --mime-type "$candidate_file")
  [[ "$mime_type" == text/* || "$mime_type" == application/json || "$mime_type" == application/xml ]] && continue

  if strings -a "$candidate_file" | sed \
      -e 's#\(^\|[^[:alnum:]_.-]\)bostec\.pairing\.v1\($\|[^[:alnum:]_.-]\)#\1\2#g' \
    -e 's#\(^\|[^[:alnum:]_.-]\)bostec-pairing\.v1\.md\($\|[^[:alnum:]_.-]\)#\1\2#g' \
    -e 's#bostec\.service-link\.hash\.v1##g' \
      -e 's#\(^\|[^[:alnum:]_.-]\)bostec\.service-link\.incident-only\.v1\($\|[^[:alnum:]_.-]\)#\1\2#g' \
      -e 's#\(^\|[^[:alnum:]_.-]\)bostec-service-link\.incident-only\.v1\(\.fixtures\.json\|\.SHA256SUMS\|\.json\|\.md\)\{0,1\}\($\|[^[:alnum:]_.-]\)#\1\3#g' \
      -e 's#bostec\.service-link\.v1##g' \
      -e 's#bostec\.service-link\.control##g' \
      -e 's#bostec\.service-link\.verify##g' \
      -e 's#bostec-service-link\.v1##g' \
      | rg -n -i -e "$blocked_text"; then
    printf 'public disclosure gate: private reference found in binary content: %s\n' "$candidate_file" >&2
    failed=true
  fi
done < <(git ls-files -co --exclude-standard)

# The canonical AGENTS.md and .agents/skills/ are intentionally public repository
# guidance. Both pass through the disclosure checks above. All other .agents
# material remains private, including hidden files and similarly named folders.
while IFS= read -r -d '' candidate_file; do
  case "$candidate_file" in
    .agents/skills/*) ;;
    .agents|.agents/*|.codex|.codex/*|openspec|openspec/*|AGENTS_Old.md)
      printf 'public disclosure gate: private workflow material is tracked at %s\n' "$candidate_file" >&2
      failed=true
      ;;
  esac
done < <(git ls-files -co --exclude-standard -z)

if [[ "$failed" == true ]]; then
  exit 1
fi

printf 'public disclosure gate passed\n'
