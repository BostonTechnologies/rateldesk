# HTTP MCP

HTTP MCP is optional. It uses the existing `rateldesk-mcp-http` image and can
share the Web public origin or use an explicitly configured standalone origin.

- [Set up HTTP MCP](setup.md): start with the Web URL for local-account gateway
  hosting, verification and optional individual client approval.
- [Configuration and authentication](configuration.md): complete host/outbound
  settings, explicit external Authentik/OAuth, secret sources and precedence.
- [Deployment reference](deployment.md): alternate terminals, source and
  release Compose, persisted configuration, migration and rotation.
- [Troubleshooting and verification](troubleshooting.md): separate routing,
  authentication, initialize and permitted read-only tool checks.
- [Web gateway](../deployment/web-gateway.md): single-origin route ownership,
  headers, streaming and overrides.
- [CLI/stdio and client profiles](../integrations/cli-stdio.md) and
  [browser-approved pairing](../integrations/browser-pairing.md).

Keep runnable recipes in the [Docker HTTP MCP example](../../docker/examples/mcp-http/README.md).
