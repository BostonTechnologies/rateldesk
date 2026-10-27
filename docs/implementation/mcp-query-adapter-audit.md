# MCP query adapter audit

Scope: the actual `Query` callers in `HelpdeskTools.cs`, checked against the
exported MCP request schemas and owning API endpoints for issue #196. This is a
source/contract audit, not an additional live authorization failure. Existing
beta.12 fixes already forward the supported ticket identifiers explicitly.

| Caller / operations | ID handling and contract |
| --- | --- |
| Logs `search` | `correlationId` is explicitly forwarded; other fields are since/contains/level/limit. The diagnostic authority remains independent of this filter. |
| Global search users/customers/organizations | query/q normalize to q; limit maps to pageSize. No advertised identifier filter. |
| Services `list`, `search_items` | list has no advertised fields; search has query/q/pageSize/includeTotal. `serviceId` belongs to get/items/breadcrumb routes, not list filtering. |
| Self service `requests`, `request_users` | requests uses page/pageSize/q; request_users uses pageSize/query. Identity/customer isolation is server-owned; arbitrary customer/organization selectors are not advertised. |
| Notifications `list`, `unread_errors` | list has page/pageSize/search/source/category/sortBy/sortDir/severity/from/to; unread errors has take. Notification ID belongs to get, not these queries. |
| Connectivity | organizationId and connectionId are explicitly forwarded from the selected provider binding; no inferred peer/tenant selection. |
| Incident list | organizationId, excludeId, customerOrgId, forId and assigneeId are explicit filters. |
| Request/change list | customerOrgId, forId and assigneeId are explicit filters. Request/change ID is a route selector, not a list field. |
| Request-task list | assignedToId, requestId and serviceId are explicit filters. taskId is a get/mutation route selector. |
| CollectionRead roles/categories | roles list has no fields. Categories advertise type/search/ids/take/allScopes/includeInactive. `ids` is an encoded comma-separated string; singular categoryId is not a list filter. API tenantId is not advertised by MCP; current-tenant and administrator allScopes behavior is intentional. |
| LookupRead organizations/customers/users | list schemas advertise no fields. The corresponding singular identifiers belong to get/auth/tenant routes. |

`Query` always excludes literal `id`, and excludes case-sensitive `*Id` unless
that operation explicitly names the field. Do not replace that with a generic
pass-through. Public schemas use exact field names and reject unsupported aliases;
non-ID fields are encoded with `Uri.EscapeDataString`. Ticket array filters expand
to repeated parameters only on explicit identifier-filter callers. Other callers
advertise scalar fields. Empty/null values retain the existing contract.

Owning regressions: `TicketAdapterContractTests` exercises real generated query
strings, repeated and encoded identifiers, route-ID exclusion and unsupported
field rejection; `TicketAdapterHttpTests` and `McpGatewayRealAccessPipelineTests`
exercise the corresponding API/identity boundary. `IncidentListSearchTests`
retains the query-to-q correction. Existing log/provider adapter tests cover
correlation and selected connection identifiers. Run these with the final PR;
no new supported identifier mismatch was found in the remaining callers.
