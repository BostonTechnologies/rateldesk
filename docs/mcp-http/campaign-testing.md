# Repeatable HTTP MCP campaign checks

For the current release, use the [beta.13 laptop campaign](beta13-laptop-campaign.md)
and its [current workspace and receipt gate](../../tools/ci/validate-current-campaign.py).
It preserves all 390 operations, 103 domain cases, 86 historical obligations and
nine cleanup assertions. Its granular fixture checklist and read-only mapping
plan help prepare fresh evidence. Its workspace starts entirely `NOT_RUN`;
previous receipts stay unchanged.

The read runner independently adapts the workflow metrics API's explicit
Prometheus `text/plain` contract to `{format:"prometheus",text:...}`, matching MCP.
Other API reads still require JSON. Include a fixture-specific `/text` assertion;
for logs, assert the actual known warning in `/items/0/message` and its trace,
so an empty search cannot establish a positive result. See the
[beta.9 finding/retest map](../implementation/0.1.2-beta.9-mcp-regressions.md).

Test a business capability through HTTP MCP, then independently observe its backend
state, UI or actual provider effect. API-only calls cannot establish an MCP pass.
Python here is external test tooling; the application and MCP runtime remain C#.

The substantive historical requirements remain in the
[beta.8 acceptance contract guide](../implementation/0.1.2-beta.8-campaign.md),
[operation catalog](../contracts/mcp-operation-catalog.json) and
[immutable beta.8 validator](../../tools/ci/validate-beta8-campaign.py). The current
adapter binds the published manifest/catalog and retains the beta.11 evidence checks.
Retain their fixed
denominators, mode exclusions, independent evidence, provider receipts, soak and
cleanup requirements. Source CI and a subset of live reads cannot satisfy that gate.

## Run selected live read checks

The [read harness](../../tests/http_mcp/campaign_reads.py) is a reusable starting
point for checking HTTP MCP/backend communication on owned fixtures. It initializes
the actual endpoint, compares its complete tools/schema with the supplied catalog,
executes selected advertised reads, then reads the corresponding API with a fresh
request-scoped execution token. Both observations and fixture-specific typed JSON
pointer assertions must agree. Empty results do not count as positive fixture coverage.

Use a private directory outside the checkout with access restricted to the campaign
operator, including its parent directory on Windows. Place a profile there:

```json
{
  "resourceUri": "https://helpdesk.example.invalid/mcp",
  "credentialFile": "approved-mcp-credential.key"
}
```

Store the explicitly approved, account-bound MCP-purpose credential in that separate
file. Use an ordinary scoped credential for ordinary access checks and a separately
approved resource-bound owner grant where the case requires owner authority. The
harness never discovers ambient CLI, browser, NetRatel or another client's credentials.

Copy the [example plan](../../tests/http_mcp/read-plan.example.json) into the private
directory and replace its placeholders with actual owned fixture values. Verify the
deployment source/version/image pins and released catalog hashes using the full
campaign preflight before running a plan. Install dependencies into a dedicated
environment, then run from the repository root:

```sh
python -m pip install -r tests/http_mcp/requirements.txt
python tests/http_mcp/campaign_reads.py \
  --catalog docs/contracts/mcp-operation-catalog.json \
  --plan /private/campaign/read-plan.json \
  --profile /private/campaign/profile.json \
  --output /private/campaign/new-read-captures
```

PowerShell users can run the same command on one line with absolute Windows paths.
The output directory must be new, its parent must exist, and the profile, credential
and output must be outside the checkout. Each run uses fresh RPC IDs and hashed
sanitized JSON captures. Tokens stay in memory and known confidential fields, signed
URL tokens and duplicate MCP JSON text are redacted before capture persistence.
Regression checks cover Unicode-escaped secrets in duplicate JSON text and signed
URL tokens inside HTML-escaped links.
Other business records still make these captures private. Do not commit them.

The runner accepts only catalog-advertised GET/read-only operations. It refuses
schema drift, redirects, plaintext HTTP, missing assertions, mismatched responses
and automatic retries. Its summary describes selected supporting reads, not a full
campaign receipt. Raw observations are retained unchanged. A case may explicitly
set `slaCountdownToleranceSeconds` to an integer from 1 to 5 when sequential reads
differ only at `/sla/responseRemainingSeconds` and
`/sla/resolutionRemainingSeconds`. Both fields must be integer counters within that
tolerance; all other fields remain exact and the assertions must identify stable
fixture values. The default remains exact comparison. This does not relax SLA state,
deadline, identity, count, notification or business-effect comparisons.

### Discover current selectors before reading dependent records

The [discovery plan](../../tests/http_mcp/read-plan-discovery.example.json) first
selects the current campaign's uniquely named owned incident. The discovery read
must pass independent MCP/API parity and typed marker/count assertions. Only then
does `${ownedIncident.id}` resolve for later requests. References must occupy the
whole string and resolve from this run; unknown references, empty results and
non-scalar selector values fail before dependent reads. A discovery plan does not
create fixtures: first create the owned record through its confirmed MCP lane and
record it in the campaign fixture registry. Never substitute IDs copied from an
older campaign whose fixtures were deleted.

### Probe actual HTTP framing and size boundaries

The [transport runner](../../tests/http_mcp/campaign_transport.py) sends valid
read-only `initialize` requests on `/mcp` and `/mcp/`, using both Content-Length
and chunked framing at the decoded body limit minus one, exactly the limit, and
the limit plus one. It also rechecks the previously reported 29,990,208-byte
chunked case at the default limit. It checks the pinned API identity before and
after, captures structured 413 responses, and records transport resets as failures.
The JSON/whitespace body hash and byte count are retained without storing thirteen
large duplicate payloads. It never invokes `tools/call` or retries a failed probe.

```sh
python tests/http_mcp/campaign_transport.py \
  --profile /private/campaign/profile.json \
  --manifest /private/campaign/release/release-manifest.json \
  --evidence-root /private/campaign \
  --output /private/campaign/transport-boundary
```

The default limit is 30,000,000 decoded bytes. Independently capture the effective
deployment configuration even when using that default. If configuration lowers
the limit, pass that exact `--limit`. The runner selects the beta.11 or beta.12
summary format from the exact pinned manifest. The beta.12 final validator requires
current Web and MCP host configuration evidence captured before the complete live
matrix. Source socket tests do not establish that the deployment's external ingress
has the same configuration.

## Expand coverage with application capabilities

When adding application behavior, update the typed API and MCP operation schemas,
regenerate the operation catalog through the existing source workflow, and add the
operation to the campaign contract. Add owned fixture plans and specific independent
postconditions for the new behavior. Preserve ordinary-access and foreign-tenant
denials alongside owner success. Record public provider contract regressions in the
owning C# test project; record deployed executions separately.

For mutations, extend a dedicated campaign lane with an unchanged preview, one
confirmed MCP execution and an independent persisted effect. Record fixture ownership
before dependent work. Reconcile uncertain outcomes before considering a retry;
never resend an external effect solely because the client timed out. Read-only
parity is insufficient for provider dispatch, approval commits, mail delivery,
idempotency, revocation, concurrency and cleanup.

Plans use flat route/query selectors and scalar query lists. Null optional query
values are omitted; nested query objects need a dedicated lane. HTTP denials retain
a bounded, sanitized structured error capture and stop the run without a retry.

Keep generic code and synthetic plans in the repository. Keep credentials, passwords,
one-time grants, private hosts, live customer data, raw captures, evidence archives
and deployment-specific fixture registries in the private campaign directory. Publish
reviewed findings without those identifiers. Never copy a laptop campaign folder
wholesale into this public repository.

Run the offline harness regressions with:

```sh
python -m unittest discover -s tests/http_mcp -p 'test_*.py'
```

These synthetic offline tests verify the harness's failure boundaries. They do not
claim deployed MCP operation coverage or change historical receipts.
