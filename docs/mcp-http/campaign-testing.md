# Repeatable HTTP MCP campaign checks

Test a business capability through HTTP MCP, then independently observe its backend
state, UI or actual provider effect. API-only calls cannot establish an MCP pass.
Python here is external test tooling; the application and MCP runtime remain C#.

The complete beta.8 requirements and acceptance gate remain in the
[canonical campaign guide](../implementation/0.1.2-beta.8-campaign.md),
[operation catalog](../contracts/mcp-operation-catalog.json) and
[offline validator](../../tools/ci/validate-beta8-campaign.py). Retain their fixed
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
Other business records still make these captures private. Do not commit them.

The runner accepts only catalog-advertised GET/read-only operations. It refuses
schema drift, redirects, plaintext HTTP, missing assertions, mismatched responses
and automatic retries. Its summary describes selected supporting reads, not a full
campaign receipt. Volatile responses may legitimately differ between reads; keep
the failed captures and use a dedicated case with precise durable assertions rather
than broadly ignoring changed fields.

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
claim deployed MCP operation coverage or change the immutable beta.8 gate.
