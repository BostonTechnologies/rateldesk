# Contracts

- [Ticket list queries](ticket-list-queries.md)
- [MCP operation contracts and authority](mcp-operation-contracts.md),
  [operation catalog](mcp-operation-catalog.json) and
  [immutable beta.3 baseline](mcp-operation-catalog-beta3.json) and
  [beta.4 baseline](mcp-operation-catalog-beta4.json), plus the exact
  [beta.7 source baseline](mcp-operation-catalog-beta7.json) and
  [beta.10 baseline](mcp-operation-catalog-beta10.json) and
  [beta.13 baseline](mcp-operation-catalog-beta13.json), plus the
  [current old/new operation mapping](mcp-operation-inventory-delta.json) and
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
The current catalog is revision `2026-10-29.beta14-contracts-v26`: 390 operations
on 37 tools. Its beta.13-to-beta.14 mapping retains every operation identity and
records 26 schema changes for existing numeric-string enum compatibility and
incomplete draft change updates. The 200-character task-search boundary is
enforced consistently. Historical catalogs and mappings retain their original
bytes and do not establish acceptance of this candidate.
