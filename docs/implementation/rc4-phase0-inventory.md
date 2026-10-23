# rc.4 Phase 0 inventory

Starting point: `cee9e1cba4deaeb1795f63337a7ceb73ffcdf66f` on `origin/main`.
The repository targets .NET 10 through `global.json`; the release line is
owned by `Directory.Build.props`.

| Surface | Existing state | rc.4 direction |
| --- | --- | --- |
| API startup | Requires `HelpdeskDb` and PostgreSQL Hangfire before serving. | Split bootstrap from the selected application runtime and gate workers until ready. |
| Database | Historical rc.4 inventory: provider migration assumptions required review. | Explicit provider selection, provider migrations, and SQLite query compatibility. |
| Authentication | API validates external JWTs; Web uses OIDC/cookie forwarding; legacy BCrypt/JWT login is Development-only. | API-owned ASP.NET Core Identity local accounts, with OIDC retained as an opt-in scheme. |
| Identity links | Customer links could fall back to email and provisioning accepted body-controlled identity fields. | Use only validated issuer/subject or provider ID and explicit links. |
| Roles | Free-text user role plus claim bundles; generic role CRUD. | Protected built-ins, custom roles, permissions, and scoped assignments. |
| Tenant boundary | `Organization.Id` is the application tenant, but `tid` could be treated as a tenant. | Resolve application organization only from an explicit application relationship. |
| Generic CRUD | Roles, organizations, customers, KB, assets, and automation had only authentication gating. | Restrict immediately; replace incrementally with scoped, operation-specific handlers. |
| Streams and jobs | SignalR, worker services, and Hangfire begin with the normal host. | Reauthorize subscriptions and prevent workers from starting before ready. |

## Route/action/scope matrix

This is the compact Phase 0 authorization baseline. Permission names below are
the current rc.4 catalog (`HelpdeskPermissions`), rather than an aspirational
reader/writer naming scheme. `Instance` means the protected `HelpdeskAdmin`
grant; `tenant` means the permission and organization must be paired in the
same scoped grant. Every list, count, search and export path is included in the
same row as its resource family and must filter before paging.

| Surface and route family | Actions | Required current capability | Scope and critical boundary |
| --- | --- | --- | --- |
| `/api/v1/incidents` | list, detail, create, update, delete, relations | `Incident.User`; mutations additionally require `Incident.Manager` | Tenant-scoped; self-service may view/create only its linked contact's records. |
| `/api/v1/requests`, `/api/v1/request-tasks` | list, detail, create, update, tasks, approvals | `Request.User`; management/task mutation requires `Request.Manager` | Tenant-scoped; approval/workflow rules remain additional checks. |
| `/api/v1/changes` | list, detail, create, update, delete, approval | `Change.User`; management requires `Change.Manager` | Tenant-scoped; approval/CAB and separation-of-duties are independent of read/write access. |
| `/api/v1/tickets`, timelines, activity and worklogs | detail, counts, live history, public/internal notes | Parent ticket capability | Resolve and authorize the parent ticket before returning child data; internal notes are never implied by self-service ticket read. Live timeline delivery re-resolves the parent scope before each event and keep-alive, ending subscriptions after revocation. |
| `/api/v1/tickets/{id}/attachments`, `/api/v1/attachments` | upload, list, download | Parent ticket capability | Attachment ID lookup resolves the parent with query filters bypassed, then returns not-found for inaccessible parents. Self-service upload is incident/request-own-resource only. |
| `/api/v1/self-service/*`, service catalog and request forms | catalog, submit, own request view, datasets | `SelfService.User` | Organization-scoped own-resource grant; catalog/form audience checks remain required. `/service-items`, `/services/{id}` and `/request-forms/{id}` require this policy or instance admin. |
| `/api/v1/ticketing/*`, dashboard and global lookup | tenant/customer/assignee lookup, counts/search | Resolved current access; customer dashboard requires `SelfService.User` | No grant means no organizations; customer/assignee data is filtered to selected authorized organization and manager status. |
| SLA policies, ticket SLA and SLA reports | policy administration, pause/resume, compliance/breach reports | Module manager or instance admin | Report type must be manageable in the requested tenant; SLA mutation reauthorizes the target ticket. |
| `/api/v1/admin/role-definitions`, `/api/v1/tenant-admin/*` | role definition, tenant membership, role assignment | `HelpdeskAdmin` or tenant delegation grants | Tenant admins stay inside their assigned organization and delegation ceiling; global account changes and instance roles require instance authorization. |
| `/api/v1/local-auth/*` | local account lifecycle, password, TOTP, recovery | Self: authenticated principal; admin lifecycle: instance/tenant rule | Password material and hashes never enter ordinary DTOs; disable/revocation changes active authorization. |
| Organizations, customers, categories, services and forms | CRUD and tenant configuration | `HelpdeskAdmin` unless a dedicated tenant handler exists | Generic authenticated CRUD is not a substitute for scope checks. Service/form reads use the self-service-or-admin catalog policy. |
| Knowledge base, assets, reporting and notifications | browse/search, manage, export, deliver | Explicit module/admin policy | Public signed/image routes retain their capability boundary; normal searches and notifications must not disclose cross-tenant resources. |
| Automation, orchestration, workflow ops and callbacks | binding/configuration, retries, execution, callback review | Request/change manager or `HelpdeskAdmin` as applicable | Machine identities have explicit bounded service-principal grants and must not become a bypass for caller scope. |
| SignalR streams, MCP and AI assistant context | subscribe, receive, tool/query execution | Same resource capability as HTTP | Subscription/delivery is rechecked when authorization revisions change; MCP/AI callers are constrained to their granted tenant/actions. |

The implementation audit must retain this matrix as handlers evolve: an
authenticated-only group is acceptable only where a handler immediately resolves
the principal and applies the row's resource-level check. Otherwise it is an
enforcement gap, not a completed authorization decision.
