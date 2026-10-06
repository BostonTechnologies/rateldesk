using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;

namespace Helpdesk.Shared.ServiceLink;

/// <summary>
/// The frozen per-operation wire payload. The nullable lifecycle input union is
/// never itself a wire shape: unrelated union members must not change its hash.
/// Endpoint state/authority validators additionally enforce values and presence.
/// </summary>
public static class ServiceLinkLifecycleProjection
{
    public static Dictionary<string, object?> Build(string kind, ServiceLinkLifecycleRequest request, bool normalizeCredentialScopes = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["contract"] = request.Contract,
            ["operation_id"] = request.OperationId,
            ["link_id"] = request.LinkId,
            ["link_revision"] = request.LinkRevision,
            ["grant_hash"] = request.GrantHash
        };
        if (request.AttemptId is not null || kind is "verify" or "ack" or "commit" or "abort")
            payload["attempt_id"] = request.AttemptId;
        switch (kind)
        {
            case "verify":
                payload["direction_id"] = request.DirectionId;
                payload["credential_revision"] = request.CredentialRevision;
                payload["rotation_id"] = request.RotationId;
                break;
            case "ack":
                payload["ack_phase"] = request.AckPhase;
                payload["peer_operation_id"] = request.PeerOperationId;
                payload["commit_id"] = request.CommitId;
                payload["exchange_response_hash"] = request.ExchangeResponseHash;
                payload["revocation_id"] = request.RevocationId;
                break;
            case "commit":
                payload["commit_id"] = request.CommitId;
                payload["descriptor_hash"] = request.DescriptorHash;
                payload["initiator_verification_receipt_id"] = request.InitiatorVerificationReceiptId;
                payload["responder_verification_receipt_id"] = request.ResponderVerificationReceiptId;
                break;
            case "abort":
                payload["abort_phase"] = request.AbortPhase;
                payload["abort_id"] = request.AbortId;
                payload["reason_code"] = request.ReasonCode;
                break;
            case "revoke":
                payload["revocation_id"] = request.RevocationId;
                payload["expected_link_revision"] = request.ExpectedLinkRevision;
                payload["reason_code"] = request.ReasonCode;
                break;
            case "rotate":
                payload["rotation_id"] = request.RotationId;
                payload["rotation_phase"] = request.RotationPhase;
                payload["direction_id"] = request.DirectionId;
                payload["expected_current_credential_revision"] = request.ExpectedCurrentCredentialRevision;
                payload["successor_credential_revision"] = request.SuccessorCredentialRevision;
                AddRotationPhase(payload, request, normalizeCredentialScopes);
                break;
            default:
                throw new JsonException("Unsupported service-link lifecycle operation.");
        }
        foreach (var property in typeof(ServiceLinkLifecycleRequest).GetProperties())
        {
            var field = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            if (field is not null && !payload.ContainsKey(field) && property.GetValue(request) is not null)
                throw new JsonException("The operation contains a semantic field belonging to a different lifecycle phase.");
        }
        return payload;
    }

    public static string Hash(string kind, ServiceLinkLifecycleRequest request, bool normalizeCredentialScopes = true) =>
        ServiceLinkCanonicalJson.HashObject(Build(kind, request, normalizeCredentialScopes));

    private static void AddRotationPhase(Dictionary<string, object?> payload, ServiceLinkLifecycleRequest request, bool normalizeCredentialScopes)
    {
        switch (request.RotationPhase)
        {
            case "request":
                payload["requested_by_instance_id"] = request.RequestedByInstanceId;
                payload["requested_policy_revision"] = request.RequestedPolicyRevision;
                break;
            case "offer":
                payload["offer_expires_at"] = request.OfferExpiresAt;
                payload["credential_for_caller"] = normalizeCredentialScopes && request.CredentialForCaller is not null
                    ? ServiceLinkPayloadNormalization.Credential(request.CredentialForCaller) : request.CredentialForCaller;
                break;
            case "verified":
                payload["successor_verification_receipt_id"] = request.SuccessorVerificationReceiptId;
                break;
            case "switched":
                payload["activate_decision_id"] = request.ActivateDecisionId;
                payload["successor_verification_receipt_id"] = request.SuccessorVerificationReceiptId;
                payload["caller_switch_revision"] = request.CallerSwitchRevision;
                break;
            case "abort":
                payload["reason_code"] = request.ReasonCode;
                break;
            default:
                throw new JsonException("Unsupported service-link rotation phase.");
        }
    }
}
