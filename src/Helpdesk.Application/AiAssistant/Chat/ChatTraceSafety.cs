using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.Shared.AiAssistant.Chat;

namespace Helpdesk.Application.AiAssistant.Chat;

/// <summary>Selected operator trace data. Never persists a native frame or exception body.</summary>
public static partial class ChatTraceSafety
{
    public static string? CallId(string? value) => value is { Length: > 0 and <= 128 } && CallIdentifier().IsMatch(value) ? value : null;
    public static ChatTraceDetails Extract(JsonElement frame)
    {
        DateTimeOffset? timestamp = null;
        if (frame.TryGetProperty("timestampMs", out var raw) && raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out var ms) && ms is >= 946684800000 and <= 4102444800000)
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        var measures = new Dictionary<string, JsonElement>();
        // Event-specific numeric/boolean diagnostics; no free-form error, path, thinking or subagent prose.
        foreach (var name in new[] { "inputTokens", "outputTokens", "totalTokens", "cachedInputTokens", "reasoningTokens", "contextWindowTokens", "usagePercent", "promptMs", "predictedPerSecond", "isProcessing", "processingStateRequired", "messagesBefore", "messagesAfter", "summarized", "toolResultsCleared", "preCompactionInputTokens", "keepCountUsed", "toolCountSub", "subAgentSuccess", "durationMs", "findingsCount" })
            if (frame.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False && value.GetRawText().Length < 32)
                measures[name] = value.Clone();
        // These exact strings belong to the authorized conversation, not general logs.
        // JSON, Markdown, plain text and malformed JSON are all valid tool output.
        // Keep the received bytes of each string; Razor renders/copies them as inert text.
        return new(Version: 2, SourceTimestampUtc: timestamp, Arguments: String(frame, "argumentsJson"),
            Result: String(frame, "result"), FailureCode: String(frame, "toolFailureCode"),
            Measures: measures.Count == 0 ? null : JsonSerializer.Serialize(measures), ToolName: String(frame, "toolName"));
    }

    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    [GeneratedRegex("\\A[A-Za-z0-9_.:/-]+\\z")]
    private static partial Regex CallIdentifier();
}
