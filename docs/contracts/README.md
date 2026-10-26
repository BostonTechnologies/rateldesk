# Contracts

- [Ticket list queries](ticket-list-queries.md)
- [MCP operation contracts and authority](mcp-operation-contracts.md),
  [operation catalog](mcp-operation-catalog.json) and
  [immutable beta.3 baseline](mcp-operation-catalog-beta3.json) and
  [beta.4 baseline](mcp-operation-catalog-beta4.json), plus the exact
  [beta.7 source baseline](mcp-operation-catalog-beta7.json) and
  [beta.10 baseline](mcp-operation-catalog-beta10.json), with the
  [current old/new operation mapping](mcp-operation-inventory-delta.json) and
  [retained beta.4 mapping](mcp-operation-inventory-delta-beta4.json) and
  [retained beta.7 mapping](mcp-operation-inventory-delta-beta7.json) and
  [retained beta.8 mapping](mcp-operation-inventory-delta-beta8.json)
- [System pairing protocol v1](bostec-pairing.v1.md)
- [RatelDesk incident-create v1](rateldesk-incident-create.v1.md),
  [schema](rateldesk-incident-create.v1.json),
  [frozen fixtures](rateldesk-incident-create.v1.fixtures.json) and
  [checksums](rateldesk-incident-create.v1.SHA256SUMS)

These versioned contracts remain distinct from account-owned browser pairing.
The current catalog is revision `2026-10-25.beta11-contracts-v25`: 390 operations
on 37 tools. Its beta.10-to-beta.11 mapping retains all 389 prior operation
contracts unchanged and adds confirmed inbound email rule deletion.
