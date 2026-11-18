using System.Globalization;
using System.Text.Json;
using Helpdesk.Shared.AiAssistant.Chat;

namespace Helpdesk.Infrastructure.AiAssistant.Chat;

internal static class NetclawScheduledReports
{
    public const int MaximumReports = 32;

    public static bool RequestsCurrentSession(string? arguments)
    {
        if (arguments is not { Length: > 0 and <= 8192 }) return false;
        try
        {
            using var json = JsonDocument.Parse(arguments);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && (json.RootElement.TryGetProperty("DeliveryKind", out var delivery)
                    || json.RootElement.TryGetProperty("delivery_kind", out delivery))
                && delivery.ValueKind == JsonValueKind.String
                && string.Equals(delivery.GetString()?.Replace("_", "", StringComparison.Ordinal),
                    "currentsession", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    // NetClaw 0.28.2 exposes registration as the built-in tool receipt. Its
    // explicitly rendered UTC due time has second precision.
    public static ChatScheduledReport? ReadRegistration(string? result)
    {
        const string scheduled = "' scheduled. ";
        const string currentSession = " Delivery: reply to this session.";
        const string once = "Runs once. Next execution: ";
        if (result is not { Length: > 0 and <= 8192 } || !result.StartsWith("Reminder '", StringComparison.Ordinal))
            return null;

        var scheduleStart = result.LastIndexOf(scheduled, StringComparison.Ordinal);
        var deliveryStart = result.LastIndexOf(currentSession, StringComparison.Ordinal);
        var idStart = result.LastIndexOf(" ID: ", StringComparison.Ordinal);
        if (scheduleStart < 0 || deliveryStart < scheduleStart + scheduled.Length || idStart < deliveryStart + currentSession.Length)
            return null;
        var deliveryEnd = deliveryStart + currentSession.Length;
        if (idStart != deliveryEnd && !result.AsSpan(deliveryEnd, idStart - deliveryEnd).StartsWith(" Expires: ", StringComparison.Ordinal))
            return null;
        var id = result[(idStart + 5)..];
        if (!IsReminderId(id)) return null;

        var schedule = result.AsSpan(scheduleStart + scheduled.Length, deliveryStart - scheduleStart - scheduled.Length);
        if (!schedule.StartsWith("Runs ", StringComparison.Ordinal)) return null;
        if (!schedule.StartsWith(once, StringComparison.Ordinal))
            return new(id, Recurring: true, DueAtUtc: null);

        const string oneShotSuffix = "). The reminder deletes itself after it runs or after you cancel it.";
        var timeStart = schedule.LastIndexOf(" (", StringComparison.Ordinal);
        if (timeStart < once.Length || timeStart + 2 >= schedule.Length - oneShotSuffix.Length
            || !schedule.EndsWith(oneShotSuffix, StringComparison.Ordinal)
            || !DateTimeOffset.TryParseExact(schedule[(timeStart + 2)..^oneShotSuffix.Length],
                "yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var due))
            return new(id, Recurring: false, DueAtUtc: null, Unconfirmed: true);
        return new(id, Recurring: false, due);
    }

    public static bool TryReadFireKey(string? key, out string reminderId, out long fireAt)
    {
        reminderId = "";
        fireAt = 0;
        if (key is not { Length: > 0 and <= 66 }) return false;
        var separator = key.IndexOf(':', StringComparison.Ordinal);
        if (separator < 1 || separator + 1 >= key.Length || key[separator + 1] == '0'
            || !long.TryParse(key.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out fireAt)
            || fireAt is <= 0 or > 253402300799999 || !IsReminderId(key[..separator]))
            return false;
        reminderId = key[..separator];
        return true;
    }

    private static bool IsReminderId(string id) => id is { Length: > 0 and <= 50 }
        && id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
