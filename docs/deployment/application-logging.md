# Diagnostic logging

RatelDesk defaults to **Warning** diagnostics. API (including restricted first-run
bootstrap), Web, HTTP MCP, stdio MCP and the Aspire AppHost suppress Trace, Debug
and Information unless an operator opts in. Warning, Error and Critical remain
enabled, including Polly warnings. Business records, audit history, notifications
created by business workflows, metrics and traces keep their own persistence and
collection settings; changing diagnostic verbosity does not disable them.

## Configuration

One startup-validated headless Options contract controls actual diagnostic
emission and forwarding:

| Configuration key | Environment variable | Default |
| --- | --- | --- |
| `RatelDesk:Logging:MinimumLevel` | `RatelDesk__Logging__MinimumLevel` | `Warning` |
| `RatelDesk:Logging:Overrides:<category>` | `RatelDesk__Logging__Overrides__<category>` | No overrides |

Use named levels `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`
or `None` (case insensitive). `None` disables diagnostics for the matching scope.
Numeric levels, unknown fields, invalid levels and invalid category settings fail
startup without echoing their values. At most 64 category prefixes are supported;
each must be nonempty, at most 128 characters, and contain no control characters.

Category matching is a literal, case-insensitive prefix match. The **longest**
matching prefix wins; otherwise the global minimum applies. Prefixes are not
regular expressions or wildcards. For example `Helpdesk.Infrastructure` matches
`Helpdesk.Infrastructure.AiAssistant`; a more specific prefix can override it.

Enable global diagnostics in a shell before starting a process:

```sh
export RatelDesk__Logging__MinimumLevel=Information
```

Enable a narrow category while keeping the global Warning default (quoted `env`
arguments support category names containing dots):

```sh
env 'RatelDesk__Logging__Overrides__Helpdesk.Infrastructure.AiAssistant=Debug' \
  dotnet Helpdesk.API.dll
```

For Compose, inject these keys into the selected service's `environment` map.
The shipped `RATELDESK_LOG_MINIMUM_LEVEL` interpolation alias is explicitly mapped
to `RatelDesk__Logging__MinimumLevel` inside application containers. An entry in
a Compose `.env` file by itself does not inject an arbitrary .NET setting. Use
an environment overlay to enable selected categories:

```yaml
services:
  api:
    environment:
      RatelDesk__Logging__Overrides__Helpdesk.Infrastructure.AiAssistant: Debug
      RatelDesk__Logging__Overrides__Polly: Information
```

The normal .NET host configuration order applies **within these canonical keys**:
base `appsettings.json`, environment-specific appsettings, Development user
secrets where enabled, environment variables, then command-line configuration
arguments. Later providers win for the same key. For example
`--RatelDesk:Logging:MinimumLevel=Debug` overrides its environment value on normal
host/stdio startup. Configuration is snapshotted during logging registration;
restart the process after changing it. Environment changes in containers require
recreation to inject the new environment.

Shipped `Logging:LogLevel` defaults are Warning. Legacy `Logging__LogLevel__...`
and `Serilog__MinimumLevel__...` settings do **not** own this policy and cannot
silently veto canonical opt-ins or restore Information defaults. Migrate explicit
verbosity settings to `RatelDesk:Logging`. The existing
`Observability:VerboseDependencyLogs=true` remains a compatibility opt-in to
Information for EF database commands, `System.Net.Http.HttpClient` and Polly.
An explicitly supplied canonical global minimum disables this legacy shortcut;
a covering canonical category override takes precedence over that dependency's
shortcut. `false`, missing or malformed legacy boolean values never enable it.
Shipped Compose recipes inject the canonical global Warning value through the
friendly alias, so use `RATELDESK_LOG_MINIMUM_LEVEL` or canonical category settings
there instead of relying on `Observability:VerboseDependencyLogs`.

Framework request-start/finish and reverse-proxy diagnostic categories
(`Microsoft.AspNetCore.Hosting.Diagnostics` and `Yarp.ReverseProxy`) retain a
Warning floor even during verbose opt-in. Their full-URL records can include
one-time public-page/setup capabilities. Application diagnostics and supported
dependency opt-ins remain available without enabling those capability-bearing
records. Existing redaction of secrets in operational buffers and MCP responses
remains in place. Keep verbose collection temporary and restrict access to logs.

## Providers and protocols

API, Web and HTTP MCP use Serilog to write structured compact JSON once to the
hosted console. The Aspire AppHost retains its standard provider with the shared
threshold policy. Serilog events
also reach the existing OpenTelemetry, notification and AI Agent Ops consumers.
The redundant default .NET Console provider is removed; other consumers remain.
The notification queue stays bounded at 2,048 entries. Routine provider diagnostics
do not bypass the configured threshold. A provider failure emits at most one
generic stderr diagnostic per provider lifetime, without exception/secret text,
and cannot recursively create notifications.

Without `OTEL_EXPORTER_OTLP_ENDPOINT`, hosted console logging is enabled. With an
OTLP endpoint, the default is console disabled, retaining OTLP export. The existing
`Observability:ConsoleLogsEnabled` / `Observability__ConsoleLogsEnabled` boolean
explicitly enables or disables the Serilog console sink. Diagnostic verbosity
applies to both routes; metrics and traces are independent.

Normal stdio MCP stdout remains JSON-RPC-only. Admitted diagnostics (including
verbose startup/initialize logs) use stderr. Invalid logging configuration exits
stdio with code **78**, empty stdout and a safe stderr explanation. Explicit
help/version, CLI command results and the setup terminal UI retain their normal
output contracts. Setup's interactive renderer does not turn diagnostics into
terminal content.

Diagnostic sinks write no new application log files. Container retention is a
separate Docker setting; use the [deployment guide](README.md) and
[MCP hosting guide](../mcp-http/setup.md) for size/count inputs and recreation.
Application Options cannot change the Docker daemon's existing `HostConfig`.
