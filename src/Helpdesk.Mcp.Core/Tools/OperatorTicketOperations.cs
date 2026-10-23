using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helpdesk.Mcp.Tools;

internal static class OperatorTicketOperations
{
    public static IReadOnlyList<OperatorOperation> All { get; } =
    [
        new("helpdesk_tickets", "organization_options", "GET", "/api/v1/ticketing/organizations", QueryType: typeof(OperatorTicketModule),
            Authority: "Current ticket module permissions and enabled organizations", Description: "Read the enabled organizations available in the caller's ticket creation and editing interface."),
        new("helpdesk_tickets", "customer_options", "GET", "/api/v1/ticketing/customers", QueryType: typeof(OperatorTicketOrganization),
            Authority: "Current module and organization authority; own-customer scope for non-managers", Description: "Read enabled customer choices for one authorized organization and ticket module."),
        new("helpdesk_tickets", "search_assignees", "GET", "/api/v1/ticketing/assignees/search", QueryType: typeof(OperatorAssigneeSearch),
            Authority: "Manager of the selected ticket module and organization", Description: "Search bounded eligible assignees, including saved external support coverage. Use the returned eligible identity in confirmed assignment actions."),
        new("helpdesk_tickets", "current_assignee", "GET", "/api/v1/ticketing/assignees/current", QueryType: typeof(OperatorCurrentAssignee),
            Authority: "Visibility of the selected ticket and matching organization", Description: "Resolve an existing ticket assignment, including a retained user who is no longer eligible for a new assignment."),
        new("helpdesk_tickets", "filter_options", "GET", "/api/v1/ticketing/filter-options", QueryType: typeof(OperatorTicketFilterOptions),
            Authority: "Current module ticket visibility", Description: "Search authorized organization, requested-for and assignee labels for ticket filters. selectedId and customerOrgId are bounded repeated query values."),
        new("helpdesk_tickets", "set_customer", "POST", "/api/v1/{ticketType}/{ticketId}/customer", typeof(OperatorSetTicketCustomer), typeof(OperatorTicketPath),
            Authority: "Manager of the parent ticket's organization and module", Description: "Set a requester only on a ticket without one, using an enabled customer in the same organization. Read the ticket afterward to verify the saved requester."),
        new("helpdesk_tickets", "incident_activity", "GET", "/api/v1/incidents/{incidentId}/activity",
            Authority: "Parent incident visibility", Description: "Read the persisted incident activity audit, independently of the rendered timeline."),
        new("helpdesk_tickets", "request_worklogs", "GET", "/api/v1/requests/{requestId}/worklogs",
            Authority: "Parent request visibility; internal notes require staff scope", Description: "Read authorized request worklog records and their stored effort."),
        new("helpdesk_tickets", "self_service_tasks", "GET", "/api/v1/self-service/requests/{requestId}/tasks",
            Authority: "SelfService.User and current customer/form audience, or HelpdeskAdmin", Description: "Read task progress through the same owned-request and released-form audience rules as the self-service interface."),
        new("helpdesk_changes", "my_approval", "GET", "/api/v1/changes/{changeId}/approval/",
            Authority: "Stored designated approver identity and current change visibility or approval authority", Description: "Read the caller's designated change approval. An owner cannot substitute another approver's identity."),
        new("helpdesk_changes", "review_approval", "POST", "/api/v1/changes/{changeId}/approval/review", ExternalIo: true,
            Authority: "Stored designated approver identity and current change visibility or approval authority", Description: "Review and approve the caller's own pending change approval. The last required review advances the change to ApprovedForImplementation and queues its timeline and notifications. Signed and authenticated decisions serialize together; a repeated committed review adds no duplicate effects. On 409 read my_approval before another action."),
        new("helpdesk_tenant_admin", "dashboard", "GET", "/api/v1/dashboard/admin-summary", Description: "Read live administrative incident, request and change dashboard metrics."),
        new("helpdesk_tenant_admin", "technician_dashboard", "GET", "/api/v1/dashboard/technician-summary", Authority: "HelpdeskStaff",
            Description: "Read the authenticated technician's authorized open ticket counts."),
        new("helpdesk_tenant_admin", "customer_dashboard", "GET", "/api/v1/dashboard/customer-summary", Authority: "SelfService.User",
            Description: "Read the current linked customer's ticket summary. An account with no linked customer receives the canonical empty summary."),
        new("helpdesk_tenant_admin", "online_users", "GET", "/api/v1/presence/online-users", Description: "Read identities currently connected to the platform's presence service."),
        new("helpdesk_notifications", "unread", "GET", "/api/v1/notifications/unread", QueryType: typeof(OperatorNotificationPage),
            Authority: "NotificationAccess and current recipient visibility", Description: "Read one bounded page of the caller's unread notifications."),
        new("helpdesk_notifications", "error_summary", "GET", "/api/v1/notifications/error-summary", Authority: "NotificationAccess and current recipient visibility",
            Description: "Read the caller's authorized error notification counters."),
        new("helpdesk_notifications", "timeline", "GET", "/api/v1/notifications/timeline", QueryType: typeof(OperatorNotificationTimeline),
            Authority: "NotificationAccess and current recipient/organization visibility", Description: "Read persisted DomainEvent notifications for a reference, correlationId or both, with bounded output. At least one selector must contain non-whitespace text.")
    ];

    internal static void RefineRequestSchema(OperatorOperation operation, JsonObject schema)
    {
        if (operation.Tool == "helpdesk_tickets" && operation.Operation == "filter_options")
        {
            foreach (var name in new[] { "customerOrgId", "selectedId" })
                schema["properties"]![name]!["items"] = new JsonObject
                {
                    ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200, ["pattern"] = "\\S"
                };
        }
        if (operation.Tool == "helpdesk_notifications" && operation.Operation == "timeline")
        {
            schema["anyOf"] = new JsonArray(NonblankSelector("reference"), NonblankSelector("correlationId"));
        }
    }

    internal static string? ValidateRequest(OperatorOperation operation, JsonElement? request)
    {
        if (operation.Tool != "helpdesk_notifications" || operation.Operation != "timeline") return null;
        return request is { } document && (HasText(document, "reference") || HasText(document, "correlationId"))
            ? null : "request must contain a nonblank reference or correlationId.";
    }

    private static JsonObject NonblankSelector(string name) => new()
    {
        ["required"] = new JsonArray(name),
        ["properties"] = new JsonObject
        {
            [name] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["pattern"] = "\\S" }
        }
    };

    private static bool HasText(JsonElement request, string name) => request.ValueKind == JsonValueKind.Object &&
        request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());
}

internal class OperatorTicketModule
{
    [Required, AllowedValues("incident", "request", "change")] public string Module { get; set; } = "incident";
}
internal class OperatorTicketOrganization : OperatorTicketModule
{
    [Required, MaxLength(128)] public string OrganizationId { get; set; } = "";
}
internal sealed class OperatorAssigneeSearch : OperatorTicketOrganization
{
    [MaxLength(200)] public string? Q { get; set; }
    [Range(1, 50)] public int? Limit { get; set; }
}
internal sealed class OperatorCurrentAssignee : OperatorTicketOrganization
{
    [Required, MaxLength(128)] public string TicketId { get; set; } = "";
}
internal sealed class OperatorTicketFilterOptions : OperatorTicketModule
{
    [Required, AllowedValues("customer", "for", "assignee")] public string Dimension { get; set; } = "customer";
    [MaxLength(200)] public string? Q { get; set; }
    [Range(1, 50)] public int? Take { get; set; }
    [MaxLength(20)] public string[]? CustomerOrgId { get; set; }
    [MaxLength(20)] public string[]? SelectedId { get; set; }
}
internal sealed class OperatorTicketPath
{
    [Required, AllowedValues("incidents", "requests", "changes")] public string TicketType { get; set; } = "incidents";
}
internal sealed class OperatorSetTicketCustomer
{
    [Required, MaxLength(128)] public string CustomerId { get; set; } = "";
}
internal sealed class OperatorNotificationPage
{
    [Range(1, 1000000)] public int? Page { get; set; }
    [Range(1, 100)] public int? PageSize { get; set; }
}
internal sealed class OperatorNotificationTimeline
{
    [MaxLength(128)] public string? Reference { get; set; }
    [MaxLength(128)] public string? CorrelationId { get; set; }
    [MaxLength(128)] public string? TenantId { get; set; }
    [Range(1, 200)] public int? Take { get; set; }
}
