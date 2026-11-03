# Artifact size discovery tooling

Campaign: [epic #318](https://github.com/BostonTechnologies/RatelDesk/issues/318).
The [implementation record](../../docs/implementation/size-campaign-20261103.md)
defines scope, evidence interpretation, target issues and follow-up acceptance.

These tools inspect pinned release assets/images and create temporary publish
experiments. They do not publish releases, push images or deploy applications.
The ordinary release workflow and production project settings are unchanged.

## Read-only inventory

Use Python 3, GitHub CLI and the repository's existing authorized connection.
Direct release inventory needs read access to the pinned release. GitHub's
read-only workflow token could not retrieve the draft beta.3 release, so the
hosted campaign explicitly selects its original release workflow artifact instead.
This mode needs `actions: read` and validates the pinned artifact ID, source run,
repository, size and SHA-256 before extracting the exact flat member set.
The token is supplied through the CLI's normal environment; never include it in
command arguments or evidence.

```bash
python3 tools/size-campaign/inventory.py release \
  --release-source workflow-artifact \
  --baseline tools/size-campaign/baseline-0.1.3-beta.3.json \
  --output /tmp/rd-size/release

python3 tools/size-campaign/inventory.py images \
  --baseline tools/size-campaign/baseline-0.1.3-beta.3.json \
  --output /tmp/rd-size/images
```

Workflow-artifact mode verifies the seven original archive files and SHA256SUMS.
It reconstructs the final manifest and detached checksum from the provisional
manifest and pinned image digests, requiring exact matches to both final published
metadata hashes. Each receipt labels those two files `RECONSTRUCTED`; they are
never described as downloaded. The original artifact expires on its own retention
schedule, and an expired or changed artifact fails verification. Omitting
`--release-source` selects direct download of all ten release files instead.

The initial failed direct acquisition and its independently recovered evidence
remain recorded in [#319](https://github.com/BostonTechnologies/RatelDesk/issues/319).

Images require Docker and Buildx. The image tool pulls exact platform digests and
copies `/app` from stopped containers; it does not start an application. It records
both architectures even on an amd64 host. This is file/image inspection, not ARM64
runtime acceptance. Compressed layer totals exclude image configuration, indexes,
SBOM and provenance blobs; the receipts state their exact byte definitions.

## Publish probes

Use the SDK selected by `global.json`. Each profile needs a fresh checkout or
worktree and a fresh output directory. The hosted workflow provides that isolation.
Linux Native AOT requires clang and zlib development libraries.

```bash
python3 tools/size-campaign/publish-probes.py clients \
  --mode baseline --output /tmp/rd-size/clients-baseline
```

Client profiles are `baseline`, `portable-symbols`, `trimmed` and `aot`, initially
on Linux x64. API/Web profiles are `baseline`, `portable-symbols`, `rid-specific`
and `analysis`. HTTP MCP additionally supports `trimmed` and `aot` research.
Each publish command has a ten-minute bound. Timeouts retain their own outcome.

The client experiment preserves the existing package layout and runs
`tools/release/test-executable-archives.sh` on the emitted companion archives.
Portable PDBs are retained separately in the evidence artifact for the
`portable-symbols` profile. Other profiles retain their complete publish payload;
Native AOT `.dbg` bytes are reported as symbols still inside the archive.

Host publish measurements are not image-size measurements. The HTTP MCP
self-contained/AOT research profiles target glibc Linux x64, while the current
shipping image is framework-dependent Alpine/musl. An accepted image requires
a correctly matched runtime/base and full container tests in its own issue.

## Verification and receipts

```bash
python3 -m unittest discover -s tools/size-campaign -p 'test_*.py'
```

Retain JSON reports and full diagnostic logs even when a publish fails. Successful
discovery means the experiment produced evidence. All initial publish reports use
`releaseEligible: false`; a smoke pass does not establish full functional or
trim/AOT acceptance. Unresolved IL2xxx/IL3xxx diagnostics remain explicit.

The separate [campaign workflow](../../.github/workflows/size-campaign.yml) runs on
the named campaign PR when tooling changes, or by explicit manual dispatch. It
does not add a mandatory size matrix to every normal PR. Evidence artifacts are
retained for 30 days; preserve decision metrics and relevant logs in the campaign
record before that period expires.

## Closed schema reflection roots

After changing a catalog request DTO or a nested DTO type, build the untrimmed
stdio application and regenerate its explicit property/enum registry. The SDK
tool inventories compiled catalog roots and their nested serialized fields; it
never starts a host or contacts a backend. Use the owning application's complete
output so the inventory can resolve its dependencies.

```bash
dotnet build src/Helpdesk.Mcp/Helpdesk.Mcp.csproj -c Release
python3 tools/size-campaign/generate-schema-types.py \
  --assembly src/Helpdesk.Mcp/bin/Release/net10.0/Helpdesk.Mcp.Core.dll \
  --work-directory /tmp/rd-size-schema-types
```

Review the generated diff and rerun schema/golden and emitted-binary catalog
acceptance. An unregistered type fails explicitly; new fields must not disappear
from an optimized catalog. The registry preserves the existing reflection and
attribute behavior for the complete compiled DTO graph.
