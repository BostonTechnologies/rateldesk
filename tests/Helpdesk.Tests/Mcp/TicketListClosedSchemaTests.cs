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
