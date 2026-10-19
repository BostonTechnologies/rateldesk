using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Tools;
using NSubstitute;

namespace Helpdesk.Tests.Mcp;

public sealed class OperatorNetclawTests
{
    [Fact]
    public async Task Pairing_preserves_the_reviewed_revision_and_one_time_input_without_echoing_secrets()
    {
        var (client, tools) = Create();
        const string pairingCode = "synthetic-netclaw-pairing-code";
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            body = new { pairingCode, expectedRevision = 3, endpoint = "https://netclaw.example.test", enabled = true, turnInactivityTimeout = "00:05:00", activityHeartbeatInterval = "00:00:15" }
        }));
        var preview = await tools.helpdesk_connections("pair_netclaw", request.RootElement);
        Assert.Equal("confirmation_required", preview.Status);
        Assert.DoesNotContain(pairingCode, JsonSerializer.Serialize(preview));
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        client.SendAsync(HttpMethod.Post, "/api/v1/admin/netclaw/pair-and-save", Arg.Any<JsonNode?>(), true, Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["revision"] = 4, ["hasDeviceToken"] = true, ["deviceToken"] = "unexpected-private-device-token", ["message"] = "Paired " + pairingCode });
        var result = await tools.helpdesk_connections("pair_netclaw", request.RootElement, true);
        Assert.True(result.Success, result.Summary);
        Assert.DoesNotContain(pairingCode, JsonSerializer.Serialize(result));
        Assert.DoesNotContain("unexpected-private-device-token", JsonSerializer.Serialize(result));
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/admin/netclaw/pair-and-save",
            Arg.Is<JsonNode?>(body => body != null && body["pairingCode"]!.GetValue<string>() == pairingCode && body["expectedRevision"]!.ToJsonString() == "3"), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Legacy_ownership_confirmation_requires_each_current_reviewed_id_exactly_once()
    {
        var (client, tools) = Create();
        const string id = "11111111-1111-4111-8111-111111111111";
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            body = new { historicalEndpoint = "https://old-netclaw.example.test/hubs/agent", historicalInstance = "dev", expectedEligibleConversations = 1, conversationIds = new[] { id } }
        }));
        Assert.True((await tools.helpdesk_connections("confirm_netclaw_session_owner", request.RootElement, true)).Success);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/admin/netclaw/legacy-sessions/confirm-owner", Arg.Any<JsonNode?>(), true, Arg.Any<CancellationToken>());
        client.ClearReceivedCalls();
        using var repeated = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            body = new { historicalEndpoint = "https://old-netclaw.example.test/hubs/agent", expectedEligibleConversations = 2, conversationIds = new[] { id, id } }
        }));
        Assert.Equal("validation_failed", (await tools.helpdesk_connections("confirm_netclaw_session_owner", repeated.RootElement, true)).Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Draft_test_validates_duration_wire_format_and_never_saves_configuration()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("""{"body":{"enabled":true,"expectedRevision":2,"turnInactivityTimeout":"00:05:00","activityHeartbeatInterval":"00:00:15"}}""");
        Assert.True((await tools.helpdesk_connections("test_netclaw_draft", request.RootElement, true)).Success);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/admin/netclaw/test-draft", Arg.Any<JsonNode?>(), true, Arg.Any<CancellationToken>());
        client.ClearReceivedCalls();
        using var invalid = JsonDocument.Parse("""{"body":{"enabled":true,"turnInactivityTimeout":"five minutes"}}""");
        Assert.Equal("validation_failed", (await tools.helpdesk_connections("test_netclaw_draft", invalid.RootElement, true)).Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    private static (IHelpdeskAgentClient Client, HelpdeskTools Tools) Create()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.Configuration.Returns(AgentClientConfiguration.Empty with { ApiBaseUrl = "https://api.example.test", CredentialMode = "gateway" });
        client.GetAsync(Arg.Any<string>(), true, Arg.Any<CancellationToken>()).Returns(new JsonObject());
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), true, Arg.Any<CancellationToken>()).Returns(new JsonObject());
        return (client, new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
    }
}
