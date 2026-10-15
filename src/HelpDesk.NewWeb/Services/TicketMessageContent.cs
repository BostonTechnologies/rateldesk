using System.Net.Http.Json;

namespace HelpDesk.NewWeb.Services;

/// <summary>Content contract audit: only incidents have original email/peek; request and change descriptions are plain text.</summary>
public sealed record TicketMessageContent(string? Html, string? Text, string? From = null, DateTimeOffset? ReceivedUtc = null)
{
    public static async Task<TicketMessageContent?> LoadAsync(HttpClient client, string ticketType, string ticketId, CancellationToken cancellationToken)
    {
        var id = Uri.EscapeDataString(ticketId);
        switch (ticketType.ToLowerInvariant())
        {
            case "incidents":
                var peek = await client.GetFromJsonAsync<Peek>($"api/v1/incidents/{id}/peek", cancellationToken);
                return peek is null ? null : new(peek.Html, peek.Text ?? peek.SnippetText, peek.From, peek.ReceivedUtc);
            case "requests":
            case "changes":
                // These endpoints retain their existing per-record access checks. Neither
                // ticket type has an incident email record. Changes normally use the template editor.
                var detail = await client.GetFromJsonAsync<DescriptionContent>($"api/v1/{ticketType.ToLowerInvariant()}/{id}", cancellationToken);
                return detail is null ? null : new(null, detail.Description);
            default:
                throw new NotSupportedException("Unsupported ticket content type.");
        }
    }

    private sealed record Peek(string? Html, string? Text, string? SnippetText, string? From, DateTimeOffset? ReceivedUtc);
    private sealed record DescriptionContent(string? Description);
}
