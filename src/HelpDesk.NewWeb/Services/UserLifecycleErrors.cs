using System.Text.Json;

namespace HelpDesk.NewWeb.Services;

public static class UserLifecycleErrors
{
    public static async Task<string> ReadAsync(HttpResponseMessage response, string fallback)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return $"{fallback} (HTTP {(int)response.StatusCode})";
            foreach (var field in new[] { "message", "detail" })
                if (document.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: > 0 and <= 500 } message && !message.Contains('<') && !message.Any(char.IsControl))
                    return message;
        }
        catch (JsonException) { return $"{fallback} (HTTP {(int)response.StatusCode})"; }
        return $"{fallback} (HTTP {(int)response.StatusCode})";
    }
}
