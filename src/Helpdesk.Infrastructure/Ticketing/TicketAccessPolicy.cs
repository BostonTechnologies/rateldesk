using System.Linq.Expressions;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;

namespace Helpdesk.Infrastructure.Ticketing;

public static class TicketAccessPolicy
{
    private static readonly ModulePermissions IncidentPermissions = new(
        HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.IncidentManager,
        HelpdeskPermissions.IncidentDelete, HelpdeskPermissions.IncidentUser, null);
    private static readonly ModulePermissions RequestPermissions = new(
        HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestManager,
        HelpdeskPermissions.RequestDelete, HelpdeskPermissions.RequestUser, HelpdeskPermissions.RequestExecute);
    private static readonly ModulePermissions ChangePermissions = new(
        HelpdeskPermissions.ChangeRead, HelpdeskPermissions.ChangeWrite, HelpdeskPermissions.ChangeManager,
        HelpdeskPermissions.ChangeDelete, HelpdeskPermissions.ChangeUser, HelpdeskPermissions.ChangeApprove);

    public static TicketType Module(Ticket ticket) => ticket switch
    {
        Incident => TicketType.Incident,
        Request => TicketType.Request,
        Change => TicketType.Change,
        _ => throw new ArgumentOutOfRangeException(nameof(ticket))
    };

    public static IQueryable<T> Readable<T>(IQueryable<T> tickets, CurrentUserAccessProfile access, TicketType module, bool includeOwned = true)
        where T : Ticket
    {
        if (!access.IsAuthenticated) return tickets.Where(ticket => false);
        if (access.IsHelpdeskAdmin) return tickets;

        var permissions = For(module);
        var broadOrganizations = access.OrganizationIdsForAny(permissions.Read, permissions.Write, permissions.Manager).ToArray();
        var ownOrganizations = includeOwned ? access.OrganizationIdsFor(permissions.Own).Except(access.RestrictedTicketOrganizationIds).ToArray() : [];
        var customerId = includeOwned ? access.CustomerId : null;
        Expression<Func<T, bool>> visible = ticket =>
            (ticket.OrganizationId != null && broadOrganizations.Contains(ticket.OrganizationId)) ||
            (customerId != null && ticket.OrganizationId != null && ownOrganizations.Contains(ticket.OrganizationId)
                && customerId == ticket.CustomerId);
        var queuesByOrganization = access.TicketQueuePermissionGrants
            .Where(grant => grant.Permission == permissions.Read || grant.Permission == permissions.Write || grant.Permission == permissions.Manager)
            .GroupBy(grant => grant.OrganizationId);

        foreach (var queues in queuesByOrganization)
        {
            var groupIds = queues.Select(grant => grant.SupportGroupId).Distinct().ToArray();
            var organizationId = queues.Key;
            Expression<Func<T, bool>> inQueue = ticket => ticket.OrganizationId == organizationId &&
                ticket.AssignedSupportGroupId != null && groupIds.Contains(ticket.AssignedSupportGroupId);
            visible = Expression.Lambda<Func<T, bool>>(
                Expression.OrElse(visible.Body, Expression.Invoke(inQueue, visible.Parameters)),
                visible.Parameters);
        }

        return tickets.Where(visible);
    }

    public static IQueryable<Ticket> Readable(HelpdeskDbContext db, CurrentUserAccessProfile access)
    {
        if (access.IsHelpdeskAdmin && access.IsAuthenticated) return db.Tickets;
        var incidents = Readable(db.Incidents, access, TicketType.Incident).Select(ticket => ticket.Id);
        var requests = Readable(db.Requests, access, TicketType.Request).Select(ticket => ticket.Id);
        var changes = Readable(db.Changes, access, TicketType.Change).Select(ticket => ticket.Id);
        return db.Tickets.Where(ticket => incidents.Contains(ticket.Id) || requests.Contains(ticket.Id) || changes.Contains(ticket.Id));
    }

    public static bool CanRead(CurrentUserAccessProfile access, Ticket ticket)
    {
        var permissions = For(Module(ticket));
        return Has(access, ticket.OrganizationId, ticket.AssignedSupportGroupId, permissions.Read) ||
               CanWrite(access, ticket) || Owns(access, ticket, permissions.Own);
    }

    public static bool CanWrite(CurrentUserAccessProfile access, Ticket ticket) =>
        CanWrite(access, Module(ticket), ticket.OrganizationId, ticket.AssignedSupportGroupId);

    public static bool CanWrite(CurrentUserAccessProfile access, TicketType module, string? organizationId, string? groupId)
    {
        var permissions = For(module);
        return Has(access, organizationId, groupId, permissions.Write) ||
               Has(access, organizationId, groupId, permissions.Manager);
    }

    public static bool CanContribute(CurrentUserAccessProfile access, Ticket ticket) =>
        CanWrite(access, ticket) || Owns(access, ticket, For(Module(ticket)).Own);

    public static bool CanDelete(CurrentUserAccessProfile access, Ticket ticket)
    {
        var permissions = For(Module(ticket));
        return Has(access, ticket.OrganizationId, ticket.AssignedSupportGroupId, permissions.Delete) ||
               Has(access, ticket.OrganizationId, ticket.AssignedSupportGroupId, permissions.Manager);
    }

    public static bool CanPerformWorkflowAction(CurrentUserAccessProfile access, Ticket ticket)
    {
        var permissions = For(Module(ticket));
        return permissions.Workflow is { } workflow &&
               (Has(access, ticket.OrganizationId, ticket.AssignedSupportGroupId, workflow) ||
                Has(access, ticket.OrganizationId, ticket.AssignedSupportGroupId, permissions.Manager));
    }

    public static bool CanTransfer(CurrentUserAccessProfile access, TicketType module, string organizationId, string? groupId) =>
        CanWrite(access, module, organizationId, groupId) &&
        (Has(access, organizationId, groupId, HelpdeskPermissions.TicketQueueTransfer) ||
         access.HasPermission(HelpdeskPermissions.TenantRolesAssign, organizationId));

    public static bool CanReadStaffContent(CurrentUserAccessProfile access, Ticket ticket)
    {
        var permissions = For(Module(ticket));
        return CanWrite(access, ticket) || Has(access, ticket.OrganizationId, ticket.AssignedSupportGroupId, permissions.Read);
    }

    public static IReadOnlySet<string> StaffOrganizations(CurrentUserAccessProfile access, TicketType module)
    {
        var permissions = For(module);
        return access.OrganizationIdsForAny(permissions.Read, permissions.Write, permissions.Manager);
    }

    public static IEnumerable<TicketQueuePermissionGrant> StaffQueueGrants(CurrentUserAccessProfile access, TicketType module)
    {
        var permissions = For(module);
        return access.TicketQueuePermissionGrants.Where(grant =>
            grant.Permission == permissions.Read || grant.Permission == permissions.Write || grant.Permission == permissions.Manager);
    }

    public static bool Has(CurrentUserAccessProfile access, string? organizationId, string? groupId, string permission) =>
        access.IsAuthenticated &&
        (access.HasPermission(permission, organizationId) ||
         (groupId != null && access.TicketQueuePermissionGrants.Any(grant =>
             grant.Permission == permission && grant.OrganizationId == organizationId && grant.SupportGroupId == groupId)));

    private static bool Owns(CurrentUserAccessProfile access, Ticket ticket, string permission) =>
        access.IsAuthenticated && ticket.OrganizationId != null &&
        !access.RestrictedTicketOrganizationIds.Contains(ticket.OrganizationId) &&
        access.CustomerId != null && access.CustomerId == ticket.CustomerId &&
        access.HasPermission(permission, ticket.OrganizationId);

    private static ModulePermissions For(TicketType module) => module switch
    {
        TicketType.Incident => IncidentPermissions,
        TicketType.Request => RequestPermissions,
        TicketType.Change => ChangePermissions,
        _ => throw new ArgumentOutOfRangeException(nameof(module))
    };

    private sealed record ModulePermissions(string Read, string Write, string Manager, string Delete, string Own, string? Workflow);
}
