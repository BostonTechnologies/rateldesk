using System.Net;
using System.Text.Json.Nodes;

namespace Helpdesk.Mcp.Tools;

internal static class OperatorSecretPolicy
{
    private static readonly HashSet<string> SecretFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "appPassword", "clientSecret", "smtpPassword", "deviceToken", "inboundSecret",
        "authorization", "accessToken", "refreshToken", "activationToken", "plainTextKey", "pairingCode",
        "signingSecret", "legacyOwnershipReviewToken", "reviewToken"
    };
    private static readonly HashSet<string> NoOneTimeFields = new(StringComparer.OrdinalIgnoreCase);

    public static HelpdeskToolResponse SanitizeResponse(OperatorOperation operation, JsonObject request, HelpdeskToolResponse response)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        var pairingCodeResult = operation.Tool == "helpdesk_connections" && operation.Operation == "generate_pairing_code";
        Collect(request, secrets);
        Collect(response.Data, secrets, pairingCodeResult);
        Collect(response.Evidence, secrets, pairingCodeResult);
        var replacements = secrets.OrderByDescending(secret => secret.Length).ToArray();
        string Clean(string value) => RedactText(value, replacements);
        return response with
        {
            Summary = Clean(response.Summary),
            Data = Sanitize(operation, response.Data, response.Success, replacements, pairingCodeResult),
            Evidence = Redact(response.Evidence, replacements, NoOneTimeFields, pairingCodeResult),
            CorrelationId = response.CorrelationId is null ? null : Clean(response.CorrelationId),
            AffectedIds = response.AffectedIds?.Select(Clean).ToArray(),
            NextActions = response.NextActions?.Select(Clean).ToArray(),
            Confirmation = response.Confirmation is null ? null : response.Confirmation with
            {
                Operation = Clean(response.Confirmation.Operation),
                AffectedIds = response.Confirmation.AffectedIds.Select(Clean).ToArray()
            },
            Failure = response.Failure is null ? null : response.Failure with
            {
                Code = Clean(response.Failure.Code),
                AllowedOperations = response.Failure.AllowedOperations?.Select(Clean).ToArray()
            }
        };
    }

    private static JsonNode? Sanitize(OperatorOperation operation, JsonNode? response, bool success,
        IReadOnlyList<string> secrets, bool pairingCodeResult)
    {
        if (response is null) return null;
        var allowedFields = success ? operation.SensitiveResultFields.ToHashSet(StringComparer.OrdinalIgnoreCase) : NoOneTimeFields;
        var result = Redact(response, secrets, allowedFields, pairingCodeResult);
        if (allowedFields.Count > 0 && result is JsonObject data)
        {
            data["sensitiveResult"] = true;
            data["sensitiveResultHandling"] = "Newly issued one-time activation, ingestion or pairing credential. Deliver only through the authorized private workflow; exclude this value from campaign evidence, public issues and diagnostic logs.";
        }
        return result;
    }

    private static void Collect(JsonNode? node, ISet<string> secrets, bool pairingCodeResult = false)
    {
        if (node is JsonObject obj)
            foreach (var property in obj)
            {
                if (IsSecretField(property.Key, pairingCodeResult) && property.Value is JsonValue value &&
                    value.TryGetValue<string>(out var secret) && !string.IsNullOrEmpty(secret))
                {
                    secrets.Add(secret);
                    secrets.Add(Uri.EscapeDataString(secret));
                    secrets.Add(WebUtility.UrlEncode(secret));
                }
                else Collect(property.Value, secrets, pairingCodeResult);
            }
        else if (node is JsonArray array) foreach (var child in array) Collect(child, secrets, pairingCodeResult);
    }

    private static bool IsSecretField(string name, bool pairingCodeResult) =>
        SecretFields.Contains(name) || pairingCodeResult && name.Equals("code", StringComparison.OrdinalIgnoreCase);

    private static JsonNode? Redact(JsonNode? node, IReadOnlyList<string> secrets, IReadOnlySet<string> allowedRootFields, bool pairingCodeResult = false)
    {
        if (node is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var property in obj)
            {
                if (property.Value is not null && IsSecretField(property.Key, pairingCodeResult))
                {
                    // Only the named successful root scalar is a delivery. Its
                    // value is still scrubbed from every sibling, link and envelope.
                    copy[property.Key] = allowedRootFields.Contains(property.Key) && property.Value is JsonValue value && value.TryGetValue<string>(out _)
                        ? property.Value.DeepClone() : JsonValue.Create("[redacted]");
                }
                else copy[property.Key] = Redact(property.Value, secrets, NoOneTimeFields, pairingCodeResult);
            }
            return copy;
        }
        if (node is JsonArray array) return new JsonArray(array.Select(child => Redact(child, secrets, NoOneTimeFields, pairingCodeResult)).ToArray());
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return JsonValue.Create(RedactText(text, secrets));
        return node?.DeepClone();
    }

    private static string RedactText(string value, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets) value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return value;
    }
}
