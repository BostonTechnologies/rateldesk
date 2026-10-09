namespace Helpdesk.Shared.Pairing;

/// <summary>Bounded, non-secret receiver readiness evidence shared by both products.</summary>
public sealed record PairingReadinessDiagnostic(string Stage, string Code, string Reference, int? HttpStatus = null);

public static class PairingReadinessDiagnostics
{
    private static readonly HashSet<string> Stages = new(StringComparer.Ordinal)
    {
        "readiness-invalidation", "paired-binding", "receipt-token", "receiver-capabilities",
        "target-token", "receiver-targets", "create-token", "readiness-persistence"
    };
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal)
    {
        "connector-changed-during-readiness", "connector-busy", "receiver-current-authority-denied",
        "receiver-binding-unverified", "receiver-endpoint-mismatch", "receiver-endpoint-outside-approved-api-base",
        "receiver-field-unverified", "receiver-number-unverified", "receiver-boolean-unverified",
        "duplicate-receiver-json-field", "receiver-response-too-large", "receiver-read-headers-unverified",
        "receiver-semantic-peer-unverified", "receiver-api-base-unverified", "receiver-observation-time-invalid",
        "receiver-replay-guarantees-unverified", "receiver-authentication-mode-unverified", "receiver-target-unverified",
        "receiver-identity-unverified", "receiver-identity-mismatch", "receiver-authentication-rejected",
        "receiver-current-grant-rejected", "receiver-target-rejected", "receiver-rate-limited", "receiver-redirect-refused",
        "receiver-capability-unavailable", "receiver-target-validation-unavailable", "receiver-json-unverified",
        "receiver-response-interrupted", "receiver-transport-unavailable", "receiver-dns-unavailable",
        "receiver-tls-unverified", "receiver-readiness-unverified", "receiver-readiness-timeout",
        "receiver-credential-unavailable", "receiver-deployment-policy-invalid", "receiver-deployment-origin-denied",
        "receiver-operation-endpoint-invalid", "receiver-approved-api-base-missing", "receiver-address-blocked",
        "business-access-rejected", "business-credential-mismatch", "token-response-invalid",
        "connection-reference-invalid", "connection-revoked", "pair-revoked", "connection-authority-removed",
        "connection-credential-mismatch", "connection-changed", "receiver-persistence-unavailable"
    };

    public static bool IsAllowedReason(string? code) => code is not null && Reasons.Contains(code);
    public static bool IsValid(PairingReadinessDiagnostic? value) => value is not null &&
        Stages.Contains(value.Stage ?? "") && IsAllowedReason(value.Code) &&
        value.Reference is { Length: 32 } && Guid.TryParseExact(value.Reference, "N", out var reference) &&
        reference != Guid.Empty && reference.ToString("N") == value.Reference &&
        (value.HttpStatus is null or >= 100 and <= 599);

    public static string NewReference(string? current)
    {
        if (current?.StartsWith("corr-", StringComparison.Ordinal) == true) current = current[5..];
        return Guid.TryParse(current, out var reference) && reference != Guid.Empty
            ? reference.ToString("N") : Guid.NewGuid().ToString("N");
    }

    public static string Describe(PairingReadinessDiagnostic value)
    {
        if (!IsValid(value)) return "Receiver readiness could not be verified. Check the server reference, then retry this connection.";
        var stage = value.Stage switch
        {
            "readiness-invalidation" => "previous readiness invalidation",
            "paired-binding" => "current connection authority",
            "receipt-token" => "receipt access token",
            "receiver-capabilities" => "receiver capabilities",
            "target-token" => "target access token",
            "receiver-targets" => "incident target validation",
            "create-token" => "incident creation access token",
            _ => "readiness persistence"
        };
        var action = value.Code switch
        {
            "receiver-endpoint-mismatch" or "receiver-endpoint-outside-approved-api-base" or "receiver-operation-endpoint-invalid" or
                "receiver-approved-api-base-missing" => "The receiver request failed endpoint validation. Check the linked server reference and paired API configuration, then retry Save after the service is corrected.",
            "receiver-target-rejected" or "receiver-target-unverified" => "Review the selected organization, customer and current permissions, then retry Save.",
            "business-access-rejected" or "business-credential-mismatch" or "receiver-authentication-rejected" or
                "receiver-current-grant-rejected" or "receiver-current-authority-denied" or "connection-authority-removed" =>
                "Check the retained administrator's current permissions and this connection's credentials, then retry Save.",
            "receiver-tls-unverified" => "Check the API certificate and server trust, then retry Save.",
            "receiver-dns-unavailable" or "receiver-transport-unavailable" or "receiver-response-interrupted" or
                "receiver-readiness-timeout" or "receiver-capability-unavailable" or "receiver-target-validation-unavailable" =>
                "Check the API service and network, then retry this same connection.",
            _ => "Check the linked server reference, correct the reported receiver failure, then retry this same connection."
        };
        return $"Receiver readiness failed at {stage} ({value.Code}{(value.HttpStatus is { } status ? $", HTTP {status}" : "")}). {action}";
    }
}
