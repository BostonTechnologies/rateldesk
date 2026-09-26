using System.Text.RegularExpressions;

namespace Helpdesk.Infrastructure.Orchestration;

internal static partial class IntegrationErrorSafety
{
    public static string ProviderMessage(string? value, int maximumLength = 500, params string?[] secrets)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "provider returned no error details";

        var sanitized = value.Trim();
        foreach (var secret in secrets.Where(secret => !string.IsNullOrWhiteSpace(secret)))
            sanitized = sanitized.Replace(secret!, "[redacted]", StringComparison.Ordinal);

        sanitized = BearerToken().Replace(sanitized, "Bearer [redacted]");
        sanitized = SensitiveAssignment().Replace(sanitized, match => $"{match.Groups["prefix"].Value}[redacted]");
        return sanitized.Length <= maximumLength ? sanitized : sanitized[..maximumLength];
    }

    public static string ProviderCode(string? value, params string?[] secrets)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "provider returned no error details";

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(value);
            foreach (var name in new[] { "error", "error_code", "code" })
            {
                if (document.RootElement.TryGetProperty(name, out var element) && element.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var code = element.GetString();
                    if (!string.IsNullOrWhiteSpace(code))
                        return ProviderMessage(code, 128, secrets);
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return "provider returned an invalid error response";
        }

        return "provider rejected the request";
    }

    [GeneratedRegex(@"(?i)\bBearer\s+[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(?i)(?<prefix>\b(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|device[_-]?token|api[_-]?key|password|authorization)\b\s*[:=]\s*(?:Bearer\s+)?)(?:""[^""]*""|'[^']*'|[^\s,;}]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignment();
}
