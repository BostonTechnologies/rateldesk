using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Tools;
using Microsoft.AspNetCore.WebUtilities;
using NSubstitute;

namespace Helpdesk.Tests.Mcp;

public sealed class IncidentListSearchTests
{
    [Fact]
    public async Task Incident_query_becomes_one_encoded_q_without_changing_request_or_list_options()
    {
        var tools = Tools(out var client);
        const string search = "München R&D + 100%_\\? space";
        var request = JsonSerializer.SerializeToElement(new
        {
            query = search, page = 2, pageSize = 3, includeTotal = true,
            activeOnly = false, historicOnly = true, state = "Resolved", view = "historic"
        });
        var original = request.GetRawText();
        using var cancellation = new CancellationTokenSource();

        var result = await tools.helpdesk_incidents("list", request, cancellationToken: cancellation.Token);

        Assert.True(result.Success);
        var arguments = Assert.Single(client.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IHelpdeskAgentClient.GetAsync)).GetArguments();
        var path = Assert.IsType<string>(arguments[0]);
        Assert.StartsWith("/api/v1/incidents/?", path, StringComparison.Ordinal);
        Assert.Contains("q=" + Uri.EscapeDataString(search), path, StringComparison.Ordinal);
        var parameters = QueryHelpers.ParseQuery(new Uri("https://api.example" + path).Query);
        Assert.Equal(search, Assert.Single(parameters["q"]));
        Assert.False(parameters.ContainsKey("query"));
        Assert.Equal("2", parameters["page"].ToString());
        Assert.Equal("3", parameters["pageSize"].ToString());
        Assert.Equal("true", parameters["includeTotal"].ToString());
        Assert.Equal("false", parameters["activeOnly"].ToString());
        Assert.Equal("true", parameters["historicOnly"].ToString());
        Assert.Equal("Resolved", parameters["state"].ToString());
        Assert.Equal("historic", parameters["view"].ToString());
        Assert.Equal(cancellation.Token, arguments[2]);
        Assert.Equal(original, request.GetRawText());
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"query\":null}", null)]
    [InlineData("{\"q\":null}", null)]
    [InlineData("{\"query\":\"\"}", null)]
    [InlineData("{\"q\":\" \\t\"}", null)]
    [InlineData("{\"query\":\"  \"}", null)]
    [InlineData("{\"query\":null,\"q\":\"\"}", null)]
    [InlineData("{\"q\":\"match\"}", "match")]
    [InlineData("{\"query\":\"match\"}", "match")]
    [InlineData("{\"query\":\"match\",\"q\":\"match\"}", "match")]
    [InlineData("{\"query\":\" match \",\"q\":\"match\"}", "match")]
    [InlineData("{\"query\":\"match\",\"q\":\" match \"}", " match ")]
    [InlineData("{\"query\":\" \\t\",\"q\":\"match\"}", "match")]
    [InlineData("{\"query\":\"match\",\"q\":null}", "match")]
    [InlineData("{\"query\":\" match \",\"q\":\" \"}", " match ")]
    public async Task Incident_search_aliases_retain_API_empty_and_trimming_behavior(string json, string? expected)
    {
        var tools = Tools(out var client);
        var result = await tools.helpdesk_incidents("list", Request(json));

        Assert.True(result.Success);
        var path = Assert.IsType<string>(Assert.Single(client.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IHelpdeskAgentClient.GetAsync)).GetArguments()[0]);
        var parameters = QueryHelpers.ParseQuery(new Uri("https://api.example" + path).Query);
        Assert.False(parameters.ContainsKey("query"));
        if (expected is null) Assert.False(parameters.ContainsKey("q"));
        else Assert.Equal(expected, Assert.Single(parameters["q"]));
    }

    [Fact]
    public async Task Missing_or_null_incident_request_remains_an_unfiltered_list()
    {
        var tools = Tools(out var client);

        Assert.True((await tools.helpdesk_incidents("list")).Success);
        Assert.True((await tools.helpdesk_incidents("list", Request("null"))).Success);

        await client.Received(2).GetAsync("/api/v1/incidents/", true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{\"query\":\"match\",\"q\":\"other\"}")]
    [InlineData("{\"query\":\"match\",\"q\":\"MATCH\"}")]
    [InlineData("{\"query\":{}}")]
    [InlineData("{\"query\":[]}")]
    [InlineData("{\"query\":123}")]
    [InlineData("{\"query\":true}")]
    [InlineData("{\"query\":\"match\",\"q\":{}}")]
    [InlineData("{\"q\":[]}")]
    [InlineData("{\"q\":123}")]
    [InlineData("{\"q\":false}")]
    [InlineData("{\"q\":\"match\",\"q\":\"other\"}")]
    [InlineData("{\"query\":\"match\",\"query\":null}")]
    [InlineData("[]")]
    [InlineData("\"match\"")]
    [InlineData("123")]
    public async Task Invalid_or_conflicting_search_is_safe_validation_before_network_IO(string json)
    {
        var tools = Tools(out var client);
        var result = await tools.helpdesk_incidents("list", Request(json));

        Assert.False(result.Success);
        Assert.Equal("validation_failed", result.Status);
        Assert.Equal("validation_error", result.Failure?.Code);
        Assert.DoesNotContain("match", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(client.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IHelpdeskAgentClient.GetAsync));
    }

    [Fact]
    public async Task Other_tools_keep_query_parameter_and_generic_identifier_exclusions()
    {
        var tools = Tools(out var client);
        var request = Request("""{"query":"match","page":2,"id":"ignored","organizationId":"ignored"}""");

        Assert.True((await tools.helpdesk_services("list", request)).Success);

        foreach (var call in client.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IHelpdeskAgentClient.GetAsync)))
        {
            var path = Assert.IsType<string>(call.GetArguments()[0]);
            var parameters = QueryHelpers.ParseQuery(new Uri("https://api.example" + path).Query);
            Assert.Equal("match", Assert.Single(parameters["query"]));
            Assert.False(parameters.ContainsKey("q"));
            Assert.False(parameters.ContainsKey("id"));
            Assert.False(parameters.ContainsKey("organizationId"));
        }
    }

    [Fact]
    public async Task Failed_filtered_request_is_not_retried_without_search()
    {
        var tools = Tools(out var client);
        client.GetAsync(Arg.Any<string>(), true, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<JsonNode?>(new AgentClientRemoteException("api_request_failed", "API request failed.", 400, null)));

        var result = await tools.helpdesk_incidents("list", Request("""{"query":"match"}"""));

        Assert.False(result.Success);
        await client.Received(1).GetAsync("/api/v1/incidents/?q=match", true, Arg.Any<CancellationToken>());
        Assert.Single(client.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IHelpdeskAgentClient.GetAsync));
    }

    [Fact]
    public async Task Discoverable_incident_schema_help_and_examples_explain_the_search_aliases()
    {
        var incident = Assert.Single(HelpdeskToolDefinitions.Create(), tool => tool.ProtocolTool.Name == "helpdesk_incidents");
        var list = Assert.Single(incident.ProtocolTool.InputSchema.GetProperty("oneOf").EnumerateArray(), alternative => alternative.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString() == "list");
        var properties = list.GetProperty("properties").GetProperty("request").GetProperty("properties");

        Assert.Equal(["string", "null"], properties.GetProperty("query").GetProperty("type").EnumerateArray().Select(type => type.GetString()));
        Assert.True(properties.TryGetProperty("q", out _));
        Assert.True(properties.TryGetProperty("includeTotal", out _));
        Assert.Contains("request.query maps to API q", incident.ProtocolTool.Description, StringComparison.Ordinal);
        Assert.Contains("conflicting nonempty", incident.ProtocolTool.Description, StringComparison.Ordinal);
        var catalog = Assert.Single(McpOperationCatalog.All, definition => definition.Tool == "helpdesk_incidents" && definition.Operation == "list");
        Assert.Contains("request.query maps to API q", catalog.Description, StringComparison.Ordinal);
        var tools = Tools(out _);
        var examples = await tools.helpdesk_examples();
        Assert.Equal("helpdesk_incidents", examples.Data?["incidentListSearch"]?["tool"]?.GetValue<string>());
        Assert.Equal("list", examples.Data?["incidentListSearch"]?["arguments"]?["operation"]?.GetValue<string>());
        Assert.Contains("totalCount zero", examples.Data?["incidentListSearch"]?["noMatchExpectation"]?.GetValue<string>(), StringComparison.Ordinal);
    }

    private static HelpdeskTools Tools(out IHelpdeskAgentClient client)
    {
        client = Substitute.For<IHelpdeskAgentClient>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        return new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), $"incident-search-{Guid.NewGuid():N}.json")));
    }

    private static JsonElement Request(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
