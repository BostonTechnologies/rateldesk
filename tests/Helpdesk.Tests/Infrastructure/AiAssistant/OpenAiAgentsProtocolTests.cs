using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.API.Endpoints.AiAssistant.Chat;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class OpenAiAgentsProtocolTests
{
    [Fact]
    public async Task Creation_sends_the_first_message_with_no_environment_and_no_unsupported_idempotency_header()
    {
        var local = Guid.NewGuid();
        using var client = Client(async (request, ct) =>
        {
            Assert.Equal("https://api.openai.com/v1/agents/sessions", request.RequestUri?.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("agents=v1", Assert.Single(request.Headers.GetValues("OpenAI-Beta")));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("proj_fixture", Assert.Single(request.Headers.GetValues("OpenAI-Project")));
            Assert.False(request.Headers.Contains("Idempotency-Key"));

            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("first admitted message", body.GetProperty("input").GetString());
            Assert.Equal("none", body.GetProperty("environment").GetProperty("type").GetString());
            Assert.Equal(local.ToString("D"), body.GetProperty("metadata").GetProperty("rateldesk_conversation").GetString());
            var tool = Assert.Single(body.GetProperty("agent").GetProperty("tools").EnumerateArray());
            Assert.Equal("service", tool.GetProperty("connection_origin").GetString());
            Assert.Equal("mcp", tool.GetProperty("type").GetString());
            Assert.True(tool.GetProperty("required").GetBoolean());
            Assert.Equal("https://helpdesk.example.com/mcp", tool.GetProperty("transport").GetProperty("server_url").GetString());
            return Response("""{"id":"sess_fixture","status":"in_progress"}""", HttpStatusCode.Created);
        });
        var adapter = Adapter(client);

        Assert.Equal("sess_fixture", await adapter.CreateAsync(local, "first admitted message", default));
        Assert.False(adapter.Features.HasFlag(ChatProviderFeatures.Artifacts));
    }

    [Fact]
    public async Task Observer_parses_chunked_text_and_ignores_unknown_events()
    {
        using var client = Client((request, _) =>
        {
            Assert.Equal("/v1/agents/sessions/sess_fixture/events?stream=true", request.RequestUri?.PathAndQuery);
            Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType);
            var frames = ": pulse\r\n\r\ndata: {\"type\":\"unrecognized\"}\n\n" +
                "data: {\"type\":\"agent.session.turn.output_text.delta\",\n" +
                "data: \"item_id\":\"item_1\",\"turn_id\":\"turn_1\",\"delta\":\"Hello \"}\n\n" +
                "data: {\"type\":\"agent.session.turn.output_text.done\",\"item_id\":\"item_1\",\"turn_id\":\"turn_1\",\"text\":\"Hello world\"}\r\n\r\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(frames, Encoding.UTF8, "text/event-stream")
            });
        });
        await using var stream = await Adapter(client).ObserveAsync("sess_fixture", default);
        var events = new List<ProviderStreamEvent>();
        await foreach (var item in stream.ReadAsync(default)) events.Add(item);
        Assert.Collection(events,
            item => { Assert.Equal(ProviderStreamEventKind.TextDelta, item.Kind); Assert.Equal("Hello ", item.Text); },
            item => { Assert.Equal(ProviderStreamEventKind.TextDone, item.Kind); Assert.Equal("item_1", item.ItemId); Assert.Equal("Hello world", item.Text); });
        var draft = new OpenAiTextDrafts();
        Assert.Equal("Hello ", draft.Apply(events[0]));
        Assert.Equal("Hello world", draft.Apply(events[1]));
    }

    [Fact]
    public async Task Saved_history_reads_root_and_delegated_pages_and_restores_only_current_actions()
    {
        var seen = new List<string>();
        using var client = Client((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            seen.Add(path);
            var body = path switch
            {
                "/v1/agents/sessions/sess_fixture" => """
                    {"id":"sess_fixture","status":"in_progress","required_actions":[{"type":"mcp_approval_request","request_id":"approval_1","turn_id":"turn_1","request":{"server_label":"rateldesk","name":"helpdesk_incidents","arguments":{"operation":"get"}}}]}
                    """,
                "/v1/agents/sessions/sess_fixture/items?order=asc&limit=100" => """
                    {"data":[{"id":"first","turn_id":"turn_1","type":"message","role":"user","status":"completed","content":[{"type":"input_text","text":"operator request"}]}],"has_more":true,"last_id":"first"}
                    """,
                "/v1/agents/sessions/sess_fixture/items?order=asc&limit=100&after=first" => """
                    {"data":[{"id":"reply","turn_id":"turn_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"saved reply"}]}],"has_more":false,"last_id":"reply"}
                    """,
                "/v1/agents/sessions/sess_fixture/turns?order=asc&limit=100" => """
                    {"data":[{"id":"turn_1","created_at":1750000000,"status":"completed","usage":{"input_tokens":13,"output_tokens":5,"total_tokens":18}}],"has_more":false}
                    """,
                "/v1/agents/sessions/sess_fixture/subagents?order=asc&limit=100" => """
                    {"data":[{"id":"child_1","name":"lookup","status":"active","opened_at":1750000001}],"has_more":true,"last_id":"child_1"}
                    """,
                "/v1/agents/sessions/sess_fixture/subagents?order=asc&limit=100&after=child_1" => """{"data":[],"has_more":false}""",
                "/v1/agents/sessions/sess_fixture/subagents/child_1/items?order=asc&limit=100" => """
                    {"data":[{"id":"child_call","turn_id":"child_turn","type":"mcp_call","name":"helpdesk_incidents","status":"failed","arguments":{"operation":"get"}}],"has_more":false}
                    """,
                "/v1/agents/sessions/sess_fixture/subagents/child_1/turns?order=asc&limit=100" => """
                    {"data":[{"id":"child_turn","status":"failed","error":{"code":"authentication_error"}}],"has_more":false}
                    """,
                _ => throw new InvalidOperationException($"Unexpected request {path}")
            };
            return Task.FromResult(Response(body));
        });

        var saved = await Adapter(client).RetrieveAsync("sess_fixture", default);
        Assert.Equal(8, seen.Count);
        Assert.Equal("saved reply", Assert.Single(saved.Items, item => item.Kind == ProviderItemKind.AssistantMessage).Text);
        var call = Assert.Single(saved.Items, item => item.Kind == ProviderItemKind.ToolCall);
        Assert.Equal("child_1", call.SubagentId);
        Assert.Equal("get", call.Operation);
        Assert.Equal("authentication_error", Assert.Single(saved.Turns, turn => turn.SubagentId == "child_1").FailureCode);
        Assert.Equal(18, Assert.Single(saved.Turns, turn => turn.SubagentId is null).Usage?.TotalTokens);
        var pending = Assert.Single(saved.PendingActions);
        Assert.Equal(ProviderActionKind.McpApproval, pending.Kind);
        Assert.Equal("approval_1", pending.Id);
    }

    [Theory]
    [InlineData("message", "agent.session.input.message")]
    [InlineData("cancel", "agent.session.input.cancel")]
    [InlineData("approve", "agent.session.input.mcp_approval_request_result")]
    [InlineData("function", "agent.session.input.tool_result")]
    public async Task Input_events_use_the_same_stable_key_and_known_wire_shapes(string command, string wireType)
    {
        using var client = Client(async (request, ct) =>
        {
            Assert.Equal("/v1/agents/sessions/sess_fixture/events", request.RequestUri?.PathAndQuery);
            Assert.Equal("logical-request", Assert.Single(request.Headers.GetValues("Idempotency-Key")));
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            var input = Assert.Single(body.GetProperty("events").EnumerateArray());
            Assert.Equal(wireType, input.GetProperty("type").GetString());
            if (command == "approve")
            {
                Assert.Equal("approval_1", input.GetProperty("request_id").GetString());
                Assert.Equal("deny", input.GetProperty("response").GetProperty("decision").GetString());
            }
            if (command == "function")
            {
                Assert.Equal("call_1", input.GetProperty("call_id").GetString());
                Assert.Equal("turn_1", input.GetProperty("turn_id").GetString());
                Assert.Equal("saved result", input.GetProperty("output").GetString());
            }
            return Response("{}", HttpStatusCode.Accepted);
        });
        var adapter = Adapter(client);
        const string key = "logical-request";
        await (command switch
        {
            "message" => adapter.SubmitAsync("sess_fixture", key, "follow-up", default),
            "cancel" => adapter.CancelAsync("sess_fixture", key, default),
            "approve" => adapter.RespondAsync("sess_fixture", key, new(ProviderActionKind.McpApproval, "approval_1", "turn_1", null, default, "rateldesk"), new(false), default),
            _ => adapter.RespondAsync("sess_fixture", key, new(ProviderActionKind.Function, "call_1", "turn_1", "note", default, null), new(true, "saved result"), default)
        });
    }

    [Fact]
    public void Signed_webhooks_are_bound_to_the_original_bytes_identity_and_timestamp()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"type":"agent.session.idle","data":{"id":"sess_fixture"}}""");
        var key = RandomNumberGenerator.GetBytes(32);
        var secret = "whsec_" + Convert.ToBase64String(key);
        var now = DateTimeOffset.FromUnixTimeSeconds(1750000000);
        const string timestamp = "1750000000";
        const string id = "message_1";
        var signed = Encoding.UTF8.GetBytes($"{id}.{timestamp}.").Concat(bytes).ToArray();
        var signature = "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, signed));
        Assert.True(OpenAiProviderWebhookEndpoints.VerifySignature(secret, id, timestamp, signature, bytes, now));
        Assert.False(OpenAiProviderWebhookEndpoints.VerifySignature(secret, "other", timestamp, signature, bytes, now));
        Assert.False(OpenAiProviderWebhookEndpoints.VerifySignature(secret, id, timestamp, signature, bytes, now.AddMinutes(6)));
        bytes[^1] = 0x20;
        Assert.False(OpenAiProviderWebhookEndpoints.VerifySignature(secret, id, timestamp, signature, bytes, now));
    }

    [Theory]
    [InlineData("/workspace/outputs/report.json", true)]
    [InlineData("/workspace/outputs/subfolder/report.txt", true)]
    [InlineData("/workspace/outputs/../secrets.txt", false)]
    [InlineData("/workspace/other/report.json", false)]
    [InlineData("/workspace/outputs/foo\\secret.txt", false)]
    public void Artifact_paths_are_confined_to_provider_outputs(string path, bool expected) =>
        Assert.Equal(expected, OpenAiChatRun.SafeArtifactPath(path));

    private static OpenAiAgentsClient Adapter(HttpClient client) => new(client,
        new("fixture-key-for-adapter", "fixture-model", null, "proj_fixture", null, new("none"),
            [new AgentMcpTool("rateldesk", new("https://helpdesk.example.com/mcp", "Bearer fixture-token"), ["helpdesk_incidents"])], 2));

    internal static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
        new(new ProviderHttpFixture(response));

    internal static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ProviderHttpFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(request, cancellationToken);
    }
}
