using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.Models;
using ModelContextProtocol.Server;

namespace Helpdesk.Mcp.Tools;

public sealed record HelpdeskToolFailure(string Code, bool Retryable, int? UpstreamStatus = null, IReadOnlyList<string>? AllowedOperations = null);
public sealed record HelpdeskConfirmation(string ConfirmField, bool RequiredValue, string Operation, IReadOnlyList<string> AffectedIds);
public sealed record HelpdeskToolResponse(bool Success, string Status, string Summary, JsonNode? Data = null, JsonNode? Evidence = null, IReadOnlyList<string>? AffectedIds = null, IReadOnlyList<string>? NextActions = null, string? CorrelationId = null, HelpdeskToolFailure? Failure = null, HelpdeskConfirmation? Confirmation = null)
{
    public HelpdeskMcpTargetInfo? Target { get; init; }

    public HelpdeskMcpHostContext? HostContext { get; init; }
}

[McpServerToolType]
public sealed class HelpdeskTools(
    IHelpdeskAgentClient client,
    IHelpdeskMcpConfigurationSurface configurationSurface,
    HelpdeskMcpHostContext hostContext)
{
    // Kept for existing direct unit callers; hosted transports supply the immutable host context.
    public HelpdeskTools(IHelpdeskAgentClient client, AgentClientConfigurationStore configurationStore, HelpdeskMcpTarget target)
        : this(client, new AgentClientConfigurationSurface(configurationStore), target.ToHostContext("stdio"))
    {
    }

    public HelpdeskTools(IHelpdeskAgentClient client, AgentClientConfigurationStore configurationStore)
        : this(client, configurationStore, new HelpdeskMcpTarget("test", new Uri("https://helpdesk.test")))
    {
    }

    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly HashSet<string> RequestTaskUpdateFields = new(StringComparer.Ordinal)
    {
        "taskId", "title", "description", "priority", "assignedToId", "clearAssignment", "linkedAssetIds", "attachments", "dueDate", "clearDueDate"
    };

    [McpServerTool(UseStructuredContent = true), Description("Read authentication status for the configured Helpdesk credential. Integration credentials use the application identity endpoint; Authentik agents use the AI-agent status endpoint. The MCP server never returns bearer tokens.")]
    public Task<HelpdeskToolResponse> helpdesk_auth(string operation = "status", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => Read("helpdesk_auth", operation, ["status"], AuthStatusPath, null, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read live, readiness, and authenticated Helpdesk health.")]
    public async Task<HelpdeskToolResponse> helpdesk_health(string operation = "get", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => operation != "get" ? Invalid("helpdesk_health", operation, ["get"]) : Completed("Helpdesk health read.", await client.GetHealthAsync(cancellationToken).ConfigureAwait(false));

    [McpServerTool(UseStructuredContent = true), Description("Read or safely update local Helpdesk MCP configuration. show is redacted; get reads an allowlisted persisted value; set and unset require confirm:true and only permit apiBaseUrl or authentikScope. Authentication endpoint, client identity, username, and passwords are externally managed and cannot be changed or returned.")]
    public Task<HelpdeskToolResponse> helpdesk_config(string operation = "show", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation == "show") return Task.FromResult(Completed("Redacted configuration read.", JsonSerializer.SerializeToNode(AgentClientConfigurationResolver.Redact(client.Configuration))));
        var payload = Object(request);
        var key = payload?["key"]?.ToString();
        if (key is not ("apiBaseUrl" or "authentikScope")) return Task.FromResult(Validation("request.key must be apiBaseUrl or authentikScope."));
        if (operation == "get")
        {
            var persisted = configurationSurface.Load();
            var persistedValue = key == "apiBaseUrl" ? persisted.ApiBaseUrl : persisted.AuthentikScope;
            return Task.FromResult(Completed($"Persisted {key} read.", new JsonObject { ["key"] = key, ["value"] = persistedValue }));
        }
        if (operation is not ("set" or "unset")) return Task.FromResult(Invalid("helpdesk_config", operation, ["show", "get", "set", "unset"]));
        var value = payload?["value"]?.ToString();
        if (operation == "set" && string.IsNullOrWhiteSpace(value)) return Task.FromResult(Validation("request.value is required for set."));
        if (operation == "set" && key == "apiBaseUrl" && !IsHttpUrl(value)) return Task.FromResult(Validation("request.value must be an absolute http or https API URL."));
        if (operation == "set" && key == "apiBaseUrl" && !hostContext.MatchesApiBaseUrl(value)) return Task.FromResult(Validation($"request.value must match the active {hostContext.Instance} MCP endpoint."));
        if (!configurationSurface.CanPersist) return Task.FromResult(Validation($"Persisted Helpdesk configuration writes are not available over {hostContext.Transport}."));
        if (!confirm) return Task.FromResult(Response(false, "confirmation_required", $"This would {operation} persisted Helpdesk configuration key {key}.", AffectedIds: [key], Confirmation: new HelpdeskConfirmation("confirm", true, operation, [key])));
        var current = configurationSurface.Load();
        var updated = operation == "unset" ? null : value;
        configurationSurface.Save(key == "apiBaseUrl" ? current with { ApiBaseUrl = updated } : current with { AuthentikScope = updated });
        return Task.FromResult(Completed($"Persisted Helpdesk configuration {operation} completed.", new JsonObject { ["key"] = key, ["value"] = updated, ["reloadRequired"] = true }));
    }

    [McpServerTool(UseStructuredContent = true), Description("Read Helpdesk capabilities. Operation must be get.")]
    public Task<HelpdeskToolResponse> helpdesk_capabilities(string operation = "get", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => operation != "get" ? Task.FromResult(Invalid("helpdesk_capabilities", operation, ["get"])) : Task.FromResult(Completed("Capabilities read.", Capabilities()));

    [McpServerTool(UseStructuredContent = true), Description("Read safe Helpdesk system metadata. Operations: version.")]
    public Task<HelpdeskToolResponse> helpdesk_system(string operation = "version", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default) => Read("helpdesk_system", operation, ["version"], "/api/v1/system/version", null, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read bounded operational diagnostics. Operation search requires request.since, request.correlationId, or request.contains; maximum limit is 100. Raw log export is not available.")]
    public async Task<HelpdeskToolResponse> helpdesk_logs(string operation = "search", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation != "search") return Invalid("helpdesk_logs", operation, ["search"]);
        if (request is not { ValueKind: JsonValueKind.Object } element) return Validation("request must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (element.EnumerateObject().Any(property => !names.Add(property.Name))) return Validation("request must not contain duplicate properties.");
        var payload = Object(request)!;
        foreach (var field in new[] { "correlationId", "contains", "level" })
            if (payload.ContainsKey(field) && !HasText(payload[field])) return Validation($"request.{field} must be a nonempty string.");
        if (payload["since"] is { } since && (since.GetValueKind() != JsonValueKind.Number || since is not JsonValue cursor || !cursor.TryGetValue<long>(out var sinceCursor) || sinceCursor < 0))
            return Validation("request.since must be a nonnegative integer log cursor.");
        if (payload["since"] is null && payload["correlationId"] is null && payload["contains"] is null) return Validation("Logs require request.since, request.correlationId, or request.contains.");
        var limit = 50;
        if (payload["limit"] is { } value && (value.GetValueKind() != JsonValueKind.Number || value is not JsonValue count || !count.TryGetValue<int>(out limit)))
            return Validation("request.limit must be an integer between 1 and 100.");
        if (limit is < 1 or > 100) return Validation("request.limit must be between 1 and 100.");
        payload["limit"] = limit;
        return await Execute(() => client.GetAsync(Query("/api/v1/ops/ai-agent/logs", payload, "correlationId"), true, cancellationToken), "Operational diagnostics read.").ConfigureAwait(false);
    }

    [McpServerTool(UseStructuredContent = true), Description("Manage incidents. Read operations: list, get, peek, timeline, timeline_count, attachments_count, listeners_count. For list, request.query searches through the API q parameter; request.q is also supported. Blank searches are unfiltered; equivalent trimmed aliases deduplicate and conflicting nonempty aliases fail validation. Confirmed mutations: create, bulk_create, update, delete, state, bulk_state, assign, assign_self, add_worklog, close. In caller-bound mode, assign_self uses the authenticated application identity; external agents use their configured agent identity. close requires request.incidentId and request.closureNote, writes an internal closure note by default, then sets the incident state to Resolved. Every mutation requires confirm:true.")]
    public Task<HelpdeskToolResponse> helpdesk_incidents(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default) => TicketOperation("helpdesk_incidents", "/api/v1/incidents", "incidentId", operation, request, confirm, cancellationToken);
    [McpServerTool(UseStructuredContent = true), Description("Manage service requests. List query/q aliases map to API q; blank searches are unfiltered, equivalent trimmed aliases deduplicate, and conflicting nonempty values fail validation. Reads: list, get, tasks, timeline, timeline_count, attachments_count, listeners_count. Confirmed mutations: create, bulk_create, update, delete, state, bulk_state, assign, assign_self, add_worklog. state requires request.requestId and request.newState; bulk_state requires request.ids and request.newState; assign requires request.ids and request.assignedToId. Every mutation requires confirm:true.")]
    public Task<HelpdeskToolResponse> helpdesk_requests(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default) => TicketOperation("helpdesk_requests", "/api/v1/requests", "requestId", operation, request, confirm, cancellationToken);
    [McpServerTool(UseStructuredContent = true), Description("Manage changes. List query/q aliases map to API q; blank searches are unfiltered, equivalent trimmed aliases deduplicate, and conflicting nonempty values fail validation. Reads: list, get, timeline, worklogs, timeline_count, attachments_count, listeners_count. Confirmed mutations: create, bulk_create, update, delete, state, bulk_state, assign, assign_self, add_worklog, lifecycle. Every mutation requires confirm:true.")]
    public Task<HelpdeskToolResponse> helpdesk_changes(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default) => TicketOperation("helpdesk_changes", "/api/v1/changes", "changeId", operation, request, confirm, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Report AiAssistant AI investigation progress to its existing Helpdesk AI worklog. Operations: report. This narrowly scoped autonomous update does not create a normal worklog or change ticket state; it requires invocationId, eventId, ticketId, ticketType, correlationId, status, and message. The existing authenticated MCP/API identity is required. Tenant scope is derived from the matched Helpdesk invocation; the tool cannot select, override, or bypass it.")]
    public async Task<HelpdeskToolResponse> helpdesk_ai_assistant(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation != "report") return Invalid("helpdesk_ai_assistant", operation, ["report"]);
        if (!TryRequest<AiAssistantWorklogRequest>(request, out var update, out var error)) return Validation(error);
        if (update.TicketType is not ("incidents" or "requests" or "changes")) return Validation("request.ticketType must be incidents, requests, or changes.");
        if (!Enum.TryParse<AiInvestigationStatus>(update.Status, true, out var status) || !Enum.IsDefined(status)) return Validation("request.status is not a valid AI investigation status.");
        if (string.IsNullOrWhiteSpace(update.EventId) || string.IsNullOrWhiteSpace(update.TicketId) || string.IsNullOrWhiteSpace(update.CorrelationId) || string.IsNullOrWhiteSpace(update.Message)) return Validation("eventId, ticketId, correlationId, and message are required.");
        var dto = new AppendAiInvestigationWorklogDto(update.EventId, update.TicketId, update.TicketType, update.CorrelationId, status, update.Severity, update.Message, update.MetadataJson, update.ArtifactReferencesJson, update.RunReference, DateTimeOffset.UtcNow);
        return await Mutation("report", [update.TicketId], true, HttpMethod.Post, $"/api/v1/ai-assistant/investigations/{update.InvocationId}/worklog", JsonSerializer.SerializeToNode(dto), cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(UseStructuredContent = true), Description("Manage request tasks. List query/q aliases map to API q; blank searches are unfiltered, equivalent trimmed aliases deduplicate, and conflicting nonempty values fail validation. Reads: list, get. Confirmed mutations: create, update, delete, start, complete, fail, retry, bulk_start, bulk_complete, bulk_retry. Single-task actions require request.taskId; bulk actions require request.ids. Every mutation requires confirm:true.")]
    public Task<HelpdeskToolResponse> helpdesk_request_tasks(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default) => TaskOperation(operation, request, confirm, cancellationToken);
    [McpServerTool(UseStructuredContent = true), Description("Inspect organizations. Operations: list, get, tenants, tenant_lookup, change_participants. tenants and tenant_lookup are global administrative reads; remaining detail operations require organizationId.")]
    public async Task<HelpdeskToolResponse> helpdesk_organizations(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation == "tenants") return await Execute(() => client.GetAsync("/api/v1/admin/tenants", true, cancellationToken), "Organization tenants completed.").ConfigureAwait(false);
        if (operation == "tenant_lookup") return await Execute(() => client.GetAsync("/api/v1/admin/tenants/lookup", true, cancellationToken), "Organization tenant lookup completed.").ConfigureAwait(false);
        if (operation == "change_participants")
        {
            var id = String(request, "organizationId");
            return string.IsNullOrWhiteSpace(id)
                ? Validation("request.organizationId is required.")
                : await Execute(() => client.GetAsync($"/api/v1/organizations/{Uri.EscapeDataString(id)}/change-participants", true, cancellationToken), "Organization change participants completed.").ConfigureAwait(false);
        }
        return await LookupRead("helpdesk_organizations", "/api/v1/organizations", "organizationId", operation, request, cancellationToken).ConfigureAwait(false);
    }
    [McpServerTool(UseStructuredContent = true), Description("Inspect customers. Operations: list, get, auth_status. get and auth_status require customerId.")]
    public Task<HelpdeskToolResponse> helpdesk_customers(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default) => LookupRead("helpdesk_customers", "/api/v1/customers", "customerId", operation, request, cancellationToken);
    [McpServerTool(UseStructuredContent = true), Description("Inspect users. Operations: list, get, by_email. get requires userId; by_email requires email.")]
    public async Task<HelpdeskToolResponse> helpdesk_users(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation == "by_email") { var email = String(request, "email"); return string.IsNullOrWhiteSpace(email) ? Validation("request.email is required.") : await Execute(() => client.GetAsync($"/api/v1/users/by-email/{Uri.EscapeDataString(email)}", true, cancellationToken), "Users by_email completed.").ConfigureAwait(false); }
        return await LookupRead("helpdesk_users", "/api/v1/users", "userId", operation, request, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(UseStructuredContent = true), Description("Search bounded Helpdesk lookup data. Operations: users, customers, organizations. request.query or request.q is required. request.limit and request.pageSize are aliases capped at 25. The existing API requires HelpdeskAdmin authority.")]
    public async Task<HelpdeskToolResponse> helpdesk_search(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation is not ("users" or "customers" or "organizations")) return Invalid("helpdesk_search", operation, ["users", "customers", "organizations"]);
        if (NormalizeSearchAliases(request, out var payload) is { } aliasError) return Validation(aliasError);
        if (string.IsNullOrWhiteSpace(payload?["q"]?.GetValue<string>())) return Validation("request.query or request.q is required.");
        int? limit = null, pageSize = null;
        foreach (var field in new[] { "limit", "pageSize" })
        {
            if (payload![field] is not { } value) continue;
            if (value.GetValueKind() != JsonValueKind.Number || value is not JsonValue number || !number.TryGetValue<int>(out var parsed) || parsed < 1)
                return Validation($"request.{field} must be a positive integer.");
            if (field == "limit") limit = parsed; else pageSize = parsed;
        }
        if (limit.HasValue && pageSize.HasValue && limit != pageSize) return Validation("request.limit and request.pageSize must specify the same bound when both are provided.");
        payload!.Remove("limit");
        payload["pageSize"] = Math.Min(pageSize ?? limit ?? 20, 25);
        return await Execute(() => client.GetAsync(Query($"/api/v1/global-search/{operation}", payload), true, cancellationToken), "Bounded search completed.").ConfigureAwait(false);
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect shared ticket counts. Operations: timeline_count, attachments_count, listeners_count. Count operations require request.ticketType (incidents, requests, or changes) and request.ticketId. Prefer the matching domain tool when the ticket type is already known.")]
    public async Task<HelpdeskToolResponse> helpdesk_tickets(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation is "timeline_count" or "attachments_count" or "listeners_count")
        {
            if (!TryRequest<TicketCountRequest>(request, out var count, out var error)) return Validation(error);
            if (count.TicketType is not ("incidents" or "requests" or "changes")) return Validation("request.ticketType must be incidents, requests, or changes.");
            if (string.IsNullOrWhiteSpace(count.TicketId)) return Validation("request.ticketId is required.");
            var countPath = operation.Replace('_', '/');
            return await Execute(() => client.GetAsync($"/api/v1/{count.TicketType}/{Uri.EscapeDataString(count.TicketId)}/{countPath}", true, cancellationToken), $"Ticket {operation} completed.").ConfigureAwait(false);
        }
        if (operation == "mark_as_seen")
        {
            var id = String(request, "ticketId");
            return string.IsNullOrWhiteSpace(id)
                ? Validation("request.ticketId is required.")
                : await Mutation(operation, [id], confirm, HttpMethod.Post, $"/api/v1/tickets/{Uri.EscapeDataString(id)}/mark-as-seen", BodyWithout(Object(request), "ticketId"), cancellationToken).ConfigureAwait(false);
        }
        return Invalid("helpdesk_tickets", operation, ["timeline_count", "attachments_count", "listeners_count", "mark_as_seen"]);
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect permission-aware role definitions. Operations: list, get; get requires request.roleId.")]
    public Task<HelpdeskToolResponse> helpdesk_roles(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default) => CollectionRead("helpdesk_roles", "/api/v1/admin/role-definitions", "roleId", operation, request, cancellationToken);
    [McpServerTool(UseStructuredContent = true), Description("Inspect ticket categories. Operation: list. The Helpdesk API has no category detail route.")]
    public Task<HelpdeskToolResponse> helpdesk_categories(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => operation == "list" ? CollectionRead("helpdesk_categories", "/api/v1/categories", "categoryId", operation, request, cancellationToken) : Task.FromResult(Invalid("helpdesk_categories", operation, ["list"]));

    [McpServerTool(UseStructuredContent = true), Description("Inspect service catalog data. Operations: list, get, items, search_items, breadcrumb. Non-list operations require request.serviceId except search_items, which requires nonempty request.query or request.q and normalizes both aliases to API q.")]
    public async Task<HelpdeskToolResponse> helpdesk_services(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation == "list") return await Execute(() => client.GetAsync(Query("/api/v1/services/", Object(request)), true, cancellationToken), "Services list completed.").ConfigureAwait(false);
        if (operation == "search_items")
        {
            if (NormalizeSearchAliases(request, out var search) is { } aliasError) return Validation(aliasError);
            return string.IsNullOrWhiteSpace(search?["q"]?.GetValue<string>())
                ? Validation("request.query or request.q is required.")
                : await Execute(() => client.GetAsync(Query("/api/v1/service-items/search", search), true, cancellationToken), "Service item search completed.").ConfigureAwait(false);
        }
        var id = String(request, "serviceId"); if (string.IsNullOrWhiteSpace(id)) return Validation("request.serviceId is required.");
        if (operation is not ("get" or "items" or "breadcrumb")) return Invalid("helpdesk_services", operation, ["list", "get", "items", "search_items", "breadcrumb"]);
        var path = operation == "items" ? $"/api/v1/service-items/{Uri.EscapeDataString(id)}" : operation == "breadcrumb" ? $"/api/v1/services/{Uri.EscapeDataString(id)}/breadcrumb" : $"/api/v1/services/{Uri.EscapeDataString(id)}";
        return await Execute(() => client.GetAsync(path, true, cancellationToken), $"Services {operation} completed.").ConfigureAwait(false);
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect request forms. Operations: get, by_service. get requires request.requestFormId; by_service requires request.serviceId.")]
    public async Task<HelpdeskToolResponse> helpdesk_request_forms(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation == "get")
        {
            if (!TryRequest<RequestFormRequest>(request, out var form, out var formError)) return Validation(formError);
            if (string.IsNullOrWhiteSpace(form.RequestFormId)) return Validation("request.requestFormId is required.");
            return await Execute(() => client.GetAsync($"/api/v1/request-forms/{Uri.EscapeDataString(form.RequestFormId)}", true, cancellationToken), "Request form get completed.").ConfigureAwait(false);
        }
        if (operation == "by_service")
        {
            if (!TryRequest<RequestFormsByServiceRequest>(request, out var service, out var serviceError)) return Validation(serviceError);
            if (string.IsNullOrWhiteSpace(service.ServiceId)) return Validation("request.serviceId is required.");
            return await Execute(() => client.GetAsync($"/api/v1/services/{Uri.EscapeDataString(service.ServiceId)}/forms/", true, cancellationToken), "Request forms by_service completed.").ConfigureAwait(false);
        }
        return Invalid("helpdesk_request_forms", operation, ["get", "by_service"]);
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect self-service data. Operations: requests, get_request, request_users. get_request requires request.requestId.")]
    public async Task<HelpdeskToolResponse> helpdesk_self_service(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation == "requests") return await Execute(() => client.GetAsync(Query("/api/v1/self-service/requests", Object(request)), true, cancellationToken), "Self-service requests completed.").ConfigureAwait(false);
        if (operation == "request_users") return await Execute(() => client.GetAsync(Query("/api/v1/self-service/request-users", Object(request)), true, cancellationToken), "Self-service request users completed.").ConfigureAwait(false);
        var id = String(request, "requestId"); return operation != "get_request" ? Invalid("helpdesk_self_service", operation, ["requests", "get_request", "request_users"]) : string.IsNullOrWhiteSpace(id) ? Validation("request.requestId is required.") : await Execute(() => client.GetAsync($"/api/v1/self-service/requests/{Uri.EscapeDataString(id)}", true, cancellationToken), "Self-service request completed.").ConfigureAwait(false);
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect notifications. Operations: list, get, summary, unread_errors. get requires request.notificationId.")]
    public async Task<HelpdeskToolResponse> helpdesk_notifications(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation == "list") return await Execute(() => client.GetAsync(Query("/api/v1/notifications", Object(request)), true, cancellationToken), "Notifications list completed.").ConfigureAwait(false);
        if (operation == "summary") return await Execute(() => client.GetAsync("/api/v1/notifications/summary", true, cancellationToken), "Notification summary completed.").ConfigureAwait(false);
        if (operation == "unread_errors") return await Execute(() => client.GetAsync(Query("/api/v1/notifications/unread-errors", Object(request)), true, cancellationToken), "Unread errors completed.").ConfigureAwait(false);
        if (operation == "mark_read")
        {
            var ids = Strings(Object(request), "ids");
            return ids.Count == 0 ? Validation("request.ids must contain notification identifiers.") : await Mutation("mark_read", ids, confirm, HttpMethod.Post, "/api/v1/notifications/mark-read", Object(request), cancellationToken).ConfigureAwait(false);
        }
        var id = String(request, "notificationId"); return operation != "get" ? Invalid("helpdesk_notifications", operation, ["list", "get", "summary", "unread_errors", "mark_read"]) : string.IsNullOrWhiteSpace(id) ? Validation("request.notificationId is required.") : await Execute(() => client.GetAsync($"/api/v1/notifications/{Uri.EscapeDataString(id)}", true, cancellationToken), "Notification get completed.").ConfigureAwait(false);
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect or test External orchestration orchestration connectivity. Reads: orchestration, orchestration_jobs, orchestration_tenants, orchestration_request_definitions, orchestration_bindings. orchestration_test is a confirmed mutation because it triggers outbound traffic; it requires confirm:true.")]
    public Task<HelpdeskToolResponse> helpdesk_connectivity(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation == "orchestration_test") return Mutation("orchestration_test", [], confirm, HttpMethod.Post, "/api/v1/admin/orchestration/test", Object(request), cancellationToken);
        var path = operation switch { "orchestration" => "/api/v1/admin/orchestration", "orchestration_jobs" => "/api/v1/admin/orchestration/catalog/jobs", "orchestration_tenants" => "/api/v1/admin/orchestration/catalog/tenants", "orchestration_request_definitions" => "/api/v1/admin/orchestration/catalog/request-definitions", "orchestration_bindings" => "/api/v1/admin/orchestration/bindings", _ => null };
        return path is null ? Task.FromResult(Invalid("helpdesk_connectivity", operation, ["orchestration", "orchestration_test", "orchestration_jobs", "orchestration_tenants", "orchestration_request_definitions", "orchestration_bindings"])) : Execute(() => client.GetAsync(path, true, cancellationToken), $"Connectivity {operation} completed.");
    }

    [McpServerTool(UseStructuredContent = true), Description("Read MCP schema metadata. Operation must be list.")]
    public Task<HelpdeskToolResponse> helpdesk_schema(string operation = "list", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default) => operation != "list" ? Task.FromResult(Invalid("helpdesk_schema", operation, ["list"])) : Task.FromResult(Completed("Schema metadata read.", new JsonObject { ["catalogRevision"] = McpOperationCatalog.Revision, ["catalogHash"] = McpOperationCatalog.CatalogHash, ["operations"] = McpOperationCatalog.ToJson() }));

    [McpServerTool(UseStructuredContent = true), Description("Read Helpdesk enum metadata. Operation is list or a closed enum name: ticket_state, ticket_priority, request_task_status, change_lifecycle_state.")]
    public Task<HelpdeskToolResponse> helpdesk_enums(string operation = "list", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        var type = operation switch { "ticket_state" => typeof(Helpdesk.Shared.Models.TicketState), "ticket_priority" => typeof(Helpdesk.Shared.Models.TicketPriority), "request_task_status" => typeof(Helpdesk.Shared.Models.RequestTaskStatus), "change_lifecycle_state" => typeof(Helpdesk.Shared.Models.ChangeLifecycleState), _ => null };
        if (operation == "list") return Task.FromResult(Completed("Enum registry read.", new JsonObject { ["enums"] = new JsonArray("ticket_state", "ticket_priority", "request_task_status", "change_lifecycle_state") }));
        return type is null ? Task.FromResult(Invalid("helpdesk_enums", operation, ["list", "ticket_state", "ticket_priority", "request_task_status", "change_lifecycle_state"])) : Task.FromResult(Completed($"Enum {operation} read.", new JsonObject { ["name"] = operation, ["values"] = new JsonArray(Enum.GetNames(type).Select(name => (JsonNode?)name).ToArray()) }));
    }

    [McpServerTool(UseStructuredContent = true), Description("Read safe Helpdesk operator examples. Operation must be list.")]
    public Task<HelpdeskToolResponse> helpdesk_examples(string operation = "list", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation != "list") return Task.FromResult(Invalid("helpdesk_examples", operation, ["list"]));
        return Task.FromResult(Completed("Operator examples read.", new JsonObject
        {
            ["examples"] = new JsonArray("triage_incident", "diagnose_request_task", "review_change", "controlled_operator_action"),
            ["normalChangeTemplate"] = NormalChangeTemplateExample(),
            ["incidentListSearch"] = new JsonObject
            {
                ["tool"] = "helpdesk_incidents",
                ["arguments"] = new JsonObject
                {
                    ["operation"] = "list",
                    ["request"] = new JsonObject { ["query"] = "synthetic search text", ["page"] = 1, ["pageSize"] = 10, ["includeTotal"] = true }
                },
                ["aliasRule"] = "query maps to API q. q is also accepted; equivalent trimmed values deduplicate, conflicting nonempty values fail validation, and null or blank values leave search unfiltered.",
                ["noMatchExpectation"] = "A search absent from the authorized records returns empty items and totalCount zero; filtering happens before paging."
            }
        }));
    }

    [McpServerTool(UseStructuredContent = true), Description("Four fixed allowlisted Helpdesk reads are enabled. Arbitrary raw access is disabled; only exact read-only method-and-path allowlist operations exist. It never accepts URLs, paths, hosts, or request bodies.")]
    public async Task<HelpdeskToolResponse> helpdesk_raw(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (HelpdeskToolDefinitions.ValidateSupportedFields("helpdesk_raw", operation, request) is { } fieldError) return Validation(fieldError);
        var id = operation switch { "get_incident" => String(request, "incidentId"), "get_request" => String(request, "requestId"), "get_change" => String(request, "changeId"), _ => null };
        var path = operation switch
        {
            "system_version" => "/api/v1/system/version",
            "get_incident" when !string.IsNullOrWhiteSpace(id) => $"/api/v1/incidents/{Uri.EscapeDataString(id)}",
            "get_request" when !string.IsNullOrWhiteSpace(id) => $"/api/v1/requests/{Uri.EscapeDataString(id)}",
            "get_change" when !string.IsNullOrWhiteSpace(id) => $"/api/v1/changes/{Uri.EscapeDataString(id)}",
            _ => null
        };
        if (path is null) return operation is "get_incident" or "get_request" or "get_change" ? Validation("The matching identifier is required.") : Invalid("helpdesk_raw", operation, ["system_version", "get_incident", "get_request", "get_change"]);
        return await Execute(() => client.GetAsync(path, true, cancellationToken), $"Allowlisted raw read {operation} completed.").ConfigureAwait(false);
    }

    [McpServerTool(UseStructuredContent = true), Description("Confirmed Helpdesk administrative operations. Closed operations: create/update/delete organizations, customers, users, roles, categories, services, request_forms; provision_user; customer_invite, customer_resend_invite, customer_disable_login, customer_sync_authentik; create_self_service_request. Secret-bearing fields are rejected.")]
    public async Task<HelpdeskToolResponse> helpdesk_admin_mutations(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        var payload = Object(request) ?? new JsonObject();
        if (payload.ContainsKey("password") || payload.ContainsKey("appPassword") || payload.ContainsKey("clientSecret")) return Validation("Secret-bearing fields are not accepted through MCP.");
        var map = operation switch
        {
            "create_organization" => (HttpMethod.Post, "/api/v1/organizations", "organizationId"),
            "update_organization" => (HttpMethod.Put, "/api/v1/organizations", "organizationId"),
            "delete_organization" => (HttpMethod.Delete, "/api/v1/organizations", "organizationId"),
            "create_customer" => (HttpMethod.Post, "/api/v1/customers", "customerId"),
            "update_customer" => (HttpMethod.Put, "/api/v1/customers", "customerId"),
            "delete_customer" => (HttpMethod.Delete, "/api/v1/customers", "customerId"),
            "create_user" => (HttpMethod.Post, "/api/v1/users", "userId"),
            "update_user" => (HttpMethod.Put, "/api/v1/users", "userId"),
            "delete_user" => (HttpMethod.Delete, "/api/v1/users", "userId"),
            "provision_user" => (HttpMethod.Post, "/api/v1/users/provision", "userId"),
            "create_role" => (HttpMethod.Post, "/api/v1/admin/role-definitions", "roleId"),
            "update_role" => (HttpMethod.Put, "/api/v1/admin/role-definitions", "roleId"),
            "delete_role" => (HttpMethod.Delete, "/api/v1/admin/role-definitions", "roleId"),
            "create_category" => (HttpMethod.Post, "/api/v1/categories", "categoryId"),
            "update_category" => (HttpMethod.Put, "/api/v1/categories", "categoryId"),
            "delete_category" => (HttpMethod.Delete, "/api/v1/categories", "categoryId"),
            "create_service" => (HttpMethod.Post, "/api/v1/services", "serviceId"),
            "update_service" => (HttpMethod.Put, "/api/v1/services", "serviceId"),
            "delete_service" => (HttpMethod.Delete, "/api/v1/services", "serviceId"),
            "create_request_form" => (HttpMethod.Post, "/api/v1/request-forms", "requestFormId"),
            "create_service_request_form" => (HttpMethod.Post, "/api/v1/services", "serviceId"),
            "update_organization_branding" => (HttpMethod.Put, "/api/v1/tenants", "tenantId"),
            "update_request_form" => (HttpMethod.Put, "/api/v1/request-forms", "requestFormId"),
            "delete_request_form" => (HttpMethod.Delete, "/api/v1/request-forms", "requestFormId"),
            "customer_invite" => (HttpMethod.Post, "/api/v1/customers", "customerId"),
            "customer_resend_invite" => (HttpMethod.Post, "/api/v1/customers", "customerId"),
            "customer_disable_login" => (HttpMethod.Post, "/api/v1/customers", "customerId"),
            "customer_sync_authentik" => (HttpMethod.Post, "/api/v1/customers", "customerId"),
            "create_self_service_request" => (HttpMethod.Post, "/api/v1/self-service/requests", "requestId"),
            _ => default
        };
        if (map.Item1 is null) return Invalid("helpdesk_admin_mutations", operation, ["create_organization", "update_organization", "delete_organization", "create_customer", "update_customer", "delete_customer", "create_user", "update_user", "delete_user", "provision_user", "create_role", "update_role", "delete_role", "create_category", "update_category", "delete_category", "create_service", "update_service", "delete_service", "create_request_form", "create_service_request_form", "update_request_form", "delete_request_form", "customer_invite", "customer_resend_invite", "customer_disable_login", "customer_sync_authentik", "create_self_service_request"]);
        var id = operation == "update_organization_branding" ? TenantId(request) : String(request, map.Item3);
        var isCreate = (operation.StartsWith("create_", StringComparison.Ordinal) || operation == "provision_user") && operation != "create_service_request_form";
        if (!isCreate && string.IsNullOrWhiteSpace(id)) return Validation($"request.{map.Item3} is required.");
        var suffix = operation switch { "customer_invite" => "/auth/invite", "customer_resend_invite" => "/auth/resend-invite", "customer_disable_login" => "/auth/disable-login", "customer_sync_authentik" => "/auth/sync-authentik", _ => "" };
        var path = operation == "create_service_request_form" ? $"/api/v1/services/{Uri.EscapeDataString(id!)}/forms/" : operation == "update_organization_branding" ? $"/api/v1/tenants/{Uri.EscapeDataString(id!)}/branding" : isCreate || operation == "create_self_service_request" ? map.Item2 : $"{map.Item2}/{Uri.EscapeDataString(id!)}{suffix}";
        return await Mutation(operation, string.IsNullOrWhiteSpace(id) ? [] : [id], confirm, map.Item1, path, isCreate ? payload : BodyWithout(payload, map.Item3), cancellationToken).ConfigureAwait(false);
    }

    private async Task<HelpdeskToolResponse> TicketOperation(string tool, string basePath, string idName, string operation, JsonElement? request, bool confirm, CancellationToken ct)
    {
        if (operation == "update" && HelpdeskToolDefinitions.ValidateSupportedFields(tool, operation, request) is { } fieldError) return Validation(fieldError);
        if (operation == "update" && PriorityValidation(Object(request)) is { } priorityError) return Validation(priorityError);
        if (operation is "state" or "bulk_state" or "lifecycle")
        {
            var field = operation == "lifecycle" ? "lifecycleState" : "newState";
            if (Object(request)?[field] is null) return Validation($"request.{field} is required.");
        }
        if (operation is "create" or "update" or "state" or "bulk_state" or "lifecycle" && TicketEnumValidation(Object(request)) is { } enumError) return Validation(enumError);
        if (operation == "list") return await ListTickets(tool, basePath, request, ct).ConfigureAwait(false);
        if (operation is "timeline_count" or "attachments_count" or "listeners_count") return await DomainTicketCount(tool, basePath, idName, operation, request, ct).ConfigureAwait(false);
        if (operation == "create")
        {
            var createRequest = Object(request);
            var validation = TicketCreateValidation(tool, createRequest, "request");
            if (validation is not null) return Validation(validation);
            return await Mutation(operation, [], confirm, HttpMethod.Post, basePath + "/", NormalizeTicketEnums(NormalizeTicketPriority(createRequest)), ct).ConfigureAwait(false);
        }
        if (operation == "bulk_create") return await BulkCreateTickets(operation, tool, basePath, Object(request), confirm, ct).ConfigureAwait(false);
        var id = String(request, idName);
        if (tool == "helpdesk_incidents" && operation == "close") return await CloseIncident(request, confirm, ct).ConfigureAwait(false);
        if (operation == "assign_self") return await AssignTicketsToConfiguredAgent(operation, basePath, Object(request), confirm, ct).ConfigureAwait(false);
        if (operation is "bulk_state" or "assign")
        {
            var ids = Strings(Object(request), "ids");
            var path = operation == "bulk_state" ? basePath + "/bulk/state" : basePath + "/bulk/assign";
            return ids.Count == 0 ? Validation("request.ids must contain at least one identifier.") : await Mutation(operation, ids, confirm, HttpMethod.Post, path, NormalizeTicketEnums(Object(request)), ct).ConfigureAwait(false);
        }
        if (operation is "update" or "delete" or "state" or "add_worklog")
        {
            if (string.IsNullOrWhiteSpace(id)) return Validation($"request.{idName} is required.");
            if (operation == "add_worklog") return await AddTicketWorklog(idName, basePath, request, confirm, ct).ConfigureAwait(false);
            var method = operation == "update" ? HttpMethod.Put : operation == "delete" ? HttpMethod.Delete : HttpMethod.Post;
            var mutationSuffix = operation == "state" ? "/state" : operation == "add_worklog" ? "/worklogs" : "";
            var mutationBody = BodyWithout(Object(request), idName);
            if (operation == "update") mutationBody = NormalizeTicketPriority(mutationBody as JsonObject);
            mutationBody = NormalizeTicketEnums(mutationBody as JsonObject);
            return await Mutation(operation, [id], confirm, method, $"{basePath}/{Uri.EscapeDataString(id)}{mutationSuffix}", mutationBody, ct).ConfigureAwait(false);
        }
        if (tool == "helpdesk_changes" && operation == "lifecycle")
        {
            if (string.IsNullOrWhiteSpace(id)) return Validation($"request.{idName} is required.");
            return await Mutation(operation, [id], confirm, HttpMethod.Post, $"{basePath}/{Uri.EscapeDataString(id)}/lifecycle", NormalizeTicketEnums(BodyWithout(Object(request), idName) as JsonObject), ct).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(id)) return Validation($"request.{idName} is required.");
        var allowed = tool == "helpdesk_incidents" ? new[] { "get", "peek", "timeline" } : tool == "helpdesk_requests" ? new[] { "get", "tasks", "timeline" } : new[] { "get", "timeline", "worklogs" };
        if (!allowed.Contains(operation)) return Invalid(tool, operation, ["list", .. allowed]);
        var suffix = operation switch { "peek" => "/peek", "timeline" => "/timeline", "tasks" => "/tasks", "worklogs" => "/worklogs", _ => "" };
        return await Execute(() => client.GetAsync($"{basePath}/{Uri.EscapeDataString(id)}{suffix}", true, ct), $"{tool} {operation} completed.").ConfigureAwait(false);
    }

    private static string? NormalizeSearchAliases(JsonElement? request, out JsonObject? payload)
    {
        payload = null;
        if (request is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
            return "request must be an object.";
        if (request is { ValueKind: JsonValueKind.Object } element)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (element.EnumerateObject().Any(property => !names.Add(property.Name)))
                return "request must not contain duplicate properties.";
        }
        payload = Object(request);
        foreach (var alias in new[] { "query", "q" })
            if (payload?[alias] is { } value && value.GetValueKind() != JsonValueKind.String)
                return $"request.{alias} must be a string or null.";
        var query = payload?["query"]?.GetValue<string>();
        var q = payload?["q"]?.GetValue<string>();
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var hasQ = !string.IsNullOrWhiteSpace(q);
        if (hasQuery && hasQ && !string.Equals(query!.Trim(), q!.Trim(), StringComparison.Ordinal))
            return "request.query and request.q must specify the same search text when both are nonempty.";
        // Clone before changing aliases; the API retains its own trim/length policy.
        if (payload is not null)
        {
            payload.Remove("query");
            payload.Remove("q");
            if (hasQ || hasQuery) payload["q"] = hasQ ? q : query;
        }
        return null;
    }

    private async Task<HelpdeskToolResponse> ListTickets(string tool, string basePath, JsonElement? request, CancellationToken ct)
    {
        if (TicketListPagination.Validate(request, out var page, out var pageSize) is { } paginationError) return Validation(paginationError);
        if (NormalizeSearchAliases(request, out var payload) is { } error) return Validation(error);
        if (page.HasValue) payload!["page"] = page.Value;
        if (pageSize.HasValue) payload!["pageSize"] = pageSize.Value;
        // Identifier filters are explicit per API contract. Route/body selectors stay
        // excluded by Query everywhere else; do not globally forward *Id fields.
        string[] identifierFilters = tool switch
        {
            "helpdesk_incidents" => ["organizationId", "excludeId", "customerOrgId", "forId", "assigneeId"],
            "helpdesk_requests" or "helpdesk_changes" => ["customerOrgId", "forId", "assigneeId"],
            "helpdesk_request_tasks" => ["assignedToId", "requestId", "serviceId"],
            _ => []
        };
        return await Execute(() => client.GetAsync(Query(basePath + "/", payload, identifierFilters), true, ct), $"{tool} list completed.").ConfigureAwait(false);
    }

    private async Task<HelpdeskToolResponse> BulkCreateTickets(string operation, string tool, string basePath, JsonObject? request, bool confirm, CancellationToken ct)
    {
        var items = request?["items"] as JsonArray;
        if (items is null || items.Count == 0) return Validation("request.items must contain at least one ticket create object.");
        if (items.Count > 25) return Validation("request.items is limited to 25 ticket creates per call.");
        var normalizedItems = new List<JsonObject>(items.Count);
        foreach (var item in items)
        {
            if (item is not JsonObject value) return Validation("Every request.items entry must be an object.");
            var validation = TicketCreateValidation(tool, value, "request.items entry");
            if (validation is not null) return Validation(validation);
            if (TicketEnumValidation(value) is { } enumError) return Validation(enumError);
            normalizedItems.Add(NormalizeTicketEnums(NormalizeTicketPriority(value))!);
        }
        if (!confirm) return Response(false, "confirmation_required", $"This would create {items.Count} tickets.", AffectedIds: [], Confirmation: new HelpdeskConfirmation("confirm", true, operation, []));
        var created = new JsonArray();
        try
        {
            foreach (var item in normalizedItems)
                created.Add(await client.SendAsync(HttpMethod.Post, basePath + "/", item, true, ct).ConfigureAwait(false));
            var ids = created.OfType<JsonObject>().Select(value => value["id"]?.ToString()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();
            return Response(true, "completed", $"Created {created.Count} tickets.", created, AffectedIds: ids);
        }
        catch (AgentClientRemoteException ex) { return Response(false, Status(ex.StatusCode), ex.Message, created, AffectedIds: [], NextActions: ["Read created records and reconcile the partial result before submitting another create."], Failure: new HelpdeskToolFailure(ex.Code, false, ex.StatusCode), CorrelationId: ex.CorrelationId); }
    }

    private async Task<HelpdeskToolResponse> AssignTicketsToConfiguredAgent(string operation, string basePath, JsonObject? request, bool confirm, CancellationToken ct)
    {
        var ids = Strings(request, "ids");
        if (ids.Count == 0) return Validation("request.ids must contain at least one ticket identifier.");
        if (UsesCallerBoundIdentity && request?["agentUserEmail"] is not null)
            return Validation("request.agentUserEmail is not permitted for caller-bound credentials; assign_self uses the authenticated application identity.");
        if (!confirm) return Response(false, "confirmation_required", $"This would assign {ids.Count} tickets to the {(UsesCallerBoundIdentity ? "authenticated caller" : "configured agent user")}.", AffectedIds: ids, Confirmation: new HelpdeskConfirmation("confirm", true, operation, ids));
        try
        {
            var userId = UsesCallerBoundIdentity
                ? (await client.GetAsync("/api/v1/auth/me", true, ct).ConfigureAwait(false) as JsonObject)?["userId"]?.ToString()
                : await ResolveConfiguredAgentUserIdAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(userId))
                return Validation(UsesCallerBoundIdentity
                    ? "The authenticated caller is not linked to an application user."
                    : "Configured agent user could not be resolved.");
            var body = new JsonObject { ["ids"] = JsonSerializer.SerializeToNode(ids), ["assignedToId"] = userId };
            return await Mutation("assign", [.. ids, userId], true, HttpMethod.Post, basePath + "/bulk/assign", body, ct).ConfigureAwait(false);
        }
        catch (AgentClientRemoteException ex) { return Response(false, Status(ex.StatusCode), ex.Message, AffectedIds: ids, NextActions: ["Read ticket assignments before submitting another mutation."], Failure: new HelpdeskToolFailure(ex.Code, false, ex.StatusCode), CorrelationId: ex.CorrelationId); }
    }

    private async Task<string?> ResolveConfiguredAgentUserIdAsync(JsonObject? request, CancellationToken ct)
    {
        var email = request?["agentUserEmail"]?.ToString() ?? client.Configuration.AgentUserEmail;
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var user = await client.GetAsync($"/api/v1/users/by-email/{Uri.EscapeDataString(email)}", true, ct).ConfigureAwait(false) as JsonObject;
        return user?["id"]?.ToString();
    }

    private async Task<HelpdeskToolResponse> DomainTicketCount(string tool, string basePath, string idName, string operation, JsonElement? request, CancellationToken ct)
    {
        var id = String(request, idName);
        if (string.IsNullOrWhiteSpace(id)) return Validation($"request.{idName} is required.");
        var ticketType = basePath[(basePath.LastIndexOf('/') + 1)..];
        var countPath = operation.Replace('_', '/');
        return await Execute(() => client.GetAsync($"/api/v1/{ticketType}/{Uri.EscapeDataString(id)}/{countPath}", true, ct), $"{tool} {operation} completed.").ConfigureAwait(false);
    }

    private async Task<HelpdeskToolResponse> AddTicketWorklog(string idName, string basePath, JsonElement? request, bool confirm, CancellationToken ct)
    {
        if (!TryRequest<TicketWorklogRequest>(request, out var worklog, out var error))
            return Validation($"{error} {WorklogRecovery(idName)}");

        var ticketId = idName switch
        {
            "incidentId" => worklog.IncidentId,
            "requestId" => worklog.RequestId,
            "changeId" => worklog.ChangeId,
            _ => null
        };
        var hasUnexpectedIdentifier = idName != "incidentId" && !string.IsNullOrWhiteSpace(worklog.IncidentId)
            || idName != "requestId" && !string.IsNullOrWhiteSpace(worklog.RequestId)
            || idName != "changeId" && !string.IsNullOrWhiteSpace(worklog.ChangeId);

        if (string.IsNullOrWhiteSpace(ticketId) || hasUnexpectedIdentifier || worklog.Hours is null || worklog.Hours < 0 || string.IsNullOrWhiteSpace(worklog.Notes))
            return Validation(WorklogRecovery(idName));

        return await Mutation("add_worklog", [ticketId], confirm, HttpMethod.Post,
            $"{basePath}/{Uri.EscapeDataString(ticketId)}/worklogs",
            new JsonObject { ["hours"] = worklog.Hours, ["notes"] = worklog.Notes, ["isInternalNote"] = worklog.IsInternalNote ?? true }, ct).ConfigureAwait(false);
    }

    private async Task<HelpdeskToolResponse> CloseIncident(JsonElement? request, bool confirm, CancellationToken ct)
    {
        if (!TryRequest<IncidentCloseRequest>(request, out var close, out var error))
            return Validation($"{error} Use request.incidentId, request.closureNote, and optional request.isInternalNote. Use add_worklog when the note needs hours.");
        if (string.IsNullOrWhiteSpace(close.IncidentId)) return Validation("request.incidentId is required.");
        if (string.IsNullOrWhiteSpace(close.ClosureNote)) return Validation("request.closureNote is required.");

        var affectedIds = new[] { close.IncidentId };
        if (!confirm)
        {
            return Response(false, "confirmation_required", $"This would add a closure note and resolve incident {close.IncidentId}.", AffectedIds: affectedIds,
                Confirmation: new HelpdeskConfirmation("confirm", true, "close", affectedIds));
        }

        var worklog = new JsonObject
        {
            ["hours"] = 0,
            ["notes"] = close.ClosureNote,
            ["isInternalNote"] = close.IsInternalNote
        };
        try
        {
            var worklogResult = await client.SendAsync(HttpMethod.Post, $"/api/v1/incidents/{Uri.EscapeDataString(close.IncidentId)}/worklogs", worklog, true, ct).ConfigureAwait(false);
            var stateResult = await client.SendAsync(HttpMethod.Post, $"/api/v1/incidents/{Uri.EscapeDataString(close.IncidentId)}/state", new JsonObject { ["newState"] = (int)TicketState.Resolved }, true, ct).ConfigureAwait(false);
            var correlationId = (stateResult as JsonObject)?["correlationId"]?.ToString() ?? (worklogResult as JsonObject)?["correlationId"]?.ToString();
            return Response(true, "completed", $"Added a closure note and resolved incident {close.IncidentId}.",
                new JsonObject { ["worklog"] = worklogResult?.DeepClone(), ["state"] = stateResult?.DeepClone() }, AffectedIds: affectedIds, CorrelationId: correlationId);
        }
        catch (AgentClientRemoteException ex)
        {
            return Response(false, Status(ex.StatusCode), ex.Message, AffectedIds: affectedIds,
                NextActions: ["Inspect the incident timeline. The closure note may have been recorded before the state update failed."],
                Failure: new HelpdeskToolFailure(ex.Code, false, ex.StatusCode), CorrelationId: ex.CorrelationId);
        }
    }

    private async Task<HelpdeskToolResponse> TaskOperation(string operation, JsonElement? request, bool confirm, CancellationToken ct)
    {
        const string basePath = "/api/v1/request-tasks";
        if (operation == "list") return await ListTickets("helpdesk_request_tasks", basePath, request, ct).ConfigureAwait(false);
        if (operation == "get") return await CollectionRead("helpdesk_request_tasks", basePath, "taskId", operation, request, ct).ConfigureAwait(false);
        if (operation == "create")
        {
            var payload = Object(request);
            var validation = RequestTaskCreateValidation(payload);
            return validation is null
                ? await Mutation(operation, [], confirm, HttpMethod.Post, basePath + "/", NormalizeTicketPriority(payload), ct).ConfigureAwait(false)
                : Validation(validation);
        }
        if (operation is "bulk_start" or "bulk_complete" or "bulk_retry")
        {
            var ids = Strings(Object(request), "ids"); var action = operation[5..].Replace('_', '-');
            return ids.Count == 0 ? Validation("request.ids must contain at least one task id.") : await Mutation(operation, ids, confirm, HttpMethod.Post, $"{basePath}/bulk/{action}", Object(request), ct).ConfigureAwait(false);
        }
        var updatePayload = Object(request);
        var id = String(request, "taskId");
        if (operation is not ("update" or "delete" or "start" or "complete" or "fail" or "retry")) return Invalid("helpdesk_request_tasks", operation, ["list", "get", "create", "update", "delete", "start", "complete", "fail", "retry", "bulk_start", "bulk_complete", "bulk_retry"]);
        if (string.IsNullOrWhiteSpace(id)) return Validation("request.taskId is required.");
        if (operation == "update" && RequestTaskUpdateValidation(updatePayload) is { } updateValidation) return Validation(updateValidation);
        var method = operation == "update" ? HttpMethod.Patch : operation == "delete" ? HttpMethod.Delete : HttpMethod.Post;
        var suffix = operation is "update" or "delete" ? "" : "/" + operation;
        var body = BodyWithout(updatePayload, "taskId") as JsonObject;
        if (operation == "update") body = NormalizeTicketPriority(body);
        return await Mutation(operation, [id], confirm, method, $"{basePath}/{Uri.EscapeDataString(id)}{suffix}", body, ct).ConfigureAwait(false);
    }
    private async Task<HelpdeskToolResponse> CollectionRead(string tool, string basePath, string idName, string operation, JsonElement? request, CancellationToken ct)
    {
        if (operation == "list") return await Execute(() => client.GetAsync(Query(basePath + "/", Object(request)), true, ct), $"{tool} list completed.").ConfigureAwait(false);
        var id = String(request, idName); return operation != "get" ? Invalid(tool, operation, ["list", "get"]) : string.IsNullOrWhiteSpace(id) ? Validation($"request.{idName} is required.") : await Execute(() => client.GetAsync($"{basePath}/{Uri.EscapeDataString(id)}", true, ct), $"{tool} get completed.").ConfigureAwait(false);
    }
    private async Task<HelpdeskToolResponse> LookupRead(string tool, string basePath, string idName, string operation, JsonElement? request, CancellationToken ct)
    {
        if (operation == "list") return await Execute(() => client.GetAsync(Query(basePath + "/", Object(request)), true, ct), $"{tool} list completed.").ConfigureAwait(false);
        var id = String(request, idName); if (string.IsNullOrWhiteSpace(id)) return Validation($"request.{idName} is required.");
        var allowed = tool switch { "helpdesk_organizations" => new[] { "get", "tenants", "tenant_lookup" }, "helpdesk_customers" => new[] { "get", "auth_status" }, _ => new[] { "get" } };
        if (!allowed.Contains(operation)) return Invalid(tool, operation, ["list", .. allowed]);
        var suffix = operation switch { "tenants" => "/tenants", "tenant_lookup" => "/tenant-lookup", "auth_status" => "/auth/status", _ => "" };
        return await Execute(() => client.GetAsync($"{basePath}/{Uri.EscapeDataString(id)}{suffix}", true, ct), $"{tool} {operation} completed.").ConfigureAwait(false);
    }
    private Task<HelpdeskToolResponse> Read(string tool, string operation, IReadOnlyList<string> allowed, string path, JsonElement? request, CancellationToken ct) => !allowed.Contains(operation) ? Task.FromResult(Invalid(tool, operation, allowed)) : Execute(() => client.GetAsync(path, true, ct), $"{tool} {operation} completed.");
    private async Task<HelpdeskToolResponse> Mutation(string operation, IReadOnlyList<string> affectedIds, bool confirm, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        if (!confirm) return Response(false, "confirmation_required", $"This would perform {operation}.", AffectedIds: affectedIds, Confirmation: new HelpdeskConfirmation("confirm", true, operation, affectedIds));
        try
        {
            var data = await client.SendAsync(method, path, body, true, ct).ConfigureAwait(false);
            var correlationId = (data as JsonObject)?["correlationId"]?.ToString();
            if (operation.StartsWith("bulk_", StringComparison.Ordinal) && data is JsonObject outcome
                && outcome["updated"] is JsonValue count && count.TryGetValue<int>(out var updated) && updated == 0)
                return Response(true, "no_op", $"{operation} changed no records.", data, AffectedIds: [],
                    NextActions: ["Check the selected records and operation prerequisites; zero updated records does not establish a successful mutation."], CorrelationId: correlationId);
            return Response(true, "completed", $"{operation} completed.", data, AffectedIds: affectedIds, CorrelationId: correlationId);
        }
        catch (AgentClientRemoteException ex)
        {
            return Response(false, Status(ex.StatusCode), ex.Message, AffectedIds: affectedIds,
                NextActions: ex.StatusCode >= 500 ? ["The write outcome may be uncertain. Read the affected record and its timeline using the correlation ID before considering another mutation."] : null,
                Failure: new HelpdeskToolFailure(ex.Code, false, ex.StatusCode), CorrelationId: ex.CorrelationId);
        }
    }
    private async Task<HelpdeskToolResponse> Execute(Func<Task<JsonNode?>> action, string summary) { try { return Completed(summary, await action().ConfigureAwait(false)); } catch (AgentClientRemoteException ex) { return Response(false, Status(ex.StatusCode), ex.Message, Failure: new HelpdeskToolFailure(ex.Code, ex.StatusCode >= 500, ex.StatusCode), CorrelationId: ex.CorrelationId); } catch (AgentClientValidationException ex) { return Validation(ex.Message); } }
    private HelpdeskToolResponse Completed(string summary, JsonNode? data)
        => Response(true, "completed", summary, data, AffectedIds: [], CorrelationId: (data as JsonObject)?["correlationId"]?.GetValue<string>());
    private HelpdeskToolResponse Invalid(string tool, string operation, IReadOnlyList<string> allowed) => Response(false, "validation_failed", $"Unsupported {tool} operation '{operation}'.", Failure: new HelpdeskToolFailure("unsupported_operation", false, AllowedOperations: allowed));
    private HelpdeskToolResponse Validation(string summary) => Response(false, "validation_failed", summary, Failure: new HelpdeskToolFailure("validation_error", false));
    private static string? TicketCreateValidation(string tool, JsonObject? request, string subject)
    {
        if (string.IsNullOrWhiteSpace(request?["title"]?.ToString()) || string.IsNullOrWhiteSpace(request?["description"]?.ToString()))
            return $"{subject}.title and {subject}.description are required.";
        if (tool == "helpdesk_incidents" && string.IsNullOrWhiteSpace(request?["customerId"]?.ToString()))
            return $"{subject}.customerId is required to create an incident. Use helpdesk_customers list to select an enabled customer.";
        if (tool == "helpdesk_incidents" && string.IsNullOrWhiteSpace(request?["organizationId"]?.ToString()))
            return $"{subject}.organizationId is required to create an incident and must match the selected customer.";
        if (tool == "helpdesk_requests" && string.IsNullOrWhiteSpace(request?["customerId"]?.ToString()))
            return $"{subject}.customerId is required to create a request. Use helpdesk_customers list to select an enabled customer.";
        if (tool == "helpdesk_requests" && string.IsNullOrWhiteSpace(request?["organizationId"]?.ToString()))
            return $"{subject}.organizationId is required to create a request and must match the selected customer.";
        if (tool == "helpdesk_changes" && string.IsNullOrWhiteSpace(request?["organizationId"]?.ToString()))
            return $"{subject}.organizationId is required to create a change.";
        if (tool == "helpdesk_changes" && string.IsNullOrWhiteSpace(request?["requestedForUserId"]?.ToString()))
            return $"{subject}.requestedForUserId is required to create a change.";
        if (tool == "helpdesk_changes" && string.IsNullOrWhiteSpace(request?["implementorUserId"]?.ToString()))
            return $"{subject}.implementorUserId is required to create a change.";
        if (tool == "helpdesk_changes" && string.IsNullOrWhiteSpace(request?["implementationStartAt"]?.ToString()) || tool == "helpdesk_changes" && string.IsNullOrWhiteSpace(request?["implementationEndAt"]?.ToString()))
            return $"{subject}.implementationStartAt and {subject}.implementationEndAt are required to create a change.";
        if (tool == "helpdesk_changes" && string.IsNullOrWhiteSpace(request?["changeType"]?.ToString()))
            return $"{subject}.changeType is required to create a change.";
        if (tool == "helpdesk_changes")
        {
            if (request?["changeTemplate"] is not JsonObject changeTemplate)
                return $"{subject}.changeTemplate must be an object to create a change.";
            if (ChangeTemplateValidation(request?["changeType"]?.ToString(), changeTemplate) is { } templateError)
                return $"{subject}.changeTemplate {templateError}";
        }
        return PriorityValidation(request);
    }

    private static string? ChangeTemplateValidation(string? changeType, JsonObject template)
    {
        var errors = new List<string>();
        var normalizedType = changeType?.Trim().ToLowerInvariant();
        if (normalizedType is not ("standard" or "normal" or "emergency"))
            errors.Add("changeType must be Standard, Normal, or Emergency.");
        if (!HasText(template["scopeOfChange"])) errors.Add("scopeOfChange is required.");
        RequireNonEmptyStrings(template, "affectedSystems", errors);
        RequireNonEmptyStrings(template, "implementationSteps", errors);
        RequireNonEmptyStrings(template, "validationSteps", errors);
        if (!HasText(template["rollbackPlan"]) && !HasText(template["rollbackReference"]))
            errors.Add("rollbackPlan or rollbackReference is required.");

        switch (normalizedType)
        {
            case "standard":
                if (template["isPreApproved"]?.ToJsonString() != "true") errors.Add("isPreApproved must be true for Standard changes.");
                if (!HasText(template["existingRunbookReference"])) errors.Add("existingRunbookReference is required for Standard changes.");
                break;
            case "normal":
                RequireText(template, "businessJustification", "Normal", errors);
                RequireText(template, "impactAssessment", "Normal", errors);
                RequireText(template, "riskAssessment", "Normal", errors);
                RequireNonEmptyStrings(template, "preChangeChecks", errors);
                RequireText(template, "testingPlan", "Normal", errors);
                break;
            case "emergency":
                RequireText(template, "emergencyReason", "Emergency", errors);
                RequireText(template, "businessImpactIfNotImplemented", "Emergency", errors);
                RequireText(template, "immediateRiskAssessment", "Emergency", errors);
                RequireText(template, "postChangeValidation", "Emergency", errors);
                break;
        }

        return errors.Count == 0 ? null : string.Join(' ', errors);
    }

    private static void RequireText(JsonObject template, string property, string changeType, ICollection<string> errors)
    {
        if (!HasText(template[property])) errors.Add($"{property} is required for {changeType} changes.");
    }

    private static void RequireNonEmptyStrings(JsonObject template, string property, ICollection<string> errors)
    {
        if (template[property] is not JsonArray values || values.Count == 0 || values.Any(value => !HasText(value)))
            errors.Add($"{property} must contain at least one non-empty value.");
    }

    private static bool HasText(JsonNode? value) => value is JsonValue && value.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.ToString());
    private static string? RequestTaskCreateValidation(JsonObject? request)
    {
        if (string.IsNullOrWhiteSpace(request?["title"]?.ToString()) || string.IsNullOrWhiteSpace(request?["description"]?.ToString()))
            return "request.title and request.description are required to create a request task.";
        if (string.IsNullOrWhiteSpace(request?["requestId"]?.ToString())) return "request.requestId is required to create a request task.";
        if (string.IsNullOrWhiteSpace(request?["organizationId"]?.ToString())) return "request.organizationId is required to create a request task.";
        return PriorityValidation(request);
    }

    private static string? RequestTaskUpdateValidation(JsonObject? request)
    {
        if (request is null) return "request must be an object.";
        var unsupported = request.Select(pair => pair.Key).FirstOrDefault(key => !RequestTaskUpdateFields.Contains(key));
        if (unsupported is not null) return $"request.{unsupported} is not supported for request-task update.";
        if (request["title"] is not null && !HasText(request["title"])) return "request.title cannot be empty.";
        if (request["description"] is not null && !HasText(request["description"])) return "request.description cannot be empty.";
        if (PriorityValidation(request) is { } priorityError) return priorityError;
        if (request["assignedToId"] is not null && !HasText(request["assignedToId"])) return "request.assignedToId cannot be empty; use clearAssignment to remove an assignment.";
        if (request["clearAssignment"] is not null && request["clearAssignment"]?.ToJsonString() is not ("true" or "false")) return "request.clearAssignment must be a boolean.";
        if (request["clearDueDate"] is not null && request["clearDueDate"]?.ToJsonString() is not ("true" or "false")) return "request.clearDueDate must be a boolean.";
        if (request["clearAssignment"]?.ToJsonString() == "true" && request["assignedToId"] is not null) return "request.clearAssignment cannot be combined with request.assignedToId.";
        if (request["clearDueDate"]?.ToJsonString() == "true" && request["dueDate"] is not null) return "request.clearDueDate cannot be combined with request.dueDate.";
        if (request["dueDate"] is not null && (!HasText(request["dueDate"]) || !DateTime.TryParse(request["dueDate"]!.ToString(), out _))) return "request.dueDate must be an ISO-8601 date-time.";
        if (request["linkedAssetIds"] is not null && !IsStringArray(request["linkedAssetIds"])) return "request.linkedAssetIds must be an array of non-empty strings.";
        if (request["attachments"] is not null && !IsStringArray(request["attachments"])) return "request.attachments must be an array of non-empty strings.";
        return request.Any(pair => pair.Key != "taskId" && (pair.Key is not ("clearAssignment" or "clearDueDate") || pair.Value?.ToJsonString() == "true"))
            ? null
            : "Provide at least one supported request-task update field.";
    }

    private static bool IsStringArray(JsonNode? value) => value is JsonArray array && array.All(HasText);
    private static JsonObject NormalChangeTemplateExample() => new()
    {
        ["scopeOfChange"] = "Describe the bounded change scope.",
        ["affectedSystems"] = new JsonArray("Dev service"),
        ["implementationSteps"] = new JsonArray("Apply the reviewed change."),
        ["validationSteps"] = new JsonArray("Verify the expected Dev behavior."),
        ["rollbackPlan"] = "Revert the reviewed change.",
        ["businessJustification"] = "Required Dev validation.",
        ["impactAssessment"] = "Bounded Dev impact.",
        ["riskAssessment"] = "Low; reversible Dev change.",
        ["preChangeChecks"] = new JsonArray("Confirm Dev target."),
        ["testingPlan"] = "Run the bounded verification."
    };
    private HelpdeskToolResponse Response(bool Success, string Status, string Summary, JsonNode? Data = null, JsonNode? Evidence = null, IReadOnlyList<string>? AffectedIds = null, IReadOnlyList<string>? NextActions = null, string? CorrelationId = null, HelpdeskToolFailure? Failure = null, HelpdeskConfirmation? Confirmation = null)
        => new(Success, Status, Summary, Data, Evidence, AffectedIds, NextActions, CorrelationId, Failure, Confirmation)
        {
            Target = hostContext.ToTargetInfo(),
            HostContext = hostContext
        };
    private static string Status(int code) => code switch { 401 => "unauthorized", 403 => "forbidden", 404 => "not_found", 409 => "conflict", _ => "failed" };
    private static string WorklogRecovery(string idName) => $"Use request.{idName}, request.hours, request.notes, and optional request.isInternalNote. Hours must be non-negative; use 0 for a diagnostic note.";
    private static JsonObject? Object(JsonElement? element) => element is { ValueKind: JsonValueKind.Object } value ? JsonNode.Parse(value.GetRawText())?.AsObject() : null;
    private static bool TryRequest<T>(JsonElement? request, out T value, out string error) where T : class
    {
        value = default!;
        error = "request must be an object.";
        if (request is not { ValueKind: JsonValueKind.Object } element) return false;
        try
        {
            value = JsonSerializer.Deserialize<T>(element.GetRawText(), RequestJsonOptions)!;
            if (value is null)
            {
                error = "request must contain a valid object.";
                return false;
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = $"request is invalid: {ex.Message}";
            return false;
        }
    }
    private static JsonNode? BodyWithout(JsonObject? value, params string[] names) { if (value is null) return null; var copy = value.DeepClone().AsObject(); foreach (var name in names) copy.Remove(name); return copy; }
    private static string? TicketEnumValidation(JsonObject? request)
    {
        if (request is null) return null;
        foreach (var field in new[] { "state", "newState", "lifecycleState" })
        {
            if (request[field] is not { } value) continue;
            var type = field == "lifecycleState" ? typeof(ChangeLifecycleState) : typeof(TicketState);
            if (!TryDefinedEnum(type, value, out _)) return $"request.{field} must be a defined {type.Name} value.";
        }
        return null;
    }
    private static bool TryDefinedEnum(Type type, JsonNode value, out object? parsed)
    {
        parsed = null;
        return value.GetValueKind() is JsonValueKind.String or JsonValueKind.Number
            && Enum.TryParse(type, value.ToString(), true, out parsed) && parsed is not null && Enum.IsDefined(type, parsed);
    }
    private static JsonObject? NormalizeTicketEnums(JsonObject? request)
    {
        if (request is null) return null;
        var normalized = request.DeepClone().AsObject();
        foreach (var field in new[] { "state", "newState", "lifecycleState" })
        {
            if (normalized[field] is not { } value) continue;
            var type = field == "lifecycleState" ? typeof(ChangeLifecycleState) : typeof(TicketState);
            if (TryDefinedEnum(type, value, out var parsed)) normalized[field] = Convert.ToInt32(parsed);
        }
        return normalized;
    }
    private static string? PriorityValidation(JsonObject? request)
    {
        if (request is null || !request.ContainsKey("priority")) return null;
        return TryPriority(request["priority"], out _) ? null : "request.priority must be Low, Medium, High, or Critical.";
    }
    private static bool TryPriority(JsonNode? value, out TicketPriority priority)
    {
        priority = default;
        return value?.GetValueKind() == JsonValueKind.String
            && Enum.GetNames<TicketPriority>().Any(name => string.Equals(name, value.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
            && Enum.TryParse(value.GetValue<string>(), true, out priority)
            && Enum.IsDefined(priority);
    }
    private static JsonObject? NormalizeTicketPriority(JsonObject? request)
    {
        if (request is null) return null;
        var normalized = request.DeepClone().AsObject();
        if (TryPriority(normalized["priority"], out var priority))
            normalized["priority"] = (int)priority;
        return normalized;
    }
    private static IReadOnlyList<string> Strings(JsonObject? value, string property) => value?[property] is JsonArray array ? array.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray() : [];
    private static string? String(JsonElement? element, string property) => element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    private static string? TenantId(JsonElement? request)
        => request is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("tenantId", out var id)
            && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var numeric) && numeric > 0
            ? numeric.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    private static bool IsHttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
    private static string Query(string path, JsonObject? values, params string[] identifierFilters)
    {
        if (values is null) return path;
        var parameters = values
            .Where(pair => pair.Value is not null && pair.Key is not "id" && (!pair.Key.EndsWith("Id", StringComparison.Ordinal) || identifierFilters.Contains(pair.Key, StringComparer.Ordinal)))
            .SelectMany(pair => identifierFilters.Length > 0 && pair.Value is JsonArray array
                ? array.Where(value => value is not null).Select(value => KeyValuePair.Create(pair.Key, value!.ToString()))
                : [KeyValuePair.Create(pair.Key, pair.Value!.ToString())])
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value));
        var query = string.Join("&", parameters);
        return query.Length == 0 ? path : path + "?" + query;
    }
    private string AuthStatusPath => UsesCallerBoundIdentity
        ? "/api/v1/auth/me"
        : "/api/v1/auth/ai-agent/status";
    private bool UsesCallerBoundIdentity => string.Equals(client.Configuration?.CredentialMode, "integration", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(client.Configuration?.CredentialMode, "gateway", StringComparison.OrdinalIgnoreCase);

    private JsonNode Capabilities() => new JsonObject { ["server"] = "Helpdesk.Mcp", ["instance"] = hostContext.Instance, ["catalogRevision"] = hostContext.CatalogRevision, ["catalogHash"] = McpOperationCatalog.CatalogHash, ["transport"] = hostContext.Transport, ["resourceUri"] = hostContext.ResourceUri, ["apiBaseUrl"] = hostContext.CanonicalApiBaseUrl, ["credentialMode"] = client.Configuration?.CredentialMode ?? "authentik", ["authStatusPath"] = AuthStatusPath, ["phase"] = 3, ["mutationsEnabled"] = true, ["rawEnabled"] = false, ["arbitraryRawAccessEnabled"] = false, ["allowlistedRawReadsEnabled"] = true, ["allowlistedRawOperations"] = new JsonArray("system_version", "get_incident", "get_request", "get_change"), ["autonomousWriteOperations"] = new JsonArray("helpdesk_ai_assistant.report"), ["mutationConfirmationRequired"] = true, ["configurationWritesEnabled"] = configurationSurface.CanPersist, ["obsoleteMutationProofToolExposed"] = false };
}
