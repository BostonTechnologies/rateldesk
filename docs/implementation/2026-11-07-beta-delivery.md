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
| #358 application policy | `fix/358-logging-policy` | First batch; source review passed | [Policy evidence](358-logging-policy.md); focused final run pending |
| #358 deployment retention | `fix/358-docker-retention` | First batch; source review passed | [Retention evidence](358-docker-retention.md); 159 focused .NET cases plus actual deployment checks passed |
| #355 native child follow-ups | `fix/355-child-followups` | Review in progress | Actual Netclaw 0.28.1 ticket/organization connected paths passed; focused browser/provider checks in progress |
| #357 task-first editor | `fix/357-task-first-editor` | Review in progress | 26 focused rendered cases passed; final browser checks in progress |
| #356 shared For column | `fix/356-for-column` | Source review passed; UI batch pending | Commit `e435d33d65551d36820a2a8007f73006b3290582`; 23 rendered and 20 browser cases passed |

The first batch is #358. Each batch must have focused verification, actual diff
review, green current-head ordinary CI and a normal merge, followed by main CI.
Release metadata follows all four merged changes. No tag or release is created
until exact-current-main checks pass.

## Acceptance and distribution status

Baseline solution build passed with 23 preexisting warnings and zero errors.
Layout, documentation and the offline HTTP MCP campaign harness (51 + 2 cases)
passed. The combined final-source full solution/provider runs remain mandatory.

An official Netclaw 0.28.1 binary (source `0d86cef`, downloaded archive SHA-256
`b49776e5551401bbde04e8b58a4dddbf461f8006b581db18aefd384ab369bdcc`)
ran the actual session/child actors and SignalR transport against a controlled
loopback model. The production RatelDesk manager, client and PostgreSQL store
observed one operator input, two replies/native turns, pending then clear child
snapshots, 13 durable events and zero resends in both scopes. This is actual
connected-daemon acceptance with a model fixture, separate from mocked transport
coverage, external-model quality, historical replay and operator deployment.

No new release has been staged or published, and no production deployment has
been performed. Exact-source CI and independent public distribution checks remain
required after all four changes merge.
