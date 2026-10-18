# Private acceptance receipt format

Start with a private copy of [closure-register.json](closure-register.json).
The public register carries requirements and empty evidence slots. Populate
names, private target observations, identity bindings and case evidence only in
the private campaign copy. The offline validator reads these files through the
explicit `--evidence-root`; it never queries a production service.

## Evidence references and common context

An evidence reference has exactly two fields:

```json
{
  "path": "observations/case-response.json",
  "sha256": "replace with the lowercase SHA256 of that file's exact bytes"
}
```

Paths are relative to the private evidence root. Every referenced file must
exist and match its recorded digest. A JSON receipt is at most 8 MiB; a leaf
observation is at most 64 MiB. Larger retained evidence can be split into bounded
parts with an independently reviewed index. Preserve earlier attempts in
separate immutable files; update the case slot to reference the later accepted
receipt without overwriting the earlier observation.

Timestamps are ISO 8601 with a timezone. The campaign records an ID, start/end
and exact 40-character `sourceRevision`. Fresh deployed case timestamps must
fall within that campaign. Each fresh case, closure, soak and final receipt
carries this exact context:

```json
{
  "campaignId": "the current private campaign ID",
  "version": "0.1.2-beta.6",
  "sourceRevision": "the exact 40-character release source revision",
  "deploymentSha256": "the deployed identity receipt file SHA256",
  "catalogReceiptSha256": "the catalog receipt file SHA256"
}
```

These hashes bind a case to one observation of the deployed artifacts and live
catalog. Switching source, target identity or discovery requires a new campaign
and fresh evidence; old receipts cannot be relabeled.

## Owners and prerequisite receipts

Every obligation has `accountable`, `implementation`, `prerequisite` and
`verifier` entries in `owners`. Each records `name`, `acknowledgedAt` and an
`acknowledgement` reference. Use an actually acknowledged person or verified
account identity. A role label such as maintainer or release owner is not a name.
The referenced acknowledgement contains:

| Field | Meaning |
| --- | --- |
| `obligationId` | Exact obligation ID, or the dependency ID for its prerequisite owner |
| `role`, `name` | Exact assigned role and acknowledged identity |
| `acknowledged` | Boolean `true` after actual acknowledgement |
| `acknowledgedAt` | Actual acknowledgement time, no later than `workStartedAt` and acceptance start |
| `targetDeliveryAt` | Actual target date acknowledged by that person |

An obligation advances through `OPEN`, `PREREQUISITE_DEMONSTRATED`,
`IMPLEMENTATION_VERIFIED`, `ACCEPTANCE_PASS`, `CLOSED`. Record `workStartedAt`
before advancing. Advanced stages require all owner acknowledgements and the
referenced demonstrated dependencies. Implementation verification adds one or
more `implementationEvidence` references. Acceptance and closure require every
individual `caseEvidence` slot plus a separate independent closure receipt.

A dependency records `state: DEMONSTRATED`, its owner/acknowledgement,
`demonstratedAt` and an `evidence` reference. That prerequisite receipt has
`dependencyId`, matching `demonstratedAt`, `campaignId`, `sourceRevision`,
`demonstrated: true`, `deployedTargetVerified: true`, and an `observation`
reference. Demonstration precedes acceptance start. Provider availability and
identity eligibility must be real; a configured UI card is insufficient.

The `original_case_corpus` dependency additionally requires `immutableSources`
(a nonempty array of evidence references) and a `caseManifest` reference. The
manifest has a `cases` array whose unique entries contain `id`, `operation` and
`executionKind`. Allowed kinds are `positive`, `expected_negative`, `preview`
and `unsupported`; the last is limited to the two historical HTTP config
restrictions. The evidence custodian verifies that this normalization accounts
for the complete immutable corpus. The validator then requires exactly one fresh
result for every manifest case in `campaign.historicalCases`.

## Release and deployed artifact identity

`campaign.releaseManifest` references the published `release-manifest.json`
from the existing [release pipeline](../../releases/README.md). Its `version`,
`tag` and `sourceRevision` must match the candidate. Preserve the three
`containers` entries and checksummed `assets`.

`campaign.deployment` references a JSON observation receipt containing:

| Field | Required content |
| --- | --- |
| `campaignId`, `observedAt` | Current campaign and actual fresh observation time |
| `components.api`, `.web`, `.mcp-http` | Each has `version`, `sourceRevision`, immutable `digest` and an `observation` reference |
| `nativeTools` | Keyed by each exact CLI/stdio archive name from the manifest; each value has `version`, `sourceRevision`, archive `sha256`, and an `observation` reference |

Native evidence covers CLI and stdio MCP on `linux-x64`, `linux-arm64` and
`win-x64`. Record the checksummed downloaded bytes and actual native identity
observation; a source build version alone does not verify a published archive.

## Live catalog receipt

`campaign.catalog` references a receipt with `campaignId`, `observedAt`, and:

| Field | Required content |
| --- | --- |
| `wireDiscovery` | Exact JSON tools/list response reference, either `{ "tools": [...] }` or its JSON-RPC `result` envelope |
| `exportedCatalog` | Current native catalog export reference, including operations, toolSchemas, catalogHash and schemaHash |
| `semanticCatalogHash`, `schemaHash` | Separate native semantic catalog and aggregate input-schema hashes matching the export |
| `semanticHashObservation` | Independently captured live semantic/schema identity observation |
| `operationChanges` | Every historical/live operation key mapped to `unchanged`, `changed` or `added`, compared with the frozen beta.4 catalog |
| `addedOperations`, `removedOperations` | Explicit added keys and an empty removals array under this preserved-scope contract |

The validator hashes wire and export files separately, compares their input
schemas and operation sets, verifies the declared semantic/schema hashes and
requires the complete operation diff. The semantic hash is not the exported-file
hash; the wire discovery hash is a third observation. The input-schema hash is
retained separately as well.

## Individual case receipts

Every slot in an obligation's `caseEvidence` references a JSON receipt. Its
`obligationId` and `caseId` must match the slot. The `executionKind` must exactly
match the registered case requirement. A negative-isolation case can pass through
the expected denial; the separate required positive cannot be satisfied by a
denial, no-op, preview, historical success or code-only test.

| Field | Required content |
| --- | --- |
| `obligationId`, `caseId`, `operation` | Exact current obligation/case/operation binding; operation is required where the register specifies one |
| `outcome`, `executionKind` | `PASS` and the exact required `positive`, `expected_negative`, `preview` or `implementation` kind |
| `executor`, `verifier` | Actual named execution and verification identities; verifier matches the row's assigned independent verifier |
| `context` | Exact fresh campaign/source/deployment/catalog context above |
| `origin`, `transport` | `fresh_deployed` and `http` for live cases |
| `startedAt`, `completedAt` | Actual ordered times inside the fresh campaign |
| `supportedPositive` | `true` for a supported positive that actually exercised the required behavior |
| `confirmedExecution` | `true` for a positive mutation whose live contract requires confirmation |
| `mutationObserved` | `true` for a positive write, established by the independent postcondition |
| `previewImmutable` | `true` for preview cases after independently verifying no change |
| `postconditionSurface` | One of `API`, `CLI`, `UI`, `storage`, `provider`, `transport_capture` |
| `identityEvidence`, `wireEvidence`, `postconditionEvidence`, `restorationEvidence` | Separate referenced observations sufficient to assess actual identity, request/response, effect and restoration |
| `transportReceiptEvidence` | Required independent captured transport evidence for S1, P1 captured outcomes and customer invitation positives |

The P1 SQLite/PostgreSQL `implementation` cases instead require the exact
candidate `sourceRevision`, named executor/verifier, `passedTests > 0`,
`failedTests: 0`, `skippedTests: 0` and `testResults` evidence. These are additional
regressions and cannot replace any fresh deployed case.

The latency `profile_and_budget` case also contains `latencyBudget` with
`metric` (`p50`, `p95`, `p99` or `maximum`), positive `budgetMs`, nonnegative
`measuredMs` no greater than that budget, `agreedAt` preceding the measured run,
and `agreementEvidence`/`workloadEvidence` references. Preserve count correctness
in its separate case. Do not convert the historical observation into a
retrospectively invented SLA failure.

For `campaign.operations`, each live operation has an `id`, `state: PASS` and
an `evidence` reference using `obligationId: catalog` and `caseId` equal to the
full operation key. Only `helpdesk_config.set` and `helpdesk_config.unset` use
`state: UNSUPPORTED_BY_DESIGN` and the exact reason `HTTP remote configuration
persistence is disabled`.

`campaign.historicalCases` similarly uses each normalized original case ID,
`state: PASS` and a receipt with `obligationId: historical-corpus`, the original
`caseId`, operation and required execution kind. Retain the two intentional
config restrictions explicitly when present in that corpus. Do not carry
historical positives into these fresh slots.

## Independent closure, soak and restoration

Each row's `closure` references `obligationId`, `verifier`,
`independentlyClosed: true`, the exact `context` and `closedAt`. Closure follows
all of that row's case verification; it cannot be bulk inferred from an issue
merge. The verifier differs from that row's implementation, prerequisite and
accountable owners.

`campaign.soak` references a receipt with the exact context, start/end,
`afterFunctionalTests: true`, `healthy: true`, `recoveryVerified: true`,
`totalRequests`, `unexpectedFailures`, `unresolvedFailures: 0`, a `samples`
reference and `workloadAndRecoveryEvidence`. The samples file is a JSON array
with one entry per request: `at`, finite nonnegative `latencyMs` and a boolean
`unexpectedFailure`. Record actual requests, including failures. The validator
requires at least 15 minutes, at most 1,000 requests, no more than two requests
per sliding second, fewer than five consecutive unexpected failures, and exact
count agreement. All functional catalog, historical and mandatory scenario
cases finish before the soak starts.

`campaign.restoration` references a receipt with `context`, `complete: true`,
the named `verifier`, `verifiedAt`, an `evidence` reference and each of these
observed invariants set to true:

- `pausedWorkerPreserved`, `savedSenderDisabled`
- `unrelatedQueuesPreserved`, `terminalHistoryPreserved`
- `temporarySettingsRestored`, `campaignJobsStopped`
- `uncertainOutcomesReconciled`, `ownedFixturesRetained`

`campaign.independentReview` has the same common fields and an independent
reviewer who is not any delivery owner, plus the #219 `acceptanceIssue`,
`allOriginalCasesReconciled: true`, `unresolvedReproducedDefects: 0`,
`missingMandatoryDependencies: 0` and `ownerReceivedCompleteReceipt: true`.
It records the completed review of actual evidence; it is not produced by a
successful validator run. Set readiness YES only after that review. The
validator's full acceptance mode then verifies that the declared complete
receipt satisfies the retained contract and referenced file hashes.
