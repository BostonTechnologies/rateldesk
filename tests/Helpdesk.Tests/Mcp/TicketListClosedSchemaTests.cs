using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Tools;
using NSubstitute;

namespace Helpdesk.Tests.Mcp;

public sealed class TicketListClosedSchemaTests
{
    [Theory]
    [InlineData("helpdesk_incidents", "q")]
    [InlineData("helpdesk_requests", "q")]
    [InlineData("helpdesk_changes", "q")]
    [InlineData("helpdesk_request_tasks", "q")]
    [InlineData("helpdesk_incidents", "query")]
    [InlineData("helpdesk_requests", "query")]
    [InlineData("helpdesk_changes", "query")]
    [InlineData("helpdesk_request_tasks", "query")]
    public async Task Published_search_length_is_enforced_before_network_io(string tool, string alias)
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        var tools = new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        async Task<HelpdeskToolResponse> Call(int length)
        {
            var request = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [alias] = new('x', length) });
            return tool switch
            {
                "helpdesk_incidents" => await tools.helpdesk_incidents("list", request),
                "helpdesk_requests" => await tools.helpdesk_requests("list", request),
                "helpdesk_changes" => await tools.helpdesk_changes("list", request),
                _ => await tools.helpdesk_request_tasks("list", request)
            };
        }
        var rejected = await Call(201);
        Assert.Equal("validation_failed", rejected.Status);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.True((await Call(200)).Success);
        await client.Received(1).GetAsync(Arg.Any<string>(), true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("helpdesk_incidents")]
    [InlineData("helpdesk_requests")]
    [InlineData("helpdesk_changes")]
    [InlineData("helpdesk_request_tasks")]
    public async Task Optional_missing_or_null_list_request_retains_unfiltered_read(string tool)
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        var tools = new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        foreach (var request in new JsonElement?[] { null, JsonSerializer.SerializeToElement<object?>(null) })
        {
            var response = tool switch
            {
                "helpdesk_incidents" => await tools.helpdesk_incidents("list", request),
                "helpdesk_requests" => await tools.helpdesk_requests("list", request),
                "helpdesk_changes" => await tools.helpdesk_changes("list", request),
                _ => await tools.helpdesk_request_tasks("list", request)
            };
            Assert.True(response.Success, response.Summary);
        }
        await client.Received(2).GetAsync(Arg.Any<string>(), true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("helpdesk_incidents")]
    [InlineData("helpdesk_requests")]
    [InlineData("helpdesk_changes")]
    [InlineData("helpdesk_request_tasks")]
    public async Task Unknown_list_fields_are_rejected_before_any_upstream_read(string tool)
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        var tools = new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        using var json = JsonDocument.Parse("""{"query":"synthetic-campaign","pageSize":1,"unexpectedCampaignField":true}""");
        var response = tool switch
        {
            "helpdesk_incidents" => await tools.helpdesk_incidents("list", json.RootElement),
            "helpdesk_requests" => await tools.helpdesk_requests("list", json.RootElement),
            "helpdesk_changes" => await tools.helpdesk_changes("list", json.RootElement),
            _ => await tools.helpdesk_request_tasks("list", json.RootElement)
        };
        Assert.False(response.Success);
        Assert.Equal("validation_failed", response.Status);
        Assert.Contains("unexpectedCampaignField", response.Summary);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
