# Installed dotnet-skills

This repository installs all 39 skills from
[Aaronontheweb/dotnet-skills](https://github.com/Aaronontheweb/dotnet-skills),
copied from its `skills/` directory into `.agents/skills`. Codex automatically
discovers this repository location, including when launched from a subdirectory;
see [Codex skill discovery](https://developers.openai.com/codex/skills).
[GitHub Copilot](https://docs.github.com/en/copilot/concepts/customizations/agent-skills)
also supports `.agents/skills`. These are ordinary tracked directories, so a fresh
Windows or Linux checkout contains the same skills without symlink setup.

The complete reference files are retained. Two explicit contents-link anchors are
added for offline link validation; these small local patches are recorded in the
source manifest.
The skills are advisory references used through the routing guidance in the root
[AGENTS.md](../../AGENTS.md); they do not replace repository requirements.

Source revision: `5ca5db8cf25566f19d2a5dc53204d4af22b23a0d`.
License: [MIT](LICENSE.dotnet-skills). The exact skill names and paths are recorded
in [dotnet-skills-source.json](dotnet-skills-source.json).

To update, fetch and review the upstream revision, replace the installed skill
directories from that revision's `skills/`, and update the source record and this
index together. Keep the license and review upstream changes before adopting them.
Do not silently download new instructions during a build or release.

## Use in Codex

Open this repository in the Codex CLI or IDE extension and use `/skills` to inspect
the discovered skills, or invoke one by name, such as `$aot-trimming` or
`$modern-csharp-coding-standards`. Agents can also read the indexed files directly.
No marketplace command is needed for this checked-in repository installation.

For skills shared across repositories, a CLI runtime that supports the upstream
plugin marketplace can additionally install the runtime plugin:

```bash
codex plugin marketplace add Aaronontheweb/dotnet-skills
codex plugin add dotnet-skills@dotnet-skills
codex plugin list
```

`codex plugin list` verifies that runtime plugin installation; it does not list
the checked-in repository skills. Keep the repository source pin explicit when
updating this copy, even if a runtime plugin has been updated separately.

Specialized Claude Code agents are not part of this portable project installation.

Slopwatch is pinned as a local tool in `.config/dotnet-tools.json`:

```bash
dotnet tool restore
dotnet tool run slopwatch -- analyze --fail-on warning --exclude '**/bin/**,**/obj/**'
```

Its initial baseline records existing findings from reviewed main
`c31b15ff15dce2ca3ba7c3b73ac39c99adf85db3`, before this UX implementation.
Do not expand it to accept new findings. The initial capture excludes generated
`bin`/`obj` output; preserve that boundary when analyzing a built workspace.

## Skill index

| Invocation name | Reference |
| --- | --- |
| akka-net-aspire-configuration | [SKILL.md](akka-aspire-configuration/SKILL.md) |
| akka-net-best-practices | [SKILL.md](akka-best-practices/SKILL.md) |
| akka-hosting-actor-patterns | [SKILL.md](akka-hosting-actor-patterns/SKILL.md) |
| akka-net-management | [SKILL.md](akka-management/SKILL.md) |
| akka-net-testing-patterns | [SKILL.md](akka-testing-patterns/SKILL.md) |
| aot-trimming | [SKILL.md](aot-trimming/SKILL.md) |
| aspire-configuration | [SKILL.md](aspire-configuration/SKILL.md) |
| aspire-integration-testing | [SKILL.md](aspire-integration-testing/SKILL.md) |
| mailpit-integration | [SKILL.md](aspire-mailpit-integration/SKILL.md) |
| aspire-service-defaults | [SKILL.md](aspire-service-defaults/SKILL.md) |
| crap-analysis | [SKILL.md](crap-analysis/SKILL.md) |
| cscheck | [SKILL.md](cscheck/SKILL.md) |
| api-design | [SKILL.md](csharp-api-design/SKILL.md) |
| modern-csharp-coding-standards | [SKILL.md](csharp-coding-standards/SKILL.md) |
| csharp-concurrency-patterns | [SKILL.md](csharp-concurrency-patterns/SKILL.md) |
| csharp-nullable-reference-types | [SKILL.md](csharp-nullable-reference-types/SKILL.md) |
| type-design-performance | [SKILL.md](csharp-type-design-performance/SKILL.md) |
| database-performance | [SKILL.md](database-performance/SKILL.md) |
| dotnet-devcert-trust | [SKILL.md](dotnet-devcert-trust/SKILL.md) |
| efcore-patterns | [SKILL.md](efcore-patterns/SKILL.md) |
| ilspy-decompile | [SKILL.md](ilspy-decompile/SKILL.md) |
| dotnet-local-tools | [SKILL.md](local-tools/SKILL.md) |
| marketplace-publishing | [SKILL.md](marketplace-publishing/SKILL.md) |
| microsoft-extensions-configuration | [SKILL.md](microsoft-extensions-configuration/SKILL.md) |
| dependency-injection-patterns | [SKILL.md](microsoft-extensions-dependency-injection/SKILL.md) |
| mjml-email-templates | [SKILL.md](mjml-email-templates/SKILL.md) |
| opentelemetry-net-instrumentation | [SKILL.md](opentelementry-dotnet-instrumentation/SKILL.md) |
| package-management | [SKILL.md](package-management/SKILL.md) |
| playwright-blazor-testing | [SKILL.md](playwright-blazor/SKILL.md) |
| playwright-ci-caching | [SKILL.md](playwright-ci-caching/SKILL.md) |
| dotnet-project-structure | [SKILL.md](project-structure/SKILL.md) |
| r3-reactive-extensions | [SKILL.md](r3-reactive-extensions/SKILL.md) |
| sdk-container-publishing | [SKILL.md](sdk-container-publishing/SKILL.md) |
| serialization | [SKILL.md](serialization/SKILL.md) |
| skills-index-snippets | [SKILL.md](skills-index-snippets/SKILL.md) |
| dotnet-slopwatch | [SKILL.md](slopwatch/SKILL.md) |
| snapshot-testing | [SKILL.md](snapshot-testing/SKILL.md) |
| testcontainers-integration-tests | [SKILL.md](testcontainers/SKILL.md) |
| verify-email-snapshots | [SKILL.md](verify-email-snapshots/SKILL.md) |
