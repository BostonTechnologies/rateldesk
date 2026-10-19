using System.Text.Json.Nodes;

namespace Helpdesk.Mcp.Tools;

/// <summary>Static authority requirements; live access is always resolved by the API.</summary>
public static class McpOperationMetadata
{
    public static JsonObject ToJson(McpOperationDefinition item)
    {
        var admin = RequiresAdministrator(item);
        var authority = Authority(item);
        var agentCallbackAuthority = item.Tool == "helpdesk_logs" || item.Tool == "helpdesk_ai_assistant" && item.Operation == "report";
        return new JsonObject
        {
            ["tool"] = item.Tool, ["operation"] = item.Operation, ["requestContract"] = item.RequestContract,
            ["method"] = item.HttpMethod, ["pathTemplate"] = item.PathTemplate,
            ["effect"] = item.Effect, ["readOnly"] = item.Effect == "read",
            ["confirmationRequired"] = item.RequiresConfirmation,
            ["previewSupported"] = item.RequiresConfirmation,
            ["description"] = item.Description,
            ["sensitiveResultFields"] = new JsonArray((OperatorOperationCatalog.Find(item.Tool, item.Operation)?.SensitiveResultFields ?? []).Select(name => JsonValue.Create(name)).ToArray()),
            ["transports"] = new JsonArray("stdio", "http"),
            ["transportSupport"] = new JsonObject
            {
                ["stdio"] = "Available with explicitly configured API identity; authority and prerequisites apply",
                ["http"] = item.Tool == "helpdesk_config" && item.RequiresConfirmation ? "unavailable_by_policy: remote configuration persistence is disabled" : admin ? "requires_explicit_administrator_identity: ordinary bounded integration credential is denied" : "available_subject_to_identity_permission_scope_and_prerequisites"
            },
            ["credentialModes"] = item.HttpMethod == "LOCAL" ? new JsonArray("local")
                : admin ? new JsonArray("explicit mcp-owner resource-bound grant from a current instance administrator (HTTP gateway)", "explicit external Authentik identity satisfying HelpdeskAdmin")
                : agentCallbackAuthority ? new JsonArray("explicit mcp-owner resource-bound grant from a current instance administrator (HTTP gateway)", "explicit external Authentik downstream identity satisfying exact AuthentikAiAgent scheme")
                : new JsonArray("api-purpose account-bound credential (stdio)", "mcp-purpose resource-bound credential with request-scoped API execution token (HTTP gateway)", "explicit external Authentik identity"),
            ["authority"] = authority,
            ["boundedIntegrationCredential"] = new JsonObject
            {
                ["support"] = item.Tool == "helpdesk_config" && item.RequiresConfirmation ? "unavailable_by_policy_on_http" : admin ? "denied_by_policy" : agentCallbackAuthority ? "denied_by_policy" : "permission_and_organization_intersection",
                ["reason"] = admin ? "HelpdeskAdmin is instance-wide authority. Assignable scoped grants do not confer it; use an explicitly supported administrator identity."
                    : agentCallbackAuthority ? "Ordinary scoped credentials cannot satisfy agent-operation authority. An explicitly approved mcp-owner grant or the exact external AuthentikAiAgent scheme is required; invocation and ticket binding remain authoritative."
                    : item.Tool == "helpdesk_config" && item.RequiresConfirmation ? "HTTP configuration surface is read-only. Local stdio persistence requires configurationWritesEnabled=true."
                    : "API checks account state, expiry, revocation, credential purpose/resource, granted permissions and organization scope."
            },
            ["ownerAdministrationCredential"] = new JsonObject
            {
                ["purpose"] = "mcp-owner", ["grantKind"] = "owner-administration",
                ["support"] = item.Tool == "helpdesk_config" && item.RequiresConfirmation ? "unavailable_by_policy_on_http" : "subject_to_current_owner_authority_and_domain_prerequisites",
                ["reason"] = "Separate explicit administrator consent creates a resource-bound owner grant. The API revalidates the owning account's current administrator authority, credential expiry/revocation and MCP resource on each call; ordinary tenant grants never gain this authority."
            },
            ["identifiers"] = new JsonArray(McpRequestSchemas.Identifiers(item.PathTemplate).Distinct().Select(name => (JsonNode)new JsonObject
            {
                ["field"] = name, ["type"] = IdentifierType(item, name),
                ["source"] = name switch
                {
                    "brandingTenantId" => "helpdesk_organizations.tenants or tenant_lookup: positive integer tenant identifier; never an organizationId",
                    "tenantId" when IdentifierType(item, name) == "integer" => "helpdesk_organizations.tenants or tenant_lookup: integer tenant identifier; never an organizationId",
                    "tenantId" => "Tenant SLA settings use the string organization identifier from the authorized organization list",
                    "invocationId" => "Existing authenticated AI investigation invocation bound to this ticket and correlationId",
                    "ticketType" => "incidents, requests, or changes", _ => "Existing authorized record; synthetic arbitrary identifiers do not establish a positive operation"
                }
            }).ToArray()),
            ["prerequisites"] = new JsonArray(Prerequisites(item).Select(value => JsonValue.Create(value)).ToArray()),
            ["requestSchema"] = item.RequestContract == "None" ? null : HelpdeskToolDefinitions.RequestSchema(item)
        };
    }

    private static string IdentifierType(McpOperationDefinition item, string name)
    {
        if (OperatorOperationCatalog.Find(item.Tool, item.Operation) is { } adapter)
            return OperatorRequestSchemas.Create(adapter)["properties"]![name]?["type"]?.ToString() ?? "string";
        return name == "tenantId" ? "integer" : name is "invocationId" or "notificationId" or "categoryId" ? "uuid" : "string";
    }

    private static bool RequiresAdministrator(McpOperationDefinition item) =>
        OperatorOperationCatalog.Find(item.Tool, item.Operation)?.Authority == "HelpdeskAdmin" ||
        item.Tool is "helpdesk_connectivity" or "helpdesk_search" ||
        (item.Tool == "helpdesk_incidents" && item.Operation == "peek") ||
        (item.Tool == "helpdesk_services" && item.Operation == "list") ||
        (item.Tool == "helpdesk_request_forms" && item.Operation == "by_service") ||
        item.Tool is "helpdesk_organizations" or "helpdesk_customers" or "helpdesk_users" ||
        (item.Tool == "helpdesk_admin_mutations" && item.Operation is not ("create_role" or "update_role" or "delete_role" or "create_self_service_request" or "provision_user")) ||
        (item.Tool == "helpdesk_changes" && item.Operation is "bulk_state" or "assign" or "assign_self");

    private static string Authority(McpOperationDefinition item)
    {
        if (OperatorOperationCatalog.Find(item.Tool, item.Operation) is { } adapter) return adapter.Authority + "; existing API identity, tenant and record-specific checks apply";
        if (item.HttpMethod == "LOCAL") return item.Tool == "helpdesk_config" && item.RequiresConfirmation ? "Local persisted non-secret configuration write; transport configuration surface must allow persistence" : "Local discovery; no business API authority implied";
        if (RequiresAdministrator(item)) return "HelpdeskAdmin (explicit administrator identity; unavailable to ordinary bounded integration credentials)";
        if (item.Tool == "helpdesk_ai_assistant") return "Exact AuthentikAiAgent scheme or explicit current administrator mcp-owner grant plus authenticated AI invocation reporting authority; matched invocation, ticket, event and correlation binding";
        if (item.Tool == "helpdesk_auth") return item.Operation == "access"
            ? "Authenticated /api/v1/auth/me resolves current execution-principal authority in every credential mode. In external HTTP mode this is the separately configured shared downstream principal, not the ingress caller. Discovery configuration does not imply HelpdeskAdmin or exact AI authority."
            : "Caller-bound integration/gateway uses authenticated /api/v1/auth/me; explicit external Authentik uses /api/v1/auth/ai-agent/status requiring exact AuthentikAiAgent scheme. No target or identity-mode fallback. Use access for current downstream API permissions in either mode.";
        if (item.Tool == "helpdesk_health") return "Backend live/ready probes are public; authenticated probe uses /auth/me for caller-bound integration/gateway or exact AuthentikAiAgent status for explicit external Authentik. MCP ingress still requires its configured identity.";
        if (item.Tool == "helpdesk_system") return "Backend /api/v1/system/version is public metadata (AllowAnonymous); MCP ingress and the configured adapter identity still apply.";
        if (item.Tool == "helpdesk_services") return "SelfService.User or HelpdeskAdmin. Non-administrators need current tenant identity and service AllowedOrganizationIds/AllowedCustomerIds visibility; items/search_items also filter forms by release/test-user and organization audience.";
        if (item.Tool == "helpdesk_request_forms") return "SelfService.User or HelpdeskAdmin; non-administrators need an audience-eligible form in Production, or an eligible linked test user, matching OrganizationId/AllowedOrganizationIds.";
        if (item.Tool == "helpdesk_categories") return "TicketReadAccess: an Incident/Request/Change Read, Write, User or Manager grant, SelfService.User, or HelpdeskAdmin. Results are scoped to system/current tenant categories; allScopes=true or includeInactive=true additionally requires HelpdeskAdmin.";
        if (item.Tool == "helpdesk_notifications") return item.Operation == "mark_read"
            ? "Authenticated NotificationAccess plus resolved application user ID. Only currently visible notification IDs may be marked; personal notices must belong to that user, global reads create that user's own marker. updated=0 is a no-op."
            : "Authenticated NotificationAccess plus resolved application user ID. Personal referenced-ticket notices require current ticket ownership/module scope; broadcasts require matching tenant module scope (unclassified notices require all module scopes). HelpdeskAdmin sees all. summary/unread_errors use the same visible set; unread_errors adds unread Error/Critical filtering.";
        if (item.Tool == "helpdesk_tickets") return "Authenticated identity; actual ticket must pass CanViewIncident/CanViewRequest/CanViewChange: family Read, Write or Manager in its organization, or family User with matching owned customer/requester identity. Counts and mark_as_seen share this visibility check; seen-state readback is separate from notification mark_read.";
        if (item.Tool == "helpdesk_raw") return item.Operation switch
        {
            "system_version" => "Fixed GET of public backend system-version metadata; configured MCP ingress/adapter identity still applies",
            "get_incident" => "Fixed incident GET: Incident.Read, Incident.Write or Incident.Manager in organization, or Incident.User with matching customer/requester ownership",
            "get_request" => "Fixed request GET: Request.Read, Request.Write or Request.Manager in organization, or Request.User with matching customer/requester ownership",
            _ => "Fixed change GET: Change.Read, Change.Write or Change.Manager in organization, or Change.User with matching customer/requester ownership"
        };
        if (item.Operation == "provision_user") return "Authenticated caller with verified email plus issuer/subject or provider-user-ID claims, an established customer login link, and enabled customer/organization; explicitly authorized external administrator may provision without a customer link. Request fields cannot choose another identity.";
        if (item.Tool == "helpdesk_logs") return "Exact AuthentikAiAgent scheme or explicit current administrator mcp-owner grant; ordinary scoped integration/gateway identities are denied regardless of scoped grants";
        if (item.Tool == "helpdesk_roles" || (item.Tool == "helpdesk_admin_mutations" && item.Operation.EndsWith("_role", StringComparison.Ordinal))) return "HelpdeskAdmin or scoped role-management authority for ownerOrganizationId; only delegable permission intersection";
        if (item.Tool == "helpdesk_self_service" && item.Operation == "request_users") return "SelfService.User or HelpdeskAdmin; search users/customer contacts in the caller's visible organizations. A linked customer identity is not required for this lookup.";
        if (item.Tool == "helpdesk_self_service") return "SelfService.User or HelpdeskAdmin; non-administrators need an enabled linked customer and organization scope, owned request visibility and any applicable request-form audience.";
        if (item.Operation == "create_self_service_request") return "SelfService.User or HelpdeskAdmin, audience-eligible request form and valid payload. Requested-for customer is resolved from the caller's linked customer or an explicit authorized requestedForPersonId/requestedForPersonSource selection in visible organizations.";
        if (item.Tool == "helpdesk_changes" && item.Operation == "lifecycle") return "ApprovedForImplementation requires Change.Approve or Change.Manager; other transitions require Change.Write or Change.Manager, within the selected organization. API lifecycle rules apply.";
        if (item.Operation == "delete" && item.Tool is "helpdesk_incidents" or "helpdesk_requests" or "helpdesk_changes") return item.Tool switch
        {
            "helpdesk_incidents" => "Incident.Delete or Incident.Manager within the selected organization; Incident.Write alone is insufficient",
            "helpdesk_requests" => "Request.Delete or Request.Manager within the selected organization; Request.Write alone is insufficient",
            _ => "Change.Delete or Change.Manager within the selected organization; Change.Write alone is insufficient"
        };
        if (item.Tool == "helpdesk_incidents") return item.Effect == "read" ? "Incident read/write/manager permission or permitted owned incident in organization scope" : "Incident management/write authority in selected organization; API action-specific policy applies";
        if (item.Tool == "helpdesk_request_tasks") return item.Effect == "read"
            ? "Request.Read, Request.Write or Request.Manager on the parent organization; owned-request permission alone does not grant task administration"
            : item.Operation is "create" or "update" or "delete"
                ? "Request.Write or Request.Manager on parent/task organization; immutable parent/customer/organization identity"
                : "Manual tasks require Request.Write or Request.Manager; automation tasks require Request.Execute or Request.Manager in the task organization. Approval tasks use the designated authenticated approver operations in helpdesk_automation or the existing signed approval flow. Retry additionally requires an eligible failed automation task.";
        if (item.Tool == "helpdesk_requests") return item.Effect == "read" ? "Request read/write/manager permission or permitted owned request in organization scope" : "Request management/write authority in selected organization; task lifecycle eligibility applies";
        if (item.Tool == "helpdesk_changes") return item.Effect == "read" ? "Change read/write/manager permission or permitted owned change in organization scope" : "Change.Write or Change.Manager authority in selected organization; draft edit lock and lifecycle invariants apply";
        throw new InvalidOperationException($"Missing explicit authority metadata for {item.Tool}.{item.Operation}.");
    }

    private static IEnumerable<string> Prerequisites(McpOperationDefinition item)
    {
        if (item.Tool == "helpdesk_config" && item.RequiresConfirmation) yield return "Local stdio only with configurationWritesEnabled=true and explicit non-secret key; HTTP remote persistence is unavailable by policy.";
        if (item.HttpMethod != "LOCAL") yield return "Configured exact API target and valid supported identity; no authentication-mode or target fallback on denial/unavailability.";
        if (item.Tool == "helpdesk_admin_mutations" && item.Operation is "create_role" or "update_role") yield return "A custom role needs a name and at least one supported permission; creation also requires an enabled ownerOrganizationId. Write/delete/execute/approve permissions include corresponding Read; legacy Manager permissions include corresponding User. Delegation remains bounded by caller authority.";
        if (item.Tool == "helpdesk_raw") yield return "Fixed named allowlisted GET only. Arbitrary raw methods, paths, hosts and unknown operations are unsupported; rawEnabled=false describes arbitrary access.";
        if (item.Tool == "helpdesk_ai_assistant" && item.Operation == "report") yield return "Real authenticated existing invocation/organization/event/ticket/correlation binding under an explicitly approved owner grant or exact AI scheme, including token lifetime validation. Report appends AI worklog autonomously; a matching duplicate retains its stored event, while conflicting event-ID reuse and new callbacks after Completed, Failed or Cancelled are rejected.";
        if (OperatorOperationCatalog.Find(item.Tool, item.Operation) is { } adapter)
            yield return adapter.Method == "GET" ? "Read current authorized records; empty results do not prove a positive lifecycle fixture." : "Use the typed request.body and confirmed operation, then independently read the changed record and its durable effect. Preview performs no upstream request. Any prerequisite external integration must already be configured through a supported secure pairing flow.";
        if (item.Tool == "helpdesk_connectivity") yield return "Configured enabled external orchestration peer, permitted network connection and a unique/selected saved automation connection. Settings/catalog/test accept optional organizationId and connectionId to select an eligible saved mapping; binding inventory has its existing contract. Multiple unresolved connections return connection_selection_required (409); missing configuration is a prerequisite failure, not a successful probe. Test uses this integration's own connectivity service and causes outbound traffic, with zero job ingestion.";
        if (item.Tool == "helpdesk_request_tasks" && item.Operation.Contains("retry", StringComparison.Ordinal)) yield return "Eligible failed automation task with configured orchestration peer, tenant and binding. Manual-task retry is unsupported. updated=0 means no eligible transition, never proof of mutation.";
        if (item.Operation is "assign" or "assign_self") yield return "Existing eligible support assignee/member for each selected organization; assign_self also needs configured agent email resolving to an eligible user.";
        if (item.Tool == "helpdesk_self_service" && item.Operation == "request_users") yield return "SelfService.User or HelpdeskAdmin and visible organization scope; this lookup supports authorized requested-for selection without requiring the caller's own linked customer.";
        if (item.Tool == "helpdesk_self_service" && item.Operation != "request_users") yield return "For non-administrators, an enabled linked customer in an authorized organization and owned request; any referenced form must remain audience-eligible.";
        if (item.Operation == "create_self_service_request") yield return "Enabled requested-for customer/organization resolved through the caller's own link or explicit authorized requestedForPersonId plus requestedForPersonSource. Select a published/audience-eligible form and payloadJson matching its schema.";
        if (item.Operation.StartsWith("customer_", StringComparison.Ordinal)) yield return "Existing enabled customer with an External identity-provider login and configured Authentik; inspect helpdesk_customers.auth_status first. Local accounts use helpdesk_tenant_admin.reset_customer_login for a new activation token and disable_local_account/enable_local_account with the linked userId. Identity-mode mismatch remains a real 409 prerequisite result; a provider denial is not an accomplished lifecycle effect.";
        if (item.Operation == "auth_status") yield return "Existing customer and HelpdeskAdmin; inspect Local/External identity-provider mode and the account linkage before selecting an activation, invitation, enable/disable or synchronization operation.";
        if (item.Operation == "provision_user") yield return "Verified external identity email plus issuer/subject or provider user identifier claims and an established customer login link (or explicitly authorized external administrator). This operation provisions the caller; request business fields cannot select another identity.";
        if (item.Operation == "update_organization_branding") yield return "Positive integer tenantId from tenant lookup; organizationId is a different identifier and is not accepted.";
        if (item.Tool == "helpdesk_changes" && item.Effect != "read") yield return "Authorized organization/participants, valid type-specific change template and implementation window; approval, draft edit lock, implementation and closure invariants remain enforced.";
        if (item.Operation == "mark_as_seen") yield return "Existing visible ticket. Verify seen state independently after acceptance; notification mark_read is a separate operation.";
    }
}
