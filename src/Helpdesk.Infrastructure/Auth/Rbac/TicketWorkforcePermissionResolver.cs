using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Ticketing;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Auth.Rbac;

internal static class TicketWorkforcePermissionResolver
{
    internal sealed record QueueRole(string OrganizationId, string[] Permissions, string[]? SelectedGroupIds);

    public static async Task<HashSet<TicketQueuePermissionGrant>> ResolveGroupsAsync(
        HelpdeskDbContext db, string userId, IReadOnlyList<QueueRole> roles, CancellationToken ct)
    {
        if (roles.Count == 0) return [];

        var owners = roles.Select(role => role.OrganizationId).Distinct().ToArray();
        var memberships = await (
            from route in TicketQueueAssignment.Routes(db)
            join member in db.SupportGroupMembers.AsNoTracking()
                on route.Group.Id equals member.SupportGroupId
            where member.UserId == userId && member.IsEnabled && owners.Contains(route.Group.OwningOrganizationId)
            select new { GroupId = route.Group.Id, OwnerId = route.Group.OwningOrganizationId, route.OrganizationId })
            .ToListAsync(ct);

        return (
            from membership in memberships
            join role in roles on membership.OwnerId equals role.OrganizationId
            where role.SelectedGroupIds == null || role.SelectedGroupIds.Contains(membership.GroupId, StringComparer.Ordinal)
            from permission in role.Permissions
            select new TicketQueuePermissionGrant(permission, membership.OrganizationId, membership.GroupId, membership.OwnerId))
            .ToHashSet();
    }
}
