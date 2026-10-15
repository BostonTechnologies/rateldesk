# Isolated ticket email display — #164

Evidence recorded 2026-10-15 against implementation `4ad221e`, with the shared
activity tabs dependency `8de40d2`. All content, accounts and image tokens in
these fixtures are synthetic. Current behavior and the actual per-ticket API
and authorization inventory are documented in [Ticket email display](../customization/email-display.md).

## Later integrated evidence

The [integrated acceptance receipt](workspace-0.1.1.md#integrated-evidence-superseding-earlier-receipts)
records hosted **13 email browser + 4 native 200% zoom passes** at `16963fa` and
the passing full .NET suite, including the owning tests described below. The
isolated Debug measurements and CSS zoom results retain their historical scope;
they are not substituted for the native check or final-head release gates.

## Reproduction and rendered result

The before fixture applies the pre-change `.ticket-message-body` blanket
foreground rule to a synthetic light email. Chromium computed
`rgb(237, 241, 245)` text on `rgb(255, 255, 255)` background: contrast **1.135:1**.
The after fixtures run the real Blazor incident list and detail components.
They preserve the original `rgb(32, 32, 32)` foreground and white background:
contrast **16.293:1**, in both application themes at 320, 768 and 1920 CSS pixels.
These are computed browser values, not assertions about stylesheet source.

- [Before: pale foreground on light email](assets/email-display-164/before-dark-washed-out.png)
- [After: dark application, mobile](assets/email-display-164/after-dark-320.png)
- [After: light application, mobile](assets/email-display-164/after-light-320.png)
- [After: dark application, desktop](assets/email-display-164/after-dark-1920.png)
- [After: light application, desktop](assets/email-display-164/after-light-1920.png)
- [All measurements, environment and individual test results](assets/email-display-164/metrics.json)

The browser checks also assert the original dark email's own foreground and
background, readable defaults when one colour is absent, coloured tables,
complete plain text, no external tracking loads or email-script execution,
actual signed inline-image loading, missing-image descriptions, opaque iframe
origin and rejection of spoofed bridge messages. Hover/focus persistence,
Escape from inside the iframe, activation focus, theme changes while open,
loading/retry/empty states and the request-specific plain-text API are exercised.

The tests found and fixed horizontal clipping at 320 pixels. The final suite
also exercises **CSS `zoom: 2` at 1280×900**, verifying enlarged preview controls
and scrolling fit the viewport. A separate 640×450 CSS viewport with device scale
2 records equivalent viewport coverage. Neither is described as browser-chrome
native zoom automation.

## Commands and result

Linux, pinned SDK **10.0.400**, Node **24.19.0**, Chromium **153.0.8010.12**,
`DOTNET_PROCESSOR_COUNT=2`, Debug Web build and loopback fixture API:

```sh
dotnet build src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --nologo
PLAYWRIGHT_JSON_OUTPUT_FILE=work/email-browser-results.json node node_modules/@playwright/test/cli.js test --config playwright.email.config.ts --reporter=list,json
```

Web build: **0 warnings, 0 errors**. Final browser run: **13 passed, 0 failed,
0 skipped, 0 flaky**, 75.024 seconds. Owning CI can set
`HELPDESK_EMAIL_CONFIGURATION=Release` to exercise its Release build.

The committed `EmailDisplayDocumentTests`, `TicketMessagesRenderedTests` and
additional `IncidentCcRecipientsEndpointsTests` cover sanitizer/content API
contracts, stale requests/disposal, complete text, unchanged stored content and
existing administrative/image-token boundaries. The local full test-project
build was stopped for shared-memory constraints before these tests executed;
the integrated solution run supplies their authoritative result. This UI run
does not claim database-provider or production deployment validation.
