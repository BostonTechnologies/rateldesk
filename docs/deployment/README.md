# Deployment

- [Self-hosting](self-hosting.md): first-run setup, providers, security,
  authentication, storage, upgrades and recovery.
- [Operations console](operations-console.md): SLA, workflow diagnostics, background jobs and notifications.
- [Administration workspaces](admin-workspaces.md): data, support routing, roles and tenant SLA editing.
- [Ticket workspaces](ticket-workspaces.md): searchable creation and permission-aware state popups.
- [Web gateway](web-gateway.md): API and optional HTTP MCP routing through one
  public origin, explicit overrides and proxy behavior.
- [Application logging](application-logging.md): validated Warning defaults,
  category/environment opt-ins and console/OTLP behavior. See
  [container retention](self-hosting.md#container-log-retention) for Docker limits.
- [HTTP MCP setup](../mcp-http/setup.md): short server and client procedure;
  [deployment reference](../mcp-http/deployment.md) covers coordinated settings,
  configuration mounts and apply/recreate instructions.

Runnable source/release examples remain under [docker](../../docker/).
