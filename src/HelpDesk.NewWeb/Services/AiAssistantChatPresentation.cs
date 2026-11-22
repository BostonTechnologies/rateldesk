using Helpdesk.Shared.AiAssistant.Chat;
using System.Text.Json;

namespace HelpDesk.NewWeb.Services;

/// <summary>Rebuildable projection of the durable transcript, independent of arrival timing.</summary>
public static class AiAssistantChatPresentation
{
    public static IReadOnlyList<ChatTurn> Build(IEnumerable<AiAssistantChatEvent> events)
    {
        var turns = new List<ChatTurn>();
        ChatTurn? foreground = null;
        var byMessage = new Dictionary<(Guid Conversation, Guid Message), ChatTurn>();
        foreach (var item in events.DistinctBy(x => (x.ConversationId, x.Sequence)).OrderBy(x => x.ConversationId).ThenBy(x => x.Sequence))
        {
            if (item.Type == "operator")
            {
                foreground = new ChatTurn(item);
                turns.Add(foreground);
                if (item.ClientMessageId is { } message) byMessage[(item.ConversationId, message)] = foreground;
                continue;
            }

            var metadata = item.Type is "tool_call" or "tool_result" or "subagent" ? ChatOperation.Read(item.MetadataJson) : null;
            var completion = item.Type is "turn_completed" or "turn_cancelled" ? ReadCompletion(item.MetadataJson) : null;
            var associated = metadata?.Association?.OperatorMessageId ?? metadata?.ProviderEvent?.OperatorMessageId
                ?? completion?.OperatorMessageId ?? (item.Type == "assistant" ? ReadReplyMessage(item.MetadataJson) : null);
            var current = associated is { } parent && parent != Guid.Empty
                ? byMessage.GetValueOrDefault((item.ConversationId, parent)) : foreground;
            if (current is null || current.Operator.ConversationId != item.ConversationId) continue;
            if (metadata?.Child is { RunId: { } run } observation)
                current.ObserveChild(run, observation, item, metadata.Details?.ToolName == "check_agent_run");
            else if (metadata?.ProviderEvent?.SubagentId is { } child && item.Type == "subagent")
                current.ObserveChild(child, new(child, metadata.DisplayName, metadata.ProviderEvent.Status,
                    metadata.Details?.FailureCode), item);
            if (item.Type == "assistant") current.Responses.Add(item);
            else if (item.Type is "turn_completed" or "turn_cancelled")
            {
                if (completion is not null && completion.OperatorMessageId != current.Operator.ClientMessageId) continue;
                if (completion is { Version: 1 })
                {
                    current.NativeTurns.Add(completion);
                    foreach (var childId in completion.PendingChildRunIds ?? completion.OpenChildRunIds ?? [])
                        current.ObserveChild(childId, new(childId, null, null), item);
                }
                current.Completed = (completion?.PendingChildRunIds ?? completion?.OpenChildRunIds) is not { Length: > 0 }
                    && completion is not { ScheduledReports.Length: > 0 } and not { ScheduledWorkUnconfirmed: true }
                    && !current.HasPendingApproval;
                if (current.Completed && current == foreground) foreground = null;
            }
            else if (item.Type is "tool_call" or "tool_result")
            {
                var boundary = metadata?.Association?.AfterNativeTurnNumber ?? current.NativeTurns.LastOrDefault()?.TurnNumber;
                var identity = new ChatToolScope(metadata?.Association?.NativeTurnNumber, boundary,
                    metadata?.ProviderEvent?.TurnId, metadata?.ProviderEvent?.SubagentId);
                var operation = item.CallId is null ? null : current.Operations.Find(x => x.CallId == item.CallId && x.Scope == identity);
                if (operation is null)
                {
                    operation = new ChatOperation(item.CallId, item.Sequence) { Scope = identity };
                    current.Operations.Add(operation);
                }
                if (item.Type == "tool_call") operation.Call ??= item;
                else operation.Result ??= item;
            }
            else if (item.Type is "approval_request" or "approval_response" or "approval_closed" or "approval_executed" or "approval_denied" or "error" or "file" or "usage" or "processing_state" or "compaction" or "subagent")
                current.Activity.Add(item);
        }
        return turns;
    }

    private static Guid? ReadReplyMessage(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<ChatOutputAssociation>(json)?.OperatorMessageId; }
        catch (JsonException) { return null; }
    }

    private static ChatTurnCompletion? ReadCompletion(string? json)
    {
        if (json is null || json.Length > 65536) return null;
        try { return JsonSerializer.Deserialize<ChatTurnCompletion>(json); }
        catch (JsonException) { return null; }
    }
}

public sealed class ChatTurn(AiAssistantChatEvent message)
{
    public AiAssistantChatEvent Operator { get; } = message;
    public List<AiAssistantChatEvent> Responses { get; } = [];
    public List<ChatOperation> Operations { get; } = [];
    public List<AiAssistantChatEvent> Activity { get; } = [];
    public List<ChatTurnCompletion> NativeTurns { get; } = [];
    public Dictionary<string, ChatChildCard> Children { get; } = new(StringComparer.Ordinal);
    internal void ObserveChild(string id, ChatChildObservation update, AiAssistantChatEvent item, bool poll = false)
    {
        if (!Children.TryGetValue(id, out var card))
            Children[id] = card = new(id);
        card.Name = update.Name ?? card.Name;
        card.State = update.State ?? card.State;
        card.Summary = update.Summary ?? card.Summary;
        card.DurationMs = update.DurationMs ?? card.DurationMs;
        if (item.Type == "subagent") card.Diagnostics.Add(item);
        if (poll) card.PollResults.Add(item.Sequence);
    }
    public bool Completed { get; internal set; }
    public bool HasError => Activity.Any(x => x.Type == "error") || Operations.Any(x => x.Metadata?.Outcome == "failed")
        || NativeTurns.Any(x => x.TurnOutcome == "failed");
    public bool HasPendingApproval => Activity.Any(x => x.Type == "approval_request"
        && !Activity.Any(answer => answer.Type is ("approval_response" or "approval_closed") && answer.CallId == x.CallId));
    public bool InitiallyExpanded => !Completed || HasError || HasPendingApproval;
    public string Summary => $"{Operations.Count} operations"
        + (Operations.Count(x => x.Metadata?.ToolKind == "mcp") is > 0 and var mcp ? $" · {mcp} MCP" : "")
        + (Operations.Count(x => x.Metadata?.ToolKind == "skill") is > 0 and var skills ? $" · {skills} skills" : "")
        + (Operations.Sum(x => x.Metadata?.DurationMs ?? 0) is > 0 and var ms ? $" · {ms / 1000d:0.##}s tool time" : "");
}

public sealed class ChatChildCard(string id)
{
    public string Id { get; } = id;
    public string? Name { get; internal set; }
    public string? State { get; internal set; }
    public string? Summary { get; internal set; }
    public long? DurationMs { get; internal set; }
    public List<AiAssistantChatEvent> Diagnostics { get; } = [];
    public HashSet<long> PollResults { get; } = [];
}

public sealed record ChatToolScope(int? NativeTurn, int? AfterTurn, string? ProviderTurn, string? Child);

public sealed class ChatOperation(string? callId, long sequence)
{
    public ChatToolScope? Scope { get; init; }
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
