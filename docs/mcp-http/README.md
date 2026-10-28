# HTTP MCP

HTTP MCP is optional. It uses the existing `rateldesk-mcp-http` image and can
share the Web public origin or use an explicitly configured standalone origin.

- [Set up HTTP MCP](setup.md): start with the Web URL for local-account gateway
  hosting, verification and optional individual client approval.
- [Edit an existing credential's access](setup.md#edit-an-existing-credentials-access):
  maintain same-organization permissions in the browser, retain the installed
  secret and exact expiry, and check current owner/policy admission.
- [Configuration and authentication](configuration.md): complete host/outbound
  settings, explicit external Authentik/OAuth, secret sources and precedence.
- [Execution identity and authority](configuration.md#discover-the-active-execution-identity):
  live configured identity, current API access, explicit caller-bound
  [owner administration](configuration.md#approve-an-agent-to-administer-the-instance),
  external administrator/AI principals and campaign prerequisites.
- [Deployment reference](deployment.md): alternate terminals, source and
  release Compose, persisted configuration, migration and rotation.
- [Troubleshooting and verification](troubleshooting.md): separate routing,
  authentication, initialize and permitted read-only tool checks.
- [Web gateway](../deployment/web-gateway.md): single-origin route ownership,
  headers, streaming and overrides.
- [CLI/stdio and client profiles](../integrations/cli-stdio.md) and
  [browser-approved pairing](../integrations/browser-pairing.md).
- [Operation contracts, previews and transport authority](../contracts/mcp-operation-contracts.md):
  executable schemas, fixed raw reads, identifiers and required prerequisites.
- [Beta.12 laptop acceptance campaign](beta12-laptop-campaign.md): fresh release identity,
  granular fixture prerequisites, evidence mapping and the complete receipt gate.
- [Repeatable campaign checks](campaign-testing.md): private live MCP/backend read
  captures, generic repository test collateral and future capability coverage.

Keep runnable recipes in the [Docker HTTP MCP example](../../docker/examples/mcp-http/README.md).
