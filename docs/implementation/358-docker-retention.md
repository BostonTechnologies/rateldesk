# #358 Docker retention implementation and focused acceptance

Implementation base: `ee7067d12dafb22de81b70a58f61b211b9e37226` (main / beta.5).
This record covers the deployment half of [#358](https://github.com/BostonTechnologies/RatelDesk/issues/358).
The companion application logging change owns the validated runtime policy.
Source verification, merged-main CI, immutable release publication and later
operator deployment are separate receipts.

## Change

- Source/release base recipes, all optional MCP overlays/examples, setup tooling,
  config initializers and bundled PostgreSQL/acceptance infrastructure use
  `json-file`, string `max-size: 200m` / `max-file: 5` defaults, configurable via
  `RATELDESK_DOCKER_LOG_MAX_SIZE` / `RATELDESK_DOCKER_LOG_MAX_FILE`.
- Application services explicitly map `RATELDESK_LOG_MINIMUM_LEVEL` to
  `RatelDesk__Logging__MinimumLevel` (Warning default). Interpolation inputs are
  not assumed to be process environment variables.
- Fresh `ComposeOverlay` exports receive bounded defaults. Effective-deployment
  proposals identify existing service roles before merging, preserving consumer
  logging drivers/options, absent daemon-default policies and explicit diagnostic
  verbosity. A genuinely new legacy initializer gets concrete reviewable default
  retention, avoiding a second interpolation after an approved rendered snapshot.
- Existing Verify remains read-only. Existing services receive no implicit log
  policy change; add/change rotation in the owning source through normal review,
  owner apply, affected-container recreation and fresh verification. Process or
  Docker restart retains the previous HostConfig.

The canonical [self-hosting guide](../deployment/self-hosting.md#container-log-retention)
and [HTTP MCP reference](../mcp-http/deployment.md#logging-and-container-retention)
explain Docker binary units, five files including the active file, approximately
1 GB combined per container, independent service/replica budgets, consumer driver
replacement and recreation. No application Docker socket or additional file sink
was introduced.

## Focused evidence — 7 November 2026

- SDK `10.0.400`; Docker Engine `28.4.0` with local `vfs`; Compose `v2.40.3`.
- Current-source `dotnet test` filtered to `HttpMcpHostingCoordinatorTests` and
  `HttpMcpEffectiveDeploymentTests` passed all **159** tests, with zero failures
  and zero skips; the coverage run independently passed the same 159 tests.
  OpenCover recorded `ComposeOverlay` at 100% sequence/branch coverage with
  cyclomatic complexity 26. Existing `Create` was 98% sequence / 88.46% branch
  coverage (complexity 26), so this change adds no untested high-risk branch.
  Existing Python source/owner regressions also passed: 62 launcher,
  19 source-review and 13 legacy-recipe tests.
- `tools/ci/test-mcp-host-recipes.py`: six original hosting contracts and 31 actual
  effective logging contracts. Fifteen supported recipe combinations at defaults
  and at `8m` / `2` / Debug, plus explicit consumer syslog replacement, passed.
  Assertions check actual rendered string types and container environment mapping.
- Disposable local `postgres:16` containers ran only a harmless synthetic shell
  logger; no database server or production data was used. Container `492b14bd36f1`
  had HostConfig `json-file`, `max-size=200m`, `max-file=5`; Docker restart retained
  the same container identity and HostConfig. After removal/recreation,
  `528524e8257c` had `max-size=4k`, `max-file=2`. Of 1,000 synthetic lines, Docker
  retained the last 40 (2,239 text bytes), retained line 999, and discarded line 0.
  Both disposable containers were removed. No 1 GB output was generated.
- A second real Compose acceptance reused the release API recipe's exact
  uninterpolated logging block with the same harmless synthetic logger. Actual
  default container `e7e6ee1f064a` had `200m` / `5`; Compose force-recreation with
  explicit `4k` / `2` inputs produced new container `52a3e01cf8ea` with those
  HostConfig options. Rollover retained the final 40 records (2,559 text bytes),
  discarded the initial record and retained the last. The disposable Compose
  project and network were removed.
- The exact deployment block of `tools/release/package-assets.sh` was executed in
  isolation with synthetic package version `0.0.0-log-retention`, without mocking
  Docker or reconstructing the packaging recipe. Its real archive was extracted.
  `tools/ci/test-deployment-archive-compose-config.sh` passed bundled documentation
  validation (174 Markdown files, 781 links), extracted launcher execution and
  12 effective logging contracts over six default/overridden archive recipes.
  This focused archive used the companion application guide; the actual published
  beta archive still requires independent final-source extraction verification.
- Source documentation validation passed (183 Markdown files, 853 local links)
  with the companion guide present. Slopwatch reported zero new findings without
  changing exclusions or its baseline.

## Handoff

The owning .NET tests extend `HttpMcpHostingCoordinatorTests` and
`HttpMcpEffectiveDeploymentTests`, including new export defaults, concrete new
initializer policy, unchanged Verify and preservation of absent/empty/json-file/
local/syslog configurations during existing environment corrections. The final
batch PR/merge and exact-source required CI/publication receipts are recorded by
the campaign coordinator. This implementation record claims no production
recreation or operator deployment acceptance.
