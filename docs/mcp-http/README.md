# HTTP MCP

HTTP MCP is optional. It uses the existing `rateldesk-mcp-http` image and can
share the Web public origin or use an explicitly configured standalone origin.

- [Configuration and authentication](configuration.md): all host/outbound
  settings, modes, secret sources, precedence and trust boundaries.
- [Setup and deployment](deployment.md): client versus host setup, source and
  release Compose, persisted configuration, migration and rotation.
- [Troubleshooting and verification](troubleshooting.md): separate routing,
  authentication, initialize and permitted read-only tool checks.
- [Web gateway](../deployment/web-gateway.md): single-origin route ownership,
  headers, streaming and overrides.
- [CLI/stdio and client profiles](../integrations/cli-stdio.md) and
  [browser-approved pairing](../integrations/browser-pairing.md).

Keep runnable recipes in the [Docker HTTP MCP example](../../docker/examples/mcp-http/README.md).
