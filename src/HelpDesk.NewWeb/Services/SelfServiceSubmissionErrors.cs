using System.Net;
using System.Text.Json;

namespace Helpdesk.NewWeb.Services;

public static class SelfServiceSubmissionErrors
{
    public static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your session has expired. Sign in again, then retry this submission.",
            HttpStatusCode.Forbidden => "You cannot submit this request with the current requester and organization. Ask an administrator to review your access.",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "Check the required fields and selected values, then retry.",
            HttpStatusCode.Conflict => "The request context changed. Review the requester and organization, or check My Requests before retrying.",
            _ => "We could not confirm the submission. Your entered values are retained. Retry this submission safely or check My Requests."
        };
        string? reference = null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[8193];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
        }
        if (length is > 0 and <= 8192)
        {
            try
            {
                using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var code = root.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    message = code switch
                    {
                        "requester_unavailable" => "Your requester profile is unavailable. Ask an administrator to review your enabled account and organization membership.",
                        "request_organization_required" or "request_organization_denied" => "Your account needs an eligible organization with self-service access. Ask an administrator to review its membership.",
                        "request_form_denied" => "This form is not available to your account. Choose an eligible form or ask an administrator to review its audience.",
                        "request_organization_mismatch" => "This form's automation uses a different organization. Choose a form available in the displayed requester organization, or ask an administrator to review the saved connection mapping.",
                        "submission_already_created" => "This submission already created a request. Open My Requests to view its progress before starting another request.",
                        _ => message
                    };
                    if (root.TryGetProperty("traceId", out var trace) && trace.ValueKind == JsonValueKind.String &&
                        trace.GetString() is { Length: > 0 and <= 80 } candidate &&
                        candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.'))
                        reference = candidate;
                }
            }
            catch (JsonException)
            {
                // Malformed upstream errors use the status-based message above.
                reference = null;
            }
        }
        return $"{message} (HTTP {(int)response.StatusCode})" + (reference is null ? string.Empty : $" Reference: {reference}");
    }
}
