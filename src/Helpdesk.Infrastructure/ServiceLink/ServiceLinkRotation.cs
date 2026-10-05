using System.Data;
using System.Security.Claims;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using static Helpdesk.Infrastructure.ServiceLink.ServiceLinkValidation;

namespace Helpdesk.Infrastructure.ServiceLink;

public sealed partial class ServiceLinkCoordinator
{
    public async Task<ServiceLinkAdminStatus> AdminActionAsync(string identifier, string kind, ServiceLinkAdminAction request, ClaimsPrincipal actor, CancellationToken ct)
    {
        var a = await db.Set<ServiceLinkAttempt>().SingleOrDefaultAsync(x => x.LinkId == identifier || x.AttemptId == identifier, ct) ?? throw new ServiceLinkProtocolException(404, "link-not-found", "The attempt or link does not exist.");
        await AuthorizeAttempt(a, actor, ct);
        if (kind == "resume") { a = await ProgressWithRetry(a, ct); return await AdminStatus(a, ct); }
        if (kind == "rotate")
        {
            Require(a.LifecycleState == "active" && a.Decision == "commit", "rotation-not-active", "Rotation requires both active approved directions.", 409);
            var direction = request.DirectionId ?? InboundGrant(a).DirectionId;
            Require(Summary(a).Grants.Any(x => x.DirectionId == direction), "rotation-not-authorized", "The direction is outside this approved link.", 403);
            if (direction == InboundGrant(a).DirectionId) await BeginIssuerRotation(a, NewId(), null, ct);
            else
            {
                var current = Read<ServiceDirectionalCredential>(Unprotect(a, "outbound-credential", a.ProtectedOutboundCredential!));
                var pending = await db.Set<ServiceLinkRotation>().SingleOrDefaultAsync(x => x.LinkId == a.LinkId && x.DirectionId == direction && x.ExpectedCurrentCredentialRevision == current.CredentialRevision && x.RotationState != "completed" && x.RotationState != "aborted", ct);
                if (pending is null) { db.Set<ServiceLinkRotation>().Add(new() { RotationId = NewId(), LinkId = a.LinkId!, DirectionId = direction, ExpectedCurrentCredentialRevision = current.CredentialRevision, ActiveRotationKey = Digest(a.LinkId + "/" + direction + "/" + current.CredentialRevision), IsIssuer = false, RotationState = "requested", CreatedAtUnixSeconds = Now }); await db.SaveChangesAsync(ct); }
            }
            return await AdminStatus(a, ct);
        }
        Require(kind is "cancel" or "revoke", "operation-not-found", "This local lifecycle action is unsupported.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (a.Decision == "commit" || kind == "revoke")
        {
            a.RevocationId ??= NewId(); a.LifecycleState = "revocation_pending"; await Disable(a, ct); PurgeEscrow(a);
        }
        else if (a.Role == "initiator")
        {
            a.Decision = "abort"; a.AbortId ??= NewId(); a.LifecycleState = a.ProtectedOutboundCredential is null && !a.ExchangeDispatched ? "expired" : "in_doubt";
            await Disable(a, ct); PurgeEscrow(a);
        }
        else
        {
            // A prepared responder cannot decide its coordinator's transaction. Retain narrow recovery authority.
            if (a.ProtectedOutboundCredential is null) { a.Decision = "abort"; a.AbortId ??= NewId(); a.LifecycleState = "expired"; await Disable(a, ct); PurgeEscrow(a); }
            else { a.AbortId ??= NewId(); a.LifecycleState = "in_doubt"; if (a.InboundPrincipalId is not null) await registry.SetStatusAsync(a.InboundPrincipalId.Value, "in_doubt", ct); }
        }
        a.NextWorkAtUnixSeconds = Now; await Save(a, ct); await tx.CommitAsync(ct); return await AdminStatus(a, ct);
    }

    private async Task DeliverRevocation(ServiceLinkAttempt a, CancellationToken ct)
    {
        Require(a.ProtectedOutboundCredential is not null && a.TerminalControlExpiresAtUnixSeconds > Now, "administrator-recovery-required", "The finite control recovery permission is unavailable; complete peer confirmation through its administrator interface.", 409);
        var result = await SendOperation(a, "revoke-link", "revoke", Request(a) with { RevocationId = a.RevocationId, ExpectedLinkRevision = a.LinkRevision, ReasonCode = "administrator-unlink" }, ct);
        Require(String(result, "revocation_id") == a.RevocationId && Boolean(result, "local_business_revoked") && Boolean(result, "local_sender_disabled"), "revocation-pending", "The peer has not confirmed local business revocation.", 409);
        a.PeerRevocationAcknowledged = true; a.LifecycleState = "revoked"; await Save(a, ct);
    }

    private async Task<ServiceLinkRotation> BeginIssuerRotation(ServiceLinkAttempt a, string rotationId, long? expected, CancellationToken ct)
    {
        await using var ownedTransaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var principal = await db.Set<ServicePrincipalRegistration>().SingleAsync(x => x.Id == a.InboundPrincipalId, ct);
        var revision = expected ?? principal.CurrentCredentialRevision;
        var existing = await db.Set<ServiceLinkRotation>().SingleOrDefaultAsync(x => x.LinkId == a.LinkId && x.DirectionId == InboundGrant(a).DirectionId && x.ExpectedCurrentCredentialRevision == revision && x.ActiveRotationKey != null, ct);
        if (existing is not null) { Require(existing.RotationId == rotationId || expected is null, "rotation-conflict", "This direction/current revision already has a durable rotation.", 409); return existing; }
        Require(principal.CurrentCredentialRevision == revision, "credential-revision-conflict", "Reload the current directional credential revision.", 409);
        var successor = await registry.CreateSuccessorAsync(principal.Id, revision, ct);
        var rotation = new ServiceLinkRotation { RotationId = rotationId, LinkId = a.LinkId!, DirectionId = principal.DirectionId!, ActiveRotationKey = Digest(a.LinkId + "/" + principal.DirectionId + "/" + revision), IsIssuer = true, ExpectedCurrentCredentialRevision = revision, SuccessorCredentialRevision = successor.CredentialRevision, RotationState = "offered", OfferExpiresAtUnixSeconds = Now + settings.RotationOfferLifetimeSeconds, CreatedAtUnixSeconds = Now };
        rotation.ProtectedOffer = Protect(a, "rotation-offer/" + rotationId + "/" + successor.CredentialRevision, Json(CredentialFrom(successor, InboundGrant(a), Local(a))));
        db.Set<ServiceLinkRotation>().Add(rotation); await db.SaveChangesAsync(ct); if (ownedTransaction is not null) await ownedTransaction.CommitAsync(ct); return rotation;
    }

    private async Task<Dictionary<string, object?>> Rotate(ServiceLinkAttempt a, ServicePrincipalRegistration principal, ServiceLinkLifecycleRequest request, ClaimsPrincipal caller, CancellationToken ct)
    {
        Require(a.Decision == "commit" && a.LocalInboundActive && a.LifecycleState == "active" && request.RotationId is not null && request.DirectionId is not null && request.ExpectedCurrentCredentialRevision is >= 1, "rotation-not-authorized", "Rotation requires the current active approved link.", 403);
        Id(request.RotationId!);
        var rotation = await db.Set<ServiceLinkRotation>().SingleOrDefaultAsync(x => x.RotationId == request.RotationId && x.LinkId == a.LinkId, ct);
        if (rotation is not null && request.RotationPhase == "request") Require(rotation.DirectionId == request.DirectionId && rotation.ExpectedCurrentCredentialRevision == request.ExpectedCurrentCredentialRevision, "rotation-binding-conflict", "A retry request cannot change its durable direction or current revision.", 409);
        if (rotation is not null && request.RotationPhase != "request")
            Require(rotation.DirectionId == request.DirectionId && rotation.ExpectedCurrentCredentialRevision == request.ExpectedCurrentCredentialRevision &&
                (request.RotationPhase == "offer" && rotation.SuccessorCredentialRevision is null || rotation.SuccessorCredentialRevision == request.SuccessorCredentialRevision), "rotation-binding-conflict", "The phase direction and both revisions must match the exact durable rotation.", 409);
        var tokenCredentialRevision = long.Parse(caller.FindFirstValue(ServiceIdentityClaims.CredentialRevision)!);
        var tokenCredential = await db.Set<ServicePrincipalSecret>().SingleAsync(x => x.ServicePrincipalId == principal.Id && x.CredentialRevision == tokenCredentialRevision, ct);
        if (tokenCredential.Status == "pending") Require(rotation is not null && rotation.IsIssuer && rotation.SuccessorCredentialRevision == tokenCredentialRevision && request.RotationPhase is "verified" or "abort", "candidate-control-restricted", "A pending successor may complete or abort only its own rotation.", 403);
        switch (request.RotationPhase)
        {
            case "request":
                Require(request.DirectionId == InboundGrant(a).DirectionId && request.RequestedByInstanceId == a.PeerInstanceId && request.SuccessorCredentialRevision is null && request.CredentialForCaller is null, "rotation-not-authorized", "The request must reach the issuer of the approved direction.", 403);
                rotation = await BeginIssuerRotation(a, request.RotationId!, request.ExpectedCurrentCredentialRevision, ct); break;
            case "offer":
                Require(request.DirectionId == OutboundGrant(a).DirectionId && request.CredentialForCaller is not null && request.SuccessorCredentialRevision > request.ExpectedCurrentCredentialRevision && request.OfferExpiresAt is not null, "rotation-not-authorized", "Only the approved directional issuer may offer this successor.", 403);
                var candidate = ServiceLinkPayloadNormalization.Credential(request.CredentialForCaller!); Credential(candidate, OutboundGrant(a), Peer(a));
                var expiry = ServiceLinkCanonicalJson.ParseWholeSecondUtcTimestamp(request.OfferExpiresAt!).ToUnixTimeSeconds();
                Require(candidate.CredentialRevision == request.SuccessorCredentialRevision && expiry > Now && expiry <= Now + settings.RotationOfferLifetimeSeconds, "invalid-rotation-offer", "The successor offer has an invalid fixed expiry/revision.");
                var current = Read<ServiceDirectionalCredential>(Unprotect(a, "outbound-credential", a.ProtectedOutboundCredential!));
                Require(current.ClientId == candidate.ClientId && current.CredentialRevision == request.ExpectedCurrentCredentialRevision, "credential-revision-conflict", "The offer does not succeed the working logical client.", 409);
                if (rotation is null) { rotation = new() { RotationId = request.RotationId!, LinkId = a.LinkId!, DirectionId = request.DirectionId!, ActiveRotationKey = Digest(a.LinkId + "/" + request.DirectionId + "/" + current.CredentialRevision), IsIssuer = false, ExpectedCurrentCredentialRevision = current.CredentialRevision, CreatedAtUnixSeconds = Now }; db.Set<ServiceLinkRotation>().Add(rotation); }
                Require(!rotation.IsIssuer && rotation.ExpectedCurrentCredentialRevision == current.CredentialRevision && (rotation.SuccessorCredentialRevision is null || rotation.SuccessorCredentialRevision == candidate.CredentialRevision), "rotation-conflict", "The existing pending successor binding changed.", 409);
                if (rotation.ProtectedCandidate is not null) Require(ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Credential(Read<ServiceDirectionalCredential>(Unprotect(a, "rotation-candidate/" + rotation.RotationId, rotation.ProtectedCandidate)))) == ServiceLinkCanonicalJson.HashObject(candidate), "rotation-payload-conflict", "The successor offer body changed.", 409);
                Require(rotation.OfferExpiresAtUnixSeconds is null || rotation.OfferExpiresAtUnixSeconds == expiry,
                    "rotation-payload-conflict", "A retry cannot change the fixed successor offer deadline.", 409);
                rotation.ProtectedCandidate ??= Protect(a, "rotation-candidate/" + rotation.RotationId, Json(candidate));
                rotation.SuccessorCredentialRevision = candidate.CredentialRevision; rotation.OfferExpiresAtUnixSeconds = expiry; rotation.RotationState = "prepared"; break;
            case "verified":
                Require(rotation is not null && rotation.IsIssuer && rotation.DirectionId == InboundGrant(a).DirectionId && rotation.SuccessorCredentialRevision == request.SuccessorCredentialRevision && rotation.ExpectedCurrentCredentialRevision == request.ExpectedCurrentCredentialRevision && request.SuccessorVerificationReceiptId is not null, "rotation-not-authorized", "The successor verification does not belong to this issuer's rotation.", 403);
                var receipt = await db.Set<ServiceLinkVerificationReceipt>().SingleOrDefaultAsync(x => x.VerificationReceiptId == request.SuccessorVerificationReceiptId && x.LinkId == a.LinkId && x.RotationId == rotation!.RotationId && x.ServicePrincipalId == a.InboundPrincipalId && x.CredentialRevision == rotation.SuccessorCredentialRevision, ct);
                Require(receipt is not null && rotation!.RotationState != "aborted", "successor-not-verified", "The issuer has no matching durable successor probe.", 409);
                if (rotation!.ActivateDecisionId is null)
                {
                    Require(rotation.OfferExpiresAtUnixSeconds > Now, "rotation-offer-expired", "The undecided offer expired.", 410);
                    rotation.ActivateDecisionId = NewId(); rotation.SuccessorVerificationReceiptId = receipt!.VerificationReceiptId;
                    await registry.ActivateSuccessorAsync(a.InboundPrincipalId!.Value, rotation.SuccessorCredentialRevision!.Value, ct);
                }
                rotation.RotationState = "activated"; break;
            case "switched":
                Require(rotation is not null && rotation.IsIssuer && request.DirectionId == InboundGrant(a).DirectionId && request.SuccessorCredentialRevision == rotation!.SuccessorCredentialRevision && request.ActivateDecisionId == rotation.ActivateDecisionId && request.SuccessorVerificationReceiptId == rotation.SuccessorVerificationReceiptId && request.CallerSwitchRevision is >= 1, "rotation-switch-conflict", "The successor switch does not match its verified activation decision.", 409);
                Require(tokenCredentialRevision == request.SuccessorCredentialRevision && principal.CurrentCredentialRevision == request.SuccessorCredentialRevision, "successor-not-active", "The switch must use the activated successor.", 403);
                rotation!.CallerSwitchRevision = request.CallerSwitchRevision; rotation.PredecessorRetireAtUnixSeconds ??= Now + settings.RotationOverlapSeconds;
                await registry.RetirePredecessorAsync(a.InboundPrincipalId!.Value, rotation.ExpectedCurrentCredentialRevision, DateTimeOffset.FromUnixTimeSeconds(rotation.PredecessorRetireAtUnixSeconds.Value), ct); rotation.RotationState = "retiring"; rotation.ProtectedOffer = null; break;
            case "abort":
                Require(rotation is not null && rotation.IsIssuer && request.DirectionId == InboundGrant(a).DirectionId, "rotation-not-authorized", "Only this directional issuer may abort its undecided offer.", 403);
                if (rotation!.ActivateDecisionId is null) await AbortSuccessor(a, rotation, ct); break;
            default: throw new ServiceLinkProtocolException(400, "invalid-rotation-phase", "The rotation phase is unsupported.");
        }
        rotation!.Revision++; var result = Common(a);
        result["rotation_id"] = rotation.RotationId; result["rotation_state"] = rotation.RotationState; result["current_credential_revision"] = rotation.ActivateDecisionId is null ? rotation.ExpectedCurrentCredentialRevision : rotation.SuccessorCredentialRevision;
        result["successor_credential_revision"] = rotation.SuccessorCredentialRevision; result["activate_decision_id"] = rotation.ActivateDecisionId; result["predecessor_retire_at"] = rotation.PredecessorRetireAtUnixSeconds is null ? null : Timestamp(rotation.PredecessorRetireAtUnixSeconds.Value); return result;
    }

    private async Task AbortSuccessor(ServiceLinkAttempt a, ServiceLinkRotation r, CancellationToken ct)
    {
        Require(r.ActivateDecisionId is null, "rotation-already-activated", "An activated successor must be recovered, never silently aborted.", 409);
        var secret = await db.Set<ServicePrincipalSecret>().SingleOrDefaultAsync(x => x.ServicePrincipalId == a.InboundPrincipalId && x.CredentialRevision == r.SuccessorCredentialRevision, ct);
        if (secret is not null) secret.Status = "revoked";
        r.RotationState = "aborted"; r.ActiveRotationKey = null; r.ProtectedOffer = null;
        var offers = await db.Set<ServiceLinkOperation>().Where(x => x.LinkId == a.LinkId && x.Outbound && x.Kind == "rotate-offer/" + r.RotationId).ToListAsync(ct);
        foreach (var offer in offers) offer.ProtectedRequestJson = null;
    }

    private async Task ProgressRotations(ServiceLinkAttempt a, CancellationToken ct)
    {
        var rotations = await db.Set<ServiceLinkRotation>().Where(x => x.LinkId == a.LinkId && x.RotationState != "completed" && x.RotationState != "aborted").ToListAsync(ct);
        if (rotations.Count == 0 && settings.AutomaticRotationEnabled)
        {
            var principal = await db.Set<ServicePrincipalRegistration>().SingleAsync(x => x.Id == a.InboundPrincipalId, ct);
            var secret = await db.Set<ServicePrincipalSecret>().SingleAsync(x => x.ServicePrincipalId == principal.Id && x.CredentialRevision == principal.CurrentCredentialRevision, ct);
            if (secret.CreatedAtUtc.AddDays(settings.RotationAgeDays) <= clock.GetUtcNow()) rotations.Add(await BeginIssuerRotation(a, NewId(), null, ct));
        }
        foreach (var r in rotations)
        {
            if (r.IsIssuer)
            {
                if (r.ActivateDecisionId is null && r.OfferExpiresAtUnixSeconds <= Now) { await AbortSuccessor(a, r, ct); continue; }
                if (r.OfferExpiresAtUnixSeconds <= Now)
                {
                    r.ProtectedOffer = null;
                    var expiredOffers = await db.Set<ServiceLinkOperation>().Where(x => x.LinkId == a.LinkId && x.Outbound && x.Kind == "rotate-offer/" + r.RotationId).ToListAsync(ct);
                    foreach (var offer in expiredOffers) offer.ProtectedRequestJson = null;
                }
                if (r.RotationState is "offered" or "requested")
                {
                    var credential = Read<ServiceDirectionalCredential>(Unprotect(a, "rotation-offer/" + r.RotationId + "/" + r.SuccessorCredentialRevision, r.ProtectedOffer!));
                    var result = await SendOperation(a, "rotate-offer/" + r.RotationId, "rotate", RotationRequest(a, r, "offer") with { OfferExpiresAt = Timestamp(r.OfferExpiresAtUnixSeconds!.Value), CredentialForCaller = credential }, ct);
                    Require(String(result, "rotation_id") == r.RotationId && String(result, "rotation_state") == "prepared", "rotation-recovery-pending", "The caller has not acknowledged its durable candidate.", 409); r.RotationState = "prepared";
                }
                if (r.PredecessorRetireAtUnixSeconds <= Now && r.CallerSwitchRevision is not null) { r.RotationState = "completed"; r.ActiveRotationKey = null; }
                else if (r.ActivateDecisionId is not null && r.CallerSwitchRevision is null) r.RotationState = "recovery_pending";
            }
            else if (r.RotationState == "requested")
            {
                var result = await SendOperation(a, "rotate-request/" + r.RotationId, "rotate", RotationRequest(a, r, "request") with { RequestedByInstanceId = issuer.InstanceId, RequestedPolicyRevision = null }, ct);
                Require(String(result, "rotation_id") == r.RotationId, "rotation-conflict", "The directional issuer assigned a foreign rotation.", 409);
            }
            else if (r.ProtectedCandidate is not null)
            {
                var candidate = Read<ServiceDirectionalCredential>(Unprotect(a, "rotation-candidate/" + r.RotationId, r.ProtectedCandidate));
                if (r.ActivateDecisionId is null && r.OfferExpiresAtUnixSeconds <= Now)
                {
                    r.RotationState = "recovery_pending"; await db.SaveChangesAsync(ct);
                    var result = await SendOperation(a, "rotate-abort/" + r.RotationId, "rotate", RotationRequest(a, r, "abort") with { ReasonCode = "offer-expired-before-observed-activation" }, ct);
                    if (String(result, "rotation_state") == "aborted") { r.RotationState = "aborted"; r.ActiveRotationKey = null; r.ProtectedCandidate = null; r.Revision++; await db.SaveChangesAsync(ct); continue; }
                    Require(String(result, "activate_decision_id") is not null && String(result, "rotation_state") is "activated" or "retiring" or "completed" or "recovery_pending", "rotation-recovery-pending", "The issuer has not supplied a durable successor decision.", 409);
                    r.ActivateDecisionId = String(result, "activate_decision_id"); r.RotationState = "activated";
                }
                if (r.SuccessorVerificationReceiptId is null)
                {
                    var result = await SendOperation(a, "rotate-probe/" + r.RotationId, "verify", Request(a) with { DirectionId = r.DirectionId, CredentialRevision = candidate.CredentialRevision, RotationId = r.RotationId }, ct, candidate);
                    Require(String(result, "rotation_id") == r.RotationId && result.GetProperty("credential_revision").GetInt64() == candidate.CredentialRevision, "successor-verification-conflict", "The successor receipt is foreign.");
                    r.SuccessorVerificationReceiptId = String(result, "verification_receipt_id");
                }
                else if (r.ActivateDecisionId is null)
                {
                    var result = await SendOperation(a, "rotate-verified/" + r.RotationId, "rotate", RotationRequest(a, r, "verified") with { SuccessorVerificationReceiptId = r.SuccessorVerificationReceiptId }, ct, candidate);
                    Require(String(result, "rotation_state") == "activated" && String(result, "activate_decision_id") is not null, "successor-activation-pending", "The issuer has not recorded its successor activation.", 409);
                    r.ActivateDecisionId = String(result, "activate_decision_id"); r.RotationState = "activated";
                }
                else if (r.CallerSwitchRevision is null)
                {
                    await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                    a.ProtectedOutboundCredential = Protect(a, "outbound-credential", Json(candidate)); await StageOutbound(a, candidate, ct);
                    a.OutboundProfileRevision = (await providers.SetLinkedOrchestratorSenderEnabledAsync(a.LinkId!, a.LinkRevision, a.OutboundProfileRevision!.Value, true, ct)).Revision;
                    r.CallerSwitchRevision = a.OutboundProfileRevision; await Save(a, ct); await tx.CommitAsync(ct);
                }
                else
                {
                    var result = await SendOperation(a, "rotate-switched/" + r.RotationId, "rotate", RotationRequest(a, r, "switched") with { ActivateDecisionId = r.ActivateDecisionId, SuccessorVerificationReceiptId = r.SuccessorVerificationReceiptId, CallerSwitchRevision = r.CallerSwitchRevision }, ct, candidate);
                    Require(String(result, "rotation_state") is "retiring" or "completed", "rotation-retirement-pending", "The issuer has not acknowledged the caller's switch.", 409);
                    var deadline = String(result, "predecessor_retire_at"); r.PredecessorRetireAtUnixSeconds = deadline is null ? null : ServiceLinkCanonicalJson.ParseWholeSecondUtcTimestamp(deadline).ToUnixTimeSeconds(); r.RotationState = r.PredecessorRetireAtUnixSeconds <= Now ? "completed" : "retiring";
                    if (r.RotationState == "completed") { r.ActiveRotationKey = null; r.ProtectedCandidate = null; }
                }
            }
            r.Revision++;
        }
        await Save(a, ct);
    }
    private ServiceLinkLifecycleRequest RotationRequest(ServiceLinkAttempt a, ServiceLinkRotation r, string phase) => Request(a) with { RotationId = r.RotationId, RotationPhase = phase, DirectionId = r.DirectionId, ExpectedCurrentCredentialRevision = r.ExpectedCurrentCredentialRevision, SuccessorCredentialRevision = r.SuccessorCredentialRevision };
}
