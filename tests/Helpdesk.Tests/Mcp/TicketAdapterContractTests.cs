using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Tools;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.WebUtilities;
using NSubstitute;

namespace Helpdesk.Tests.Mcp;

public sealed class TicketAdapterContractTests
{
    private static readonly string[] Families = ["incidents", "requests", "changes", "request_tasks"];

    [Fact]
    public async Task Every_ticket_family_normalizes_aliases_preserves_encoding_and_does_not_mutate_the_input()
    {
        foreach (var family in Families)
        foreach (var json in new[] { "{}", "{\"query\":null}", "{\"query\":\" \"}", "{\"q\":null}", "{\"query\":\" München R&D + 100%_\\\\? \"}", "{\"q\":\"needle\",\"query\":\" needle \"}" })
        {
            var tools = Tools(out var client);
            var request = Request(json);
            var original = request.GetRawText();
            var result = await List(tools, family, request);
            Assert.True(result.Success);
            var path = Assert.IsType<string>(Assert.Single(client.ReceivedCalls()).GetArguments()[0]);
            Assert.StartsWith("/api/v1/" + family.Replace('_', '-') + "/", path, StringComparison.Ordinal);
            var query = QueryHelpers.ParseQuery(new Uri("https://api.example.test" + path).Query);
            Assert.False(query.ContainsKey("query"));
            var expected = request.TryGetProperty("q", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : request.TryGetProperty("query", out var alias) && alias.ValueKind == JsonValueKind.String ? alias.GetString() : null;
            if (string.IsNullOrWhiteSpace(expected)) Assert.False(query.ContainsKey("q"));
            else Assert.Equal(expected, Assert.Single(query["q"]));
            Assert.Equal(original, request.GetRawText());
        }
    }

    [Fact]
    public async Task Every_ticket_family_rejects_conflicts_wrong_types_and_duplicates_before_network_IO()
    {
        foreach (var family in Families)
        foreach (var json in new[] { "[]", "123", "{\"query\":123}", "{\"q\":false}", "{\"q\":[]}", "{\"query\":{},\"q\":\"ok\"}", "{\"query\":\"one\",\"q\":\"two\"}", "{\"query\":\"x\",\"query\":null}", "{\"q\":\"x\",\"q\":\"x\"}" })
        {
            var tools = Tools(out var client);
            var result = await List(tools, family, Request(json));
            Assert.Equal("validation_failed", result.Status);
            Assert.Empty(client.ReceivedCalls());
        }
    }

    [Fact]
    public async Task List_identifier_filters_are_explicit_and_arrays_become_repeated_parameters()
    {
        foreach (var family in Families)
        {
            var tools = Tools(out var client);
            var fields = new JsonObject { ["query"] = "match", ["page"] = 2, ["pageSize"] = 3, ["includeTotal"] = true };
            if (family == "request_tasks")
            {
                fields["requestId"] = "parent"; fields["serviceId"] = "service"; fields["assignedToId"] = "user";
            }
            else
            {
                fields["customerOrgId"] = new JsonArray("org", "a&b"); fields["forId"] = new JsonArray("contact");
                fields["assigneeId"] = new JsonArray("user"); fields["stateValues"] = new JsonArray("New", "Resolved");
                if (family == "incidents") { fields["organizationId"] = "org"; fields["excludeId"] = "excluded"; }
            }
            var request = JsonSerializer.SerializeToElement(fields);
            Assert.True((await List(tools, family, request)).Success);
            var path = Assert.IsType<string>(Assert.Single(client.ReceivedCalls()).GetArguments()[0]);
            var query = QueryHelpers.ParseQuery(new Uri("https://api.example.test" + path).Query);
            Assert.Equal("match", Assert.Single(query["q"]));
            Assert.False(query.ContainsKey("id"));
            Assert.False(query.ContainsKey("taskId"));
            if (family == "request_tasks")
            {
                Assert.Equal("parent", Assert.Single(query["requestId"]));
                Assert.Equal("service", Assert.Single(query["serviceId"]));
                Assert.Equal("user", Assert.Single(query["assignedToId"]));
                Assert.False(query.ContainsKey("customerOrgId"));
            }
            else
            {
                Assert.Equal(new[] { "org", "a&b" }, query["customerOrgId"].ToArray());
                Assert.Equal(new[] { "New", "Resolved" }, query["stateValues"].ToArray());
                Assert.False(query.ContainsKey("requestId"));
            }
            Assert.Equal(family == "incidents", query.ContainsKey("organizationId"));
            Assert.Equal(family == "incidents", query.ContainsKey("excludeId"));
            Assert.Equal("2", query["page"].ToString());
            Assert.Equal("3", query["pageSize"].ToString());
            Assert.Equal("true", query["includeTotal"].ToString());
            // Closed list contracts reject route selectors and filters belonging
            // to other families; none may be silently ignored or reach the API.
            foreach (var unsupported in new[] { "id", "taskId", family == "request_tasks" ? "customerOrgId" : "requestId" })
            {
                var invalid = (JsonObject)fields.DeepClone(); invalid[unsupported] = "route";
                Assert.Equal("validation_failed", (await List(tools, family, JsonSerializer.SerializeToElement(invalid))).Status);
            }
            Assert.Single(client.ReceivedCalls());
        }
    }

    [Theory]
    [InlineData("Low", 0)][InlineData("Medium", 1)][InlineData("High", 2)][InlineData("Critical", 3)]
    [InlineData("high", 2)]
    public async Task Task_create_and_update_normalize_every_named_priority(string priority, int numeric)
    {
        foreach (var operation in new[] { "create", "update" })
        {
            var tools = Tools(out var client);
            var request = JsonSerializer.SerializeToElement(new { taskId = "task-1", requestId = "parent", organizationId = "org", title = "task", description = "details", priority });
            if (operation == "update") request = JsonSerializer.SerializeToElement(new { taskId = "task-1", title = "task", description = "details", priority });
            var response = await tools.helpdesk_request_tasks(operation, request, confirm: true);
            Assert.True(response.Success);
            var arguments = Assert.Single(client.ReceivedCalls()).GetArguments();
            Assert.Equal(operation == "create" ? HttpMethod.Post : HttpMethod.Patch, arguments[0]);
            Assert.Equal(operation == "create" ? "/api/v1/request-tasks/" : "/api/v1/request-tasks/task-1", arguments[1]);
            var body = Assert.IsType<JsonObject>(arguments[2]);
            Assert.Equal(numeric, body["priority"]!.GetValue<int>());
            if (operation == "update") Assert.False(body.ContainsKey("taskId"));
        }
    }

    [Theory]
    [InlineData("\"invalid\"")][InlineData("\"999\"")][InlineData("\"2\"")][InlineData("999")][InlineData("2")]
    [InlineData("null")][InlineData("true")][InlineData("[]")][InlineData("{}")]
    public async Task Invalid_priority_rejects_the_entire_create_update_and_preview(string priority)
    {
        foreach (var operation in new[] { "create", "update" })
        foreach (var confirm in new[] { false, true })
        {
            var tools = Tools(out var client);
            var fields = operation == "create" ? "\"requestId\":\"parent\",\"organizationId\":\"org\"," : "\"taskId\":\"task-1\",";
            var response = await tools.helpdesk_request_tasks(operation, Request("{" + fields + "\"title\":\"changed\",\"description\":\"changed\",\"priority\":" + priority + "}"), confirm);
            Assert.Equal("validation_failed", response.Status);
            Assert.Contains("request.priority", response.Summary, StringComparison.Ordinal);
            Assert.Empty(client.ReceivedCalls());
        }
    }

    [Fact]
    public async Task Named_ticket_and_change_enums_are_normalized_and_undefined_values_never_reach_the_API()
    {
        var tools = Tools(out var client);
        Assert.True((await tools.helpdesk_incidents("state", Request("""{"incidentId":"ticket","newState":"Resolved"}"""), true)).Success);
        Assert.Equal((int)TicketState.Resolved, Assert.IsType<JsonObject>(Assert.Single(client.ReceivedCalls()).GetArguments()[2])["newState"]!.GetValue<int>());
        client.ClearReceivedCalls();
        Assert.True((await tools.helpdesk_incidents("state", Request("""{"incidentId":"ticket","newState":"2"}"""), true)).Success);
        Assert.Equal(2, Assert.IsType<JsonObject>(Assert.Single(client.ReceivedCalls()).GetArguments()[2])["newState"]!.GetValue<int>());
        client.ClearReceivedCalls();
        Assert.True((await tools.helpdesk_changes("lifecycle", Request("""{"changeId":"ticket","lifecycleState":"Submitted"}"""), true)).Success);
        Assert.Equal((int)ChangeLifecycleState.Submitted, Assert.IsType<JsonObject>(Assert.Single(client.ReceivedCalls()).GetArguments()[2])["lifecycleState"]!.GetValue<int>());
        client.ClearReceivedCalls();
        foreach (var confirm in new[] { false, true })
            Assert.Equal("validation_failed", (await tools.helpdesk_incidents("state", Request("""{"incidentId":"ticket","newState":999}"""), confirm)).Status);
        Assert.Empty(client.ReceivedCalls());
    }

    [Theory]
    [InlineData("customer_invite", "invite")][InlineData("customer_resend_invite", "resend-invite")]
    [InlineData("customer_disable_login", "disable-login")][InlineData("customer_sync_authentik", "sync-authentik")]
    public async Task Customer_auth_mutations_use_the_existing_modern_routes(string operation, string suffix)
    {
        var tools = Tools(out var client);
        Assert.True((await tools.helpdesk_admin_mutations(operation, Request("""{"customerId":"customer /1"}"""), true)).Success);
        var arguments = Assert.Single(client.ReceivedCalls()).GetArguments();
        Assert.Equal(HttpMethod.Post, arguments[0]);
        Assert.Equal("/api/v1/customers/customer%20%2F1/auth/" + suffix, arguments[1]);
        Assert.Empty(Assert.IsType<JsonObject>(arguments[2]));
    }

    [Fact]
    public async Task Customer_auth_status_uses_the_existing_modern_route()
    {
        var tools = Tools(out var client);
        Assert.True((await tools.helpdesk_customers("auth_status", Request("""{"customerId":"customer /1"}"""))).Success);
        Assert.Equal("/api/v1/customers/customer%20%2F1/auth/status", Assert.Single(client.ReceivedCalls()).GetArguments()[0]);
    }

    [Fact]
    public async Task Mutating_server_error_is_uncertain_and_requires_reconciliation_before_retry()
    {
        var tools = Tools(out var client);
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), true, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<JsonNode?>(new AgentClientRemoteException("api_request_failed", "API failed", 500, null) { CorrelationId = "synthetic-correlation" }));
        var response = await tools.helpdesk_request_tasks("complete", Request("""{"taskId":"task-1"}"""), true);
        Assert.False(response.Success);
        Assert.False(response.Failure!.Retryable);
        Assert.Equal("synthetic-correlation", response.CorrelationId);
        Assert.Equal("task-1", Assert.Single(response.AffectedIds!));
        Assert.Contains("uncertain", Assert.Single(response.NextActions!), StringComparison.Ordinal);
        Assert.Single(client.ReceivedCalls());
    }

    [Fact]
    public async Task Bulk_zero_updated_is_reported_as_a_no_op_with_no_claimed_affected_records()
    {
        var tools = Tools(out var client);
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), true, Arg.Any<CancellationToken>()).Returns(new JsonObject { ["updated"] = 0 });
        var result = await tools.helpdesk_request_tasks("bulk_retry", Request("""{"ids":["task-1"]}"""), true);
        Assert.True(result.Success);
        Assert.Equal("no_op", result.Status);
        Assert.Empty(result.AffectedIds!);
        Assert.Contains("zero updated", Assert.Single(result.NextActions!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_correlation_filter_is_forwarded_explicitly_with_integer_cursor_and_bounded_limit()
    {
        var tools = Tools(out var client);
        var result = await tools.helpdesk_logs(request: Request("""{"correlationId":"synthetic-correlation","since":42,"limit":5}"""));
        Assert.True(result.Success, result.Summary);
        var path = Assert.IsType<string>(Assert.Single(client.ReceivedCalls()).GetArguments()[0]);
        var parameters = QueryHelpers.ParseQuery(new Uri("https://api.example.test" + path).Query);
        Assert.Equal("synthetic-correlation", Assert.Single(parameters["correlationId"]));
        Assert.Equal("42", Assert.Single(parameters["since"]));
        Assert.Equal("5", Assert.Single(parameters["limit"]));
    }

    [Fact]
    public async Task Invalid_log_filter_types_and_unbounded_requests_return_structured_validation_without_network_IO()
    {
        foreach (var json in new[] { "[]", "{}", "{\"correlationId\":123}", "{\"contains\":true}", "{\"contains\":\" \"}", "{\"since\":-1}", "{\"since\":\"42\"}", "{\"since\":1.5}", "{\"since\":0,\"limit\":\"5\"}", "{\"since\":0,\"limit\":1.5}", "{\"since\":0,\"limit\":101}", "{\"since\":0,\"limit\":0}", "{\"contains\":\"a\",\"contains\":\"b\"}" })
        {
            var tools = Tools(out var client);
            var result = await tools.helpdesk_logs(request: Request(json));
            Assert.Equal("validation_failed", result.Status);
            Assert.Empty(client.ReceivedCalls());
        }
    }

    [Fact]
    public async Task Global_search_aliases_map_query_and_bound_explicitly_and_conflicts_never_call_the_API()
    {
        foreach (var operation in new[] { "users", "customers", "organizations" })
        {
            var tools = Tools(out var client);
            Assert.True((await tools.helpdesk_search(operation, Request("""{"query":"search & term","q":" search & term ","limit":3,"pageSize":3}"""))).Success);
            var path = Assert.IsType<string>(Assert.Single(client.ReceivedCalls()).GetArguments()[0]);
            var query = QueryHelpers.ParseQuery(new Uri("https://api.example.test" + path).Query);
            Assert.Equal(" search & term ", Assert.Single(query["q"]));
            Assert.Equal("3", Assert.Single(query["pageSize"]));
            Assert.False(query.ContainsKey("query")); Assert.False(query.ContainsKey("limit"));
            client.ClearReceivedCalls();
            foreach (var json in new[] { "{\"query\":\"a\",\"q\":\"b\"}", "{\"query\":\"a\",\"limit\":3,\"pageSize\":4}", "{\"query\":123}", "{\"query\":\"a\",\"limit\":\"3\"}", "{\"query\":\"a\",\"pageSize\":1.5}", "{\"query\":\"a\",\"limit\":0}" })
            {
                var denied = await tools.helpdesk_search(operation, Request(json));
                Assert.Equal("validation_failed", denied.Status);
                Assert.Empty(client.ReceivedCalls());
            }
        }
    }

    private static HelpdeskTools Tools(out IHelpdeskAgentClient client)
    {
        client = Substitute.For<IHelpdeskAgentClient>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        return new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), $"adapter-{Guid.NewGuid():N}.json")));
    }
    private static Task<HelpdeskToolResponse> List(HelpdeskTools tools, string family, JsonElement request) => family switch
    {
        "incidents" => tools.helpdesk_incidents("list", request),
        "requests" => tools.helpdesk_requests("list", request),
        "changes" => tools.helpdesk_changes("list", request),
        _ => tools.helpdesk_request_tasks("list", request)
    };
    private static JsonElement Request(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
