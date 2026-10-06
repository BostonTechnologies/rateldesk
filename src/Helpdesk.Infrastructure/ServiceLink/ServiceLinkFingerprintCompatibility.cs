using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.ServiceLink;

public sealed partial class ServiceLinkCoordinator
{
    private bool ExchangeFingerprintMatches(ServiceLinkAttempt attempt, ServiceLinkExchangeRequest original,
        ServiceLinkExchangeRequest normalized)
    {
        if (attempt.ExchangeFingerprint == ServiceLinkCanonicalJson.HashObject(normalized) ||
            attempt.ExchangeFingerprint == ServiceLinkCanonicalJson.HashObject(original)) return true;
        if (attempt.ProtectedOutboundCredential is null) return false;
        var accepted = Read<ServiceDirectionalCredential>(Unprotect(attempt, "outbound-credential", attempt.ProtectedOutboundCredential));
        return ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Credential(accepted)) ==
                   ServiceLinkCanonicalJson.HashObject(normalized.CredentialForResponder) &&
               attempt.ExchangeFingerprint == ServiceLinkCanonicalJson.HashObject(original with { CredentialForResponder = accepted });
    }

    private async Task<bool> LifecycleFingerprintMatches(ServiceLinkAttempt attempt, ServiceLinkOperation operation,
        string kind, ServiceLinkLifecycleRequest request, CancellationToken ct)
    {
        if (operation.RequestFingerprint == ServiceLinkLifecycleProjection.Hash(kind, request) ||
            operation.RequestFingerprint == ServiceLinkLifecycleProjection.Hash(kind, request, false)) return true;
        if (kind != "rotate" || request.RotationPhase != "offer" || request.CredentialForCaller is null) return false;
        var rotation = await db.Set<ServiceLinkRotation>().AsNoTracking().SingleOrDefaultAsync(
            value => value.LinkId == attempt.LinkId && value.RotationId == request.RotationId && !value.IsIssuer, ct);
        if (rotation is null) return false;
        ServiceDirectionalCredential accepted;
        if (rotation.ProtectedCandidate is not null)
            accepted = Read<ServiceDirectionalCredential>(Unprotect(attempt, "rotation-candidate/" + rotation.RotationId, rotation.ProtectedCandidate));
        else if (attempt.ProtectedOutboundCredential is not null)
        {
            accepted = Read<ServiceDirectionalCredential>(Unprotect(attempt, "outbound-credential", attempt.ProtectedOutboundCredential));
            if (accepted.ClientId != request.CredentialForCaller.ClientId ||
                accepted.CredentialRevision != rotation.SuccessorCredentialRevision ||
                accepted.CredentialRevision != request.SuccessorCredentialRevision) return false;
        }
        else return false;
        // Original material bounds compatibility: no guessed permutations and no rewritten durable hash.
        return ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Credential(accepted)) ==
                   ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Credential(request.CredentialForCaller)) &&
               operation.RequestFingerprint == ServiceLinkLifecycleProjection.Hash(kind,
                   request with { CredentialForCaller = accepted }, false);
    }
}
