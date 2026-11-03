# RatelDesk artifact size campaign — 3 November 2026

Tracking: [epic #318](https://github.com/BostonTechnologies/RatelDesk/issues/318).
Campaign ID: `rd-size-20261103`.

## Scope and starting evidence

Review source is `59248cacce44ad33a1c3367f0eb000e8705e072b`. The reference assets are
`0.1.3-beta.3`, built from `6a2be88000a7bcea3e70369a26413daee2a430c3`. Keep those
identities separate. The checked-in [baseline](../../tools/size-campaign/baseline-0.1.3-beta.3.json)
records all ten asset IDs, exact byte sizes and hashes, plus the three image index
digests. Initial GitHub metadata reports the release as a draft. This campaign
does not change its publication state.

The [historical release run](https://github.com/BostonTechnologies/RatelDesk/actions/runs/41618847344)
passed release validation and extracted archive runtime jobs on Linux x64,
Linux ARM64 and Windows x64. Those tests describe the unoptimized reference.
Fresh inventory verifies downloaded bytes; each experiment must test its own
emitted executable or image before it can be adopted.

## Target register

| Target | Tracking | Initial direction |
| --- | --- | --- |
| Measurements, manifests and checksums | [#319](https://github.com/BostonTechnologies/RatelDesk/issues/319) | Exact bytes, layer/file inventory, commands and compatibility receipts |
| Shared dependency/serialization work | [#320](https://github.com/BostonTechnologies/RatelDesk/issues/320) | Attribute framework/package payload; own reflection and JSON work |
| CLI Linux x64 | [#321](https://github.com/BostonTechnologies/RatelDesk/issues/321) | First client pilot: JIT, symbols, trim, Native AOT |
| CLI Linux ARM64 | [#323](https://github.com/BostonTechnologies/RatelDesk/issues/323) | Reuse the pilot changes; build/run on native ARM64 |
| CLI Windows x64 | [#324](https://github.com/BostonTechnologies/RatelDesk/issues/324) | Native Windows toolchain, storage ACL and runtime checks |
| Stdio MCP Linux x64 | [#322](https://github.com/BostonTechnologies/RatelDesk/issues/322) | First stdio pilot with exact discovery and stdout contracts |
| Stdio MCP Linux ARM64 | [#325](https://github.com/BostonTechnologies/RatelDesk/issues/325) | Native ARM64 acceptance of the selected profile |
| Stdio MCP Windows x64 | [#326](https://github.com/BostonTechnologies/RatelDesk/issues/326) | Native Windows acceptance and privacy checks |
| Deployment archive | [#327](https://github.com/BostonTechnologies/RatelDesk/issues/327) | Packaging/duplicate-content/compression review; AOT not applicable |
| HTTP MCP image | [#328](https://github.com/BostonTechnologies/RatelDesk/issues/328) | FDD payload first, then separate trim/AOT research |
| Web image | [#329](https://github.com/BostonTechnologies/RatelDesk/issues/329) | Supported JIT payload/static assets/runtime image improvements |
| API image | [#330](https://github.com/BostonTechnologies/RatelDesk/issues/330) | Supported glibc payload first; isolated trim/AOT feasibility work |

Each image issue owns both Linux amd64 and arm64. Each client archive has its own
outcome even when shared code changes fix several targets.

## Findings that determine the experiments

The existing [archive packager](../../tools/release/package-assets.sh) publishes
the CLI and stdio self-contained, then bundles shared documentation, deployment
collateral and setup helpers. Their common size therefore has multiple causes.
Measure runtime and package files separately from bundled content.

The client dependency graph includes broad ASP.NET framework references through
[AgentClient](../../src/Helpdesk.AgentClient/Helpdesk.AgentClient.csproj),
[Setup](../../src/Helpdesk.Setup/Helpdesk.Setup.csproj) and
[stdio](../../src/Helpdesk.Mcp/Helpdesk.Mcp.csproj). Shared also references Npgsql.
Investigate the restored graph and actual type usage before removing anything.
Setup's expected MCP schemas are currently obtained by constructing the reflected
tool catalog. A common plain schema representation may reduce that coupling while
retaining the full setup experience. Generated JSON metadata, configuration binding
and precise registrations are separate compatibility work, not automatic savings.

The [Web startup](../../src/HelpDesk.NewWeb/Program.cs) uses Blazor Interactive
Server and MVC. Microsoft's [.NET 10 Native AOT table](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0)
does not support those features. Web stays on JIT while its published assemblies,
symbols, static resources and base image are assessed. Browser asset transfer and
container size are different measurements.

The API includes EF SQLite/PostgreSQL, dynamic query construction, runtime
migrations, Npgsql dynamic JSON, reflected request registration and Hangfire.
[EF Native AOT](https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries)
is still experimental and has dynamic-query limitations. The initial API lane
collects analyzer evidence and supported payload alternatives; it does not enable
whole-application AOT.

The [API Dockerfile](../../docker/api/Dockerfile) deliberately uses Noble/glibc
with GSS, ICU and timezone support after an Alpine GSS compatibility problem.
Web and HTTP MCP already use Alpine plus ICU. MCP initializers and health checks
rely on shell utilities; shell-free bases need coordinated, tested replacements.
RID-specific framework-dependent publishing is a useful first experiment because
the current Dockerfiles publish portable output. Match glibc/musl and architecture.

[Root build settings](../../Directory.Build.props) embed debug information.
The portable-symbol experiment retains PDBs separately and reports both costs.
Deleting loose PDBs from an embedded-symbol baseline would not measure that change.
Native AOT symbol output is also included explicitly in the inventory.

## First hosted discovery batch

The separate [workflow](../../.github/workflows/size-campaign.yml) and
[tool guide](../../tools/size-campaign/README.md) provide:

1. Exact manifest/checksum reconciliation, member/file inventories for all seven
   archives, and a fresh Linux x64 archive smoke check. Direct release mode downloads
   all ten files. The hosted workflow selects the pinned original release workflow
   artifact because read-only direct access to the draft was unavailable; it verifies
   the seven original archives and SHA256SUMS, then reconstructs the two final metadata
   files and requires their exact pinned hashes. Receipts distinguish reconstructed
   metadata from downloaded bytes.
2. Exact OCI index/platform verification and compressed layer, image and `/app`
   inventories for all three image families on both architectures. Inspection
   uses stopped containers and does not establish native runtime acceptance.
3. Same-source Linux x64 client publishes for incumbent JIT, separate portable
   symbols, trimmed JIT and Native AOT. Successfully packaged companion archives
   run through the existing extracted-apphost smoke harness.
4. Separate clean worktrees for API/Web/HTTP baseline, portable-symbol,
   RID-specific FDD and analysis profiles. HTTP additionally attempts trimmed
   and AOT publishes as discovery, with its different libc/runtime model recorded.

All reports retain source, SDK, platform, command, exit status, resolved package
names/versions, file sizes and diagnostic messages. Each publish has a ten-minute
bound; a timeout is recorded as such. Publish failures are evidence, not a hidden
success. Full candidate runtime, catalog, provider, startup/memory/latency and
remaining native-platform acceptance are owned by the target issues.

## Interpreting the results

The first source change adds measurement tooling only. It changes no product
project, Dockerfile, production publish flags or product version. It does not
publish optimized artifacts or release the draft reference version.

Inventory `PASS` means the specified metadata/bytes were checked. Discovery
completion means a publish result was captured. `SMOKE_PASSED` means the existing
archive smoke checks executed against those emitted files. None implies complete
trim/AOT compatibility, and initial reports always keep `releaseEligible: false`.
Any unresolved IL2xxx/IL3xxx diagnostic remains a prerequisite for adoption.

Compare each candidate with its same-source control. Also retain the released
baseline as historical context. Never mix compressed archive size with logical
expanded bytes, registry layer transfer with Docker's uncompressed image size,
or a glibc self-contained publish with a musl framework-dependent image without
labeling the difference. Report symbols and shared layers separately.

## Initial measured findings

[Discovery run 41677597295](https://github.com/BostonTechnologies/RatelDesk/actions/runs/41677597295)
used source `2da1979dbab491f3f45d01fff6b27e3efd9e57f2` and SDK 10.0.403. All 22 publish
commands completed; compatibility and runtime acceptance remained separate:

- API RID-specific framework-dependent output fell from 126,783,669 to 94,266,996
  bytes (25.647%), predominantly by selecting the correct SQLite native asset.
- CLI/stdio Linux x64 trimming reduced archive bytes by 61.275%/61.056%, but both
  optimized profiles failed the paired malformed-stdio-configuration assertion.
  The baseline and portable-symbol pairs passed the existing archive smoke.
- AOT archives retain 39,438,008/42,502,736 bytes of debug files; compare consistent
  symbol policies before ranking AOT against trimming.
- Web's Monaco content contributes 22,454,657 bytes; RID-specific Web/HTTP publish
  changes provide no meaningful saving. HTTP needs a complete-image comparison.
- All six image/platform baselines and all seven original archive hashes were
  verified. The initial workflow's direct draft acquisition failed; its receipt
  remains historical evidence. The explicit original-workflow mode makes the
  independent recovery reproducible with read-only permissions.

Exact measurements, interpretation and follow-up tests are retained in #319 and
the target issues linked above. The original normal PR validation passed 3,601
tests with eight skips; its separate HTTP/provider job passed 437 tests. These
source tests do not replace optimized executable or container acceptance.

## Execution sequence and closure

The measured order starts with API RID-specific framework-dependent publishing
(#330), including native amd64/arm64 and SQLite/PostgreSQL candidate acceptance.
Then process shared dependencies and the Linux x64 CLI/stdio pilots, followed by
their native ARM64/Windows gates. Deployment packaging and supported Web asset
work can proceed independently; HTTP trim/AOT follows its shared prerequisites.
Retain separate outcomes for every architecture. Merge reasonable tested batches
individually, keeping unresolved research in its owning issue. A supported
improvement can finish while an unsuitable AOT technique receives an evidenced
rejected/deferred or not-applicable disposition.

Each accepted change needs its owning published-candidate tests and normal
repository checks. Keep standalone clients, setup commands, full MCP discovery,
identity boundaries, SQLite/PostgreSQL, diagnostics, globalization, licenses,
image families and SBOM/provenance intact. Final release integration uses a fresh
version and the normal reviewed release workflow, with manifest/checksum/digest
reconciliation owned by #319.
