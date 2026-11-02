using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.Shared.AiAssistant.Chat;

namespace Helpdesk.Application.AiAssistant.Chat;

/// <summary>Selected, bounded display data only. Never persists a native frame or exception body.</summary>
public static partial class ChatTraceSafety
{
    public static string? CallId(string? value) => value is { Length: > 0 and <= 128 } && CallIdentifier().IsMatch(value) ? value : null;
    public static ChatTraceDetails Extract(JsonElement frame)
    {
        var redacted = false; var truncated = false;
        var arguments = Payload(String(frame, "argumentsJson"), ref redacted, ref truncated);
        var result = Payload(String(frame, "result"), ref redacted, ref truncated);
        DateTimeOffset? timestamp = null;
        if (frame.TryGetProperty("timestampMs", out var raw) && raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out var ms) && ms is >= 946684800000 and <= 4102444800000)
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        var failure = String(frame, "toolFailureCode");
        if (failure is not null) failure = ChatOutputSafety.Identifier(failure);
        var measures = new Dictionary<string, JsonElement>();
        // Event-specific numeric/boolean diagnostics; no free-form error, path, thinking or subagent prose.
        foreach (var name in new[] { "inputTokens", "outputTokens", "totalTokens", "cachedInputTokens", "reasoningTokens", "contextWindowTokens", "usagePercent", "promptMs", "predictedPerSecond", "isProcessing", "processingStateRequired", "messagesBefore", "messagesAfter", "summarized", "toolResultsCleared", "preCompactionInputTokens", "keepCountUsed", "toolCountSub", "subAgentSuccess", "durationMs", "findingsCount" })
            if (frame.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False && value.GetRawText().Length < 32)
                measures[name] = value.Clone();
        return new(SourceTimestampUtc: timestamp, Arguments: arguments, Result: result, FailureCode: failure,
            Redacted: redacted, Truncated: truncated, Measures: measures.Count == 0 ? null : JsonSerializer.Serialize(measures));
    }

    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static string? Payload(string? text, ref bool redacted, ref bool truncated)
    {
        if (text is null) return null;
        if (text.Length > 32768) { truncated = true; return "[Payload omitted: size limit]"; }
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 });
            var flags = new Flags();
            var value = Clean(document.RootElement, flags, 0);
            var formatted = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            redacted |= flags.Redacted; truncated |= flags.Truncated;
            if (formatted.Length <= 8192) return formatted;
            truncated = true;
            return formatted[..8192] + "\n[Truncated]";
        }
        catch (JsonException)
        {
            // Unknown plain output may contain secrets without labels. Fail closed, including malformed JSON.
            redacted = true;
            return "[Unstructured payload omitted for privacy]";
        }
    }

    private sealed class Flags { public bool Redacted; public bool Truncated; public int Items; }
    private static object? Clean(JsonElement value, Flags flags, int depth)
    {
        if (++flags.Items > 256 || depth > 8) { flags.Truncated = true; return "[Truncated]"; }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var result = new Dictionary<string, object?>();
                foreach (var field in value.EnumerateObject().Take(65))
                {
                    if (result.Count >= 64) { flags.Truncated = true; break; }
                    var normalizedKey = new string(field.Name.Where(char.IsLetterOrDigit).ToArray());
                    if (SensitiveKey().IsMatch(normalizedKey)) { flags.Redacted = true; result[SafeText(field.Name, flags)] = "[Redacted]"; }
                    else result[SafeText(field.Name, flags)] = Clean(field.Value, flags, depth + 1);
                }
                return result;
            case JsonValueKind.Array:
                if (value.GetArrayLength() > 32) flags.Truncated = true;
                return value.EnumerateArray().Take(32).Select(item => Clean(item, flags, depth + 1)).ToArray();
            case JsonValueKind.String: return SafeText(value.GetString()!, flags);
            case JsonValueKind.Number: return value.GetRawText().Length <= 32 ? value.Clone() : "[Number omitted]";
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return null;
        }
    }

    private static string SafeText(string text, Flags flags)
    {
        if (SecretValue().IsMatch(text) || text.Contains("-----BEGIN", StringComparison.Ordinal)
            || text.Contains("://", StringComparison.Ordinal) && (text.Contains('@') || text.Contains('?'))
            || text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
        { flags.Redacted = true; return "[Redacted]"; }
        if (text.Length > 1024) { flags.Truncated = true; return text[..1024] + " [Truncated]"; }
        return text;
    }
    [GeneratedRegex("password|passwd|secret|token|auth|credential|cookie|connectionstring|key|certificate|environment|headers|^env$|^pwd$", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveKey();
    [GeneratedRegex("bearer\\s|basic\\s|eyJ[A-Za-z0-9_-]+\\.|(?:sk-|gh[pousr]_|github_pat_|AKIA)[A-Za-z0-9_-]+|(?:password|passwd|secret|token|api.?key|authorization)(?:\\s*[:=]|\\s+)|(?:^|\\s)(?:-u|--user|--header|-p)\\s", RegexOptions.IgnoreCase)]
    private static partial Regex SecretValue();
    [GeneratedRegex("\\A[A-Za-z0-9_.:/-]+\\z")]
    private static partial Regex CallIdentifier();
}
