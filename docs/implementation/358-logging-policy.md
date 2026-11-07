# #358 application diagnostic policy evidence

Scope: source implementation and synthetic acceptance. This document establishes
no production deployment or public-release receipt.

Reviewed baseline: `ee7067d12dafb22de81b70a58f61b211b9e37226` (published beta.5).
Implementation: `31b43fa834932543d99455665a703acfd5f397ed`.
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
| Emitted-event/global/category/invalid/legacy policy and early OTLP/consumer regressions | 30 passed |
| Actual API bootstrap/normal process acceptance | 4 passed |
| Actual stdio initialize/default/verbose/invalid acceptance and existing process regressions | 11 passed |
| Existing HostTelemetry/AiAgentOps regressions | 6 passed, including real PostgreSQL telemetry |
| Existing Data Management rendered regressions after removing direct Console writes | 13 passed |
| Total focused acceptance | 64 passed, zero failed or skipped; 18 seconds |
| Aspire AppHost build | Passed, zero errors or warnings |
| Slopwatch | Passed with existing baseline, zero new findings |
| Documentation links and placement | Passed |

The final owning-project run used the checked-in tests and OpenCover collector.
The source policy validator has 100% sequence/branch coverage; category resolution
has 100% sequence/branch coverage; configuration loading has 96.15% sequence and
95.45% branch coverage. Their complexity/coverage CRAP scores are 14, 12 and 22.03,
respectively, below the 30 risk threshold. Existing exclusions and the Slopwatch
baseline remain unchanged. Process fixtures exercise the real stdio and API
outputs; their child processes are separate from in-process coverage collection.

Docker recipes, generated-new/existing preservation, actual HostConfig recreation
and extracted deployment archive evidence are recorded in the accompanying
retention workstream. Final merged source, reviewed PR/main CI and publication
receipts belong to the campaign delivery ledger.
