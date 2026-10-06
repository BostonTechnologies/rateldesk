using Microsoft.EntityFrameworkCore;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.ServiceLink;
using static Helpdesk.Infrastructure.ServiceLink.ServiceLinkValidation;

namespace Helpdesk.Infrastructure.ServiceLink;

public sealed partial class ServiceLinkCoordinator
{
    private Task<string> ProtocolToken(ServiceLinkAttempt captured, string scope, string route,
        ServiceLinkLifecycleRequest? request, ServiceDirectionalCredential credential, CancellationToken ct, bool candidateCredential = false) =>
        protocolTokens.GetAsync(token => CurrentProtocolTokenContext(captured, scope, route, request, credential, candidateCredential, token), scope, ct);

    private async Task<ServiceLinkProtocolTokenContext> CurrentProtocolTokenContext(ServiceLinkAttempt captured, string scope,
        string route, ServiceLinkLifecycleRequest? request, ServiceDirectionalCredential credential, bool candidateCredential, CancellationToken ct)
    {
        var current = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == captured.AttemptId, ct);
        Require(current is not null && current.LinkId == captured.LinkId && current.LinkRevision == captured.LinkRevision &&
            current.GrantHash == captured.GrantHash && current.DescriptorHash == captured.DescriptorHash &&
            current.Role == captured.Role && current.LocalTenantId == captured.LocalTenantId &&
            current.PeerInstanceId == captured.PeerInstanceId && current.PeerTenantId == captured.PeerTenantId &&
            current.OutboundProfileRevision == captured.OutboundProfileRevision && current.ProtectedOutboundCredential is not null,
            "protocol-profile-conflict", "The current approved link or outbound profile changed.", 409);
        var a = current!;
        var summary = Summary(a);
        Require(summary.AttemptId == a.AttemptId && summary.LinkId == a.LinkId && summary.ProposedLinkRevision == a.LinkRevision &&
            summary.DescriptorHash == a.DescriptorHash && ServiceLinkPayloadNormalization.SummaryHashMatches(summary, a.GrantHash!) &&
            ServiceLinkAuthority.LocalIdentityMatches(summary, a.Role, issuer, settings),
            "protocol-authority-unavailable", "The approved local identity is no longer current.", 403);
        var grant = OutboundGrant(a);
        var peer = Peer(a);
        Credential(credential, grant, peer);
        var principal = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == a.InboundPrincipalId, ct);
        var secret = principal is null ? null : await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ServicePrincipalId == principal.Id && x.CredentialRevision == principal.CurrentCredentialRevision, ct);
        var customers = principal is null ? Array.Empty<string>() : ServicePrincipalRegistry.ReadArray(principal.CustomerIdsJson);
        Require(principal is not null && secret is not null && secret.Status != "revoked" &&
            secret.ExpiresAtUtc > clock.GetUtcNow() && (secret.RetireAtUtc is null || secret.RetireAtUtc > clock.GetUtcNow()) &&
            registry.PermittedScopes(principal, secret).Contains(scope, StringComparer.Ordinal) &&
            await db.Organizations.AsNoTracking().AnyAsync(x => x.Id == principal.OrganizationId &&
                x.State == Helpdesk.Shared.Models.EntityState.Enabled, ct) &&
            await db.Customers.AsNoTracking().CountAsync(x => customers.Contains(x.Id) && x.OrganizationId == principal.OrganizationId &&
                x.State == Helpdesk.Shared.Models.EntityState.Enabled, ct) == customers.Length &&
            await registry.CanIssueScopesAsync(new(principal, secret), [scope], ct),
            "protocol-authority-unavailable", "The exact local registration has no current retained protocol authority.", 403);

        var terminal = a.Decision == "abort" || a.LifecycleState is "revocation_pending" or "revoked" or "expired" or "failed";
        long? authorityDeadline = secret!.ExpiresAtUtc.ToUnixTimeSeconds();
        if (secret.RetireAtUtc is { } retirement) authorityDeadline = Math.Min(authorityDeadline.Value, retirement.ToUnixTimeSeconds());
        if (terminal)
        {
            Require(scope == ServiceLinkContract.ControlScope && a.TerminalControlExpiresAtUnixSeconds > Now &&
                (a.LifecycleState is "revocation_pending" or "revoked"
                    ? route == "revoke" || route == "ack" && request?.AckPhase == "revoked" || route == "status"
                    : route is "abort" or "status"),
                "terminal-control-restricted", "Only the original finite terminal control recovery may continue.", 403);
            authorityDeadline = Math.Min(authorityDeadline.Value, a.TerminalControlExpiresAtUnixSeconds!.Value);
        }
        else Require(a.LifecycleState is not ("awaiting_approval" or "failed" or "expired" or "revoked"),
            "protocol-authority-unavailable", "This attempt has no prepared protocol authority.", 403);

        var outbound = Read<ServiceDirectionalCredential>(Unprotect(a, "outbound-credential", a.ProtectedOutboundCredential!));
        var accepted = ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Credential(outbound)) ==
            ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Credential(credential));
        var candidate = candidateCredential;
        if (candidate)
        {
            Require(request?.RotationId is not null &&
                (route == "verify" || route == "rotate" && request.RotationPhase is "verified" or "switched" or "abort"),
                "candidate-control-restricted", "A candidate may only complete its own pending rotation.", 403);
            var rotation = await db.Set<ServiceLinkRotation>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.LinkId == a.LinkId && x.RotationId == request!.RotationId && !x.IsIssuer, ct);
            Require(!terminal && rotation is not null && rotation.DirectionId == grant.DirectionId &&
                rotation.SuccessorCredentialRevision == credential.CredentialRevision &&
                rotation.RotationState != "aborted", "candidate-control-restricted", "The candidate belongs to another rotation.", 403);
            var durableCandidate = rotation!.ProtectedCandidate is null ? null : Read<ServiceDirectionalCredential>(
                Unprotect(a, "rotation-candidate/" + rotation.RotationId, rotation.ProtectedCandidate));
            // After a durable caller switch, escrow may be gone while an unfinished
            // exact switched journal still needs the already installed successor.
            var pendingCandidate = rotation.ActiveRotationKey is not null && rotation.RotationState != "completed" &&
                durableCandidate is not null && ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Credential(durableCandidate)) ==
                    ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Credential(credential));
            var retainedSwitch = accepted && rotation.CallerSwitchRevision is not null && rotation.ActivateDecisionId is not null &&
                route == "rotate" && request!.RotationPhase == "switched" &&
                await db.Set<ServiceLinkOperation>().AsNoTracking().AnyAsync(x => x.LinkId == a.LinkId &&
                    x.OperationId == request.OperationId && x.Outbound && !x.Completed && x.Kind == "rotate-switched/" + rotation.RotationId, ct);
            Require(pendingCandidate || retainedSwitch,
                "candidate-control-restricted", "The current candidate or installed successor differs from this rotation.", 403);
            if (rotation.ActivateDecisionId is null && request!.RotationPhase != "abort")
            {
                Require(rotation.OfferExpiresAtUnixSeconds > Now, "candidate-control-expired", "The unactivated candidate offer expired.", 403);
                authorityDeadline = Math.Min(authorityDeadline.Value, rotation.OfferExpiresAtUnixSeconds!.Value);
            }
        }
        else Require(accepted, "protocol-profile-conflict", "The current outbound credential differs from the captured credential.", 409);
        return new(a.AttemptId, a.LinkId!, a.LinkRevision, a.GrantHash!, a.DescriptorHash, grant.DirectionId,
            a.Decision + "/" + a.LifecycleState + (candidate ? "/candidate" : "/current"), a.OutboundProfileRevision,
            Local(a), peer, credential, candidate ? request!.RotationId : null, authorityDeadline);
    }
}
