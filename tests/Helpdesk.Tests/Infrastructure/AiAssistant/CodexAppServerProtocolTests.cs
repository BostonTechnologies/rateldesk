using System.Text.Json;
using System.Threading.Channels;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat.Codex;
using Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;
using Helpdesk.Shared.AiAssistant.Chat;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class CodexAppServerProtocolTests
{
    private static readonly OpenAiChatProfileSettings Profile = new("Synthetic Codex", true, "org", "fixture-model", "",
        null, null, null, "https://mcp.example.com/mcp", ["helpdesk_incidents.get"], false, ChatProviderEnvironment.None,
        null, null) { ReasoningEffort = "medium" };

    [Theory]
    [InlineData("https://auth.openai.com/codex/device", "ABCD-1234", true)]
    [InlineData("http://auth.openai.com/codex/device", "ABCD-1234", false)]
    [InlineData("https://auth.openai.com.evil.example/codex/device", "ABCD-1234", false)]
    [InlineData("https://auth.openai.com/codex/device", "secret?unexpected", false)]
    public void Device_challenge_only_exposes_the_official_verification_page(string url, string code, bool valid)
    {
        var input = JsonSerializer.SerializeToElement(new { type = "chatgptDeviceCode", loginId = Guid.NewGuid(), verificationUrl = url, userCode = code });
        if (!valid)
        {
            Assert.Throws<InvalidDataException>(() => CodexConnections.ParseChallenge(input));
            return;
        }
        var challenge = CodexConnections.ParseChallenge(input);
        Assert.Equal(url, challenge.Url);
        Assert.Equal(code, challenge.Code);
        Assert.DoesNotContain(code, challenge.ToString());
    }

    [Fact]
    public async Task Recovery_reads_the_bound_history_without_resuming_or_submitting()
    {
        var message = Guid.NewGuid();
        var rpc = new ScriptedRpc((method, request) => method switch
        {
            "thread/read" => Parse("""{"thread":{"id":"thread-fixture","status":{"type":"notLoaded"}}}"""),
            "thread/turns/list" => Parse($$$"""
                {"data":[{"id":"turn-fixture","status":"completed","items":[
                    {"type":"userMessage","id":"item-user","clientId":"{{{message:D}}}","content":[{"type":"text","text":"Synthetic original"}]},
                    {"type":"mcpToolCall","id":"item-call","server":"rateldesk","tool":"helpdesk_incidents","status":"completed","arguments":{"operation":"get"}},
                    {"type":"agentMessage","id":"item-result","text":"Saved reply"}
                ]}],"nextCursor":"earlier"}
                """),
            _ => throw new InvalidOperationException($"Unexpected recovery command {method}.")
        });
        var session = new CodexAppServerSession(rpc, Profile, message);
        var history = await session.RetrieveAsync("thread-fixture", default);
        Assert.Equal("idle", history.Status);
        Assert.Equal("turn-fixture", Assert.Single(history.Turns).Id);
        Assert.Equal("get", Assert.Single(history.Items, item => item.Kind == ProviderItemKind.ToolCall).Operation);
        Assert.Equal("Saved reply", Assert.Single(history.Items, item => item.Kind == ProviderItemKind.AssistantMessage).Text);
        Assert.Equal(new[] { "thread/read", "thread/turns/list" }, rpc.Calls);
    }

    [Fact]
    public async Task Follow_up_pins_model_and_id_and_interrupts_only_the_confirmed_turn()
    {
        var message = Guid.NewGuid();
        var rpc = new ScriptedRpc((method, request) =>
        {
            Assert.Equal("thread-fixture", request.GetProperty("threadId").GetString());
            switch (method)
            {
                case "thread/resume":
                    Assert.True(request.GetProperty("excludeTurns").GetBoolean());
                    Assert.Equal("fixture-model", request.GetProperty("model").GetString());
                    return Parse("""{"thread":{"id":"thread-fixture"},"model":"fixture-model","modelProvider":"openai","approvalPolicy":"never","sandbox":{"type":"readOnly"}}""");
                case "mcpServerStatus/list":
                    return Parse("""{"data":[{"name":"rateldesk","runtimeStatus":"connected","tools":{"helpdesk_incidents":{}}}]}""");
                case "turn/start":
                    Assert.Equal(message.ToString("D"), request.GetProperty("clientUserMessageId").GetString());
                    Assert.Equal("fixture-model", request.GetProperty("model").GetString());
                    Assert.Equal("medium", request.GetProperty("effort").GetString());
                    return Parse("""{"turn":{"id":"turn-fixture","status":"inProgress","items":[]}}""");
                case "turn/interrupt":
                    Assert.Equal("turn-fixture", request.GetProperty("turnId").GetString());
                    return Parse("{}");
                default:
                    throw new InvalidOperationException($"Unexpected command {method}.");
            }
        });
        var session = new CodexAppServerSession(rpc, Profile, message);
        await session.PrepareAsync("thread-fixture", default);
        await session.CheckMcpAsync("thread-fixture", default);
        await session.SubmitAsync("thread-fixture", "correlation-only", "Synthetic follow-up", default);
        await session.CancelAsync("thread-fixture", "cancel", default);
        Assert.Equal(new[] { "thread/resume", "mcpServerStatus/list", "turn/start", "turn/interrupt" }, rpc.Calls);
    }

    [Fact]
    public async Task Account_check_distinguishes_chatgpt_from_an_api_key_and_binds_the_original_workspace()
    {
        var connection = new CodexConnection { AccountLabel = "operator@example.test", WorkspaceLabel = "Synthetic workspace" };
        var account = JsonSerializer.SerializeToElement(new
        {
            account = new { type = "chatgpt", email = "operator@example.test", planType = "pro", workspaceName = "Synthetic workspace" }
        });
        var rpc = new ScriptedRpc((method, request) =>
        {
            Assert.Equal("account/read", method);
            Assert.False(request.GetProperty("refreshToken").GetBoolean());
            return account;
        });
        Assert.True(await CodexConnections.AccountMatchesAsync(connection, rpc, default));
        connection.WorkspaceLabel = "Different workspace";
        Assert.False(await CodexConnections.AccountMatchesAsync(connection, rpc, default));
        account = Parse("""{"account":{"type":"apiKey"}}""");
        Assert.False(await CodexConnections.AccountMatchesAsync(connection, rpc, default));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class ScriptedRpc(Func<string, JsonElement, JsonElement> respond) : ICodexAppServerRpc
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public List<string> Calls { get; } = [];
        public ChannelReader<CodexNotification> Notifications { get; } = Channel.CreateUnbounded<CodexNotification>().Reader;
        public Task<JsonElement> CallAsync<T>(string method, T parameters, CancellationToken ct)
        {
            Calls.Add(method);
            return Task.FromResult(respond(method, JsonSerializer.SerializeToElement(parameters, Json)));
        }
    }
}
