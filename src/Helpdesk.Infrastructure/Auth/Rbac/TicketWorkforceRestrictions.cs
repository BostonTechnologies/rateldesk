using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Entities;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Auth.Rbac;

public static class TicketWorkforceRestrictions
{
    public static async Task EnsureAsync(
        HelpdeskDbContext db, string organizationId, IEnumerable<string> userIds, string actorId, CancellationToken ct)
    {
        var targets = userIds.Distinct().ToArray();
        if (targets.Length == 0) return;

        var existing = await db.TicketWorkforceRestrictions
            .Where(restriction => restriction.OrganizationId == organizationId && targets.Contains(restriction.UserId))
            .Select(restriction => restriction.UserId)
            .ToArrayAsync(ct);
        foreach (var userId in targets.Except(existing))
        {
            db.TicketWorkforceRestrictions.Add(new TicketWorkforceRestriction
            {
                UserId = userId,
                OrganizationId = organizationId,
                RestrictedUtc = DateTime.UtcNow
            });
            db.ActivityLogs.Add(new ActivityLog
            {
                RelatedEntityId = userId,
                UserId = actorId,
                Message = $"Restricted ticket workforce access to support groups in organization '{organizationId}'."
            });
        }
    }
}
