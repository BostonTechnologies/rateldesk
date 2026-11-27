# NetRatel incident receiver with queue routing

The receiver negotiates explicit queue routing through
`GET /api/v1/integrations/netratel/queue-capabilities`. An approved source sends
its `X-NetRatel-Source-Instance` header and authenticates with the paired
service principal for its incident-create capability. The response identifies `rateldesk.incident-create.v3`,
the receiver instance, source instance, source namespace, organization and
customer. Resolve `queuePath`, `createPath` and `receiptPath` against the
connector's configured receiver origin. Bind them to the same receiver and
source before preparing a request.

`GET /api/v1/integrations/netratel/queues` returns stable `id` and `name`
values for enabled destinations approved for that source and credential.
Optional `q` searches names, `id` resolves a selection and `take` bounds the
response from 1 to 100. Authorization runs before search and limiting.

## Create and replay

Send a JSON object to `POST /api/v1/integrations/netratel/queued-incidents`
with the existing source and `Idempotency-Key` headers. The object contains the
three members in this table. [JSON Schema](rateldesk-incident-create.v3.json)

| Member | Value |
| --- | --- |
| `contractVersion` | `rateldesk.incident-create.v3` |
| `assignedSupportGroupId` | A named queue ID returned by the authorized lookup |
| `incident` | A complete typed [v2 incident request](rateldesk-incident-create.v2.md) |

The nested request retains its v2 version and normalization. The receiver rejects
unknown or duplicate members. The queue ID is case sensitive, is at most 64
characters, and uses ASCII letters, digits, hyphens or underscores.

The v3 fingerprint is the 64-character lowercase hexadecimal SHA-256 of the
UTF-8 bytes formed by joining the following three strings with a single line feed.

1. `rateldesk.incident-create.v3`
2. The 64-character v2 fingerprint of the normalized nested request
3. The exact queue ID

The byte sequence ends with the queue ID. The v2 fixture's `create` object
with queue `app-vendor` produces fingerprint
`bcfab6e3eb8b9e5a3a5f20d258bb2098e8af3e471c02cdd299ae5715ca4300b9`. [V2 fixture](rateldesk-incident-create.v2.fixtures.json)

Save the version, selected ID, source namespace, key and complete prepared
request before dispatch. Retry with the
same bytes and key. A different fingerprint in that namespace returns HTTP 409,
including switching between v2 and v3 or changing the queue. Invalid or revoked
destinations return `queue-not-authorized`.

The accepted response retains the v2 outer response and receipt fields. The
receipt's `contractVersion` is v3 and `assignedSupportGroupId` is the queue at
acceptance. A 201 response records the incident, assignment, receipt and
notification effect in one transaction. An authorized replay returns the
original JSON and Location with HTTP 200 and `Idempotency-Replayed: true`.
For recovery, send the original source and key headers to
`GET /api/v1/integrations/netratel/queue-receipt`. The receipt records acceptance
even when a later authorized operation changes the ticket's current queue.
Current source, route and credential authority are checked again before replay.

## Approving routes

An interactive instance administrator can read the allowlist and current
revision at `GET /api/v1/integrations/netratel/sources/{namespaceId}/queues`.
Replace it with `PUT` to the same path using `expectedRevision` and a
`supportGroupIds` array. An empty array revokes all explicit destinations.
Each group belongs to the source organization or serves it through enabled
provider coverage and the existing support relationship. Paired service
principals require an approved receiver source binding and the existing
incident-create or receipt capability for the requested action. Ordinary account-bound API credentials can create a named incident
through the ticket workspace API when they have current queue transfer and
incident write authority.

Existing v1 and v2 paths, fingerprints and accepted receipts keep their wire
contracts. New v1 and v2 requests continue to create in Main Queue. Configure
each NetRatel flow to use v3 only after the receiver is published and advertises
the capability. Surface validation or permission failures on an explicitly
routed action for operator review. The companion writer is tracked in NetRatel #862.
