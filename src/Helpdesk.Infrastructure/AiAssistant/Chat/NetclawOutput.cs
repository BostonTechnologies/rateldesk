using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Shared.AiAssistant.Chat;

namespace Helpdesk.Infrastructure.AiAssistant.Chat;

internal sealed class NetclawOutput
{
    private readonly JsonElement frame;

    public NetclawOutput(JsonElement output)
    {
        frame = output.Clone();
        Type = String(frame, "type");
        SessionId = String(frame, "sessionId");
    }

    public string? Type { get; }
    public string? SessionId { get; }
    public string? Text => String(frame, "text");
    public string? ToolName => String(frame, "toolName");
    public string? CallId => ChatTraceSafety.CallId(String(frame, "callId"));
    public string? ToolArguments => String(frame, "argumentsJson");
    public string? ToolResult => String(frame, "result");
    public bool ToolFailed => String(frame, "toolFailureCode") is not null;
    public bool HasReminderSource => frame.TryGetProperty("sourceReminderId", out var source) && source.ValueKind != JsonValueKind.Null;
    public string? ReminderFireKey => String(frame, "sourceReminderId");
    public string? CloseKind => String(frame, "interactionCloseKind");
    public string AgentName => ChatOutputSafety.Identifier(String(frame, "agentName"));
    public string FileName => ChatOutputSafety.FileName(String(frame, "fileName"));
    public string MimeType => ChatOutputSafety.MimeType(String(frame, "mimeType"));
    public ChatTraceDetails Trace => ChatTraceSafety.Extract(frame);
    public string ErrorCategory => String(frame, "errorCategory") switch
    {
        "ProviderFailure" => "ProviderFailure",
        "ToolFailure" => "ToolFailure",
        "SessionFailure" => "SessionFailure",
        _ => "UnknownError"
    };
    public string? ErrorCorrelation => Guid.TryParse(String(frame, "errorCorrelationId"), out var reference) ? reference.ToString("N") : null;
    public int? TurnOrdinal => frame.TryGetProperty("turnNumber", out var turn) &&
        turn.ValueKind == JsonValueKind.Number && turn.TryGetInt32(out var number) ? number : null;

    public ChatTurnCompletion Complete(Guid admittedMessage, DateTimeOffset? deadline) =>
        ChatContinuationSafety.Parse(frame, admittedMessage, deadline);

    public bool ProvesAdmission(Guid conversation, Guid message)
    {
        if (!frame.TryGetProperty("recentMessages", out var history) || history.ValueKind != JsonValueKind.Array) return false;
        var marker = ChatOutboundMessage.Marker(conversation, message);
        return history.EnumerateArray().Count(item =>
            String(item, "role") == "user" && String(item, "content")?.StartsWith(marker, StringComparison.Ordinal) == true) == 1;
    }

    public IReadOnlyList<ChatOption> Options()
    {
        if (!frame.TryGetProperty("interactionOptions", out var options) || options.ValueKind != JsonValueKind.Array) return [];
        return options.EnumerateArray().Select(item => String(item, "key"))
            .Where(key => key is "approve_once" or "approve_session" or "approve_always" or "approve_everywhere" or "deny")
            .Select(key => new ChatOption(key!, key switch
            {
                "approve_once" => "Allow once",
                "approve_session" => "Allow for this chat",
                "approve_always" => "Always allow here",
                "approve_everywhere" => "Always allow globally",
                _ => "Deny"
            })).ToArray();
    }

    public ChatActivityMetadata Activity(long? duration)
    {
        var started = Type == "tool_call";
        return ChatActivitySafety.Extract(ToolName, started ? ToolArguments : null,
            !started, ToolFailed, duration) with { Details = Trace };
    }

    public ChatChildObservation? ChildObservation()
    {
        if (Type == "subagent")
        {
            var phase = String(frame, "phase");
            var state = phase == "completed" ? String(frame, "subAgentOutcome") ?? phase : phase;
            var milliseconds = frame.TryGetProperty("durationMs", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var number)
                && number is > 0 and <= long.MaxValue ? (long?)number : null;
            return new(null, String(frame, "agentName"), state, String(frame, "subAgentOutcomeReason"), milliseconds);
        }
        if (Type != "tool_result" || ToolFailed || ToolName is not ("spawn_agent" or "check_agent_run")
            || ToolResult is not { Length: > 0 and <= 131072 } payload) return null;
        try
        {
            using var result = JsonDocument.Parse(payload);
            var json = result.RootElement;
            if (String(json, "run_id") is not { Length: > 0 and <= 256 } id) return null;
            if (ToolName == "spawn_agent" && String(json, "control_tool") != "check_agent_run") return null;
            var state = String(json, "state");
            if (state is null) return null;
            if (json.TryGetProperty("terminal", out var terminal) && terminal.ValueKind == JsonValueKind.True)
                state = String(json, "outcome") ?? state;
            return new(id, String(json, "agent"), state, String(json, "summary") ?? String(json, "reason"));
        }
        catch (JsonException) { return null; }
    }

    private static string? String(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
