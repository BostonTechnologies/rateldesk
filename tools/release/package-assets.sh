#!/usr/bin/env bash
set -euo pipefail

# Builds the release payload used by tag workflows and local release rehearsal.
# It deliberately never tags, pushes, or publishes anything.
if [[ $# -ne 3 ]]; then
  echo "usage: $0 <version-without-v> <source-revision> <output-directory>" >&2
  exit 64
fi

version="$1"
source_revision="$2"
output_directory="$3"
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
mkdir -p "$output_directory"
output_directory="$(cd "$output_directory" && pwd)"
staging_directory="$output_directory/staging"
mkdir -p "$staging_directory"

declare -a rids=(linux-x64 linux-arm64 win-x64)

package_tool() {
  local project="$1" tool="$2" archive_prefix="$3" rid="$4"
  local publish_directory="$staging_directory/${tool}-${rid}"
  local archive_base="${archive_prefix}-${version}-${rid}"
  local package_directory="$staging_directory/$archive_base"
  rm -rf "$publish_directory" "$package_directory"
  dotnet restore "$project" --runtime "$rid"
  dotnet publish "$project" --configuration Release --runtime "$rid" --self-contained true \
    --output "$publish_directory" --no-restore \
    -p:Version="$version" -p:SourceRevisionId="$source_revision"
  mkdir -p "$package_directory"
  cp -a "$publish_directory/." "$package_directory/"
  cp "$repository_root/LICENSE" "$package_directory/LICENSE"
  python3 "$repository_root/tools/release/package-documentation.py" "$package_directory"
  cat > "$package_directory/README.txt" <<EOF
RatelDesk $tool $version

Run ./$tool --help or ./$tool --version after extracting. Run ./$tool setup explicitly from an interactive terminal for client onboarding. Linux archives require a glibc-based distribution; they are not Alpine/musl builds. Configure credentials in a protected file or environment variable, never on a command line.

Canonical configuration/setup guidance is bundled in docs/integrations/cli-stdio.md and docs/mcp-http/. Third-party notices and licenses are included alongside LICENSE.
EOF
  if [[ "$rid" == win-* ]]; then
    (cd "$staging_directory" && zip -qr "$output_directory/$archive_base.zip" "$archive_base")
  else
    tar -C "$staging_directory" -czf "$output_directory/$archive_base.tar.gz" "$archive_base"
  fi
}

for rid in "${rids[@]}"; do
  package_tool "$repository_root/src/Helpdesk.Cli/Helpdesk.Cli.csproj" rateldesk rateldesk-cli "$rid"
  package_tool "$repository_root/src/Helpdesk.Mcp/Helpdesk.Mcp.csproj" rateldesk-mcp rateldesk-mcp-stdio "$rid"
done

deployment_directory="$staging_directory/rateldesk-deployment-$version"
rm -rf "$deployment_directory"
mkdir -p "$deployment_directory"
cp -a "$repository_root/docker/." "$deployment_directory/"
cp "$repository_root/LICENSE" "$deployment_directory/LICENSE"
python3 "$repository_root/tools/release/package-documentation.py" "$deployment_directory" --flat-deployment
tar -C "$staging_directory" -czf "$output_directory/rateldesk-deployment-$version.tar.gz" "rateldesk-deployment-$version"

(cd "$output_directory" && find . -maxdepth 1 -type f \( -name '*.zip' -o -name '*.tar.gz' \) -printf '%f\n' | sort | xargs -r sha256sum > SHA256SUMS)
python3 - "$output_directory" "$version" "$source_revision" <<'PY'
import hashlib, json, pathlib, sys
directory = pathlib.Path(sys.argv[1])
hashes = {}
for line in (directory / "SHA256SUMS").read_text().splitlines():
    digest, name = line.split(maxsplit=1)
    hashes[name.strip()] = digest
(directory / "release-manifest.json").write_text(json.dumps({
    "version": sys.argv[2], "sourceRevision": sys.argv[3],
    "supportedRids": ["linux-x64", "linux-arm64", "win-x64"],
    "assets": hashes,
    "containers": [
        f"ghcr.io/bostontechnologies/rateldesk-web:{sys.argv[2]}",
        f"ghcr.io/bostontechnologies/rateldesk-api:{sys.argv[2]}",
        f"ghcr.io/bostontechnologies/rateldesk-mcp-http:{sys.argv[2]}"
    ]
}, indent=2) + "\n")
PY
