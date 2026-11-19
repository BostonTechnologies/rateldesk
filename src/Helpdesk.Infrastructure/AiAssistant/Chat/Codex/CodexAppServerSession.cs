using System.Runtime.CompilerServices;
using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Shared.AiAssistant.Chat;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.Codex;

internal sealed class CodexAppServerSession(ICodexAppServerRpc server, OpenAiChatProfileSettings profile, Guid messageId) : IChatProviderSession
{
    private string? openedThread;
    internal string? LastTurnId { get; set; }

    internal bool ModelWasRerouted(string session, string turn) =>
        server is CodexAppServerProcess { ModelReroute: { } notice } &&
        CodexWire.String(notice.Parameters, "threadId") == session && CodexWire.String(notice.Parameters, "turnId") == turn;

    public ChatProviderFeatures Features => ChatProviderFeatures.SavedHistory | ChatProviderFeatures.RemoteCancellation;

    public Task<string> CreateAsync(Guid conversationId, string firstMessage, CancellationToken ct) =>
        throw new InvalidOperationException("Create and bind a Codex thread before reserving its first turn.");

    internal async Task<string> OpenThreadAsync(CancellationToken ct)
    {
        var response = await server.CallAsync("thread/start", new
        {
            profile.Model,
            modelProvider = "openai",
            approvalPolicy = "never",
            sandbox = "read-only",
            ephemeral = false,
            developerInstructions = "Use only the scoped RatelDesk MCP reads configured for this conversation."
        }, ct);
        openedThread = ValidateThread(response);
        return openedThread;
    }

    public Task<IChatProviderObservation> ObserveAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IChatProviderObservation>(new Observation(server, sessionId));

    public async Task SubmitAsync(string sessionId, string requestId, string message, CancellationToken ct)
    {
        if (openedThread != sessionId) throw new InvalidOperationException("The bound Codex thread is not loaded for this turn.");
        var turn = await server.CallAsync("turn/start", new
        {
            threadId = sessionId,
            clientUserMessageId = messageId.ToString("D"),
            input = new[] { new { type = "text", text = message } },
            profile.Model,
            effort = profile.ReasoningEffort
        }, ct);
        LastTurnId = Id(turn.GetProperty("turn"), "id");
    }

    internal async Task PrepareAsync(string sessionId, CancellationToken ct)
    {
        if (openedThread == sessionId) return;
        var response = await server.CallAsync("thread/resume", new
        {
            threadId = sessionId, excludeTurns = true, profile.Model, modelProvider = "openai",
            approvalPolicy = "never", sandbox = "read-only"
        }, ct);
        if (ValidateThread(response) != sessionId)
            throw new InvalidDataException("Codex returned a different thread identity.");
        openedThread = sessionId;
    }

    internal async Task CheckMcpAsync(string sessionId, CancellationToken ct)
    {
        var response = await server.CallAsync("mcpServerStatus/list", new
        {
            threadId = sessionId, serverName = "rateldesk", detail = "toolsAndAuthOnly", limit = 1
        }, ct);
        var expected = profile.ReadOperations.Select(operation => operation[..operation.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal);
        var connected = CodexWire.Array(response, "data").Any(item =>
            CodexWire.String(item, "name") == "rateldesk" &&
            CodexWire.String(item, "runtimeStatus") == "connected" &&
            item.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Object &&
            expected.All(name => tools.TryGetProperty(name, out _)));
        if (!connected) throw new ChatConflictException("Codex could not initialize the selected read-only RatelDesk MCP connection.");
    }

    public Task RespondAsync(string sessionId, string requestId, ProviderPendingAction action, ProviderDecision decision, CancellationToken ct) =>
        throw new InvalidOperationException("This Codex profile does not enable interactive or write actions.");

    public Task CancelAsync(string sessionId, string requestId, CancellationToken ct)
    {
        var turnId = LastTurnId ?? throw new ChatConflictException("The Codex turn identity is not confirmed.");
        return server.CallAsync("turn/interrupt", new { threadId = sessionId, turnId }, ct);
    }

    public Task DeleteAsync(string sessionId, CancellationToken ct) =>
        server.CallAsync("thread/archive", new { threadId = sessionId }, ct);

    public async Task<ProviderSessionHistory> RetrieveAsync(string sessionId, CancellationToken ct)
    {
        var thread = (await server.CallAsync("thread/read", new { threadId = sessionId }, ct)).GetProperty("thread");
        if (Id(thread, "id") != sessionId) throw new InvalidDataException("Codex returned a different thread identity.");
        var state = CodexWire.String(thread.GetProperty("status"), "type") switch
        {
            "idle" or "notLoaded" => "idle",
            "active" => "in_progress",
            "systemError" => "failed",
            _ => "unknown"
        };
        var items = new List<ProviderItem>();
        var turns = new List<ProviderTurn>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await server.CallAsync("thread/turns/list", new
            {
                threadId = sessionId, cursor, limit = 20, sortDirection = "desc", itemsView = "full"
            }, ct);
            foreach (var turn in CodexWire.Array(page, "data"))
                MapTurn(turn, turns, items);
            // Descending pages need only go back as far as this RatelDesk
            // message's durable binding. An unknown turn is never replayed.
            if (turns.Any(item => item.Id == LastTurnId) ||
                items.Any(item => item.Kind == ProviderItemKind.UserMessage && item.Id == messageId.ToString("D")))
                break;
            cursor = CodexWire.String(page, "nextCursor");
            if (cursor is not null && (cursors.Count >= 24 || !cursors.Add(cursor)))
                throw new InvalidDataException("Codex returned incomplete or invalid paged history.");
        } while (cursor is not null);
        return new(sessionId, state, items, turns, [], []);
    }

    internal static void MapTurn(JsonElement turn, List<ProviderTurn> turns, List<ProviderItem> items)
    {
        var id = Id(turn, "id");
        var status = CodexWire.String(turn, "status") switch
        {
            "completed" => "completed",
            "interrupted" => "interrupted",
            "failed" => "failed",
            "inProgress" => "in_progress",
            _ => "unknown"
        };
        turns.Add(new(id, null, status, Date(turn, "startedAt"), Date(turn, "completedAt"), null, null));
        foreach (var item in CodexWire.Array(turn, "items"))
        {
            var itemId = Id(item, "id");
            switch (CodexWire.String(item, "type"))
            {
                case "userMessage":
                    items.Add(new(CodexWire.String(item, "clientId") ?? itemId, id, null, ProviderItemKind.UserMessage, "completed",
                        string.Join("\n", CodexWire.Array(item, "content").Select(content => CodexWire.String(content, "text")).OfType<string>())));
                    break;
                case "agentMessage":
                    items.Add(new(itemId, id, null, ProviderItemKind.AssistantMessage, status == "in_progress" ? "in_progress" : "completed",
                        CodexWire.String(item, "text") ?? ""));
                    break;
                case "mcpToolCall" when CodexWire.String(item, "server") == "rateldesk":
                    var operation = item.TryGetProperty("arguments", out var args) ? CodexWire.String(args, "operation") : null;
                    var outcome = CodexWire.String(item, "status") switch
                    {
                        "completed" => "completed",
                        "failed" => "failed",
                        _ => "in_progress"
                    };
                    items.Add(new(itemId, id, null, ProviderItemKind.ToolCall, outcome, "", CodexWire.String(item, "tool"),
                        itemId, operation, "mcp"));
                    break;
            }
        }
    }

    private string ValidateThread(JsonElement response)
    {
        if (CodexWire.String(response, "model") != profile.Model || CodexWire.String(response, "modelProvider") != "openai" ||
            CodexWire.String(response, "approvalPolicy") != "never" ||
            CodexWire.String(response.GetProperty("sandbox"), "type") != "readOnly")
            throw new ChatConflictException("Codex did not accept the pinned model and read-only runtime policy.");
        return Id(response.GetProperty("thread"), "id");
    }

    private static string Id(JsonElement element, string property)
        => CodexWire.String(element, property) is { Length: > 0 and <= 128 } id ? id
            : throw new InvalidDataException("Codex returned an invalid runtime identity.");

    private static DateTimeOffset? Date(JsonElement element, string property)
        => element.TryGetProperty(property, out var timestamp) && timestamp.ValueKind == JsonValueKind.Number &&
            timestamp.TryGetInt64(out var seconds) && seconds is >= 0 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private sealed class Observation(ICodexAppServerRpc server, string threadId) : IChatProviderObservation
    {
        public async IAsyncEnumerable<ProviderStreamEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var notice in server.Notifications.ReadAllAsync(ct))
            {
                var value = notice.Parameters;
                if (CodexWire.String(value, "threadId") != threadId) continue;
                var turn = CodexWire.String(value, "turnId");
                switch (notice.Method)
                {
                    case "item/agentMessage/delta":
                        yield return new(ProviderStreamEventKind.TextDelta, CodexWire.String(value, "itemId"), turn, null,
                            CodexWire.String(value, "delta"));
                        break;
                    case "model/rerouted":
                        yield return new(ProviderStreamEventKind.ModelChanged, null, turn, null, null);
                        break;
                    case "turn/completed":
                    case "item/completed":
                        yield return new(ProviderStreamEventKind.HistoryChanged, null, turn, null, null);
                        break;
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
