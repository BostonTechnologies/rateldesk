using System.Collections.Concurrent;
using System.Text.Json;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Microsoft.Extensions.Options;

var options = new AiAssistantChatOptions
{
    Enabled = true,
    Endpoint = Environment.GetEnvironmentVariable("AI_ASSISTANT_CHAT_ENDPOINT") ?? "",
    DeviceToken = Environment.GetEnvironmentVariable("AI_ASSISTANT_CHAT_DEVICE_TOKEN") ?? "",
    AllowPrivateHttp = true
};
if (!options.IsValid()) throw new InvalidOperationException("Supply AI_ASSISTANT_CHAT_ENDPOINT and AI_ASSISTANT_CHAT_DEVICE_TOKEN via protected environment injection.");
if (args.FirstOrDefault() == "continuation") return await ChatContinuationSmoke.RunAsync(options);
if (args.FirstOrDefault() == "approval") return await ApprovalSmokeAsync(options);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var types = new ConcurrentDictionary<string, int>();
var markerSeen = false;
await using var client = new AiAssistantSignalRChatClient(new AiAssistantChatRuntimeState(Options.Create(options)).Current);
var session = await client.ConnectAsync(null, output =>
{
    var type = output.GetProperty("type").GetString() ?? "unknown";
    types.AddOrUpdate(type, 1, (_, count) => count + 1);
    if (type == "text" && output.TryGetProperty("text", out var text) && text.GetString()?.Contains("HELPDESK_CHAT_794_OK", StringComparison.Ordinal) == true) markerSeen = true;
    if (type == "turn_completed") completed.TrySetResult();
    return Task.CompletedTask;
}, timeout.Token);
var prompt = args.FirstOrDefault() == "mcp"
    ? "Use the Helpdesk MCP capabilities or system-info read operation only, then reply HELPDESK_CHAT_794_OK. Do not read customer or ticket records, run shell commands, send notifications, or change anything. If no such read tool is available, say unavailable."
    : "Reply with exactly HELPDESK_CHAT_794_OK. Do not use tools or access any customer data.";
await client.SendAsync(session.SessionId, prompt, timeout.Token);
await completed.Task.WaitAsync(timeout.Token);
Console.WriteLine(JsonSerializer.Serialize(new { session.SessionId, session.Created, MarkerSeen = markerSeen, OutputTypes = types }));
if (!markerSeen || args.FirstOrDefault() == "mcp" && !types.ContainsKey("tool_call")) return 1;
await using var resumed = new AiAssistantSignalRChatClient(new AiAssistantChatRuntimeState(Options.Create(options)).Current);
var rejoined = await resumed.ConnectAsync(session.SessionId, _ => Task.CompletedTask, timeout.Token);
Console.WriteLine(JsonSerializer.Serialize(new { ResumedSameSession = rejoined.SessionId == session.SessionId, rejoined.Created }));
return rejoined.Created || rejoined.SessionId != session.SessionId ? 1 : 0;

static async Task<int> ApprovalSmokeAsync(AiAssistantChatOptions options)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task Observe(JsonElement output)
    {
        var type = output.GetProperty("type").GetString();
        if (type == "tool_interaction") pending.TrySetResult(output.Clone());
        if (type == "turn_completed") finished.TrySetResult();
        return Task.CompletedTask;
    }
    SessionEnsureResult session;
    await using (var initial = new AiAssistantSignalRChatClient(new AiAssistantChatRuntimeState(Options.Create(options)).Current))
    {
        session = await initial.ConnectAsync(null, Observe, timeout.Token);
        await initial.SendAsync(session.SessionId,
            "Helpdesk integration approval smoke: request the shell tool to run only printf HELPDESK_CHAT_APPROVAL_794. Do not access files, customer data, network services, or change anything. Ask for approval if required by the tool policy; do not bypass policy. If denied, acknowledge the denial without retrying or using another tool.", timeout.Token);
        await Task.WhenAny(pending.Task, finished.Task).WaitAsync(timeout.Token);
        if (!pending.Task.IsCompletedSuccessfully)
        {
            Console.WriteLine("Approval smoke inconclusive: no approval was offered under the current daemon policy.");
            return 2;
        }
    }
    var interaction = await pending.Task;
    var denyOffered = interaction.GetProperty("interactionOptions").EnumerateArray()
        .Any(x => x.GetProperty("key").GetString() == "deny");
    if (!denyOffered) throw new InvalidOperationException("No deny option was offered; parked approval was left unanswered.");
    await using var resumed = new AiAssistantSignalRChatClient(new AiAssistantChatRuntimeState(Options.Create(options)).Current);
    var rejoined = await resumed.ConnectAsync(session.SessionId, Observe, timeout.Token);
    if (rejoined.Created || rejoined.SessionId != session.SessionId) return 1;
    await resumed.RespondAsync(session.SessionId, interaction.GetProperty("callId").GetString()!, "deny", timeout.Token);
    await finished.Task.WaitAsync(timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { session.SessionId, ColdReattached = true, DenialAccepted = true, TurnCompleted = true }));
    return 0;
}
