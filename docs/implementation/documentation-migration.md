# Documentation path migration

The #154 documentation cleanup moved the loose guides with `git mv`. Their
operator content and historical results remain available at the categorized
paths below. Existing published artifacts/tags are unchanged; update bookmarks
and external links to the new paths when convenient.

| Previous path | Canonical path |
| --- | --- |
| `docs/SELF_HOSTING.md` | [docs/deployment/self-hosting.md](../deployment/self-hosting.md) |
| `docs/branding.md` | [docs/customization/branding.md](../customization/branding.md) |
| `docs/theme-first-paint.md` | [docs/customization/theme-first-paint.md](../customization/theme-first-paint.md) |
| `docs/mailbox-ingestion.md` | [docs/integrations/mailbox-ingestion.md](../integrations/mailbox-ingestion.md) |
| `docs/netclaw.md` | [docs/integrations/netclaw.md](../integrations/netclaw.md) |
| `docs/rbac.md` | [docs/security/rbac.md](../security/rbac.md) |
| `docs/rc4-phase0-inventory.md` | [docs/implementation/rc4-phase0-inventory.md](rc4-phase0-inventory.md) |
| `docs/releases.md` | [docs/releases/README.md](../releases/README.md) |

`docs/README.md` is the only permitted docs-root file. Release notes, contracts,
ADRs and existing implementation history retained their category and identity.
Runnable Docker examples retain their location and link to canonical guides.
