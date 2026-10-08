namespace Helpdesk.Shared.ServiceLink;

/// <summary>Only allowlisted diagnostics cross the API and browser boundary. No peer text or addresses.</summary>
public sealed record ServiceLinkFailure(string Code, string Stage, string? CorrelationId)
{
    public const string NotificationEventType = "ServiceLink.OperationFailed";
    public string Message => MessageFor(Code);
    public string? ExistingAttemptId { get; init; }

    public static ServiceLinkFailure From(string? code, string? stage = null, string? correlationId = null, int statusCode = 400, string? existingAttemptId = null) =>
        new(NormalizeCode(code, statusCode), NormalizeStage(stage), NormalizeCorrelation(correlationId))
        { ExistingAttemptId = code == "relationship-already-exists" ? NormalizeAttemptId(existingAttemptId) : null };

    public static string? NormalizeAttemptId(string? value) => value is { Length: >= 16 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? value : null;

    public static string NormalizeStage(string? stage) => stage switch
    {
        "start" or "remote-review" or "remote-approve" or "callback" or "approve" or "continue" or
        "resume" or "cancel" or "revoke" or "rotate" or "test" or "status" or "configuration" => stage,
        _ => "request"
    };

    public static string? NormalizeCorrelation(string? value) => value is { Length: 32 } && value.All(Uri.IsHexDigit)
        ? value.ToLowerInvariant() : null;

    private static string NormalizeCode(string? code, int statusCode) => code switch
    {
        "network-policy-rejected" or "peer-unavailable" or "peer-timeout" or "unsupported-peer" or
        "invalid-organization" or "invalid-tenant" or "approval-required" or "not-authorized" or "expired" or "session-expired" or "form-expired" or
        "invalid-proof" or "upgrade-required" or "deployment-managed" or "profile-occupied" or
        "source-unavailable" or "service-unavailable" or "callback-required" or "grant-unavailable" or
        "service-link-conflict" or "relationship-already-exists" or "protected-state-unavailable" or "invalid-request" or "needs-attention" => code,
        "organization-disabled" or "organization-required" => "invalid-organization",
        "administrator-required" or "actor-mismatch" or "actor-session-mismatch" => "not-authorized",
        "attempt-expired" => "expired",
        "invalid-browser-session" or "invalid-session" or "browser-session-mismatch" or "session-mismatch" => "session-expired",
        "invalid-local-consent" or "invalid-browser-proof" or "invalid-browser-state" or "invalid-pairing-proof" => "invalid-proof",
        "unsupported-contract" or "service-link-unsupported" or "unsupported-client-authentication" => "upgrade-required",
        "deployment-owned-profile" => "deployment-managed",
        "profile-ownership-conflict" => "profile-occupied",
        "source-identity-unavailable" or "identity-unconfigured" => "source-unavailable",
        "service-link-unavailable" or "service-link-configuration-invalid" => "service-unavailable",
        "peer-operation-failed" => "peer-unavailable",
        _ => statusCode switch
        {
            401 or 403 => "not-authorized", 404 or 410 => "expired", 409 => "needs-attention",
            502 => "peer-unavailable", 504 => "peer-timeout", 503 => "service-unavailable", _ => "invalid-request"
        }
    };

    // Retain locally defined protocol codes for existing peer callers; presentation still uses From.
    public static string ProtocolCode(string? code, int statusCode) => code switch
    {
        "peer-operation-failed" or "commit-not-prepared" or "descriptor-binding-mismatch" or "invalid-incident-only-grant" or "invalid-reason-code" or "protocol-authority-unavailable" or "grant-not-authorized" or "identity-configuration-invalid" or "profile-semantic-conflict" or "profile-state-conflict" or "request-definition-required" or "source-identity-conflict" or
        "abort-conflict" or "abort-not-authorized" or "abort-recovery-pending" or "activation-not-proven" or
        "activation-pending" or "administrator-recovery-required" or "administrator-required" or "approval-required" or
        "attempt-conflict" or "attempt-expired" or "attempt-not-found" or "callback-not-authorized" or
        "callback-required" or "candidate-control-expired" or "candidate-control-restricted" or "commit-conflict" or
        "consent-conflict" or "credential-binding-mismatch" or "credential-revision-conflict" or "deployment-owned-profile" or
        "exchange-binding-mismatch" or "exchange-payload-conflict" or "expired-token-response" or "grant-binding-mismatch" or
        "grant-conflict" or "grant-expansion-rejected" or "grant-not-approved" or "grant-unavailable" or
        "handoff-unavailable" or "identity-configuration-drift" or "identity-revision-conflict" or "identity-unconfigured" or
        "invalid-abort" or "invalid-acknowledgement" or "invalid-browser-proof" or "invalid-browser-session" or
        "invalid-browser-state" or "invalid-correlation" or "invalid-customer" or "invalid-endpoint" or
        "invalid-grant" or "invalid-identifier" or "invalid-local-consent" or "invalid-metadata" or
        "invalid-operation-fields" or "invalid-pairing-proof" or "invalid-profile" or "invalid-resource-constraints" or
        "invalid-rotation-offer" or "invalid-rotation-phase" or "invalid-set" or "invalid-source-grant" or
        "invalid-source-identity" or "invalid-tenant" or "invalid-tenant-grant" or "invalid-token-response" or
        "invalid-verification-receipt" or "lifecycle-binding-conflict" or "link-not-authorized" or "link-not-found" or
        "local-grant-mismatch" or "operation-not-found" or "operation-payload-conflict" or "organization-disabled" or
        "origin-mismatch" or "peer-lifecycle-binding-mismatch" or "peer-redirect-rejected" or "peer-response-too-large" or
        "preparation-not-proven" or "profile-ownership-conflict" or "profile-revision-conflict" or "protocol-authority-expired" or
        "protocol-profile-conflict" or "protocol-scope-required" or "revocation-conflict" or "revocation-not-proven" or
        "revocation-pending" or "rotation-already-activated" or "rotation-binding-conflict" or "rotation-conflict" or
        "rotation-not-active" or "rotation-not-authorized" or "rotation-offer-expired" or "rotation-payload-conflict" or
        "rotation-recovery-pending" or "rotation-retirement-pending" or "rotation-switch-conflict" or "scope-not-authorized" or
        "service-link-configuration-invalid" or "service-link-unavailable" or "source-disabled" or "source-identity-mismatch" or
        "source-identity-unavailable" or "source-mapping-conflict" or "successor-activation-pending" or "successor-not-active" or
        "successor-not-verified" or "successor-verification-conflict" or "tenant-pair-mismatch" or "terminal-control-restricted" or
        "unsupported-client-authentication" or "unsupported-grant" or "unsupported-peer" or "upgrade-required" or
        "verification-not-authorized" or "verification-pending" => code!,
        _ => NormalizeCode(code, statusCode)
    };

    public static string MessageFor(string? code) => NormalizeCode(code, 400) switch
    {
        "network-policy-rejected" => "The peer address was blocked by this installation's network policy. Check the address and ask an administrator to review the permitted network configuration before retrying.",
        "peer-unavailable" => "The peer could not complete the request. Check the peer address and availability, then retry.",
        "peer-timeout" => "The peer request timed out. Check connectivity and retry the operation.",
        "invalid-organization" => "The selected organization is unavailable. Select an enabled organization you administer. Cancel an old unapproved setup with an invalid organization and start a fresh setup.",
        "invalid-tenant" => "The selected tenant is unavailable. Select a current tenant you administer and start a fresh setup.",
        "approval-required" => "This setup still needs explicit approval. Reopen it from the connection list and review its exact grants.",
        "not-authorized" => "Your current account cannot perform this operation. Sign in with the original approving account and check its current administration permissions.",
        "expired" => "This setup has expired. Start a fresh setup and approve its exact grants again.",
        "session-expired" or "form-expired" or "invalid-proof" => "The approval session is no longer valid. Sign in again and reopen the setup from the connection list, or start a fresh setup.",
        "unsupported-peer" => "The selected address did not identify a distinct compatible peer. Check the entered peer address and its advertised installation identity before retrying.",
        "upgrade-required" => "The peer does not support the required connection contract. Upgrade the peer before retrying guided setup.",
        "deployment-managed" => "This connection is managed by deployment configuration. Ask its administrator to update that configuration.",
        "profile-occupied" => "The connection profile is already owned by another link. Review existing connections before starting again.",
        "relationship-already-exists" => "This organization and peer tenant already have an approved connection or setup. Open that existing setup to continue it, or cancel this duplicate. Disconnect the existing connection before replacing its consent.",
        "source-unavailable" => "The producer identity is unavailable. Complete the local producer configuration before retrying.",
        "service-unavailable" => "The installation's connection configuration is unavailable. Review its enabled state and canonical addresses before retrying.",
        "callback-required" => "Task invocation requires explicit consent to the task callback grant. Approve that optional grant or leave task access off.",
        "grant-unavailable" => "The approved grant is unavailable. Refresh the connection and review its current permissions.",
        "protected-state-unavailable" => "The protected setup state is unavailable. Ask an administrator to restore the shared protection keys.",
        "service-link-conflict" or "needs-attention" => "The setup state changed. Refresh the connection list and use the action offered for the current state.",
        _ => "The setup request is invalid. Check the peer address and local selection, then retry setup."
    };
}
