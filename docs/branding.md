# Instance branding

RatelDesk is the immutable upstream product identity. A deployment can present its own customer-facing identity without changing namespaces, assemblies, package IDs, database schema names, routes, OIDC identifiers, cookies, telemetry resource names, or the `DataProtection:ApplicationName` cryptographic isolation value.

## Defaults and precedence

Without configuration, the application displays **RatelDesk**, uses the bundled mark and wordmark, and uses the RatelDesk email sender fallback. For each field the effective value is resolved as:

1. `Branding__*` deployment configuration;
2. Administrator value in **Administration → Branding & Identity**;
3. the built-in RatelDesk default.

Deployment-managed values are visible in the Admin page and labelled as environment-managed. They cannot be changed there. Tenant email branding remains supported and is applied above the effective global brand for tenant-specific name, logo, footer, colour, sender, and reply-to settings.

## Environment variables

Use the names in [`.env.example`](../.env.example): `Branding__ApplicationName`, `Branding__OrganizationName`, `Branding__ApplicationUrl`, `Branding__OrganizationUrl`, `Branding__SupportUrl`, `Branding__SupportEmail`, `Branding__LogoUrl`, `Branding__CompactLogoUrl`, `Branding__FaviconUrl`, `Branding__EmailFromDisplayName`, and `Branding__Tagline`.

For Compose, make these values available to the API service. For example:

```yaml
environment:
  Branding__ApplicationName: Acme Service Desk
  Branding__ApplicationUrl: https://support.example.com
  Branding__EmailFromDisplayName: Acme Service Desk
```

`ApplicationUrl`, organization/site URL, and support URL must be absolute HTTP(S) URLs. Asset URLs for the main logo, compact logo, and favicon may instead be safe root-relative application paths such as `/branding/rateldesk-wordmark.webp`; protocol-relative and non-HTTP schemes are rejected. The administrator editor retains database overrides separately from effective values, so saving one field does not turn inherited defaults into overrides. Use **Use default** on a field to remove only that database override.

The bundled browser defaults are `/branding/rateldesk-mark.webp`, `/branding/rateldesk-wordmark.webp`, and `/branding/rateldesk-splash.webp`; PNG counterparts are included for transparent-image fallback. The email default is the mail-client-compatible `/email-brand/rateldesk-email-wordmark.png`. `LogoUrl`, `CompactLogoUrl`, and `FaviconUrl` continue to override these upstream defaults, and tenant branding remains more specific where configured.

Login and portal compact logos preserve transparency and aspect ratio in both themes. Custom opaque images retain their own background; custom transparent images should provide contrast against both light and dark surfaces. No theme-specific white backing or crop is added by the application.

In Hybrid mode, local sign-in remains the main form action and external sign-in uses a compact provider link. Set `LoginUi__ProviderDisplayName` on Web to customize its label (default: `Authentik`). This is a display label for the configured provider and does not change its authority or challenge endpoint.

## Email templates

Existing `BRAND_NAME`, `LOGO_HTML`, and `FOOTER_HTML` tokens continue to work. Templates may also use structured, escaped tokens such as `{{brand.application_name}}`, `{{brand.organization_name}}`, `{{brand.application_url}}`, `{{brand.organization_url}}`, `{{brand.support_url}}`, `{{brand.support_email}}`, `{{brand.logo_url}}`, `{{brand.email_from_display_name}}`, and `{{brand.tagline}}`.

An explicitly configured tenant sender name remains more specific than the global email-display-name fallback.

## Upstream attribution

Repository, licence, and open-source attribution remain RatelDesk and `BostonTechnologies/RatelDesk`. Branding only changes human-facing deployment identity; it never changes security or protocol compatibility identities.
