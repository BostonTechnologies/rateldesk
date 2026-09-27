#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
temporary_directory="$(mktemp -d)"
trap 'rm -rf "$temporary_directory"' EXIT
assets="$temporary_directory/assets"
fake_gh_root="$temporary_directory/fake-gh"
mkdir -p "$assets" "$fake_gh_root/assets"

version="0.0.0-test"
tag="v$version"
declare -a rids=(linux-x64 linux-arm64 win-x64)
declare -a archives=()
for rid in "${rids[@]}"; do
  suffix=tar.gz
  [[ "$rid" == win-x64 ]] && suffix=zip
  archives+=("rateldesk-cli-${version}-${rid}.${suffix}" "rateldesk-mcp-stdio-${version}-${rid}.${suffix}")
done
archives+=("rateldesk-deployment-${version}.tar.gz")
expected_asset_count=$((${#archives[@]} + 3)) # SHA256SUMS and the manifest plus its detached checksum
for asset in "${archives[@]}"; do printf '%s\n' "$asset" > "$assets/$asset"; done
(cd "$assets" && printf '%s\n' "${archives[@]}" | sort | xargs sha256sum > SHA256SUMS)
"$repository_root/tools/release/prepare-release-manifest.sh" "$version" deadbeef "$assets" sha256:web sha256:api sha256:mcp "$tag"

cat > "$temporary_directory/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
root="${FAKE_GH_ROOT:?}"
if [[ "$1 $2" == "release view" ]]; then
  [[ -f "$root/release" ]] || exit 1
  python3 - "$root" <<'PY'
import json, pathlib, sys
root = pathlib.Path(sys.argv[1])
state_path = root / "state.json"
state = json.loads(state_path.read_text()) if state_path.exists() else {"isDraft": True}
state["assets"] = [{"name": item.name} for item in sorted((root / "assets").iterdir())]
print(json.dumps(state))
PY
elif [[ "$1 $2" == "release create" ]]; then
  touch "$root/release"
  shift 2
  draft=false
  while (($#)); do
    if [[ "$1" == --draft ]]; then draft=true; fi
    shift
  done
  printf '{"isDraft":%s,"isPrerelease":false}\n' "$draft" > "$root/state.json"
elif [[ "$1 $2" == "release upload" ]]; then
  [[ "${FAKE_GH_FAIL_UPLOAD:-false}" != true ]] || { echo "Synthetic upload failure" >&2; exit 1; }
  cp "$4" "$root/assets/$(basename "$4")"
elif [[ "$1 $2" == "release download" ]]; then
  pattern=""
  directory=""
  for ((index=1; index <= $#; index++)); do
    argument="${!index}"
    if [[ "$argument" == --pattern ]]; then next=$((index + 1)); pattern="${!next}"; fi
    if [[ "$argument" == --dir ]]; then next=$((index + 1)); directory="${!next}"; fi
  done
  mkdir -p "$directory"
  cp "$root/assets/$pattern" "$directory/$pattern"
elif [[ "$1 $2" == "release edit" ]]; then
  shift 2
  release_tag="$1"
  shift
  python3 - "$root" "$release_tag" "$@" <<'PY'
import json, pathlib, sys
root = pathlib.Path(sys.argv[1])
args = sys.argv[3:]
state_path = root / "state.json"
state = json.loads(state_path.read_text())
index = 0
while index < len(args):
    argument = args[index]
    if argument == "--title":
        state["title"] = args[index + 1]
        index += 2
    elif argument == "--notes":
        state["notes"] = args[index + 1]
        index += 2
    elif argument.startswith("--prerelease="):
        state["isPrerelease"] = argument.split("=", 1)[1] == "true"
        index += 1
    elif argument == "--draft":
        state["isDraft"] = True
        index += 1
    elif argument.startswith("--draft="):
        state["isDraft"] = argument.split("=", 1)[1] == "true"
        index += 1
    else:
        raise SystemExit(f"Unexpected fake gh release edit argument: {argument}")
state_path.write_text(json.dumps(state) + "\n")
PY
else
  echo "Unexpected fake gh command: $*" >&2
  exit 64
fi
EOF
chmod 700 "$temporary_directory/gh"

assert_release_state() {
  local root="$1"
  local expected_draft="$2"
  local expected_prerelease="$3"
  local expected_title="$4"
  local expected_notes="$5"
  python3 - "$root/state.json" "$expected_draft" "$expected_prerelease" "$expected_title" "$expected_notes" <<'PY'
import json, pathlib, sys
state = json.loads(pathlib.Path(sys.argv[1]).read_text())
expected_draft = sys.argv[2] == "true"
expected_prerelease = sys.argv[3] == "true"
assert state.get("isDraft") is expected_draft, state
assert state.get("isPrerelease", False) is expected_prerelease, state
if sys.argv[4]:
    assert state.get("title") == sys.argv[4], state
if sys.argv[5]:
    assert state.get("notes") == sys.argv[5], state
PY
}

RELEASE_ASSET_DRY_RUN=true "$repository_root/tools/release/publish-release-assets.sh" "$tag" "$version" "$assets" > "$temporary_directory/dry-run"
test "$(wc -l < "$temporary_directory/dry-run")" = "$expected_asset_count"

# Missing assets are rejected before any release command is run.
mv "$assets/${archives[0]}" "$temporary_directory/missing-asset"
if RELEASE_ASSET_DRY_RUN=true "$repository_root/tools/release/publish-release-assets.sh" "$tag" "$version" "$assets"; then
  echo "Missing release asset unexpectedly succeeded." >&2
  exit 1
fi
mv "$temporary_directory/missing-asset" "$assets/${archives[0]}"

# The first run creates a draft and uploads the complete payload.
FAKE_GH_ROOT="$fake_gh_root" GH_BIN="$temporary_directory/gh" "$repository_root/tools/release/publish-release-assets.sh" "$tag" "$version" "$assets"
test "$(find "$fake_gh_root/assets" -maxdepth 1 -type f | wc -l)" = "$expected_asset_count"
assert_release_state "$fake_gh_root" true false "" ""

# A complete rerun reuses verified assets, while differing content is rejected.
FAKE_GH_ROOT="$fake_gh_root" GH_BIN="$temporary_directory/gh" "$repository_root/tools/release/publish-release-assets.sh" "$tag" "$version" "$assets"
draft_notes=$'Release notes for a test draft.\nValidated payload details.'
FAKE_GH_ROOT="$fake_gh_root" GH_BIN="$temporary_directory/gh" "$repository_root/tools/release/update-release-metadata.sh" "$tag" "$version" true "$draft_notes"
assert_release_state "$fake_gh_root" true true "RatelDesk $version" "$draft_notes"

# A failed upload leaves a resumable draft; a later run verifies its matching
# asset and uploads the remainder.
partial_root="$temporary_directory/partial-gh"
mkdir -p "$partial_root/assets"
touch "$partial_root/release"
printf '%s\n' '{"isDraft":true,"isPrerelease":false}' > "$partial_root/state.json"
cp "$assets/${archives[0]}" "$partial_root/assets/${archives[0]}"
if FAKE_GH_ROOT="$partial_root" FAKE_GH_FAIL_UPLOAD=true GH_BIN="$temporary_directory/gh" "$repository_root/tools/release/publish-release-assets.sh" "$tag" "$version" "$assets"; then
  echo "Upload failure unexpectedly succeeded." >&2
  exit 1
fi
FAKE_GH_ROOT="$partial_root" GH_BIN="$temporary_directory/gh" "$repository_root/tools/release/publish-release-assets.sh" "$tag" "$version" "$assets"
test "$(find "$partial_root/assets" -maxdepth 1 -type f | wc -l)" = "$expected_asset_count"
assert_release_state "$partial_root" true false "" ""

# A manually published empty release should accept verified assets, then update
# stable metadata while remaining published. This reproduces the beta.4 failure.
published_root="$temporary_directory/published-gh"
mkdir -p "$published_root/assets"
touch "$published_root/release"
printf '%s\n' '{"isDraft":false,"isPrerelease":true}' > "$published_root/state.json"
FAKE_GH_ROOT="$published_root" GH_BIN="$temporary_directory/gh" "$repository_root/tools/release/publish-release-assets.sh" "$tag" "$version" "$assets"
test "$(find "$published_root/assets" -maxdepth 1 -type f | wc -l)" = "$expected_asset_count"
assert_release_state "$published_root" false true "" ""
published_notes=$'Stable release notes for a test.\nExisting publication state must stay published.'
FAKE_GH_ROOT="$published_root" GH_BIN="$temporary_directory/gh" "$repository_root/tools/release/update-release-metadata.sh" "$tag" "$version" false "$published_notes"
assert_release_state "$published_root" false false "RatelDesk $version" "$published_notes"

# Published partial and complete releases resume idempotently, and unrelated
# user assets remain untouched without blocking required asset completion.
published_partial_root="$temporary_directory/published-partial-gh"
mkdir -p "$published_partial_root/assets"
touch "$published_partial_root/release"
printf '%s\n' '{"isDraft":false,"isPrerelease":false}' > "$published_partial_root/state.json"
cp "$assets/${archives[0]}" "$published_partial_root/assets/${archives[0]}"
printf '%s\n' "User attachment" > "$published_partial_root/assets/notes.txt"
if FAKE_GH_ROOT="$published_partial_root" FAKE_GH_FAIL_UPLOAD=true GH_BIN="$temporary_directory/gh" "$repository_root/tools/release/publish-release-assets.sh" "$tag" "$version" "$assets" > "$temporary_directory/published-upload-failure.log" 2>&1; then
  echo "Synthetic published partial upload unexpectedly succeeded." >&2
  exit 1
fi
if grep -Eq 'not a draft|refusing to modify' "$temporary_directory/published-upload-failure.log"; then
  cat "$temporary_directory/published-upload-failure.log" >&2
  echo "Published partial release was incorrectly rejected before upload." >&2
  exit 1
fi
grep -q 'Synthetic upload failure' "$temporary_directory/published-upload-failure.log"
assert_release_state "$published_partial_root" false false "" ""
FAKE_GH_ROOT="$published_partial_root" GH_BIN="$temporary_directory/gh" "$repository_root/tools/release/publish-release-assets.sh" "$tag" "$version" "$assets"
test "$(find "$published_partial_root/assets" -maxdepth 1 -type f | wc -l)" = "$((expected_asset_count + 1))"
test "$(cat "$published_partial_root/assets/notes.txt")" = "User attachment"
assert_release_state "$published_partial_root" false false "" ""
FAKE_GH_ROOT="$published_partial_root" GH_BIN="$temporary_directory/gh" "$repository_root/tools/release/publish-release-assets.sh" "$tag" "$version" "$assets"
test "$(find "$published_partial_root/assets" -maxdepth 1 -type f | wc -l)" = "$((expected_asset_count + 1))"
published_prerelease_notes=$'Beta release notes for a test.\nPrerelease metadata is updated explicitly.'
FAKE_GH_ROOT="$published_partial_root" GH_BIN="$temporary_directory/gh" "$repository_root/tools/release/update-release-metadata.sh" "$tag" "$version" true "$published_prerelease_notes"
assert_release_state "$published_partial_root" false true "RatelDesk $version" "$published_prerelease_notes"

cp "$fake_gh_root/assets/${archives[0]}" "$temporary_directory/original-conflicting-asset"
printf '%s\n' changed > "$assets/${archives[0]}"
(cd "$assets" && printf '%s\n' "${archives[@]}" | sort | xargs sha256sum > SHA256SUMS)
"$repository_root/tools/release/prepare-release-manifest.sh" "$version" deadbeef "$assets" sha256:web sha256:api sha256:mcp "$tag"
if FAKE_GH_ROOT="$fake_gh_root" GH_BIN="$temporary_directory/gh" "$repository_root/tools/release/publish-release-assets.sh" "$tag" "$version" "$assets" > "$temporary_directory/conflict.log" 2>&1; then
  echo "Conflicting release asset unexpectedly succeeded." >&2
  exit 1
fi
grep -Fq "Existing release asset '${archives[0]}' conflicts with the validated payload." "$temporary_directory/conflict.log"
cmp --silent "$temporary_directory/original-conflicting-asset" "$fake_gh_root/assets/${archives[0]}"
