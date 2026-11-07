# #358 application diagnostic policy evidence

Scope: source implementation and synthetic acceptance. This document establishes
no production deployment or public-release receipt.

Reviewed baseline: `ee7067d12dafb22de81b70a58f61b211b9e37226` (published beta.5).
Companion reviewed: NetRatel PR #655, merge
`ccb7b363a849f308dec418a8732c22a05c3e57f0`. RatelDesk retains its own OTLP-aware
console selection and keeps headless configuration in `Helpdesk.AgentClient`;
its Shared project forbids service logic.

## Implementation

- `RatelDeskLoggingOptions` and its startup registration own hosted and stdio
  diagnostic thresholds; canonical environment/global/category opt-ins,
  case-insensitive longest-prefix selection, bounded named settings and legacy
  precedence are documented in the [operator guide](../deployment/application-logging.md).
- API diagnostics register before restricted bootstrap construction. Early
  logging includes OTEL export when OTLP disables console; normal service
  registration reuses the same logging setup. Metrics and traces remain separate.
- Removed contradictory authentication/Helpdesk filters and Polly's Error floor.
  Packaged JSON no longer requests Information. AppHost uses the same policy and
  forwards explicitly configured canonical settings to its project resources.
- Removed only the default Console provider from Serilog's forwarded consumers.
  Notification, AI Agent Ops, OTEL and explicit consumers remain; no new file sink.
- Notification diagnostics no longer write routine console records. Failures
  produce at most one nonsecret stderr record per provider lifetime, and the
  dispatch context prevents notification-diagnostic feedback loops.
- Stdio retains stderr-only logging, safe invalid-policy exit 78, JSON-RPC-only
  stdout, and existing explicit help/version/setup command output.

## Baseline console measurement

A separate process referencing the unmodified baseline ServiceDefaults created a
normal generic host and emitted one uniquely named Warning. Its actual stdout
contained **two records**: the default `warn: Helpdesk.LoggingProbe[0]` record
and a compact Serilog JSON record with the same message. This demonstrates the
duplicate route independently of source inspection. The changed-code regression
requires one console record while checking OTEL, Notification and AI Agent Ops
delivery of that same event.

## Verification ledger

| Check | Status |
| --- | --- |
| Pinned SDK | .NET SDK 10.0.400 |
| Initial owning test-project build | Passed, zero errors; existing warnings retained |
| Emitted-event/global/category/invalid/legacy policy regressions | Running |
| Early bootstrap OTLP and normal-host consumer regressions | Running |
| Actual API bootstrap/normal process acceptance | Running |
| Actual stdio initialize/default/verbose/invalid acceptance | Running |
| Existing HostTelemetry/AiAgentOps regressions | Running |
| Slopwatch | Passed with existing baseline, zero new findings |
| Documentation links and placement | Passed |

Docker recipes, generated-new/existing preservation, actual HostConfig recreation
and extracted deployment archive evidence are recorded in the accompanying
retention workstream. Final merged source, reviewed PR/main CI and publication
receipts belong to the campaign delivery ledger.
