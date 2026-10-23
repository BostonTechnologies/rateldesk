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
  cp -a "$repository_root/tools/mcp/." "$package_directory/"
  find "$package_directory" -type d -name __pycache__ -prune -exec rm -rf {} +
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
cp -a "$repository_root/tools/mcp/." "$deployment_directory/"
find "$deployment_directory" -type d -name __pycache__ -prune -exec rm -rf {} +
chmod 0755 "$deployment_directory/setup-http-mcp.sh" "$deployment_directory/setup-http-mcp.py"
cp "$repository_root/LICENSE" "$deployment_directory/LICENSE"
python3 "$repository_root/tools/release/package-documentation.py" "$deployment_directory" --flat-deployment
python3 - "$deployment_directory/setup-release.json" "$version" <<'PY'
import json, pathlib, sys
pathlib.Path(sys.argv[1]).write_text(json.dumps({
    "schemaVersion": 1,
    "version": sys.argv[2],
    "composeFile": "docker-compose.release.yml",
    "projectName": "rateldesk",
    "manager": "release-compose",
    "image": f"ghcr.io/bostontechnologies/rateldesk-mcp-http:{sys.argv[2]}",
}, indent=2) + "\n", encoding="utf-8")
PY
cat > "$deployment_directory/README.txt" <<EOF
RatelDesk deployment $version

From this extracted directory on the Docker host, run ./setup-http-mcp.sh
with --native-executable pointing to the verified matching rateldesk or
rateldesk-mcp apphost (a colocated native executable is selected automatically).
The adjacent helpers require Python 3; Docker with Compose is required.
Default Verify existing deployment records a reviewed baseline unchanged:
no source import, new service/mount, resource creation or restart.
For a necessary correction use --intent prepare-changes, review the readable
minimal source diff/service counts, then save/deploy through its existing owner.
After adoption or separately after approved owner apply, use the exact printed
NEW --verify invocation for temporary status consent and source/runtime checks.
If Compose lacks a required capability, explicitly select an integrity-checked
isolated binary or consent to --prepare-compose PRIVATE_DIRECTORY for the pinned
setup-only tool; the normal plugin and Docker context remain unchanged.
Optional ordinary client setup: ./setup-http-mcp.sh --target http-client -- --profile incident-reader
Keep the same selected DOCKER_CONFIG/context/host/TLS settings for every command.
Changed sources/env/images require re-read and new review; do not edit protected
receipts or manually repair an approved candidate. Four services remain four
by default; an initializer is a separate Advanced topology choice.
Follow docs/mcp-http/setup.md for the short guide
and docs/mcp-http/deployment.md for alternate contexts and advanced configuration.

Compose files are at this directory's root. Keep existing volumes, API state,
ingress and deployment-manager ownership. Use exact image tags or manifest
digests; beta publication never promotes latest. Third-party notices/licenses
are included in licenses/.
EOF
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
