# 7 November 2026 next-beta delivery

The owner authorized implementation, reviewed merges and complete prerelease
publication for #355–#358. Production deployment remains a separate operator
step. All acceptance data is synthetic and disposable.

## Baseline and allocation

- Refetched main: `ee7067d12dafb22de81b70a58f61b211b9e37226` (published beta.5).
- Read the current four issue bodies and active PR/branch inventory before edits.
- SDK: repository-pinned .NET `10.0.400`; local Docker `28.4.0`, Compose `2.40.3`.
- Initial allocation check: neither accepted beta.6 tag prefix, no draft/public
  beta.6 release, and all three anonymous beta.6 GHCR manifests are unallocated.
  This is provisional; repeat immediately before version preparation/publication.
- Current workflows require all six ordinary jobs, including Offline HTTP MCP
  campaign harness. Conditional deep jobs are separate acceptance.

## Work register

| Issue | Branch | PR / merge | Focused evidence |
| --- | --- | --- | --- |
| #358 application policy | `fix/358-logging-policy` | Pending integration/review | In progress |
| #358 deployment retention | `fix/358-docker-retention` | Pending first batch | In progress |
| #355 native child follow-ups | `fix/355-child-followups` | Pending review | In progress |
| #357 task-first editor | `fix/357-task-first-editor` | Pending review | In progress |
| #356 shared For column | `fix/356-for-column` | Pending review | In progress |

The first batch is #358. Each batch must have focused verification, actual diff
review, green current-head ordinary CI and a normal merge, followed by main CI.
Release metadata follows all four merged changes. No tag or release is created
until exact-current-main checks pass.

## Acceptance and distribution status

Baseline solution build is running. Native-daemon, browser, emitted-log,
generated/effective deployment and extracted-archive acceptance are pending.
No new release has been staged or published, and no production deployment has
been performed. Native model/transport fixtures and actual daemon acceptance
will be identified separately in the final receipts.
