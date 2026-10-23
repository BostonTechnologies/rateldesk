# Contributing to RatelDesk

Open an issue before substantial changes. Keep changes focused, include tests, and do not add real customer data, credentials, infrastructure identifiers, or screenshots from private environments.

Run `dotnet build Helpdesk.sln` and `dotnet test Helpdesk.sln` before opening a pull request. Configuration examples must use reserved example values and secrets must be injected at runtime.

Follow [AGENTS.md](AGENTS.md) and start documentation work from the
[documentation index](docs/README.md). Update the canonical guide in its existing
category and move all affected links/distribution references together. Run
`tools/ci/validate-layout.sh`; its fast documentation check validates docs-root
placement and local links with case-sensitive paths.
