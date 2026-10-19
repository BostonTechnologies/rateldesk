using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Tools;
using NSubstitute;

namespace Helpdesk.Tests.Mcp;

public sealed class OperatorIncidentTests
{
    [Theory]
    [InlineData("create_relation", """{"incidentId":"source","body":{"targetTicketId":"target","relationType":1}}""")]
    [InlineData("create_relation", """{"incidentId":"source","body":{"targetTicketId":"target","relationType":7,"closeSourceTicket":false}}""")]
    [InlineData("create_relation", """{"incidentId":"source","body":{"targetTicketId":" ","relationType":1,"closeSourceTicket":false}}""")]
    [InlineData("delete_relation", """{"incidentId":"source","relationId":"not-a-uuid"}""")]
    [InlineData("bulk_delete", """{"body":{"ids":[]}}""")]
    [InlineData("bulk_marketing_spam", """{"body":{"ids":[" "]}}""")]
    public async Task Destructive_incident_actions_reject_invalid_targets_or_implicit_closure(string operation, string json)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(json);

        var result = await tools.helpdesk_incidents(operation, request.RootElement, confirm: true);

        Assert.Equal("validation_failed", result.Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Related_incident_creation_preserves_the_explicit_open_source_choice_after_confirmation()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("""{"incidentId":"source/a","body":{"targetTicketId":"target-b","relationType":1,"closeSourceTicket":false,"note":"Investigate together"}}""");

        var preview = await tools.helpdesk_incidents("create_relation", request.RootElement);
        Assert.Equal("confirmation_required", preview.Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        var result = await tools.helpdesk_incidents("create_relation", request.RootElement, confirm: true);

        Assert.True(result.Success, result.Summary);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/incidents/source%2Fa/relations",
            Arg.Is<JsonNode?>(body => body != null && body["targetTicketId"]!.GetValue<string>() == "target-b" &&
                body["relationType"]!.GetValue<long>() == 1 && !body["closeSourceTicket"]!.GetValue<bool>()), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Relationship_read_and_confirmed_removal_use_the_selected_incident_and_relation()
    {
        const string relationId = "21598734-3aac-43b8-8152-9024488a7f2b";
        var (client, tools) = Create();
        using var selection = JsonDocument.Parse("""{"incidentId":"INC-1"}""");
        using var removal = JsonDocument.Parse($$"""{"incidentId":"INC-1","relationId":"{{relationId}}"}""");

        var read = await tools.helpdesk_incidents("relations", selection.RootElement);
        var preview = await tools.helpdesk_incidents("delete_relation", removal.RootElement);
        Assert.True(read.Success, read.Summary);
        Assert.Equal("confirmation_required", preview.Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        var deleted = await tools.helpdesk_incidents("delete_relation", removal.RootElement, confirm: true);

        Assert.True(deleted.Success, deleted.Summary);
        await client.Received(1).GetAsync("/api/v1/incidents/INC-1/relations", true, Arg.Any<CancellationToken>());
        await client.Received(1).SendAsync(HttpMethod.Delete, $"/api/v1/incidents/INC-1/relations/{relationId}", null, true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("bulk_delete", "delete")]
    [InlineData("bulk_marketing_spam", "marketing-spam")]
    public async Task Silent_bulk_actions_have_bounded_targets_and_require_confirmation(string operation, string route)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("""{"body":{"ids":["incident-a","incident-b"]}}""");
        var schema = HelpdeskToolDefinitions.RequestSchema(McpOperationCatalog.All.Single(item => item.Tool == "helpdesk_incidents" && item.Operation == operation));
        Assert.Equal(100, schema["properties"]!["body"]!["properties"]!["ids"]!["maxItems"]!.GetValue<int>());

        var preview = await tools.helpdesk_incidents(operation, request.RootElement);
        Assert.Equal("confirmation_required", preview.Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        var result = await tools.helpdesk_incidents(operation, request.RootElement, confirm: true);

        Assert.True(result.Success, result.Summary);
        await client.Received(1).SendAsync(HttpMethod.Post, $"/api/v1/incidents/bulk/{route}",
            Arg.Is<JsonNode?>(body => body != null && body["ids"]!.AsArray().Count == 2 &&
                body["ids"]![0]!.GetValue<string>() == "incident-a" && body["ids"]![1]!.GetValue<string>() == "incident-b"), true, Arg.Any<CancellationToken>());
    }

    private static (IHelpdeskAgentClient Client, HelpdeskTools Tools) Create()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.Configuration.Returns(new AgentClientConfiguration("https://api.example.test", null, null, null, null, null, null) { CredentialMode = "gateway" });
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonArray());
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        return (client, new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
    }
}
