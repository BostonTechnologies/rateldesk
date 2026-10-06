# Helpdesk UX Playwright checks

Run against a local Development app with `HELPDESK_E2E_AUTH_MODE=development`, or an Authentik-backed instance with `HELPDESK_E2E_AI_TOKEN`.

```bash
HELPDESK_E2E_BASE_URL=https://helpdesk.example.com \
HELPDESK_E2E_AI_TOKEN=<token> \
npm run test:ux
```

The suite deliberately has no authentication bypass. It uses the existing `/auth/ai-agent/exchange` endpoint for public dev and the local-only `/auth/development` endpoint only when explicitly selected.

Run the NetRatel service credential suite with `tools/ci/run-ux-local.sh tests/ux/netratel-service-link.spec.ts`. The runner creates an isolated PostgreSQL installation, enables its separate service issuer and supplies `HELPDESK_E2E_LOCAL_EMAIL` and `HELPDESK_E2E_LOCAL_PASSWORD` for the synthetic bootstrap administrator. The manual journey and signed-out peer approval journey sign in through the real local login form; service credential management requires an enabled human administrator, so a development system principal or AI token cannot substitute for that account. The signed-out check requires a clean login URL with bounded guidance and no ceremony correlation. The full Web application fixture in `WebAuthRoutesTests` separately renders the awaiting initiator's Continue form and verifies actual antiforgery and protected-cookie reuse against an explicit backchannel test fixture; that check does not prove a deployed reciprocal peer. Direct runs need the same fixture values and an explicitly configured service issuer. Automatic traces and failure screenshots are disabled for this suite because create and rotate reveal a synthetic secret once.

CI also starts an isolated, unconfigured bootstrap API and local Web host to drive the anonymous first-run wizard through SQLite selection, instance details, administrator details, and review. The Compose smoke checks perform the final initialization and restart verification for both SQLite and PostgreSQL.

## AiAssistant presentation fixture

Build `src/HelpDesk.NewWeb`, then run `npm run test:ux:ai-assistant`. The separate
configuration starts a loopback-only deterministic API fixture and the actual
feature NewWeb host. It uses local Development authentication and a local HTTPS
certificate (the existing `__Host-` cookie requires HTTPS). Test-only placeholder
credentials never reach a real API. Screenshots and computed styles are saved
under `test-results/`.

This suite exercises UI grouping, streaming, keyboard/IME, replay, approval
presentation, archive, responsive layout and themes. It is not live AiAssistant,
attachment ingestion or authorization evidence. It is deliberately excluded
from the ordinary live-instance UX configuration.
