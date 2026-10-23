using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Tools;
using NSubstitute;

namespace Helpdesk.Tests.Mcp;

public sealed class OperatorAiTests
{
    private const string WebhookId = "11111111-1111-4111-8111-111111111111";
    private const string ConversationId = "22222222-2222-4222-8222-222222222222";
    private const string MessageId = "33333333-3333-4333-8333-333333333333";

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"organizationId\":\"\"}")]
    [InlineData("{\"organizationId\":\"org-a\",\"url\":\"https://other.example.test\"}")]
    public async Task Webhook_listing_requires_one_explicit_organization_without_arbitrary_route_fields(string json)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(json);
        var result = await tools.helpdesk_ai_assistant("webhook_configurations", request.RootElement);
        Assert.Equal("validation_failed", result.Status);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Webhook_secret_is_write_only_and_never_appears_in_confirmation_or_normal_result()
    {
        const string secret = "synthetic-webhook-secret-input";
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            webhookId = WebhookId,
            body = new { organizationId = "org-a", name = "Fixture webhook", ticketAreas = new[] { 0, 1, 2 }, endpoint = "https://agent.example.test/webhook", signingSecret = secret }
        }));
        var preview = await tools.helpdesk_ai_assistant("update_webhook", request.RootElement);
        Assert.Equal("confirmation_required", preview.Status);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(preview));
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        client.SendAsync(HttpMethod.Put, $"/api/v1/admin/ai-assistant-webhooks/{WebhookId}", Arg.Any<JsonNode?>(), true, Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["hasSigningSecret"] = true, ["signingSecret"] = "unexpected-saved-secret", ["message"] = "Saved " + secret });
        var result = await tools.helpdesk_ai_assistant("update_webhook", request.RootElement, confirm: true);
        Assert.True(result.Success, result.Summary);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result));
        Assert.DoesNotContain("unexpected-saved-secret", JsonSerializer.Serialize(result));
        Assert.True(result.Data!["hasSigningSecret"]!.GetValue<bool>());
        await client.Received(1).SendAsync(HttpMethod.Put, $"/api/v1/admin/ai-assistant-webhooks/{WebhookId}",
            Arg.Is<JsonNode?>(body => body != null && body["signingSecret"]!.GetValue<string>() == secret && body["organizationId"]!.GetValue<string>() == "org-a"), true, Arg.Any<CancellationToken>());
        var schema = HelpdeskToolDefinitions.RequestSchema(Definition("update_webhook"));
        Assert.True(schema["properties"]!["body"]!["properties"]!["signingSecret"]!["writeOnly"]!.GetValue<bool>());
        Assert.Equal("uuid", schema["properties"]!["webhookId"]!["format"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("incident")]
    [InlineData("requests/../admin")]
    [InlineData("all")]
    public async Task Ticket_AI_adapters_accept_only_the_three_canonical_ticket_families(string ticketType)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new { ticketType, ticketId = "ticket-a" }));
        Assert.Equal("validation_failed", (await tools.helpdesk_ai_assistant("eligible_configurations", request.RootElement)).Status);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Investigation_dispatch_preserves_idempotency_binding_and_requires_confirmation()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ticketType = "requests", ticketId = "request / fixture",
            body = new { configurationId = WebhookId, operatorAssistanceRequest = "Investigate the isolated request failure.", idempotencyKey = "request-fixture-investigation-1" }
        }));
        Assert.Equal("confirmation_required", (await tools.helpdesk_ai_assistant("dispatch_investigation", request.RootElement)).Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.True((await tools.helpdesk_ai_assistant("dispatch_investigation", request.RootElement, confirm: true)).Success);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/requests/request%20%2F%20fixture/ai-assistant/investigations",
            Arg.Is<JsonNode?>(body => body != null && body["configurationId"]!.GetValue<string>() == WebhookId &&
                body["idempotencyKey"]!.GetValue<string>() == "request-fixture-investigation-1" && body["ticketType"] == null), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Opening_a_first_chat_is_discovered_and_executed_as_a_confirmed_mutation()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("""{"ticketType":"incidents","ticketId":"incident-a"}""");
        var definition = Definition("chat_open");
        Assert.True(definition.RequiresConfirmation);
        Assert.Equal("GET", definition.HttpMethod);
        Assert.Equal("confirmation_required", (await tools.helpdesk_ai_assistant("chat_open", request.RootElement)).Status);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.True((await tools.helpdesk_ai_assistant("chat_open", request.RootElement, confirm: true)).Success);
        await client.Received(1).SendAsync(HttpMethod.Get, "/api/v1/incidents/incident-a/ai-assistant/chat", null, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reading_chat_requires_an_existing_conversation_and_preserves_cursor_without_opening_one()
    {
        var (client, tools) = Create();
        using var missing = JsonDocument.Parse("""{"ticketType":"changes","ticketId":"change-a"}""");
        Assert.Equal("validation_failed", (await tools.helpdesk_ai_assistant("chat", missing.RootElement)).Status);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new { ticketType = "changes", ticketId = "change-a", conversationId = ConversationId, cursor = 12 }));
        Assert.False(Definition("chat").RequiresConfirmation);
        Assert.True((await tools.helpdesk_ai_assistant("chat", request.RootElement)).Success);
        await client.Received(1).GetAsync(Arg.Is<string>(path =>
            path.StartsWith("/api/v1/changes/change-a/ai-assistant/chat?", StringComparison.Ordinal) && path.Contains("conversationId=" + ConversationId, StringComparison.Ordinal) &&
            path.Contains("cursor=12", StringComparison.Ordinal) && !path.Contains("ticketType=", StringComparison.Ordinal)), true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", 0)]
    [InlineData("not-a-conversation", 0)]
    [InlineData(ConversationId, -1)]
    public async Task Chat_cursor_and_conversation_schema_reject_invalid_values_before_transport(string conversationId, long cursor)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new { ticketType = "requests", ticketId = "request-a", conversationId, cursor }));
        Assert.Equal("validation_failed", (await tools.helpdesk_ai_assistant("chat", request.RootElement)).Status);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Chat_interaction_uses_the_exact_offered_call_and_does_not_rewrite_its_choice()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ticketType = "requests", ticketId = "request-a", callId = "offered/call?1",
            body = new { conversationId = ConversationId, selectedKey = "deny & explain" }
        }));
        Assert.Equal("confirmation_required", (await tools.helpdesk_ai_assistant("chat_respond", request.RootElement)).Status);
        Assert.True((await tools.helpdesk_ai_assistant("chat_respond", request.RootElement, confirm: true)).Success);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/requests/request-a/ai-assistant/chat/interactions/offered%2Fcall%3F1",
            Arg.Is<JsonNode?>(body => body != null && body["selectedKey"]!.GetValue<string>() == "deny & explain" && body["conversationId"]!.GetValue<string>() == ConversationId), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Abandon_requires_explicit_possible_delivery_acknowledgement_and_current_message_identity()
    {
        var (client, tools) = Create();
        foreach (var body in new object[]
        {
            new { conversationId = ConversationId, resolutionId = WebhookId, expectedMessageId = MessageId, acknowledgePossibleDelivery = false },
            new { conversationId = ConversationId, resolutionId = WebhookId, acknowledgePossibleDelivery = true }
        })
        {
            using var request = JsonDocument.Parse(JsonSerializer.Serialize(new { ticketType = "requests", ticketId = "request-a", body }));
            Assert.Equal("validation_failed", (await tools.helpdesk_ai_assistant("chat_abandon", request.RootElement, confirm: true)).Status);
        }
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_native_chat_is_reported_as_unavailable_and_never_becomes_a_successful_send()
    {
        var (client, tools) = Create();
        client.GetAsync("/api/v1/incidents/incident-a/ai-assistant/chat-capabilities", true, Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["enabled"] = false, ["reason"] = "Native chat requires PostgreSQL." });
        using var capability = JsonDocument.Parse("""{"ticketType":"incidents","ticketId":"incident-a"}""");
        var read = await tools.helpdesk_ai_assistant("chat_capabilities", capability.RootElement);
        Assert.True(read.Success);
        Assert.False(read.Data!["enabled"]!.GetValue<bool>());
        client.SendAsync(HttpMethod.Post, "/api/v1/incidents/incident-a/ai-assistant/chat/messages", Arg.Any<JsonNode?>(), true, Arg.Any<CancellationToken>())
            .Returns<JsonNode?>(_ => throw new AgentClientRemoteException("chat_disabled", "Chat is not enabled.", 503, null));
        using var message = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            ticketType = "incidents", ticketId = "incident-a", body = new { conversationId = ConversationId, clientMessageId = MessageId, text = "Synthetic test request." }
        }));
        var result = await tools.helpdesk_ai_assistant("chat_send", message.RootElement, confirm: true);
        Assert.False(result.Success);
        Assert.Equal(503, result.Failure!.UpstreamStatus);
        Assert.False(result.Failure.Retryable);
    }

    private static McpOperationDefinition Definition(string operation) => McpOperationCatalog.All.Single(item =>
        item.Tool == "helpdesk_ai_assistant" && item.Operation == operation);

    private static (IHelpdeskAgentClient Client, HelpdeskTools Tools) Create()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.Configuration.Returns(new AgentClientConfiguration("https://api.example.test", null, null, null, null, null, null) { CredentialMode = "gateway" });
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        return (client, new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
    }
}
