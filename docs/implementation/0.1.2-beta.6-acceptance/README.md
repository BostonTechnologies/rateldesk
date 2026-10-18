# 0.1.2-beta.6 acceptance register

The [closure register](closure-register.json) preserves the beta.6 delivery
contract as **86 individually tracked obligations, all OPEN**. Production
readiness is **NO**. Named owners, acknowledgements, deployment observations
and acceptance evidence are deliberately unfilled. No personnel assignment or
live positive execution is implied by this template.

Deployed acceptance remains tracked by
[#219](https://github.com/BostonTechnologies/RatelDesk/issues/219). A merged
repair, successful CI run or published beta.6 artifact does not close that
issue. The final acceptance receipt must cover the complete supported live
catalog, all retained historical cases and all mandatory scenarios.

The beta.6 delivery epic is
[#226](https://github.com/BostonTechnologies/RatelDesk/issues/226); this executable
register and gate are tracked by
[#232](https://github.com/BostonTechnologies/RatelDesk/issues/232). Each register
row links its relevant delivery issue as well as the continuing acceptance issue.

| Delivery issue | Scope |
| --- | --- |
| [#227](https://github.com/BostonTechnologies/RatelDesk/issues/227) | P1 truthful durable delivery history |
| [#228](https://github.com/BostonTechnologies/RatelDesk/issues/228) | Supported HTTP execution authority and actual identity readback |
| [#229](https://github.com/BostonTechnologies/RatelDesk/issues/229) | Eligible caller self-assignment |
| [#230](https://github.com/BostonTechnologies/RatelDesk/issues/230) | Authenticated customer-owned request visibility |
| [#231](https://github.com/BostonTechnologies/RatelDesk/issues/231) | Orchestration tenant linkage and actual automated retries |
| [#233](https://github.com/BostonTechnologies/RatelDesk/issues/233) | Prospective notification-summary latency budget |
| [#234](https://github.com/BostonTechnologies/RatelDesk/issues/234) | Source-observed AI callback identity collision and terminal ordering |

The two AI source observations are explicitly recorded as source review, with
`historicalReproducedFailure: false`. They are not additional reproduced beta.5
failures. Fresh B01/S3 acceptance covers cross-invocation event identity and
terminal-state regression separately from implementation regressions.

## What the register preserves

| Obligations | Count | Requirement |
| --- | ---: | --- |
| B6-B01 through B6-B59 | 59 | Exact historical blocked operation identities; an individual fresh positive, isolation evidence and applicable preview for every row |
| RD-B5-MCP-001 | 1 | Truthful durable worklog mail lifecycle, canonical event/effect binding, captured outcomes and SQLite/PostgreSQL regressions |
| S1 through S5 | 5 | Mail, notification/approval identities, orchestration/AI, historical timelines and credential/resume scenarios, each with individual required cases |
| RD-MCP-001 through RD-MCP-011 | 11 | Fresh coverage of the original finding families tracked in #207 through #217 |
| B6-R01 through B6-R08 | 8 | The eight beta.4 failures, executed before the rest of the catalog |
| RD-B5-OBS-001 | 1 | Notification-summary profiling and a prospectively agreed latency budget, preserving count correctness |
| B6-N01 | 1 | Positive and negative authority evidence for the added `helpdesk_auth.access` operation |

The historical beta.5 denominator remains 154 operations: 91 historical passes
to rerun, two affected operation failures, 59 blocked operations and two
intentional HTTP configuration restrictions. Historical success does not
populate a new positive. The live catalog is rediscovered and becomes the new
operation denominator, including additions such as `helpdesk_auth.access`.
Removing historical keys or reclassifying any of the 59 blockers cannot improve
the acceptance count.

The original immutable case corpus and referenced beta.5 reproduction evidence
are **not bundled here**. The `original_case_corpus` dependency is explicitly
`MISSING`. A private evidence custodian must retain the original sources,
provide a complete normalized case manifest, and reconcile an individual fresh
result for every historical case. Aggregate counts and a claim that a family
passed do not replace that manifest.

See the existing [beta.4 finding map](../0.1.2-beta.4-mcp-regressions.md),
[beta.5 repair handoff](../0.1.2-beta.5-mcp-regressions.md) and
[public operation catalog](../../contracts/mcp-operation-catalog.json) for
source history. Historical documents retain their own version and scope.

## Blocker ownership and prerequisites

| Group | Rows | Required prerequisite |
| --- | --- | --- |
| Instance/global authority | B02–B08, B10–B12, B14–B41, B46–B52, B54–B59 | A supported HTTP identity carrying the actual required authority and provider mode; bounded issuance, expiry/revocation and tenant separation |
| Exact AI identity | B01, B53 | Actual AuthentikAiAgent scheme, issuer/audience/groups, invocation and log binding |
| Eligible caller self-assignment | B09, B13 | The actual caller must be an eligible support member under approved organization-restricted coverage |
| Automated retries | B42, B43 | Genuinely failed automated tasks plus peer, organization tenant link, saved binding and callback identity |
| Customer-owned visibility | B44, B45 | An authenticated enabled linked customer with owned requests and an eligible form audience |

Every row has separate accountable, implementation, prerequisite and verifier
roles. Fill them only after an actual named person or verified account identity
acknowledges the assignment and target delivery. Acknowledgements precede the
recorded work start. The independent verifier must differ from the other
delivery owners for that row. Issue assignment alone is not an acknowledgement.

The historical global-authority grouping does not replace each operation's
specific policy. In particular, B34 `provision_user` requires the verified
external caller's claims and supported customer linkage, or an explicitly
authorized external administrator. It provisions that caller; request fields
cannot select another person. B35 branding uses a positive integer tenant ID,
which differs from an organization ID. Invitations require independent captured
delivery, and disabling login requires existing-session and fresh-login checks.

The ordinary scoped credential's tenant grants do not confer HelpdeskAdmin.
Native transport is not an HTTP acceptance substitute. A configured provider
card, discovery metadata, a successful denial, an empty customer list, a
manual-task retry rejection or `updated=0` is not the missing positive.

## P1 worklog mail verification

The reproduced failure is a manual task failure whose request history claims
email delivery while the durable effect requires sender review. Queue acceptance
must not become `Delivered` or successfully-sent wording. The same canonical
delivery event remains linked to its exact durable effect and is updated only
from dispatch evidence. Preserve worklog, ticket, organization and event identity.

The register requires missing/disabled sender and paused-worker reproductions;
public-note, closure and workflow checks; internal-note non-delivery; captured
success, definite failure, partial and uncertain outcomes; duplicate, cancellation
and retry cases; and both database regressions. Audit direct and transactional
emitters, retaining the distinction between the reproduced defect and other
source-observed callers. Preserve history and reconcile only from exact durable
evidence. Text, authors and nearby timestamps do not establish delivery or a
historical worklog association.

## Validation commands

These checks use Python's standard library and perform no network or service
actions. From the repository root:

```sh
python3 tools/ci/validate-mcp-acceptance.py --mode structure
python3 tools/ci/test-mcp-acceptance.py
```

Structural validation passes for the unfilled public template and reports
`productionReadiness: NO`. It checks the fixed row identities, mandatory case
slots, original finding links and required dependencies. It also validates any
claimed advanced or closed states: changing a state to `CLOSED` or readiness to
`YES` does not bypass evidence checks.

Keep a populated copy and all observations in the private campaign directory.
The checked-in file remains the reusable, unfilled template. After the independent
reviewer has reconciled complete evidence, validate that private register with:

```sh
python3 tools/ci/validate-mcp-acceptance.py \
  /absolute/private/campaign/closure-register.json \
  --mode acceptance \
  --evidence-root /absolute/private/campaign
```

Acceptance validation exits nonzero while readiness is NO or any required
receipt is missing or inconsistent. Each evidence reference is a relative path
and SHA256; the validator reads and hashes the actual file. Absolute paths,
path traversal, symlink escapes, duplicate JSON properties and mismatched hashes
are rejected. No private evidence is downloaded or uploaded by the validator.

The validator checks completeness, required execution kinds, timestamps,
ownership, exact source/deployment/catalog bindings, immutable file hashes,
postcondition receipts and ordering. The independent reviewer establishes that
the recorded observations actually prove the claimed behavior. JSON fields and
file hashes alone cannot authenticate a human's testimony or assess a screenshot.
The test suite uses synthetic receipts solely to exercise these gate rules.

The [receipt format](receipt-format.md) defines the required fields and how to
preserve failed attempts while attaching subsequent successful evidence.

## Fresh campaign sequence

1. Demonstrate every identity, permission, provider, eligible fixture, capture
   and historical-source dependency on the selected deployed target. Record
   actual named owner acknowledgements and target dates.
2. Verify deployed API, Web and MCP version, source and immutable image digests
   against the release manifest. Verify the six CLI/stdio platform archives and
   native version observations against the same manifest. An identity mismatch
   stops acceptance before mutations.
3. Rediscover live wire schemas and export the catalog. Preserve separate wire
   bytes, exported-file hash, semantic catalog hash and aggregate schema hash.
   Map every unchanged, changed and added operation. Keep the historical
   denominator and include all new live keys.
4. Run B6-R01 through B6-R08 first: correct notification populations, all four
   ticket-list pagination operations, and the three timeline reads. Preserve
   mathematical-integer wire validation, fractional/zero effort, canonical
   WorkLogs, `recordedHours`, `workLogId` and request ticket timelines.
5. Run the complete supported live catalog, all 59 blocked positives, the P1
   reproduction/readback, S1–S5, the original eleven families and every original
   case. No historical passing result carries forward. New scenarios require
   an explicit reviewed addition to this contract and its mandatory case slots.
6. After functional scenarios finish, run a 15-minute soak with at most two
   requests per second and 1,000 total requests. Record actual per-request
   timestamps, latency, outcomes, workload, health and recovery. Stop on an
   unhealthy service or five consecutive unexpected failures.
7. Reconcile retention/restoration and obtain independent closure for every
   individual obligation. Only complete fresh positive evidence, complete
   original-case reconciliation, successful soak/restoration, no unresolved
   reproduced release defects and the owner's complete receipt can close #219.

Mutations are sequential; reads use at most four concurrent requests. Preserve
the paused worker and disabled saved sender, exact terminal delivery history
and unrelated queues. Use isolated captured delivery only. Reconcile uncertain
outcomes before retrying. Restore temporary shared settings, stop campaign jobs
and retain useful owned fixtures and diagnostic history with receipts. There is
no bulk flush, repeated cancellation or deletion of unrelated history.
