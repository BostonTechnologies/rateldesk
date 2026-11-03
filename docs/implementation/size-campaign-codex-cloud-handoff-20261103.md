# Artifact size campaign: Codex Cloud handoff

Snapshot: 3 November 2026, approximately 18:45 UTC. Campaign `rd-size-20261103`.

Continue [epic #318](https://github.com/BostonTechnologies/RatelDesk/issues/318) and
[the campaign design](size-campaign-20261103.md) until every linked target has a
tested outcome. Refresh live GitHub state before acting: this document preserves
the starting evidence and continuation contract, not a permanently current CI status.

## Authority and outcome

The user authorized implementation and merging to `main` after each reasonable,
tested batch. They requested an overnight Codex Cloud handoff because this chat
can disconnect. Continue implementing, testing, reviewing and merging; do not
stop after another plan or the first successful PR. No release, version bump,
registry publication or production deployment is authorized by this campaign.

Preserve all six self-contained client downloads, the deployment archive,
integrity metadata and all three image families on both architectures. Preserve
features, authentication/authorization, SQLite and PostgreSQL, migrations and
existing data, setup entrypoints, documentation, diagnostics and notices.

Read root and scoped `AGENTS.md`, `.agents/skills/aot-trimming/SKILL.md` and the
relevant repository skills before changes. Generate NuGet project edits through
the .NET CLI. Keep Slopwatch's existing baseline and required repository checks.
Do not suppress unresolved trim/AOT diagnostics or remove behavior to make tests
or size targets pass.

## Start here

1. Fetch the repository's current `main`, campaign branches, PRs #331, #332, #335,
   #334 and #336, and epic/child discussions. Reuse existing work and successful
   evidence where it still applies. Do not recreate these PRs.
2. Finish the existing merge queue below. Verify the current head, all required
   normal CI and the relevant native gate before each merge. Review any new
   changes and resolve conflicts normally. Do not bypass branch protection.
3. Use **merge commits** for this existing stack. Package-generation receipts
   check preparation ancestry; squash/rebase merges would discard that proof.
4. Continue the remaining experiments and implementation by target. Coordinate
   shared-file changes; independent reviews/builds may run on isolated branches.
5. After every reasonable batch, commit and push, update the owning issue and
   epic with measurements and exact run links, merge when verified, then start
   the next outstanding batch.

### Existing merge queue

| PR | State at snapshot | Source and evidence | Next action |
| --- | --- | --- | --- |
| [#331](https://github.com/BostonTechnologies/RatelDesk/pull/331) | Merged | Tested head `07472a4498efe05aed9736a08c59f9a4c7a2313d`; merge `9d8ca1034b7406c00c5d9dc4dba4020ed779d56f`; [normal CI](https://github.com/BostonTechnologies/RatelDesk/actions/runs/41690243510) and [discovery](https://github.com/BostonTechnologies/RatelDesk/actions/runs/41690243524) passed | Retain the acquisition and measurement tooling. |
| [#332](https://github.com/BostonTechnologies/RatelDesk/pull/332) | Merged | Tested head `c39b071ddf9ccb806aaea2a2933d4b724f32f01a`; merge `d7f90d6ae4a1f20288e827edc024ec7e9db920a5`; [normal CI](https://github.com/BostonTechnologies/RatelDesk/actions/runs/41690262414) and [native API](https://github.com/BostonTechnologies/RatelDesk/actions/runs/41690262680) passed | Retain the supported API RID-specific FDD change. |
| [#335](https://github.com/BostonTechnologies/RatelDesk/pull/335) | Open | Head `145a3e9815cdae4562377676dbec2a332dcef013`; [seven-archive/native acceptance](https://github.com/BostonTechnologies/RatelDesk/actions/runs/41692162007) passed; [normal CI](https://github.com/BostonTechnologies/RatelDesk/actions/runs/41692256582) pending | Merge after current-head normal CI passes. Closes the exported-links finding #333. |
| [#334](https://github.com/BostonTechnologies/RatelDesk/pull/334) | Open | Head `62a40fb00a456dfd8b3b20262dee4b49947dc10f`; [all three native RIDs](https://github.com/BostonTechnologies/RatelDesk/actions/runs/41694094307) passed; [normal CI](https://github.com/BostonTechnologies/RatelDesk/actions/runs/41694105092) pending | Merge after #335 and current-head normal CI. |
| [#336](https://github.com/BostonTechnologies/RatelDesk/pull/336) | Open | Branch `campaign/rd-size-host-telemetry`; preparation parent `064daefc5b7e05ca8c1d0206a1d1ef39bf1b77a2`; [preparation](https://github.com/BostonTechnologies/RatelDesk/actions/runs/41695134403) passed | This document accompanies the exact generated project files/receipt. Discover the resulting head and runs; require native amd64/ARM64 acceptance and normal CI, then merge after the prerequisites. |

The latest observed main after #332 is
`d7f90d6ae4a1f20288e827edc024ec7e9db920a5`. A later live state supersedes this pin.
The original preparation-only #336 workflow deliberately skipped native jobs;
that success is not container or telemetry acceptance.

## Proven reductions already measured

These are paired, same-source measurements, not comparisons against a different
release source or estimates based on a publish directory. Byte counts are exact;
percentages are rounded. Workflow artifacts contain detailed receipts and logs.

### API: #332

| Architecture | Compressed image layers: control → candidate | Saving | Complete unpacked image: control → candidate |
| --- | ---: | ---: | ---: |
| amd64 | 141,159,446 → 124,405,961 B | 16,753,485 B / 11.868% | 359,698,903 → 327,181,718 B |
| ARM64 | 137,644,152 → 120,917,265 B | 16,726,887 B / 12.152% | 391,916,008 → 359,462,341 B |

Native acceptance passed all eight fresh/upgrade SQLite/PostgreSQL cases. It
verified correct SQLite native architecture, FDD output, measured/executed OCI
identity, matching base/runtime layers and runtime configuration, ICU/timezones,
GSS library resolution, setup/login/CRUD, attachments, persistence and retained
sessions. Actual container SDK: 10.0.402. Live Kerberos, Graph/mail services and
load tests were outside this publishing change's gate.

### Client dependencies: #334

| Download | Control bytes | Candidate bytes | Saving |
| --- | ---: | ---: | ---: |
| CLI Linux x64 | 61,896,580 | 49,642,336 | 19.798% |
| stdio Linux x64 | 61,793,637 | 49,544,213 | 19.823% |
| CLI Linux ARM64 | 59,330,413 | 47,507,107 | 19.928% |
| stdio Linux ARM64 | 59,235,981 | 47,405,634 | 19.972% |
| CLI Windows x64 | 62,829,065 | 50,286,773 | 19.963% |
| stdio Windows x64 | 62,734,584 | 50,190,849 | 19.995% |

Together the six downloads remove **73,243,348 compressed bytes**. They remain
self-contained, untrimmed JIT applications with the same embedded-debug policy.
The candidate removes inherited ASP.NET/Npgsql payload while preserving the
.NET Core runtime. All three native runners passed source/version checks,
root/nested helper entrypoints, strict exported docs, setup, invalid stdio
configuration and safe exits, Linux PTY or Windows privacy checks, and exact
discovery parity: **37 tools, four resources, zero templates and four prompts**.
Natural EOF exits zero and discovery makes zero backend requests. Each native
runner passed 27 boundary tests. Candidate publish logs contained no warnings or
errors, and Slopwatch reported zero issues. Artifact/inner-archive hashes were
independently verified.

The first Windows run selected a WSL `bash` shim and failed before application
smoke. Head `62a40fb` fixes the harness to use the active Git Bash and correctly
normalize native `.exe` paths; the corrected native run above passed. Do not
attribute that old harness failure to application behavior.

### Packaging and exported docs: #335

All seven archives, metadata relationships, deployment helpers/Compose and three
native client RIDs passed. The unchanged strict docs validator went from 43
broken exported source links to zero; each payload included 167 Markdown files
and 739 local links. Fourteen focused and 42 existing packaged-helper cases
passed. Root helpers are canonical; compatible nested forwarders preserve both
entrypoints and vendored YAML behavior. Source documentation and historical
non-Markdown evidence, including JSON and images, remain byte-preserved, as do
skills and notices. Eleven exported Markdown files change only to pin
source-only links to the exact source revision.

Helper deduplication removes 446,316 expanded bytes per client; pinned exported
source links add 3,777 bytes, for **442,539 net expanded bytes removed per client**.
Do not present archives built from different source/version as a causal
compressed-size comparison. Level-9 deployment gzip alone saved only 29,446
bytes / 0.2582% in the original experiment.

## Immediate unfinished host batch: #336

The product change moves `Npgsql.OpenTelemetry` 10.0.3 and `AddNpgsql()` from
shared ServiceDefaults to API ownership. It preserves shared HTTP tracing,
resource attributes, metrics/logging/exporter configuration and existing
Dockerfiles/runtime contracts. Preparation used SDK 10.0.403, restored the full
solution and passed Slopwatch with zero issues.

The two SDK-generated project files and receipt in this commit were copied
exactly from the preparation artifact, with outer artifact digests, individual
file hashes, original-parent hashes and the exact package delta verified:

| File | SHA-256 |
| --- | --- |
| `src/Helpdesk.API/Helpdesk.API.csproj` | `7178ce33934e666b4035cedde46daa85eca48b039ac54d69f3e617a9855034a5` |
| `src/Helpdesk.ServiceDefaults/Helpdesk.ServiceDefaults.csproj` | `288a5476d1ffa8272cf504c1ecaea5b99fa2a1f80fbc128bfe67f30c9fe26a66` |
| `tools/size-campaign/host-telemetry-receipt.json` | `fe6a7ae9027bd1b86bf6f5533e637a111c357f8b308a7e23617cc62b301c5c84` |

Preparation artifact ID `13258786788`, ZIP SHA-256
`e5d664aee4991c2039b220c59bd4576ba3da0a5a8e6291271f4017436cb13118`.
Quality artifact ID `13261485153`, ZIP SHA-256
`2eb44612adbc7a023c721889c5ec4591bc4904bec2f359f79f26b4a2b36f8821`.
Both belong to preparation run 41695134403.

Use `.github/workflows/host-telemetry-batch.yml`. For each native architecture,
require exactly three non-skipped real tracing tests: API DI tracing observes a
real PostgreSQL command, shared-only tracing does not subscribe to Npgsql, and
shared host paths retain Kestrel/HttpClient parentage, health filtering and
resource attributes. Then require all eight runtime cases covering paired Web
static checks, paired HTTP configuration, both database first-run paths and
both authenticated HTTP MCP gateway pairs. Gateway coverage includes catalog,
scoped reads, permission denial, invalid/revoked credentials, non-root UID and
health.

The image gate requires identical resolved SDK/runtime material digests,
observed SDK, non-application compressed layers and runtime configuration; the
measured OCI config/rootfs must identify the containers actually executed.
Npgsql must remain in API and be absent from Web/HTTP. Positive whole-image and
application savings are required. No host saving is claimed before these pass.
External Authentik, full browser/editor acceptance and emitted metric samples
are not established by this dependency-only gate.

Preserve client preparation ancestor
`43c04183025f0db0341864bc6df9c159be89ca64`, host ownership baseline
`173ba8baef9074f015a9219e81cbde1e62e98f63`, and host preparation ancestor
`064daefc5b7e05ca8c1d0206a1d1ef39bf1b77a2`. The checked-in receipts explain the
hash/ancestry requirements. Never rewrite them just to make a gate green.

## Remaining epic queue

| Owner | Remaining work and decision standard |
| --- | --- |
| #319 | Preserve original provenance; close direct final manifest/checksum acquisition gaps where access permits; reconcile final combined artifacts, image/platform digests, aggregate savings and regression budgets. |
| #320 | Continue JSON source generation, configuration binding, reflected tool/resource/prompt catalogs, registration and serialization prerequisites. Retain full schema/catalog and error behavior. Dependency cleanup is only the first accepted step. |
| #321 / #322 | Linux x64 CLI/stdio trim and AOT pilots. First capture the complete failing negative-config process result and fix the actual cause. Then run the emitted binaries through full command/setup/profile/auth/catalog/runtime acceptance. |
| #323 / #325 | Independently validate selected CLI/stdio profiles on native Linux ARM64. Do not inherit x64 runtime acceptance. |
| #324 / #326 | Independently build/test selected CLI/stdio profiles with the native Windows toolchain, Git Bash path handling and credential/privacy behavior. |
| #327 / #333 | Finish packaging merge; rebuild all seven archives from the final combined source, including this handoff. Retain strict extracted docs, all three native RIDs, deployment/Compose helpers and metadata. If claiming full extracted-launcher lifecycle acceptance, run the existing lifecycle harness with `--deployment-archive` and its matching native executable against a disposable stack. |
| #328 | Finish host dependency gate, then evaluate HTTP MCP trim/AOT and compatible runtime/base-image options with full authenticated gateway behavior, setup, health and both native architectures. Compare complete images using matching runtime/libc assumptions. |
| #329 | Finish host dependency gate; investigate measured editor/static-asset and symbol opportunities on the supported JIT path. Full trim/AOT is unsupported for the current Blazor Interactive Server/MVC framework. Preserve all editor languages/workers and browser behavior unless a genuinely equivalent build is demonstrated. |
| #330 | Retain merged RID change; assess other measured API opportunities without removing either provider, migrations, Graph/mail, dynamic query, JSON or Hangfire behavior. Record an evidence-backed disposition for techniques the current framework/dependency graph cannot support. |

The initial 22-profile discovery used SDK 10.0.403. Linux x64 trim archives were
24,058,308 / 24,157,389 bytes, around 61% below their controls, but failed the
negative-config smoke before MCP initialization and retained unresolved
diagnostics. They are **not accepted**. AOT archives of 31,358,257 / 32,153,195
bytes included 39,438,008 / 42,502,736 bytes of external debug symbols; compare
equal symbol policies. The original failing process tuple was not recorded, so
do not invent its cause.

Monaco contributed 22,454,657 bytes (about 38% of Web's application payload).
An apparently unused worker can support embedded JavaScript in HTML. Validate
Markdown/plain text/HTML/shell/PowerShell modes, workers, fonts/PDF behavior,
fingerprints and compression before adopting any bundle change.

The API's Microsoft.Graph assembly was 47,439,712 bytes; it is an investigation
lead, not permission to remove Graph functionality. EF design/Roslyn assets
were not present in the shipping payload. HTTP MCP's application was only
about 10.44 MiB, so full-image accounting matters. Existing glibc self-contained
probes cannot establish savings against an Alpine FDD image without a fair
comparison. API Noble/glibc preserves its GSS contract; Web/HTTP Alpine needs
ICU and existing setup/health shell utilities.

## Evidence, completion and checkpoint rules

For each candidate record source/tooling revision, actual SDK/compiler, RID,
OS/libc, publish flags and symbol policy, resolved packages/base digests,
compressed and expanded/archive or image/application bytes, warnings, build
time, test commands/counts/results, startup/readiness, memory and representative
workload evidence. Do not invent missing measurements. Native runtime and
published-artifact acceptance must match the candidate under review.

Use the existing discovery, API, client, packaging and host workflows before
adding redundant infrastructure. Read their inputs before dispatch. Normal
repository validation remains required. Docs checks are unchanged:

```bash
bash tools/ci/check-public-disclosure.sh
python3 tools/ci/validate-docs.py
bash tools/ci/validate-layout.sh
```

Rebuild the final combined source with
`.github/workflows/size-packaging-acceptance.yml`; preserve the seven-archive
set, checksums/provisional manifest, strict exported-doc validation, extracted
helpers/Compose and all native client lanes. Final published registry digests,
SBOM/provenance, public downloads/pulls and finalized release metadata remain
release integration tasks: do not manufacture or publish them to close this
implementation campaign. Mark any permission-dependent release checks pending.

A child closes only when its own acceptance criteria and technique decisions
are documented with evidence. Valid technique outcomes are adopted, rejected
with evidence, or not applicable. An external access/toolchain blocker stays
explicit and open with the exact unblock action. Do not use a deferred label to
skip feasible work or claim the whole epic complete while required work remains.

After each batch, leave a durable issue/PR checkpoint with: current branch and
commit, merged commits, tests/runs and artifact identities, measured results,
remaining failures/blockers, and the exact next action. Keep a concise final
matrix for all six client downloads, deployment/integrity files and all six
image/platform combinations. Preserve failed experiments as evidence.

If a session, budget or environment limit ends the run, commit/push all coherent
work and leave the next goal prompt in the epic. Do not confuse interruption
with completion. The next session must be able to resume from GitHub alone;
no transient workspace file or private signed download URL is a required input.
