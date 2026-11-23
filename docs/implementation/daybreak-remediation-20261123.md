# Daybreak remediation verification

The Daybreak scan used source revision `25108286c443f6064df127f151a9e01c9ca68d02`.
Implementation started from main `77b7ee9adb010f2fb590dab2c60f6eadef04f27d`.
The [parent epic](https://github.com/BostonTechnologies/RatelDesk/issues/464)
records the original findings and their acceptance scope.

| Issue | Code boundary | Regression and positive controls |
| --- | --- | --- |
| [#465](https://github.com/BostonTechnologies/RatelDesk/issues/465) | Shared raster validation and normalized storage. Both signed asset routes and upload-enabled branding routes check stored bytes before serving. | `InboundInlineImageResolverTests`, `IncidentInlineImageEndpointsTests`, `IncidentTimelineInlineImageTests` and `GraphEmailProcessorTests` cover invalid content, distinct CID payloads and working raster refresh. The local browser suite opens retained document fixtures directly through the gateway. |
| [#466](https://github.com/BostonTechnologies/RatelDesk/issues/466) | Native chat binds mutations to the owning operator before persistence, events or provider calls. Shared Netclaw retains scope-based permissions. | `AiAssistantChatEndpointsTests` exercises two operators in the same scope on real PostgreSQL, including scheduled stop-waiting, decisions and owner recovery. |
| [#467](https://github.com/BostonTechnologies/RatelDesk/issues/467) | Public and system ticket submission share the ingress eligibility transaction and durable delivery capture. | `PublicTicketSubmissionTests` covers disabled entities and concurrent disables on SQLite and PostgreSQL. Mailbox tests exercise existing ownership rules. |
| [#468](https://github.com/BostonTechnologies/RatelDesk/issues/468) | Anonymous form and legacy Graph paths preserve existing customer profiles. First-contact provisioning is fenced by organization and domain. | `PublicTicketSubmissionTests` covers case-varied addresses, preserved canonical profiles and two first-contact tickets sharing one customer. |
| [#469](https://github.com/BostonTechnologies/RatelDesk/issues/469) | Trusted proxy processing, a request-specific Web relay proof, and independent API admission windows. | `PublicIngressAdmissionTests`, `BootstrapStartupTests` and the browser login and setup suites cover relay budgets, direct peers, forwarding trust and both API hosts. |
| [#470](https://github.com/BostonTechnologies/RatelDesk/issues/470) | Rate admission, render slots and a dedicated bounded challenge store. | `CaptchaEndpointsTests` and `CaptchaServiceTests` check PNG output, parallel rendering, expiry and single consumption. The public ticket browser flow exercises normal refresh and submit. |
| [#471](https://github.com/BostonTechnologies/RatelDesk/issues/471) | Persisted one-time link redemption, scoped browser cookies, independent downstream grants and explicit approval forms. | `PublicTicketEndpointsTests`, `SignedPublicTicketPageTests`, change and task approval provider tests, and the login browser suite cover races, legacy cutoff, replacement, cookie scope, history and approval CSRF. |
| [#472](https://github.com/BostonTechnologies/RatelDesk/issues/472) | Allowlisted public JSON and scoped raster references. Staff timeline endpoints keep their contracts. | `PublicTicketEndpointsTests` asserts raw fields and event order across ticket types. `TimelineEffortPersistenceTests` compares public and staff timeline contracts. |
| [#473](https://github.com/BostonTechnologies/RatelDesk/issues/473) | Antiforgery-validated browser logout forms for desktop, mobile and primary navigation. | `WebAuthRoutesTests`, `NavMenuRenderedTests` and the Chromium login and local suites cover harmless GET/HEAD, rejected POSTs, successful logout and provider end-session. |

Public-link migrations are additive for both database providers. The old
query-authorized access API has been retired. New browser access depends on the
scoped cookie and the API's dedicated grant header. The compatibility period and
deployment settings are in [Security and access](../security/README.md) and the
[Web gateway guide](../deployment/web-gateway.md#public-request-admission).

SkiaSharp and its raster codecs are confined to the API dependency graph.
CAPTCHA still uses the managed PNG encoder. The dependency does not change the
published CLI, stdio MCP, HTTP MCP or Web runtime profiles. The API publish
carries the decoder's license notices under `Storage/ThirdParty`.

Framework-dependent Linux API publishes used the repository SDK, the release
Dockerfile's architecture selection, and `UseAppHost=false`. Each candidate
contains the one native decoder for its target architecture.

| Target | Main publish bytes | Candidate publish bytes | Main gzip bytes | Candidate gzip bytes |
| --- | ---: | ---: | ---: | ---: |
| `linux-x64` | 46,362,965 | 59,648,723 | 15,878,960 | 21,019,860 |
| `linux-arm64` | 46,426,483 | 59,585,827 | 15,896,030 | 20,878,440 |

The byte comparison uses the starting main revision and candidate `f14043c1`.
Gzip values measure a tar stream of the publish directory. PostgreSQL and
SQLite tests ran on the native x64 runtime. The arm64 measurement verifies
cross-publish output.

The integrated review checked the authorization boundary of each native chat
mutation, both signed asset serving paths, lock order for anonymous intake,
caller provenance across Web relays, grant revocation and replacement, and
browser antiforgery. Review changes protect queued launch fragments, retain
the ticket's original sender routing, separate decision links from email
copies, and enforce a request deadline while native raster work holds its slot.

The pre-existing [live Codex provider acceptance](https://github.com/BostonTechnologies/RatelDesk/issues/441)
is a separate dependency. The PostgreSQL tests exercise the authorization and
persistence behavior with synthetic transports.
