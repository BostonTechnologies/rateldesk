using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Tools;
using ModelContextProtocol.Server;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed class McpOperationCatalogTests
{
    [Fact]
    public void Every_tool_schema_has_unique_required_properties()
    {
        foreach (var tool in HelpdeskToolDefinitions.Create())
            AssertUniqueRequired(tool.ProtocolTool.InputSchema, tool.ProtocolTool.Name);
    }

    private static void AssertUniqueRequired(JsonElement schema, string path)
    {
        if (schema.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in schema.EnumerateObject())
            {
                if (property.Name == "required")
                {
                    var names = property.Value.EnumerateArray().Select(value => value.GetString()).ToArray();
                    Assert.True(names.Length == names.Distinct(StringComparer.Ordinal).Count(), $"Duplicate required fields at {path}");
                }
                AssertUniqueRequired(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (schema.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schema.EnumerateArray())
                AssertUniqueRequired(item, path);
        }
    }

    [Theory]
    [InlineData("timeline_count")]
    [InlineData("attachments_count")]
    [InlineData("listeners_count")]
    public void Ticket_count_schema_requires_each_identifier_once(string operation)
    {
        var tickets = Assert.Single(HelpdeskToolDefinitions.Create(), tool => tool.ProtocolTool.Name == "helpdesk_tickets");
        var request = Alternative(tickets, operation).GetProperty("properties").GetProperty("request");

        Assert.Equal(["ticketType", "ticketId"], request.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["incidents", "requests", "changes"], request.GetProperty("properties").GetProperty("ticketType").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void Catalog_rows_have_a_contract_method_and_path()
    {
        Assert.NotEmpty(McpOperationCatalog.All);
        Assert.All(McpOperationCatalog.All, operation =>
        {
            Assert.False(string.IsNullOrWhiteSpace(operation.RequestContract));
            Assert.False(string.IsNullOrWhiteSpace(operation.HttpMethod));
            Assert.False(string.IsNullOrWhiteSpace(operation.PathTemplate));
        });
    }

    [Fact]
    public void Every_discoverable_tool_has_at_least_one_catalog_operation()
    {
        var toolNames = typeof(HelpdeskTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        var catalogToolNames = McpOperationCatalog.All.Select(operation => operation.Tool).ToHashSet(StringComparer.Ordinal);

        Assert.True(toolNames.IsSubsetOf(catalogToolNames), $"Missing catalog tool(s): {string.Join(", ", toolNames.Except(catalogToolNames))}");
    }

    [Fact]
    public void Catalog_covers_the_reported_regressions()
    {
        Assert.Contains(McpOperationCatalog.All, item => item.Tool == "helpdesk_request_forms" && item.Operation == "by_service" && item.PathTemplate.EndsWith("/forms/", StringComparison.Ordinal));
        Assert.Contains(McpOperationCatalog.All, item => item.Tool == "helpdesk_tickets" && item.Operation == "timeline_count" && item.PathTemplate == "/api/v1/{ticketType}/{ticketId}/timeline/count");
        Assert.Contains(McpOperationCatalog.All, item => item.Tool == "helpdesk_incidents" && item.Operation == "add_worklog" && item.RequiresConfirmation);
        Assert.Contains(McpOperationCatalog.All, item => item.Tool == "helpdesk_incidents" && item.Operation == "close" && item.RequiresConfirmation);
    }

    [Theory]
    [InlineData("mark_as_seen", "/api/v1/tickets/{ticketId}/mark-as-seen")]
    public void Ticket_helper_mutations_have_the_executable_paths(string operation, string path)
    {
        Assert.Contains(McpOperationCatalog.All, item => item.Tool == "helpdesk_tickets" && item.Operation == operation && item.PathTemplate == path && item.RequiresConfirmation);
    }

    [Fact]
    public void Category_catalog_exposes_only_the_supported_list_route()
    {
        var categoryOperations = McpOperationCatalog.OperationsFor("helpdesk_categories");

        Assert.Equal(["list"], categoryOperations);
    }

    [Theory]
    [InlineData("system_version", "/api/v1/system/version")]
    [InlineData("get_incident", "/api/v1/incidents/{incidentId}")]
    [InlineData("get_request", "/api/v1/requests/{requestId}")]
    [InlineData("get_change", "/api/v1/changes/{changeId}")]
    public void Raw_reads_have_exact_allowlisted_paths(string operation, string path)
    {
        Assert.Contains(McpOperationCatalog.All, item => item.Tool == "helpdesk_raw" && item.Operation == operation && item.PathTemplate == path);
    }

    [Fact]
    public void Obsolete_mutation_proof_tool_is_not_discoverable_from_the_tool_type()
    {
        var tools = typeof(HelpdeskTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain("helpdesk_mutations", tools);
        Assert.Contains("helpdesk_incidents", tools);
    }

    [Fact]
    public void Discovery_definitions_expose_titles_annotations_and_a_discriminated_incident_create_schema()
    {
        var tools = HelpdeskToolDefinitions.Create();

        Assert.All(tools, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Title));
            Assert.NotNull(tool.ProtocolTool.Annotations);
        });

        var incident = Assert.Single(tools, tool => tool.ProtocolTool.Name == "helpdesk_incidents");
        var create = Assert.Single(incident.ProtocolTool.InputSchema.GetProperty("oneOf").EnumerateArray(), item =>
            item.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString() == "create");
        var properties = create.GetProperty("properties");
        var request = properties.GetProperty("request");

        Assert.Contains("outer envelope", incident.ProtocolTool.InputSchema.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("inside request", incident.ProtocolTool.Description, StringComparison.Ordinal);
        Assert.Contains("outer operation field", properties.GetProperty("operation").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("inside this request object", request.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal("boolean", properties.GetProperty("confirm").GetProperty("type").GetString());
        Assert.DoesNotContain("confirm", create.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("request", create.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.True(request.GetProperty("additionalProperties").GetBoolean() == false);
        Assert.True(request.GetProperty("properties").TryGetProperty("title", out _));
        Assert.False(request.GetProperty("properties").TryGetProperty("subject", out _));
        Assert.Contains("Low", request.GetProperty("properties").GetProperty("priority").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        var required = request.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("customerId", required);
        Assert.Contains("organizationId", required);
    }

    [Fact]
    public void Discovery_definitions_close_bulk_ticket_and_request_task_create_contracts()
    {
        var tools = HelpdeskToolDefinitions.Create();
        var incidents = Assert.Single(tools, tool => tool.ProtocolTool.Name == "helpdesk_incidents");
        var bulkCreate = Alternative(incidents, "bulk_create").GetProperty("properties").GetProperty("request");
        var items = bulkCreate.GetProperty("properties").GetProperty("items");
        var item = items.GetProperty("items");

        Assert.False(bulkCreate.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(25, items.GetProperty("maxItems").GetInt32());
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("customerId", item.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("organizationId", item.GetProperty("required").EnumerateArray().Select(value => value.GetString()));

        var tasks = Assert.Single(tools, tool => tool.ProtocolTool.Name == "helpdesk_request_tasks");
        var taskCreate = Alternative(tasks, "create").GetProperty("properties").GetProperty("request");
        var taskRequired = taskCreate.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray();

        Assert.False(taskCreate.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("title", taskRequired);
        Assert.Contains("description", taskRequired);
        Assert.Contains("requestId", taskRequired);
        Assert.Contains("organizationId", taskRequired);

        var taskUpdate = Alternative(tasks, "update").GetProperty("properties").GetProperty("request");
        Assert.False(taskUpdate.GetProperty("additionalProperties").GetBoolean());
        Assert.True(taskUpdate.GetProperty("properties").TryGetProperty("clearDueDate", out _));
        Assert.Equal(9, taskUpdate.GetProperty("anyOf").GetArrayLength());
    }

    [Fact]
    public async Task Change_creation_schema_and_example_expose_the_normal_template_contract()
    {
        var changes = Assert.Single(HelpdeskToolDefinitions.Create(), tool => tool.ProtocolTool.Name == "helpdesk_changes");
        var template = Alternative(changes, "create").GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("changeTemplate");
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, Store());
        var examples = await tools.helpdesk_examples();
        var normal = (JsonObject)((JsonObject)examples.Data!)["normalChangeTemplate"]!;

        Assert.False(template.GetProperty("additionalProperties").GetBoolean());
        Assert.True(template.GetProperty("properties").TryGetProperty("scopeOfChange", out _));
        Assert.True(template.GetProperty("properties").TryGetProperty("businessJustification", out _));
        Assert.True(template.GetProperty("properties").TryGetProperty("postChangeValidation", out _));
        Assert.Contains("request.title, request.description", changes.ProtocolTool.Description, StringComparison.Ordinal);
        Assert.Contains("request.requestedForUserId", changes.ProtocolTool.Description, StringComparison.Ordinal);
        Assert.Contains("never use requesterId, implementorId, or normalTemplate", changes.ProtocolTool.Description, StringComparison.Ordinal);
        Assert.Contains("affectedSystems, implementationSteps, validationSteps, and preChangeChecks are non-empty string arrays", changes.ProtocolTool.Description, StringComparison.Ordinal);
        Assert.Contains("request.changeId, request.hours, request.notes", changes.ProtocolTool.Description, StringComparison.Ordinal);
        Assert.Equal("Describe the bounded change scope.", normal["scopeOfChange"]?.ToString());
        Assert.NotNull(normal["rollbackPlan"]);
    }

    [Fact]
    public async Task Capabilities_and_schema_report_the_catalog_revision()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), $"helpdesk-mcp-{Guid.NewGuid():N}.json")));

        var capabilities = await tools.helpdesk_capabilities();
        var schema = await tools.helpdesk_schema();

        Assert.Equal(McpOperationCatalog.Revision, ((JsonObject)capabilities.Data!)["catalogRevision"]?.ToString());
        Assert.Equal(McpOperationCatalog.Revision, ((JsonObject)schema.Data!)["catalogRevision"]?.ToString());
        Assert.NotNull(((JsonObject)schema.Data!)["operations"]);
    }

    [Fact]
    public async Task Every_catalog_operation_has_a_deterministic_tool_contract_probe()
    {
        foreach (var operation in McpOperationCatalog.All)
        {
            var client = Substitute.For<IHelpdeskAgentClient>();
            client.Configuration.Returns(new AgentClientConfiguration(null, null, null, null, null, null, "operator@example.test"));
            client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject { ["id"] = "record-1" });
            client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject { ["id"] = "record-1", ["correlationId"] = "corr-1" });
            client.GetHealthAsync(Arg.Any<CancellationToken>()).Returns(new JsonArray());
            var store = Store();
            if (operation.Tool == "helpdesk_config")
                store.Save(AgentClientConfiguration.Empty);
            var tools = new HelpdeskTools(client, store);
            var request = ContractRequest(operation);

            if (operation.RequiresConfirmation)
            {
                var preview = await Invoke(tools, operation, request, confirm: false);
                Assert.Equal("confirmation_required", preview.Status);
                Assert.DoesNotContain(client.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IHelpdeskAgentClient.SendAsync));
            }

            var response = await Invoke(tools, operation, request, confirm: operation.RequiresConfirmation);
            Assert.True(response.Success, $"{operation.Tool}.{operation.Operation}: {response.Summary}");

            if (operation.HttpMethod == "LOCAL" || operation.Tool == "helpdesk_health" || operation.Tool == "helpdesk_config") continue;
            var expectedPath = operation.PathTemplate.Split(" then ", StringSplitOptions.None)[0].Replace("{incidentId}", "incident-1", StringComparison.Ordinal).Replace("{requestId}", "request-1", StringComparison.Ordinal).Replace("{changeId}", "change-1", StringComparison.Ordinal).Replace("{taskId}", "task-1", StringComparison.Ordinal).Replace("{organizationId}", "organization-1", StringComparison.Ordinal).Replace("{tenantId}", "1", StringComparison.Ordinal).Replace("{customerId}", "customer-1", StringComparison.Ordinal).Replace("{userId}", "user-1", StringComparison.Ordinal).Replace("{roleId}", "role-1", StringComparison.Ordinal).Replace("{categoryId}", "category-1", StringComparison.Ordinal).Replace("{serviceId}", "service-1", StringComparison.Ordinal).Replace("{requestFormId}", "form-1", StringComparison.Ordinal).Replace("{notificationId}", "notification-1", StringComparison.Ordinal).Replace("{ticketType}", "incidents", StringComparison.Ordinal).Replace("{ticketId}", "incident-1", StringComparison.Ordinal).Replace("{invocationId}", "00000000-0000-0000-0000-000000000001", StringComparison.Ordinal).Replace("{email}", "operator%40example.test", StringComparison.Ordinal);
            var calls = client.ReceivedCalls().Select(call => call.GetArguments()).ToArray();
            Assert.Contains(calls, arguments => arguments.OfType<string>().Any(path => path.StartsWith(expectedPath, StringComparison.Ordinal)));
            if (operation.HttpMethod is not ("GET" or "LOCAL"))
                Assert.Contains(calls, arguments => arguments.OfType<HttpMethod>().Any(method => method.Method == operation.HttpMethod));
        }
    }

    private static async Task<HelpdeskToolResponse> Invoke(HelpdeskTools tools, McpOperationDefinition operation, JsonElement? request, bool confirm) => operation.Tool switch
    {
        "helpdesk_auth" => await tools.helpdesk_auth(operation.Operation, request, confirm),
        "helpdesk_health" => await tools.helpdesk_health(operation.Operation, request, confirm),
        "helpdesk_config" => await tools.helpdesk_config(operation.Operation, request, confirm),
        "helpdesk_capabilities" => await tools.helpdesk_capabilities(operation.Operation, request, confirm),
        "helpdesk_system" => await tools.helpdesk_system(operation.Operation, request, confirm),
        "helpdesk_logs" => await tools.helpdesk_logs(operation.Operation, request, confirm),
        "helpdesk_incidents" => await tools.helpdesk_incidents(operation.Operation, request, confirm),
        "helpdesk_requests" => await tools.helpdesk_requests(operation.Operation, request, confirm),
        "helpdesk_changes" => await tools.helpdesk_changes(operation.Operation, request, confirm),
        "helpdesk_ai_assistant" => await tools.helpdesk_ai_assistant(operation.Operation, request, confirm),
        "helpdesk_request_tasks" => await tools.helpdesk_request_tasks(operation.Operation, request, confirm),
        "helpdesk_organizations" => await tools.helpdesk_organizations(operation.Operation, request, confirm),
        "helpdesk_customers" => await tools.helpdesk_customers(operation.Operation, request, confirm),
        "helpdesk_users" => await tools.helpdesk_users(operation.Operation, request, confirm),
        "helpdesk_search" => await tools.helpdesk_search(operation.Operation, request, confirm),
        "helpdesk_tickets" => await tools.helpdesk_tickets(operation.Operation, request, confirm),
        "helpdesk_roles" => await tools.helpdesk_roles(operation.Operation, request, confirm),
        "helpdesk_categories" => await tools.helpdesk_categories(operation.Operation, request, confirm),
        "helpdesk_services" => await tools.helpdesk_services(operation.Operation, request, confirm),
        "helpdesk_request_forms" => await tools.helpdesk_request_forms(operation.Operation, request, confirm),
        "helpdesk_self_service" => await tools.helpdesk_self_service(operation.Operation, request, confirm),
        "helpdesk_notifications" => await tools.helpdesk_notifications(operation.Operation, request, confirm),
        "helpdesk_connectivity" => await tools.helpdesk_connectivity(operation.Operation, request, confirm),
        "helpdesk_schema" => await tools.helpdesk_schema(operation.Operation, request, confirm),
        "helpdesk_enums" => await tools.helpdesk_enums(operation.Operation, request, confirm),
        "helpdesk_examples" => await tools.helpdesk_examples(operation.Operation, request, confirm),
        "helpdesk_raw" => await tools.helpdesk_raw(operation.Operation, request, confirm),
        "helpdesk_admin_mutations" => await tools.helpdesk_admin_mutations(operation.Operation, request, confirm),
        _ => throw new InvalidOperationException($"No test invoker exists for {operation.Tool}.")
    };

    private static JsonElement Alternative(McpServerTool tool, string operation)
        => Assert.Single(tool.ProtocolTool.InputSchema.GetProperty("oneOf").EnumerateArray(), item =>
            item.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString() == operation);

    private static JsonElement? ContractRequest(McpOperationDefinition operation)
    {
        if (operation.RequestContract == "None") return null;
        var json = operation.Tool switch
        {
            "helpdesk_config" => operation.Operation == "set" ? """{"key":"authentikScope","value":"openid"}""" : """{"key":"apiBaseUrl"}""",
            "helpdesk_logs" => """{"contains":"MCP matrix","limit":1}""",
            "helpdesk_incidents" => TicketRequest("incidentId", operation.Operation, incident: true),
            "helpdesk_requests" => TicketRequest("requestId", operation.Operation),
            "helpdesk_changes" => TicketRequest("changeId", operation.Operation, change: true),
            "helpdesk_ai_assistant" => """{"invocationId":"00000000-0000-0000-0000-000000000001","eventId":"event-1","ticketId":"incident-1","ticketType":"incidents","correlationId":"corr-1","status":"Started","message":"matrix"}""",
            "helpdesk_request_tasks" => TaskRequest(operation.Operation),
            "helpdesk_organizations" => """{"organizationId":"organization-1"}""",
            "helpdesk_customers" => """{"customerId":"customer-1"}""",
            "helpdesk_users" => operation.Operation == "by_email" ? """{"email":"operator@example.test"}""" : """{"userId":"user-1"}""",
            "helpdesk_search" => """{"query":"matrix","limit":1}""",
            "helpdesk_tickets" => operation.Operation.EndsWith("_count", StringComparison.Ordinal) ? """{"ticketType":"incidents","ticketId":"incident-1"}""" : """{"ticketId":"incident-1"}""",
            "helpdesk_roles" => """{"roleId":"role-1"}""",
            "helpdesk_categories" => """{"page":1}""",
            "helpdesk_services" => operation.Operation == "search_items" ? """{"query":"matrix"}""" : """{"serviceId":"service-1"}""",
            "helpdesk_request_forms" => operation.Operation == "by_service" ? """{"serviceId":"service-1"}""" : """{"requestFormId":"form-1"}""",
            "helpdesk_self_service" => """{"requestId":"request-1"}""",
            "helpdesk_notifications" => operation.Operation == "mark_read" ? """{"ids":["notification-1"]}""" : """{"notificationId":"notification-1"}""",
            "helpdesk_connectivity" => """{}""",
            "helpdesk_raw" => operation.Operation == "get_incident" ? """{"incidentId":"incident-1"}""" : operation.Operation == "get_request" ? """{"requestId":"request-1"}""" : operation.Operation == "get_change" ? """{"changeId":"change-1"}""" : """{}""",
            "helpdesk_admin_mutations" when operation.Operation == "update_organization_branding" => """{"tenantId":1,"brandName":"Matrix"}""",
            "helpdesk_admin_mutations" => """{"organizationId":"organization-1","customerId":"customer-1","userId":"user-1","roleId":"role-1","categoryId":"category-1","serviceId":"service-1","requestFormId":"form-1","requestId":"request-1"}""",
            _ => """{}"""
        };
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string TicketRequest(string idName, string operation, bool incident = false, bool change = false)
    {
        var id = idName switch { "incidentId" => "incident-1", "requestId" => "request-1", "changeId" => "change-1", _ => "ticket-1" };
        return operation switch
        {
            "list" => "{}",
            "create" when incident => """{"title":"matrix","description":"matrix","customerId":"customer-1","organizationId":"organization-1","priority":"Low"}""",
            "create" when change => ChangeCreateRequest(),
            "create" => """{"title":"matrix","description":"matrix","priority":"Low","customerId":"customer-1","organizationId":"organization-1"}""",
            "bulk_create" when incident => """{"items":[{"title":"matrix","description":"matrix","customerId":"customer-1","organizationId":"organization-1"}]}""",
            "bulk_create" when change => $"{{\"items\":[{ChangeCreateRequest()}]}}",
            "bulk_create" => """{"items":[{"title":"matrix","description":"matrix","customerId":"customer-1","organizationId":"organization-1"}]}""",
            "bulk_state" => $"{{\"ids\":[\"{id}\"],\"newState\":\"Resolved\"}}",
            "assign" => $"{{\"ids\":[\"{id}\"],\"assignedToId\":\"user-1\"}}",
            "assign_self" => $"{{\"ids\":[\"{id}\"],\"agentUserEmail\":\"operator@example.test\"}}",
            "add_worklog" => $"{{\"{idName}\":\"{id}\",\"hours\":0,\"notes\":\"matrix\",\"isInternalNote\":true}}",
            "lifecycle" => """{"changeId":"change-1","lifecycleState":"Draft"}""",
            "close" => """{"incidentId":"incident-1","closureNote":"matrix"}""",
            "state" => $"{{\"{idName}\":\"{id}\",\"newState\":\"Resolved\"}}",
            _ => $"{{\"{idName}\":\"{id}\"}}"
        };
    }

    private static string ChangeCreateRequest() => """
        {"title":"matrix","description":"matrix","organizationId":"organization-1","requestedForUserId":"user-1","implementorUserId":"user-2","implementationStartAt":"2026-08-23T10:00:00Z","implementationEndAt":"2026-08-23T11:00:00Z","changeType":"Normal","changeTemplate":{"scopeOfChange":"bounded matrix change","affectedSystems":["Dev service"],"implementationSteps":["apply change"],"validationSteps":["verify change"],"rollbackPlan":"revert change","businessJustification":"contract probe","impactAssessment":"bounded Dev impact","riskAssessment":"low and reversible","preChangeChecks":["confirm Dev target"],"testingPlan":"verify expected behavior"}}
        """;

    private static string TaskRequest(string operation) => operation switch
    {
        "list" => "{}",
        "bulk_start" or "bulk_complete" or "bulk_retry" => """{"ids":["task-1"]}""",
        "create" => """{"title":"matrix","description":"matrix","requestId":"request-1","organizationId":"organization-1"}""",
        "update" => """{"taskId":"task-1","title":"updated matrix task"}""",
        _ => """{"taskId":"task-1"}"""
    };

    private static AgentClientConfigurationStore Store() => new(Path.Combine(Path.GetTempPath(), $"helpdesk-mcp-{Guid.NewGuid():N}.json"));
}
