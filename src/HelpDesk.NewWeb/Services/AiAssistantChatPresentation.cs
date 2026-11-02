using Helpdesk.Shared.AiAssistant.Chat;
using System.Text.Json;

namespace HelpDesk.NewWeb.Services;

/// <summary>Rebuildable projection of the durable transcript, independent of arrival timing.</summary>
public static class AiAssistantChatPresentation
{
    public static IReadOnlyList<ChatTurn> Build(IEnumerable<AiAssistantChatEvent> events)
    {
        var turns = new List<ChatTurn>();
        ChatTurn? current = null;
        foreach (var item in events.DistinctBy(x => (x.ConversationId, x.Sequence)).OrderBy(x => x.ConversationId).ThenBy(x => x.Sequence))
        {
            if (item.Type == "operator")
            {
                current = new ChatTurn(item);
                turns.Add(current);
                continue;
            }

            if (current is null || current.Operator.ConversationId != item.ConversationId) continue;
            if (item.Type == "assistant") current.Responses.Add(item);
            else if (item.Type == "turn_completed")
            {
                current.Completed = true;
                current = null;
            }
            else if (item.Type is "tool_call" or "tool_result")
            {
                var operation = item.CallId is null ? null : current.Operations.Find(x => x.CallId == item.CallId);
                if (operation is null)
                {
                    operation = new ChatOperation(item.CallId, item.Sequence);
                    current.Operations.Add(operation);
                }
                if (item.Type == "tool_call") operation.Call = item;
                else operation.Result = item;
            }
            else if (item.Type is "approval_request" or "approval_response" or "error" or "file" or "usage" or "processing_state" or "compaction" or "subagent")
                current.Activity.Add(item);
        }
        return turns;
    }
}

public sealed class ChatTurn(AiAssistantChatEvent message)
{
    public AiAssistantChatEvent Operator { get; } = message;
    public List<AiAssistantChatEvent> Responses { get; } = [];
    public List<ChatOperation> Operations { get; } = [];
    public List<AiAssistantChatEvent> Activity { get; } = [];
    public bool Completed { get; internal set; }
    public bool HasError => Activity.Any(x => x.Type == "error") || Operations.Any(x => x.Metadata?.Outcome == "failed");
    public bool HasPendingApproval => Activity.Any(x => x.Type == "approval_request"
        && !Activity.Any(answer => answer.Type == "approval_response" && answer.CallId == x.CallId));
    public bool InitiallyExpanded => !Completed || HasError || HasPendingApproval;
    public string Summary => $"{Operations.Count} operations"
        + (Operations.Count(x => x.Metadata?.ToolKind == "mcp") is > 0 and var mcp ? $" · {mcp} MCP" : "")
        + (Operations.Count(x => x.Metadata?.ToolKind == "skill") is > 0 and var skills ? $" · {skills} skills" : "")
        + (Operations.Sum(x => x.Metadata?.DurationMs ?? 0) is > 0 and var ms ? $" · {ms / 1000d:0.##}s tool time" : "");
}

public sealed class ChatOperation(string? callId, long sequence)
{
    public string? CallId { get; } = callId;
    public long Sequence { get; } = sequence;
    public AiAssistantChatEvent? Call { get; internal set; }
    public AiAssistantChatEvent? Result { get; internal set; }
    public ChatActivityMetadata? Metadata
    {
        get
        {
            var call = Read(Call?.MetadataJson);
            var result = Read(Result?.MetadataJson);
            return call is null ? result : call with
            {
                DurationMs = result?.DurationMs,
                Outcome = result?.Outcome ?? (Result is null ? "running" : "completed")
            };
        }
    }

    public static ChatActivityMetadata? Read(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<ChatActivityMetadata>(json); }
        catch (JsonException) { return null; }
    }
}
