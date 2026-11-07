using System.Text.Json;
using Helpdesk.Shared.AiAssistant.Chat;

namespace Helpdesk.Application.AiAssistant.Chat;

public static class ChatContinuationSafety
{
    public const int MaximumChildRuns = 128;
    public static readonly TimeSpan ObservationLimit = TimeSpan.FromHours(1);

    public static ChatTurnCompletion Parse(JsonElement output, Guid messageId, DateTimeOffset? deadline)
    {
        var number = ReadTurnNumber(output);
        var children = ReadChildren(output, number);
        return new(messageId, number, ReadOutcome(output), ReadQuietEnd(output), children, deadline, ChildSnapshotKnown: children is not null,
            PendingChildRunIds: children);
    }

    private static int? ReadTurnNumber(JsonElement output)
    {
        if (!output.TryGetProperty("turnNumber", out var ordinal) || ordinal.ValueKind == JsonValueKind.Null) return null;
        if (ordinal.ValueKind != JsonValueKind.Number || !ordinal.TryGetInt32(out var value) || value < 0)
            throw new InvalidOperationException("Invalid native turn ordinal.");
        return value;
    }

    private static string? ReadOutcome(JsonElement output)
    {
        if (!output.TryGetProperty("turnOutcome", out var outcome) || outcome.ValueKind == JsonValueKind.Null) return null;
        var value = outcome.ValueKind == JsonValueKind.String ? outcome.GetString() : null;
        return value is "completed" or "failed" or "skipped" ? value
            : throw new InvalidOperationException("Invalid native turn outcome.");
    }

    private static bool ReadQuietEnd(JsonElement output)
    {
        if (!output.TryGetProperty("quietEnd", out var quiet) || quiet.ValueKind == JsonValueKind.Null) return false;
        if (quiet.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("Invalid native quiet-turn flag.");
        return quiet.GetBoolean();
    }

    private static string[]? ReadChildren(JsonElement output, int? number)
    {
        if (!output.TryGetProperty("openChildRunIds", out var raw) || raw.ValueKind == JsonValueKind.Null) return null;
        if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() > MaximumChildRuns)
            throw new InvalidOperationException("Invalid or excessive native child runs.");
        var children = raw.EnumerateArray().Select(ReadChildId).ToArray();
        if (children.Distinct(StringComparer.Ordinal).Count() != children.Length)
            throw new InvalidOperationException("Duplicate native child run identity.");
        if (children.Length > 0 && number is null)
            throw new InvalidOperationException("Native child continuations require a turn ordinal.");
        return children;
    }

    public static ChatTurnCompletion? Read(string? json)
    {
        if (json is null || json.Length > 65536) return null;
        try
        {
            var value = JsonSerializer.Deserialize<ChatTurnCompletion>(json);
            return value is { Version: 1 } && value.OperatorMessageId != Guid.Empty ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static string ReadChildId(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 128 } id
            || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new InvalidOperationException("Invalid native child run identity.");
        return id;
    }
}
