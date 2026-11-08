# Contracts

- [Ticket list queries](ticket-list-queries.md)
- [MCP operation contracts and authority](mcp-operation-contracts.md),
  [operation catalog](mcp-operation-catalog.json) and
  [immutable beta.3 baseline](mcp-operation-catalog-beta3.json) and
  [beta.4 baseline](mcp-operation-catalog-beta4.json), plus the exact
  [beta.7 source baseline](mcp-operation-catalog-beta7.json) and
  [beta.10 baseline](mcp-operation-catalog-beta10.json) and
  [beta.13 baseline](mcp-operation-catalog-beta13.json), plus the
  [retained beta.14 operation mapping](mcp-operation-inventory-delta.json) and
  [retained beta.4 mapping](mcp-operation-inventory-delta-beta4.json) and
  [retained beta.7 mapping](mcp-operation-inventory-delta-beta7.json) and
  [retained beta.8 mapping](mcp-operation-inventory-delta-beta8.json) and
  [mapping shipped with beta.13](mcp-operation-inventory-delta-beta13.json)
- [System pairing protocol v1](bostec-pairing.v1.md)
- [RatelDesk incident-create v1](rateldesk-incident-create.v1.md),
  [schema](rateldesk-incident-create.v1.json),
  [frozen fixtures](rateldesk-incident-create.v1.fixtures.json) and
  [checksums](rateldesk-incident-create.v1.SHA256SUMS)

These versioned contracts remain distinct from account-owned browser pairing.
The current catalog is revision `2026-11-08.beta8-contracts-v28`: 390 operations
on 37 tools. The retained beta.13-to-beta.14 mapping preserves every operation
identity and records 26 schema changes for numeric-string enum compatibility and
incomplete draft change updates. The 200-character task-search boundary is
enforced consistently. Historical catalogs and mappings retain their original
bytes and do not establish acceptance of this candidate.

`helpdesk_datasets.create` also accepts optional nullable UUID
`request.body.requestId` for retrying the same explicit creation draft. Omitting
it preserves legacy creation behavior, including the default schema when columns
are omitted. Supplying it requires a nonempty UUID and an explicit valid schema.
This adds a field to an existing operation; the counts remain 390 operations on
37 tools. See [dataset request semantics](mcp-operation-contracts.md#typed-operator-adapters).
