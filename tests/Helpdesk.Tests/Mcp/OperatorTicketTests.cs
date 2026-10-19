using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Tools;
using NSubstitute;

namespace Helpdesk.Tests.Mcp;

public sealed class OperatorTicketTests
{
    [Theory]
    [InlineData("""{"correlationId":"correlation/fixture"}""", "correlationId=correlation%2Ffixture")]
    [InlineData("""{"reference":"INC-FIXTURE"}""", "reference=INC-FIXTURE")]
    [InlineData("""{"reference":null,"correlationId":"correlation-fixture"}""", "correlationId=correlation-fixture")]
    [InlineData("""{"reference":" ","correlationId":"correlation-fixture"}""", "correlationId=correlation-fixture")]
    public async Task Notification_timeline_accepts_either_supported_selector(string json, string query)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(json);

        var result = await tools.helpdesk_notifications("timeline", request.RootElement);

        Assert.True(result.Success, result.Summary);
        await client.Received(1).GetAsync(Arg.Is<string>(path => path.StartsWith("/api/v1/notifications/timeline?", StringComparison.Ordinal) &&
            path.Contains(query, StringComparison.Ordinal)), true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"reference":null,"correlationId":null}""")]
    [InlineData("""{"reference":" ","correlationId":"\t"}""")]
    [InlineData("""{"reference":[],"correlationId":"fixture"}""")]
    public async Task Notification_timeline_rejects_absent_or_invalid_selectors_before_io(string json)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(json);

        var result = await tools.helpdesk_notifications("timeline", request.RootElement);

        Assert.Equal("validation_failed", result.Status);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("customerOrgId", "")]
    [InlineData("customerOrgId", " ")]
    [InlineData("selectedId", "")]
    [InlineData("selectedId", "\t")]
    public async Task Filter_options_reject_blank_repeated_identifiers_before_io(string field, string value)
    {
        await AssertInvalidFilterAsync(field, [value]);
    }

    [Theory]
    [InlineData("customerOrgId")]
    [InlineData("selectedId")]
    public async Task Filter_options_bound_identifier_length_and_collection_count(string field)
    {
        await AssertInvalidFilterAsync(field, [new string('a', 201)]);
        await AssertInvalidFilterAsync(field, Enumerable.Range(0, 21).Select(index => "fixture-" + index).ToArray());
        await AssertInvalidFilterAsync(field, [null!]);
    }

    [Fact]
    public async Task Filter_options_emit_separate_encoded_values_at_the_canonical_boundary()
    {
        var (client, tools) = Create();
        var boundary = new string('a', 200);
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            module = "incident", dimension = "assignee", take = 50,
            customerOrgId = new[] { "organization/a", "organization & b" }, selectedId = new[] { boundary, "member?b" }
        }));

        var result = await tools.helpdesk_tickets("filter_options", request.RootElement);

        Assert.True(result.Success, result.Summary);
        await client.Received(1).GetAsync(Arg.Is<string>(path =>
            path.Contains("customerOrgId=organization%2Fa&customerOrgId=organization%20%26%20b", StringComparison.Ordinal) &&
            path.Contains("selectedId=" + boundary + "&selectedId=member%3Fb", StringComparison.Ordinal)), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Advertised_timeline_alternatives_and_filter_item_bounds_are_executable_contracts()
    {
        var timeline = HelpdeskToolDefinitions.RequestSchema(McpOperationCatalog.All.Single(item => item.Tool == "helpdesk_notifications" && item.Operation == "timeline"));
        var alternatives = timeline["anyOf"]!.AsArray();
        Assert.Equal(2, alternatives.Count);
        Assert.Contains(alternatives, branch => branch!["required"]![0]!.GetValue<string>() == "reference");
        Assert.Contains(alternatives, branch => branch!["required"]![0]!.GetValue<string>() == "correlationId");
        var filter = HelpdeskToolDefinitions.RequestSchema(McpOperationCatalog.All.Single(item => item.Tool == "helpdesk_tickets" && item.Operation == "filter_options"));
        foreach (var name in new[] { "customerOrgId", "selectedId" })
        {
            var field = filter["properties"]![name]!;
            Assert.Equal(20, field["maxItems"]!.GetValue<int>());
            Assert.Equal(200, field["items"]!["maxLength"]!.GetValue<int>());
            Assert.Equal("\\S", field["items"]!["pattern"]!.GetValue<string>());
        }
    }

    private static async Task AssertInvalidFilterAsync(string field, string[] values)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["module"] = "incident", ["dimension"] = "assignee", [field] = values
        }));

        var result = await tools.helpdesk_tickets("filter_options", request.RootElement);

        Assert.Equal("validation_failed", result.Status);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    private static (IHelpdeskAgentClient Client, HelpdeskTools Tools) Create()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.Configuration.Returns(new AgentClientConfiguration("https://api.example.test", null, null, null, null, null, null) { CredentialMode = "gateway" });
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        return (client, new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
    }
}
