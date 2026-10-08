using System.Security.Claims;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using static Helpdesk.Infrastructure.ServiceLink.ServiceLinkValidation;

namespace Helpdesk.Infrastructure.ServiceLink;

public sealed partial class ServiceLinkCoordinator
{
    private async Task RequireAvailableRelationship(string localTenant, string peerInstance, string? peerTenant,
        ClaimsPrincipal actor, string? exceptAttemptId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(peerTenant)) return; // The peer's explicit selection may still be unknown.
        var key = Digest(localTenant + "\n" + peerInstance + "\n" + peerTenant);
        var existing = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleOrDefaultAsync(a =>
            a.ActiveRelationshipKey == key && a.AttemptId != exceptAttemptId, ct);
        if (existing is null) return;
        await Authorize(actor, existing.LocalTenantId, ct);
        Require(existing.LocalTenantId == localTenant && existing.PeerInstanceId == peerInstance && existing.PeerTenantId == peerTenant,
            "service-link-conflict", "The retained relationship binding requires administrator review.", 409);
        throw ExistingRelationship(existing);
    }

    private static ServiceLinkProtocolException ExistingRelationship(ServiceLinkAttempt existing) =>
        new(409, "relationship-already-exists", "Continue the existing approved relationship before starting another consent ceremony.")
        { ExistingAttemptId = existing.AttemptId };

    private async Task<ServiceLinkNavigation?> ContinueMatchingProposal(ServiceLinkStartRequest request, ClaimsPrincipal actor,
        string actorId, ServiceLinkMetadata local, ServiceLinkMetadata peer, ServiceLinkGrant[] grants, CancellationToken ct)
    {
        var peerTenant = grants.Single(g => g.TargetInstanceId == peer.InstanceId).TargetTenantId;
        var candidates = await db.Set<ServiceLinkAttempt>().Where(a => a.Role == "initiator" &&
            a.LocalTenantId == request.LocalTenantId && a.PeerInstanceId == peer.InstanceId &&
            a.Decision == "undecided" &&
            a.LifecycleState == "awaiting_approval" && a.GrantSummaryJson == null && a.ExpiresAtUnixSeconds > Now)
            .OrderBy(a => a.CreatedAtUnixSeconds).Take(100).ToListAsync(ct);
        ServiceLinkAttempt? conflicting = null;
        foreach (var candidate in candidates)
        {
            var descriptor = Descriptor(candidate);
            if (!string.IsNullOrEmpty(peerTenant) && descriptor.RequestedGrants.Any(g =>
                    g.TargetInstanceId == peer.InstanceId && g.TargetTenantId == peerTenant)) conflicting ??= candidate;
            if (descriptor.RequestedResponderTenantId != request.RequestedResponderTenantId ||
                ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Grants(descriptor.RequestedGrants)) !=
                ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Grants(grants)) ||
                ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Metadata(descriptor.InitiatorEndpointSnapshot), "product_version") !=
                ServiceLinkCanonicalJson.HashObject(local, "product_version") ||
                ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Metadata(descriptor.ResponderEndpointSnapshot), "product_version") !=
                ServiceLinkCanonicalJson.HashObject(peer, "product_version")) continue;
            // Same proposal is reusable only by its retained actor and browser.
            // Another browser receives an inspectable local record, never its proofs.
            if (candidate.LocalActorId != actorId || !Same(candidate.SessionBindingHash, Digest(request.SessionBinding)))
            { conflicting ??= candidate; continue; }
            return await ContinueAsync(candidate.AttemptId, new(request.SessionBinding), actor, ct);
        }
        if (conflicting is not null) throw ExistingRelationship(conflicting);
        return null;
    }

    private bool CanReturnApprovedResponder(ServiceLinkAttempt a) => a.Role == "responder" && a.Decision == "undecided" &&
        a.LifecycleState == "approved" && a.ExpiresAtUnixSeconds > Now && a.GrantSummaryJson is not null &&
        a.GrantHash is not null && a.InboundPrincipalId is not null && a.ProtectedOutboundCredential is null &&
        !a.ExchangeDispatched && a.ExchangeFingerprint is null && a.ProtectedBrowserState is not null &&
        a.ProtectedPairingCode is not null && a.PairingCodeHash is not null;

    private ServiceLinkNavigation ReturnApprovedResponder(ServiceLinkAttempt a, string actorId)
    {
        Require(a.LocalActorId == actorId && CanReturnApprovedResponder(a), "invalid-local-consent",
            "The original administrator and live saved responder approval are required.", 403);
        var descriptor = Descriptor(a); var summary = Summary(a);
        Require(descriptor.AttemptId == a.AttemptId && descriptor.DescriptorHash == a.DescriptorHash &&
            ServiceLinkPayloadNormalization.DescriptorHashMatches(descriptor, a.DescriptorHash) && summary.AttemptId == a.AttemptId && summary.LinkId == a.LinkId &&
            summary.DescriptorHash == a.DescriptorHash && summary.ProposedLinkRevision == a.LinkRevision &&
            ServiceLinkPayloadNormalization.SummaryHashMatches(summary, a.GrantHash!) &&
            ServiceLinkAuthority.LocalIdentityMatches(summary, a.Role, issuer, settings),
            "grant-binding-mismatch", "The saved approval no longer matches its immutable local identity and grant.", 409);
        var pairingCode = Unprotect(a, "pairing-code", a.ProtectedPairingCode!);
        Require(Same(a.PairingCodeHash, Digest(pairingCode)), "invalid-pairing-proof",
            "The retained pairing proof no longer matches its saved approval.", 403);
        return new(a.AttemptId, CallbackNavigation(a, Unprotect(a, "browser-state", a.ProtectedBrowserState!),
            pairingCode), a.LifecycleState);
    }
}
