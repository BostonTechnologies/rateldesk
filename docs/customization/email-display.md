# Ticket email display

Ticket previews, original incident messages, timeline HTML and the full-message
dialog share an isolated email document. Original inline foreground/background
pairs render inside a light default canvas; the surrounding controls follow the
application theme. Select **Plain text** for a themed reading view. This changes
only the display, never the stored or sent message.

Incident subject previews open on hover, focus or activation. They remain open
while the pointer or focus is in the preview. Escape (including within the email)
or **Close preview** dismisses it. Loading, an unavailable response with Retry,
and an empty message are distinct states. Changing the ticket or leaving the
component cancels outstanding requests.

| Surface | Content and existing authorization |
| --- | --- |
| Incident preview and Messages | `/api/v1/incidents/{id}/peek`, existing administrative policy; sanitized original HTML and complete plain text, plus legacy snippet |
| Request Messages | `/api/v1/requests/{id}`, RequestAccess and per-record access; description displayed as literal text |
| Change Messages fallback | `/api/v1/changes/{id}`, ChangeAccess and per-record access; literal description |
| Change detail | Existing change template editor and lifecycle permissions; the Messages override is retained |
| Timeline and full message | Existing authorized timeline/worklog APIs; rich content enters the same display boundary |

The display sanitizer allows passive text/table formatting and inline colours.
It removes email scripts, event handlers, forms, embeds, stylesheet rules, CSS
resource URLs, positioning and unsafe links. Links are listed outside the email
as explicit new-tab controls with `noopener noreferrer`. The iframe cannot
navigate the application, open popups, submit forms or access the parent origin.

The sandbox has an opaque origin. Its restrictive CSP permits exactly one
SHA-256-pinned application script: a geometry/Escape bridge. It reports only
bounded document height and keyboard dismissal through `postMessage`; the parent
accepts messages only from the corresponding iframe. Email scripts remain
blocked. `allow-same-origin` is never granted. The bridge is required for Escape
from inside the focused isolated document; it never reads credentials or email
content into the parent.

Remote images are blocked to avoid tracking loads. Raster data images and
existing signed `/api/incidents/{id}/images/{file}` and
`/api/worklogs/{id}/images/{file}` URLs can render. Historical absolute signed
image paths are rebased onto the configured public Web gateway; the existing API
verifies each scoped image token. Inbound CID resolution remains with the
existing ingestion/storage pipeline. Unresolved CID and blocked images show
their alt descriptions; failed authorized image loads retain their alt text.

Tests use synthetic identities and local fixtures. Run `npm run test:ux:email`
after building the Web project with the pinned SDK. The suite captures the
original pale-on-pale reproduction and rendered foreground/background evidence,
then exercises the actual Blazor display in both themes and responsive widths.
