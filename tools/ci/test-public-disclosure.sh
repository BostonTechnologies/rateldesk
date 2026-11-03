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
  'bostec.pairing.v1' \
  'bostec-pairing.v1.md' \
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

public_agents="$(new_fixture public-agents)"
printf '# Repository instructions\nKeep documentation in categorized guides.\n' > "$public_agents/AGENTS.md"
git -C "$public_agents" add AGENTS.md
(cd "$public_agents" && bash tools/ci/check-public-disclosure.sh) >/dev/null

public_skills="$(new_fixture public-skills)"
mkdir -p "$public_skills/.agents/skills/example/.references"
printf '%s\n' '---' 'name: example' 'description: Public development guidance.' '---' '# Public skill' > "$public_skills/.agents/skills/example/SKILL.md"
printf '# Public reference\n' > "$public_skills/.agents/skills/example/.references/guide.md"
git -C "$public_skills" add .agents
(cd "$public_skills" && bash tools/ci/check-public-disclosure.sh) >/dev/null

for skill_reference in SKILL.md .references/private.md; do
  private_skill="$(new_fixture "private-skill-$skill_reference")"
  mkdir -p "$(dirname "$private_skill/.agents/skills/example/$skill_reference")"
  printf '# Skill reference\n%s%s\n' 'bos' 'tec-private-infrastructure' > "$private_skill/.agents/skills/example/$skill_reference"
  git -C "$private_skill" add .agents
  expect_rejected "$private_skill"
done

ignored_skill="$(new_fixture ignored-skill)"
mkdir -p "$ignored_skill/.agents/skills/example/.references"
printf '.references/\n' > "$ignored_skill/.gitignore"
printf '%s%s\n' 'bos' 'tec-private-infrastructure' > "$ignored_skill/.agents/skills/example/.references/private.md"
git -C "$ignored_skill" add -f .agents
expect_rejected "$ignored_skill"

for secret_name in .env .references/id_ed25519; do
  secret_skill="$(new_fixture "secret-skill-$secret_name")"
  mkdir -p "$(dirname "$secret_skill/.agents/skills/example/$secret_name")"
  printf 'synthetic-secret-fixture\n' > "$secret_skill/.agents/skills/example/$secret_name"
  git -C "$secret_skill" add .agents
  expect_rejected "$secret_skill"
done

for private_path in \
  .agents/workflow.md \
  .agents/.private/instructions.md \
  .agents/skills-private/SKILL.md \
  .codex/config.toml \
  openspec/.private/proposal.md; do
  private_directory="$(new_fixture "private-directory-$private_path")"
  mkdir -p "$(dirname "$private_directory/$private_path")"
  printf '# Private workflow fixture\n' > "$private_directory/$private_path"
  git -C "$private_directory" add -- "$private_path"
  expect_rejected "$private_directory"
done

private_agents="$(new_fixture private-agents)"
printf '# Repository instructions\n%s%s\n' 'bos' 'tec-private-infrastructure' > "$private_agents/AGENTS.md"
git -C "$private_agents" add AGENTS.md
expect_rejected "$private_agents"

private_workflow="$(new_fixture private-workflow)"
printf '# Retired private workflow\n' > "$private_workflow/AGENTS_Old.md"
git -C "$private_workflow" add AGENTS_Old.md
expect_rejected "$private_workflow"

near_pairing="$(new_fixture near-pairing)"
printf '%s%s\n' 'bos' 'tec.pairing.v10' >> "$near_pairing/README.md"
git -C "$near_pairing" add README.md
expect_rejected "$near_pairing"

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
