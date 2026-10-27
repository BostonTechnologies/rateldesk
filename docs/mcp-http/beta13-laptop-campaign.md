# Beta.13 laptop campaign and continuation

Use this guide after verified beta.13 publication and owner deployment. A source
branch, draft release or passing build does not establish a released candidate.
The public release manifest owns the exact source revision, seven downloadable
archives and three multi-architecture image digests. Download and verify them below, then use the existing
[profile procedure](campaign-testing.md#run-selected-live-read-checks).
Do not rebuild release executables on the laptop.

## Preserve beta.12 and prepare beta.13

The supplied beta.12 report remains **NOT_READY**: 316 positive rows, seven guarded
Local exclusions, 67 NOT_RUN, 30/103 domain cases, 49/86 complete obligations,
eight ready prerequisites and 13 blocked. No post-functional soak ran. Mandatory
body limits (13/13) and duplicate/valid HTTP/stdio controls (16/16) passed;
two optional header-only Expect observations remain unresolved. These historical
counts are neither beta.13 results nor proof that a missing fixture is a defect.

Keep the original private ZIP, its checksum and extracted evidence read-only.
Retain the original report, receipt, fixture registry, open gates, failed attempts
and cleanup evidence. Do not change their source/version/catalog pins or copy
positive statuses into a new campaign. Previously disabled accounts, archived
mailboxes and revoked credentials are not reusable prerequisites without fresh
supported preparation and readback. Preserve the working Netclaw pair.

Install Git, GitHub CLI and Python 3. From PowerShell, download the public release
into a new staging folder and verify its manifest and every archive:

```powershell
$ErrorActionPreference = 'Stop'
$Tag = 'v0.1.2-beta.13'
$ReleaseDirectory = Join-Path $env:USERPROFILE ('Documents\RatelDesk-beta13-artifacts-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$ReleaseInfo = gh release view $Tag --repo BostonTechnologies/RatelDesk --json isDraft,isPrerelease | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $ReleaseInfo.isDraft -or -not $ReleaseInfo.isPrerelease) { throw 'Published beta.13 prerelease required' }
gh release download $Tag --repo BostonTechnologies/RatelDesk --dir $ReleaseDirectory
if ($LASTEXITCODE -ne 0) { throw 'Release download failed' }
$ManifestPath = Join-Path $ReleaseDirectory 'release-manifest.json'
$ExpectedManifestHash = ((Get-Content "$ManifestPath.sha256" -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash $ManifestPath -Algorithm SHA256).Hash -ine $ExpectedManifestHash) { throw 'Manifest checksum mismatch' }
$Published = Get-Content $ManifestPath -Raw | ConvertFrom-Json
if ($Published.version -ne '0.1.2-beta.13' -or $Published.tag -ne $Tag -or $Published.sourceRevision -notmatch '^[0-9a-f]{40}$') { throw 'Unexpected release identity' }
foreach ($Asset in $Published.assets.PSObject.Properties) {
    if ((Get-FileHash (Join-Path $ReleaseDirectory $Asset.Name) -Algorithm SHA256).Hash -ine $Asset.Value) { throw "Checksum mismatch: $($Asset.Name)" }
}
$Source = Join-Path $env:USERPROFILE ('Documents\RatelDesk-beta13-source-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
git clone --filter=blob:none --branch $Tag https://github.com/BostonTechnologies/RatelDesk.git $Source
if ($LASTEXITCODE -ne 0) { throw 'Source checkout failed' }
Set-Location $Source
if ((git rev-parse HEAD) -ne $Published.sourceRevision) { throw 'Tag and manifest source differ' }
```

Keep the checksummed originals. Extract the Windows CLI and stdio ZIPs into
separate new folders and use those executables, not an older executable on PATH.
Now initialize the private campaign from this exact checkout:

```powershell
$Repo = (Resolve-Path .).Path
$Catalog = Join-Path $Repo 'docs\contracts\mcp-operation-catalog.json'
$Parent = Join-Path $env:USERPROFILE 'Documents\RatelDesk-Campaigns'
New-Item -ItemType Directory -Force $Parent | Out-Null
$Campaign = Join-Path $Parent ('beta13-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
py -3 tools/ci/validate-current-campaign.py init --catalog $Catalog --output $Campaign
py -3 -m venv (Join-Path $Campaign 'python')
$Python = Join-Path $Campaign 'python\Scripts\python.exe'
& $Python -m pip install -r tests/http_mcp/requirements.txt
Copy-Item $ReleaseDirectory (Join-Path $Campaign 'release') -Recurse
$Manifest = Join-Path $Campaign 'release\release-manifest.json'
$Receipt = Join-Path $Campaign 'receipt.json'
$Prerequisites = Join-Path $Campaign 'prerequisites.json'
```

Apply a user-only ACL to the private workspace. The verified release files are now copied under `$Campaign\release`, with
the manifest at `$Manifest`. The current adapter derives the product version from the root
`Directory.Build.props`; it retains the established 390-operation contract and
all domain, history, transport, cleanup and soak requirements. Historical
validators and receipts remain unchanged. The new receipt format is
`rateldesk-campaign/v1`; prerequisites use `rateldesk-prerequisites/v1`. Existing
body-boundary evidence keeps its protocol format, with fresh candidate identity.

Verify actual Web/API/HTTP MCP deployment identity and extracted Windows CLI and
stdio versions/checksums against the manifest. Rediscover live tools, operations
and semantic schemas. Declare the prospective latency budget before measurement;
the supplied beta.12 campaign used 5,000 ms p95, which is historical unless the
owner explicitly adopts it for this run.

## Resolve prerequisites before their dependent operations

Use the [granular fixture procedure](beta12-laptop-campaign.md#2-prepare-exact-fixture-prerequisites)
and the previous private `open-gates.json` as a planning input. Re-observe every
prerequisite for beta.13; do not mark a lane ready from a previous receipt.

- NetRatel: current exact execution agent/profile revision and reviewed admission,
  intended tenant mapping, eligible definitions/bindings, callbacks and independent
  provider reads. RatelDesk MCP Policies cannot confer NetRatel execution rights.
- AI/events: bounded invocation fixtures, phase/correlation-specific authenticated
  503-then-accepted and terminal 403 controls; actual pending, uncertain, offered
  and unbound native sessions on disposable fixtures.
- Mail: isolated unrelated outbound work, authorized global sender, definite,
  partial and uncertain provider receipts, source/fence history and recovery.
  Preserve held effects; do not flush, retry or delete the shared queue.
- Scheduler/SLA: isolated intended runtime; keep the shared disabled scheduler
  unchanged. Pair/source lifecycle tests need a separate disposable pairing.
- Graph, legacy numeric branding identity and bootstrap/owner-account fixtures:
  obtain actual provider/owner prerequisites; do not substitute identifiers or
  expand grants merely to satisfy a row.
- Customer replacement: preserve the observed non-null replacement guard and
  distinguish it from eligible requesterless assignment. Record the product
  requirement and actual expected outcome before treating rejection as a defect.

Read the retained fixture state before reusing it. Restore only task-owned,
authorized settings with exact independent receipts. The historical security
rotation action is an owner prerequisite; no secret belongs in evidence or issues.

```powershell
& $Python tools/ci/validate-current-campaign.py status --catalog $Catalog --receipt $Receipt
& $Python tools/ci/validate-current-campaign.py check-prerequisites --catalog $Catalog --manifest $Manifest --receipt $Receipt --prerequisites $Prerequisites --evidence-root $Campaign
& $Python tools/ci/validate-current-campaign.py mapping-plan --catalog $Catalog --manifest $Manifest --receipt $Receipt --evidence-root $Campaign
```

These commands inspect existing evidence; they do not execute product operations
or manufacture readiness. Follow the [campaign runner instructions](campaign-testing.md)
for live checks and the existing canonical case corpus for mutations.

## New UI acceptance

1. Admin → Integration: System connections, API & MCP credentials and Netclaw;
   verify old links, account-only access and direct unauthorized endpoint denial.
2. Admin → MCP Policies: use a separate disposable operator credential. Preview,
   confirm, independently read policy/audit and prove allowed/denied operations.
   Test stale preview, wrong actor, expiry, disable and permanent revoke. Policy
   ceilings intersect current account/credential authority; they never grant more.
3. Operations → Prompts: create organization default and optional guidance;
   verify revisions, archive and cross-organization denial.
4. Operations → AI investigations: select authorized incidents, requests, changes
   and tasks; preview guidance, save without sending, then explicitly Send. Verify
   the actual Netclaw first turn and later turns carry the pinned snapshot, even
   after a prompt edit. Test UTF-8 overflow without truncation, retry identity,
   unauthorized context and loss of ticket access. Search for the saved title
   globally and prove foreign-context investigations remain hidden.
5. Verify long-brand login at desktop/320/390 px, light/dark, keyboard focus,
   200% text scaling and loading/error states. Verify local and Hybrid submission.
6. Verify IT support organization ticket visibility for a scoped technician,
   unrelated-organization denial, no tenant-admin delegation and revocation when
   the support link or source role is removed.

Native investigations retain the product's PostgreSQL requirement. SQLite must
continue to support prompts, policies and existing non-native product features.

## Resume after interruption

Resume the same private campaign only while source, deployment, catalog, schema
and selected identities still match. Record the interruption and first reconcile
every uncertain mutation through independent API/provider/storage reads. Do not
replay an acknowledged completion or an uncertain send. Preserve request and
conversation IDs. Netclaw's Check saved session reconciles without resending.
If the deployment/source changes, start a fresh workspace and retain the earlier
one as history; continuation means carrying forward the work plan, not its passes.

Complete all current operations and mandatory domain/history obligations before
setting functional completion. Then run the existing 15-minute soak (at most two
requests/second and 1,000 total, stop on unhealthy service or five consecutive
unexpected failures), record actual latency/errors and perform all nine cleanup
assertions with independent evidence.

```powershell
& $Python tools/ci/validate-current-campaign.py validate --catalog $Catalog --manifest $Manifest --receipt $Receipt --evidence-root $Campaign
```

Retain stdout, stderr and exit code. A planning/mapping check, source test, archive
verification or release publication is not a completed campaign. Keep #219 and
any missing real-provider obligations open until their actual acceptance exists.
