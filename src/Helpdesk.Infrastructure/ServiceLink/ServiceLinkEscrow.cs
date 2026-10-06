using System.Data;
using Helpdesk.Infrastructure.ServiceIdentity;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.ServiceLink;

public sealed partial class ServiceLinkCoordinator
{
    private async Task CleanupBootstrapEscrow(CancellationToken ct)
    {
        var ids = await db.Set<ServiceLinkAttempt>().AsNoTracking().Where(a => a.ExpiresAtUnixSeconds <= Now &&
                (a.ProtectedInboundEscrow != null || a.ProtectedExchangeResponse != null ||
                 a.ProtectedPairingCode != null || a.ProtectedVerifier != null || a.ProtectedBrowserState != null))
            .OrderBy(a => a.ExpiresAtUnixSeconds).ThenBy(a => a.AttemptId)
            .Select(a => a.AttemptId).Take(20).ToListAsync(ct);
        foreach (var id in ids)
        {
            for (var retry = 0; ; retry++)
            {
                db.ChangeTracker.Clear();
                var attempt = await Attempt(id, ct);
                if (attempt.ExpiresAtUnixSeconds > Now) break;
                // Sweep only temporary bootstrap material. A retained commit or
                // in-doubt decision and permanent recovery credential stay intact.
                PurgeEscrow(attempt);
                try { await Save(attempt, ct); break; }
                catch (DbUpdateConcurrencyException) when (retry < 3) { }
            }
        }
        db.ChangeTracker.Clear();
    }

    // This is disposal of temporary handoff material, independent of business authority or peer availability.
    // Permanent outbound credentials remain available only for the existing finite terminal-control recovery.
    private async Task PurgeTerminalRotationEscrow(ServiceLinkAttempt a, CancellationToken ct)
    {
        var rotations = await db.Set<ServiceLinkRotation>().Where(x => x.LinkId == a.LinkId).ToListAsync(ct);
        foreach (var rotation in rotations)
        {
            if (rotation.IsIssuer && rotation.ActivateDecisionId is null && rotation.RotationState is not ("aborted" or "completed"))
                await AbortSuccessor(a, rotation, ct);
            else if (!rotation.IsIssuer && rotation.ActivateDecisionId is null)
                rotation.RotationState = "aborted";
            rotation.ProtectedOffer = null;
            rotation.ProtectedCandidate = null;
            rotation.ActiveRotationKey = null;
            rotation.Revision++;
        }
        var offers = await db.Set<ServiceLinkOperation>().Where(x => x.LinkId == a.LinkId && x.Outbound &&
            x.Kind.StartsWith("rotate-offer/") && x.ProtectedRequestJson != null).ToListAsync(ct);
        foreach (var offer in offers) offer.ProtectedRequestJson = null;
    }

    private async Task CleanupRotationEscrow(CancellationToken ct)
    {
        var expiredLinks = await db.Set<ServiceLinkRotation>().AsNoTracking().Where(rotation =>
                rotation.OfferExpiresAtUnixSeconds <= Now &&
                (rotation.ProtectedOffer != null || rotation.IsIssuer && rotation.ActivateDecisionId == null && rotation.ActiveRotationKey != null ||
                 db.Set<ServiceLinkOperation>().Any(operation => operation.LinkId == rotation.LinkId && operation.Outbound &&
                     operation.Kind == "rotate-offer/" + rotation.RotationId && operation.ProtectedRequestJson != null)))
            .Select(x => x.LinkId).Distinct().Take(20).ToListAsync(ct);
        var terminalLinks = await db.Set<ServiceLinkAttempt>().AsNoTracking().Where(attempt => attempt.LinkId != null &&
                (attempt.Decision == "abort" || attempt.LifecycleState == "revoked" || attempt.LifecycleState == "revocation_pending") &&
                (db.Set<ServiceLinkRotation>().Any(rotation => rotation.LinkId == attempt.LinkId &&
                     (rotation.ProtectedOffer != null || rotation.ProtectedCandidate != null || rotation.ActiveRotationKey != null)) ||
                 db.Set<ServiceLinkOperation>().Any(operation => operation.LinkId == attempt.LinkId && operation.Outbound &&
                     operation.Kind.StartsWith("rotate-offer/") && operation.ProtectedRequestJson != null)))
            .Select(x => x.LinkId!).Take(20).ToListAsync(ct);
        foreach (var linkId in expiredLinks.Concat(terminalLinks).Distinct(StringComparer.Ordinal))
        {
            for (var retry = 0; ; retry++)
            {
                db.ChangeTracker.Clear();
                try
                {
                    await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                    var attempt = await Link(linkId, ct);
                    if (attempt.Decision == "abort" || attempt.LifecycleState is "revoked" or "revocation_pending")
                        await PurgeTerminalRotationEscrow(attempt, ct);
                    else
                    {
                        var expired = await db.Set<ServiceLinkRotation>().Where(x => x.LinkId == linkId &&
                            x.OfferExpiresAtUnixSeconds <= Now).ToListAsync(ct);
                        foreach (var rotation in expired)
                        {
                            if (rotation.IsIssuer && rotation.ActivateDecisionId is null && rotation.RotationState is not ("aborted" or "completed"))
                                await AbortSuccessor(attempt, rotation, ct);
                            else
                            {
                                rotation.ProtectedOffer = null;
                                var journals = await db.Set<ServiceLinkOperation>().Where(x => x.LinkId == linkId && x.Outbound &&
                                    x.Kind == "rotate-offer/" + rotation.RotationId && x.ProtectedRequestJson != null).ToListAsync(ct);
                                foreach (var journal in journals) journal.ProtectedRequestJson = null;
                            }
                            // A caller's candidate can be the only recoverable material for a remote activation
                            // decision. Its expiry recovery must learn that decision; expiry alone cannot erase it.
                            rotation.Revision++;
                        }
                    }
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    break;
                }
                catch (DbUpdateConcurrencyException) when (retry < 3) { }
            }
        }
        db.ChangeTracker.Clear();
    }
}
