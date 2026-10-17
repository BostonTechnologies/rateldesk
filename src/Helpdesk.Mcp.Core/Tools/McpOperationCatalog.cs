using System.Text.Json.Nodes;

namespace Helpdesk.Mcp.Tools;

public sealed record McpOperationDefinition(
    string Tool,
    string Operation,
    string RequestContract,
    string HttpMethod,
    string PathTemplate,
    bool RequiresConfirmation,
    string Description)
{
    public string Effect => HttpMethod is "GET" || (HttpMethod == "LOCAL" && !RequiresConfirmation) ? "read"
        : Tool == "helpdesk_connectivity" ? "external_io" : HttpMethod == "LOCAL" ? "local_write" : "write";
}

/// <summary>
/// The checked-in contract used by MCP discovery, tests, and operator documentation.
/// It lists supported MCP adapters; API identity, authority and domain rules remain
/// authoritative. Secret-returning, unrestricted raw and debug operations stay excluded.
/// </summary>
public static class McpOperationCatalog
{
    public const string Revision = "2026-10-17.mcp-executable-contracts-v20";

    public static IReadOnlyList<McpOperationDefinition> All { get; } =
    [
        Read("helpdesk_auth", "status", "None", "/api/v1/auth/ai-agent/status", "Read the authenticated agent status."),
        Read("helpdesk_health", "get", "None", "/health/live, /health/ready, /api/v1/auth/ai-agent/status", "Read live, ready, and authenticated health."),
        Local("helpdesk_config", "show", "None", false, "Read redacted configuration."),
        Local("helpdesk_config", "get", "ConfigKeyRequest", false, "Read an allowlisted persisted configuration value."),
        Local("helpdesk_config", "set", "ConfigValueRequest", true, "Set an allowlisted non-secret configuration value."),
        Local("helpdesk_config", "unset", "ConfigKeyRequest", true, "Unset an allowlisted non-secret configuration value."),
        Local("helpdesk_capabilities", "get", "None", false, "Read the supported catalog revision and safety policy."),
        Read("helpdesk_system", "version", "None", "/api/v1/system/version", "Read safe system metadata."),
        Read("helpdesk_logs", "search", "LogSearchRequest", "/api/v1/ops/ai-agent/logs", "Run a bounded, redacted log search."),

        Ticket("helpdesk_incidents", "incidents", "incidentId", "IncidentRequest", "get", "GET", false),
        Ticket("helpdesk_incidents", "incidents", "incidentId", "IncidentRequest", "peek", "GET", false),
        Ticket("helpdesk_incidents", "incidents", "incidentId", "IncidentRequest", "timeline", "GET", false),
        TicketCount("helpdesk_incidents", "incidents", "incidentId", "timeline_count"),
        TicketCount("helpdesk_incidents", "incidents", "incidentId", "attachments_count"),
        TicketCount("helpdesk_incidents", "incidents", "incidentId", "listeners_count"),
        Ticket("helpdesk_incidents", "incidents", "incidentId", "IncidentCreateRequest", "create", "POST", true),
        Ticket("helpdesk_incidents", "incidents", "incidentId", "IncidentBulkCreateRequest", "bulk_create", "POST", true),
        Ticket("helpdesk_incidents", "incidents", "incidentId", "IncidentUpdateRequest", "update", "PUT", true),
        Ticket("helpdesk_incidents", "incidents", "incidentId", "IncidentRequest", "delete", "DELETE", true),
        Ticket("helpdesk_incidents", "incidents", "incidentId", "TicketStateRequest", "state", "POST", true),
        Ticket("helpdesk_incidents", "incidents", "incidentId", "BulkTicketStateRequest", "bulk_state", "POST", true),
        Ticket("helpdesk_incidents", "incidents", "incidentId", "TicketAssignmentRequest", "assign", "POST", true),
        Ticket("helpdesk_incidents", "incidents", "incidentId", "ConfiguredAgentAssignmentRequest", "assign_self", "POST", true),
        Ticket("helpdesk_incidents", "incidents", "incidentId", "IncidentWorklogRequest", "add_worklog", "POST", true),
        Ticket("helpdesk_incidents", "incidents", "incidentId", "IncidentCloseRequest", "close", "POST", true),

        Ticket("helpdesk_requests", "requests", "requestId", "RequestRequest", "get", "GET", false),
        Ticket("helpdesk_requests", "requests", "requestId", "RequestRequest", "tasks", "GET", false),
        Ticket("helpdesk_requests", "requests", "requestId", "RequestRequest", "timeline", "GET", false),
        TicketCount("helpdesk_requests", "requests", "requestId", "timeline_count"),
        TicketCount("helpdesk_requests", "requests", "requestId", "attachments_count"),
        TicketCount("helpdesk_requests", "requests", "requestId", "listeners_count"),
        Ticket("helpdesk_requests", "requests", "requestId", "RequestCreateRequest", "create", "POST", true),
        Ticket("helpdesk_requests", "requests", "requestId", "RequestUpdateRequest", "update", "PUT", true),
        Ticket("helpdesk_requests", "requests", "requestId", "RequestRequest", "delete", "DELETE", true),
        Ticket("helpdesk_requests", "requests", "requestId", "TicketStateRequest", "state", "POST", true),
        Ticket("helpdesk_requests", "requests", "requestId", "TicketWorklogRequest", "add_worklog", "POST", true),

        Ticket("helpdesk_changes", "changes", "changeId", "ChangeRequest", "get", "GET", false),
        Ticket("helpdesk_changes", "changes", "changeId", "ChangeRequest", "timeline", "GET", false),
        Ticket("helpdesk_changes", "changes", "changeId", "ChangeRequest", "worklogs", "GET", false),
        TicketCount("helpdesk_changes", "changes", "changeId", "timeline_count"),
        TicketCount("helpdesk_changes", "changes", "changeId", "attachments_count"),
        TicketCount("helpdesk_changes", "changes", "changeId", "listeners_count"),
        Ticket("helpdesk_changes", "changes", "changeId", "ChangeCreateRequest", "create", "POST", true),
        Ticket("helpdesk_changes", "changes", "changeId", "ChangeUpdateRequest", "update", "PUT", true),
        Ticket("helpdesk_changes", "changes", "changeId", "ChangeRequest", "delete", "DELETE", true),
        Ticket("helpdesk_changes", "changes", "changeId", "TicketStateRequest", "state", "POST", true),
        Ticket("helpdesk_changes", "changes", "changeId", "TicketWorklogRequest", "add_worklog", "POST", true),
        Ticket("helpdesk_changes", "changes", "changeId", "ChangeLifecycleRequest", "lifecycle", "POST", true),
        Operation("helpdesk_ai_assistant", "report", "AiAssistantWorklogRequest", "POST", "/api/v1/ai-assistant/investigations/{invocationId}/worklog", false),

        Read("helpdesk_request_forms", "get", "RequestFormRequest", "/api/v1/request-forms/{requestFormId}", "Get one request form."),
        Read("helpdesk_request_forms", "by_service", "RequestFormsByServiceRequest", "/api/v1/services/{serviceId}/forms/", "List forms for a service."),
        Read("helpdesk_tickets", "timeline_count", "TicketCountRequest", "/api/v1/{ticketType}/{ticketId}/timeline/count", "Count timeline entries."),
        Read("helpdesk_tickets", "attachments_count", "TicketCountRequest", "/api/v1/{ticketType}/{ticketId}/attachments/count", "Count attachments."),
        Read("helpdesk_tickets", "listeners_count", "TicketCountRequest", "/api/v1/{ticketType}/{ticketId}/listeners/count", "Count listeners."),
        .. AdditionalOperations()
    ];

    public static IReadOnlyList<string> OperationsFor(string tool) => All
        .Where(item => string.Equals(item.Tool, tool, StringComparison.Ordinal))
        .Select(item => item.Operation)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public static JsonArray ToJson() => new(All.Select(McpOperationMetadata.ToJson).ToArray());

    public static string CatalogHash => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(ToJson().ToJsonString()))).ToLowerInvariant();

    private static McpOperationDefinition Read(string tool, string operation, string contract, string path, string description) => new(tool, operation, contract, "GET", path, false, description);
    private static McpOperationDefinition Local(string tool, string operation, string contract, bool confirmationRequired, string description) => new(tool, operation, contract, "LOCAL", "local", confirmationRequired, description);
    private static McpOperationDefinition Ticket(string tool, string ticketType, string idName, string contract, string operation, string method, bool confirmationRequired) => new(tool, operation, contract, method, TicketPath(ticketType, idName, operation), confirmationRequired, $"{operation} {ticketType}.");
    private static McpOperationDefinition TicketCount(string tool, string ticketType, string idName, string operation) => new(tool, operation, $"{char.ToUpperInvariant(ticketType[0])}{ticketType[1..^1]}CountRequest", "GET", $"/api/v1/{ticketType}/{{{idName}}}/{operation.Replace('_', '/')}", false, $"Read {operation} for one {ticketType[..^1]}.");
    private static string TicketPath(string ticketType, string idName, string operation) => operation switch
    {
        "create" or "bulk_create" => $"/api/v1/{ticketType}/",
        "bulk_state" => $"/api/v1/{ticketType}/bulk/state",
        "assign" or "assign_self" => $"/api/v1/{ticketType}/bulk/assign",
        "add_worklog" => $"/api/v1/{ticketType}/{{{idName}}}/worklogs",
        "close" => $"/api/v1/{ticketType}/{{{idName}}}/worklogs then /state",
        "state" => $"/api/v1/{ticketType}/{{{idName}}}/state",
        "peek" => $"/api/v1/{ticketType}/{{{idName}}}/peek",
        "timeline" => $"/api/v1/{ticketType}/{{{idName}}}/timeline",
        "tasks" => $"/api/v1/{ticketType}/{{{idName}}}/tasks",
        "worklogs" => $"/api/v1/{ticketType}/{{{idName}}}/worklogs",
        "lifecycle" => $"/api/v1/{ticketType}/{{{idName}}}/lifecycle",
        _ => $"/api/v1/{ticketType}/{{{idName}}}"
    };

    private static IEnumerable<McpOperationDefinition> AdditionalOperations()
    {
        foreach (var definition in TicketRemainder("helpdesk_incidents", "incidents", "incidentId", ["list"])) yield return definition;
        foreach (var definition in TicketRemainder("helpdesk_requests", "requests", "requestId", ["list", "bulk_create", "bulk_state", "assign", "assign_self"])) yield return definition;
        foreach (var definition in TicketRemainder("helpdesk_changes", "changes", "changeId", ["list", "bulk_create", "bulk_state", "assign", "assign_self"])) yield return definition;

        foreach (var operation in new[] { "list", "get", "create", "update", "delete", "start", "complete", "fail", "retry", "bulk_start", "bulk_complete", "bulk_retry" })
            yield return Operation("helpdesk_request_tasks", operation, RequestTaskContract(operation), operation is "list" ? "GET" : operation == "get" ? "GET" : operation is "update" ? "PATCH" : operation == "delete" ? "DELETE" : "POST", RequestTaskPath(operation), operation is not ("list" or "get"));

        yield return Operation("helpdesk_organizations", "list", "ListRequest", "GET", "/api/v1/organizations/", false);
        yield return Operation("helpdesk_organizations", "get", "OrganizationRequest", "GET", "/api/v1/organizations/{organizationId}", false);
        yield return Operation("helpdesk_organizations", "tenants", "None", "GET", "/api/v1/admin/tenants", false);
        yield return Operation("helpdesk_organizations", "tenant_lookup", "None", "GET", "/api/v1/admin/tenants/lookup", false);
        yield return Operation("helpdesk_organizations", "change_participants", "OrganizationRequest", "GET", "/api/v1/organizations/{organizationId}/change-participants", false);

        foreach (var resource in new[] { ("helpdesk_customers", "/api/v1/customers", "customer"), ("helpdesk_users", "/api/v1/users", "user"), ("helpdesk_roles", "/api/v1/admin/role-definitions", "role") })
        {
            yield return Operation(resource.Item1, "list", "ListRequest", "GET", resource.Item2 + "/", false);
            yield return Operation(resource.Item1, "get", $"{resource.Item3[..1].ToUpperInvariant()}{resource.Item3[1..]}Request", "GET", $"{resource.Item2}/{{{resource.Item3}Id}}", false);
        }
        yield return Operation("helpdesk_categories", "list", "ListRequest", "GET", "/api/v1/categories/", false);
        yield return Operation("helpdesk_customers", "auth_status", "CustomerRequest", "GET", "/api/v1/customers/{customerId}/auth/status", false);
        yield return Operation("helpdesk_users", "by_email", "UserEmailRequest", "GET", "/api/v1/users/by-email/{email}", false);
        foreach (var operation in new[] { "users", "customers", "organizations" }) yield return Operation("helpdesk_search", operation, "SearchRequest", "GET", $"/api/v1/global-search/{operation}", false);

        yield return Operation("helpdesk_tickets", "mark_as_seen", "TicketSeenRequest", "POST", "/api/v1/tickets/{ticketId}/mark-as-seen", true);

        yield return Operation("helpdesk_services", "list", "ListRequest", "GET", "/api/v1/services/", false);
        yield return Operation("helpdesk_services", "get", "ServiceRequest", "GET", "/api/v1/services/{serviceId}", false);
        yield return Operation("helpdesk_services", "items", "ServiceRequest", "GET", "/api/v1/service-items/{serviceId}", false);
        yield return Operation("helpdesk_services", "search_items", "SearchRequest", "GET", "/api/v1/service-items/search", false);
        yield return Operation("helpdesk_services", "breadcrumb", "ServiceRequest", "GET", "/api/v1/services/{serviceId}/breadcrumb", false);

        foreach (var operation in new[] { "requests", "request_users" }) yield return Operation("helpdesk_self_service", operation, "ListRequest", "GET", $"/api/v1/self-service/{operation.Replace('_', '-')}", false);
        yield return Operation("helpdesk_self_service", "get_request", "RequestRequest", "GET", "/api/v1/self-service/requests/{requestId}", false);
        foreach (var operation in new[] { "list", "get", "summary", "unread_errors" }) yield return Operation("helpdesk_notifications", operation, operation == "get" ? "NotificationRequest" : operation == "summary" ? "None" : "ListRequest", "GET", operation switch { "summary" => "/api/v1/notifications/error-summary", "unread_errors" => "/api/v1/notifications/unread-errors", "get" => "/api/v1/notifications/{notificationId}", _ => "/api/v1/notifications" }, false);
        yield return Operation("helpdesk_notifications", "mark_read", "NotificationMarkReadRequest", "POST", "/api/v1/notifications/mark-read", true);

        foreach (var (operation, path) in new[]
                 {
                     ("orchestration", "/api/v1/admin/orchestration"),
                     ("orchestration_jobs", "/api/v1/admin/orchestration/catalog/jobs"),
                     ("orchestration_tenants", "/api/v1/admin/orchestration/catalog/tenants"),
                     ("orchestration_request_definitions", "/api/v1/admin/orchestration/catalog/request-definitions"),
                     ("orchestration_bindings", "/api/v1/admin/orchestration/bindings")
                 }) yield return Operation("helpdesk_connectivity", operation, "None", "GET", path, false);
        yield return Operation("helpdesk_connectivity", "orchestration_test", "None", "POST", "/api/v1/admin/orchestration/test", true);
        yield return Local("helpdesk_schema", "list", "None", false, "Read the executable MCP operation catalog.");
        foreach (var operation in new[] { "list", "ticket_state", "ticket_priority", "request_task_status", "change_lifecycle_state" }) yield return Local("helpdesk_enums", operation, "None", false, "Read enum metadata.");
        yield return Local("helpdesk_examples", "list", "None", false, "Read operator examples.");
        yield return Operation("helpdesk_raw", "system_version", "None", "GET", "/api/v1/system/version", false);
        yield return Operation("helpdesk_raw", "get_incident", "AllowlistedReadRequest", "GET", "/api/v1/incidents/{incidentId}", false);
        yield return Operation("helpdesk_raw", "get_request", "AllowlistedReadRequest", "GET", "/api/v1/requests/{requestId}", false);
        yield return Operation("helpdesk_raw", "get_change", "AllowlistedReadRequest", "GET", "/api/v1/changes/{changeId}", false);

        foreach (var (operation, method, path) in AdminOperations()) yield return Operation("helpdesk_admin_mutations", operation, operation == "provision_user" ? "None" : "AdministrativeMutationRequest", method, path, true);
    }

    private static IEnumerable<McpOperationDefinition> TicketRemainder(string tool, string type, string idName, IReadOnlyList<string> operations)
    {
        foreach (var operation in operations)
            yield return Operation(tool, operation, operation switch
                {
                    "bulk_create" => $"{char.ToUpperInvariant(type[0])}{type[1..^1]}BulkCreateRequest",
                    "bulk_state" => "BulkTicketStateRequest",
                    "assign" => "TicketAssignmentRequest",
                    "assign_self" => "ConfiguredAgentAssignmentRequest",
                    _ => "ListRequest"
                }, operation == "list" ? "GET" : "POST", operation == "list" ? $"/api/v1/{type}/" : TicketPath(type, idName, operation), operation != "list");
    }

    private static string RequestTaskPath(string operation) => operation switch
    {
        "list" or "create" => "/api/v1/request-tasks/",
        "get" or "update" or "delete" => "/api/v1/request-tasks/{taskId}",
        "bulk_start" or "bulk_complete" or "bulk_retry" => "/api/v1/request-tasks/bulk/" + operation[5..].Replace('_', '-'),
        _ => "/api/v1/request-tasks/{taskId}/" + operation
    };

    private static string RequestTaskContract(string operation) => operation switch
    {
        "list" => "ListRequest",
        "get" or "delete" or "start" or "complete" or "fail" or "retry" => "RequestTaskRequest",
        "create" => "RequestTaskCreateRequest",
        "update" => "RequestTaskUpdateRequest",
        "bulk_start" or "bulk_complete" or "bulk_retry" => "BulkRequestTaskActionRequest",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown request task operation.")
    };

    private static IEnumerable<(string Operation, string Method, string Path)> AdminOperations()
    {
        foreach (var (resource, singular) in new[] { ("organizations", "organization"), ("customers", "customer"), ("users", "user"), ("categories", "category"), ("services", "service") })
        {
            yield return ($"create_{singular}", "POST", $"/api/v1/{resource}");
            yield return ($"update_{singular}", "PUT", $"/api/v1/{resource}/{{{singular}Id}}");
            yield return ($"delete_{singular}", "DELETE", $"/api/v1/{resource}/{{{singular}Id}}");
        }
        yield return ("create_role", "POST", "/api/v1/admin/role-definitions");
        yield return ("update_role", "PUT", "/api/v1/admin/role-definitions/{roleId}");
        yield return ("delete_role", "DELETE", "/api/v1/admin/role-definitions/{roleId}");
        yield return ("create_request_form", "POST", "/api/v1/request-forms");
        yield return ("create_service_request_form", "POST", "/api/v1/services/{serviceId}/forms/");
        yield return ("provision_user", "POST", "/api/v1/users/provision");
        yield return ("update_organization_branding", "PUT", "/api/v1/tenants/{tenantId}/branding");
        yield return ("update_request_form", "PUT", "/api/v1/request-forms/{requestFormId}");
        yield return ("delete_request_form", "DELETE", "/api/v1/request-forms/{requestFormId}");
        yield return ("create_self_service_request", "POST", "/api/v1/self-service/requests");
        foreach (var operation in new[] { "customer_invite", "customer_resend_invite", "customer_disable_login", "customer_sync_authentik" }) yield return (operation, "POST", "/api/v1/customers/{customerId}/auth/" + operation[9..].Replace('_', '-'));
    }

    private static McpOperationDefinition Operation(string tool, string operation, string contract, string method, string path, bool confirmationRequired) => new(tool, operation, contract, method, path, confirmationRequired,
        tool is "helpdesk_incidents" or "helpdesk_requests" or "helpdesk_changes" && operation == "list"
            ? "List authorized tickets. request.query maps to API q; request.q is an alias. Equivalent trimmed aliases deduplicate; conflicting nonempty aliases fail validation. Null or blank search is unfiltered; filtering precedes counts and paging."
            : $"{operation} operation.");
}
