# Security and access

- [Roles and scoped authorization](rbac.md)
- [Owner administration through HTTP MCP](rbac.md#owner-administration-through-http-mcp)
- [Deployment security and account authentication](../deployment/self-hosting.md)
- [Account-bound integration credential pairing](../integrations/browser-pairing.md)
- [HTTP MCP authentication boundaries](../mcp-http/configuration.md)
- [MCP operation authority and prerequisites](../contracts/mcp-operation-contracts.md#transport-identity-and-authority)

## Customer-facing access

- Public ticket intake checks the customer and organization before provisioning
  or creating any ticket, notification or workflow effect. Existing profiles
  retain their saved names and identity links. First-contact submissions can
  create a customer in an eligible organization.
- Emailed links open a confirmation page before they create a browser grant.
  Each launch authorizes one browser. Access is scoped to its recipient,
  purpose and ticket or approval task. A browser can keep several authorized
  tickets open at once. Change view links grant read access. Assigned approvers
  use a separate approval link, and decisions require explicit forms.
- The launch lifetime defaults to seven days for tickets and changes and
  fourteen days for request approvals. `PublicLinks:LaunchDays` can shorten
  those limits. A browser grant lasts at most 30 minutes by default.
  `PublicLinks:GrantMinutes` accepts 5 through 120 minutes, capped at the
  launch expiry. Open the original email to request a replacement. Delivery
  goes to the original recipient and revokes the superseded grant.
- The database migration opens a 30-day compatibility window for previously
  emailed links. Each link also retains its signed expiry. Administrators can
  shorten the window with `PublicLinks:LegacyCutoffUtc`. Legacy change links
  can open the plan. An assigned approver can request a fresh decision link
  because the old signature used the same purpose for viewing and approving.
- Public timelines contain customer-visible replies and worklogs. The public
  response uses generic author labels and omits staff IDs and internal ticket
  and worklog identifiers. Authorized staff timelines retain their existing
  details.
- Browser sign-out uses an antiforgery-protected POST in Local, OIDC, and
  Hybrid modes. Local and AI-agent sessions end at RatelDesk. OIDC sign-out
  also invokes the configured provider end-session flow.

## Uploaded and emailed inline images

Worklog data images, email CID images, uploaded tenant logos and template images
use the same raster validation. RatelDesk accepts correctly identified PNG, JPEG,
GIF, WebP and BMP with up to 12 MiB of encoded input, 8,192 pixels on either side,
12 million pixels and 32 frames. GIF and WebP images display their first frame.
Re-encoding produces PNG or JPEG with a 24 MiB output limit and discards metadata.
Conversion has two active decoder slots, four waiting slots, and five-second
queue and conversion budgets. Native decoding checks the conversion budget
between its bounded stages.

Previously stored raster links remain valid. The serving routes require a matching
raster signature and add browser isolation headers. Legacy document images are
unavailable inline. An unsupported CID image can be omitted while the ticket text
and separate attachments are kept.

Report vulnerabilities under the repository [security policy](../../SECURITY.md).
