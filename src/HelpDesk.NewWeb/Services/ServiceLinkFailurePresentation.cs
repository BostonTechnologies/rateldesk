using System.Text.Json;
using Helpdesk.Shared.ServiceLink;

namespace HelpDesk.NewWeb.Services;

internal static class ServiceLinkFailurePresentation
{
    public static async Task<ServiceLinkFailure> ReadAsync(HttpResponseMessage response, string stage)
    {
        var fallback = ServiceLinkFailure.From(null, stage, statusCode: (int)response.StatusCode);
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[4097];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length));
                if (read == 0) break;
                length += read;
            }
            if (length == buffer.Length) return fallback;
            using var body = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (body.RootElement.ValueKind != JsonValueKind.Object) return fallback;
            string? Value(string name) => body.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return ServiceLinkFailure.From(Value("code") ?? Value("error"), Value("stage") ?? stage, Value("correlationId"), (int)response.StatusCode);
        }
        catch (Exception exception) when (exception is JsonException or IOException) { return fallback; }
    }

    public static string Message(ServiceLinkFailure failure) => failure.Message + " Stage: " + failure.Stage + "." +
        (failure.CorrelationId is null ? "" : " Reference: " + failure.CorrelationId + ".");
}
