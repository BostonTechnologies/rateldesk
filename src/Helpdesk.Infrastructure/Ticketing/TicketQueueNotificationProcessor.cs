using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Entities;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Ticketing;

public sealed class TicketQueueNotificationProcessor(
    HelpdeskDbContext db, ICurrentUserAccessService accessService, ISupportNotificationService notifications)
{
    public async Task DispatchAssignmentAsync(string transitionId, CancellationToken ct)
    {
        var transition = await db.TicketQueueTransitions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == transitionId, ct);
        if (transition?.AssignedSupportGroupId is not { } groupId) return;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LoadTicketAsync(transition.TicketId, ct);
        if (ticket == null || !await IsCurrentAsync(ticket, transition, ct)) return;

        var group = await TicketQueueAssignment.Destinations(db, transition.OrganizationId).AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == groupId, ct);
        if (group == null || !await HasRuleAsync(transition.OrganizationId, groupId, ct)) return;

        var users = await (from member in db.SupportGroupMembers.AsNoTracking()
            join user in db.Users.AsNoTracking() on member.UserId equals user.Id
            where member.SupportGroupId == groupId && member.IsEnabled && !user.IsDeleted &&
                user.Email != "" && !db.UserSupportNotificationPreferences.Any(preference =>
                    preference.UserId == user.Id && !preference.IsEnabled &&
                    preference.EventType == SupportNotificationEventType.TicketAssignedToSupportGroup &&
                    preference.Channel == SupportNotificationChannel.Email)
            orderby user.Id
            select user).ToArrayAsync(ct);

        var recipients = new List<SupportNotificationRecipient>();
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in users)
        {
            if (!string.IsNullOrWhiteSpace(user.Email) && await CanReadAsync(ticket, user.Id, ct) && addresses.Add(user.Email.Trim()))
                recipients.Add(new SupportNotificationRecipient(user.Id, user.Name, user.Email.Trim(), groupId, group.Name));
        }
        await notifications.NotifyTicketAssignedToSupportGroupAsync(ticket, transitionId, recipients, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IngressEmailEffect?> AuthorizeDeliveryAsync(IngressEmailEffect email, CancellationToken ct)
    {
        if (email.TicketId == null) return email;
        var ticket = await LoadTicketAsync(email.TicketId, ct);
        var supportDelivery = email.SupportDeliveryId == null ? null : await db.SupportNotificationDeliveries.AsNoTracking()
            .SingleOrDefaultAsync(delivery => delivery.Id == email.SupportDeliveryId, ct);
        if (ticket == null) return supportDelivery?.QueueTransitionId == null ? email : null;
        if (ticket is RequestTask task)
        {
            ticket = await LoadTicketAsync(task.RequestId, ct);
            if (ticket is not Request || ticket.OrganizationId != task.OrganizationId) return null;
        }
        if (supportDelivery?.EventType == SupportNotificationEventType.TicketAssignedToSupportGroup)
            return email.Recipients is [var recipient] && email.Cc is [] && email.Bcc is [] &&
                string.Equals(recipient, supportDelivery.RecipientEmail, StringComparison.OrdinalIgnoreCase) &&
                await QueueRecipientStillAuthorizedAsync(ticket, supportDelivery, ct) ? email : null;
        if (supportDelivery?.EventType == SupportNotificationEventType.TicketAssigned &&
            ticket.AssignedSupportGroupId != null && ticket.AssignedToId != supportDelivery.RecipientUserId)
            return null;
        if (supportDelivery?.EventType == SupportNotificationEventType.TicketCreatedUnassigned &&
            ticket.AssignedSupportGroupId != null)
            return null;

        return await FilterRecipientsAsync(ticket, email, supportDelivery != null, ct);
    }

    private async Task<bool> QueueRecipientStillAuthorizedAsync(Ticket ticket, SupportNotificationDelivery delivery, CancellationToken ct)
    {
        if (delivery.QueueTransitionId == null || delivery.RecipientUserId == null || ticket.AssignedSupportGroupId == null ||
            ticket.OrganizationId == null || delivery.TicketId != ticket.Id)
            return false;
        var transition = await db.TicketQueueTransitions.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == delivery.QueueTransitionId, ct);
        if (transition == null || !await IsCurrentAsync(ticket, transition, ct) ||
            !await TicketQueueAssignment.Destinations(db, ticket.OrganizationId).AnyAsync(group => group.Id == ticket.AssignedSupportGroupId, ct) ||
            !await HasRuleAsync(ticket.OrganizationId, ticket.AssignedSupportGroupId, ct))
            return false;
        var member = await db.SupportGroupMembers.AnyAsync(row => row.SupportGroupId == ticket.AssignedSupportGroupId &&
            row.UserId == delivery.RecipientUserId && row.IsEnabled, ct);
        var currentRecipient = await db.Users.AnyAsync(user => user.Id == delivery.RecipientUserId && !user.IsDeleted &&
            user.Email.ToLower() == delivery.RecipientEmail.ToLower(), ct);
        var optedOut = await db.UserSupportNotificationPreferences.AnyAsync(preference => preference.UserId == delivery.RecipientUserId &&
            preference.EventType == SupportNotificationEventType.TicketAssignedToSupportGroup &&
            preference.Channel == SupportNotificationChannel.Email && !preference.IsEnabled, ct);
        return member && currentRecipient && !optedOut && await CanReadAsync(ticket, delivery.RecipientUserId, ct);
    }

    private async Task<IngressEmailEffect?> FilterRecipientsAsync(
        Ticket ticket, IngressEmailEffect email, bool supportNotification, CancellationToken ct)
    {
        var addresses = email.Recipients.Concat(email.Cc ?? []).Concat(email.Bcc)
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim().ToLowerInvariant()).Distinct().ToArray();
        var workforce = await db.Users.AsNoTracking().Where(user => addresses.Contains(user.Email.ToLower()))
            .Select(user => new { user.Id, user.Email, user.IsDeleted }).ToArrayAsync(ct);
        if (workforce.Length == 0) return email;

        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in workforce)
        {
            // Requester email is independently authorized by the current customer
            // registration. Support notification recipients always require workforce access.
            if (!supportNotification && ticket.CustomerId != null && await db.Customers.AnyAsync(customer =>
                customer.Id == ticket.CustomerId && customer.OrganizationId == ticket.OrganizationId && customer.IsEnabled &&
                customer.Email.ToLower() == person.Email.ToLower(), ct))
                continue;

            var access = person.IsDeleted ? null : await AssigneeQueries.ResolveAccountAccessAsync(db, accessService, person.Id, ct);
            var restricted = access?.HasRestrictedWorkforceAccess == true ||
                await db.TicketWorkforceRestrictions.AnyAsync(row => row.UserId == person.Id, ct);
            if ((restricted || ticket.AssignedSupportGroupId != null) && (access == null || !TicketAccessPolicy.CanReadStaffContent(access, ticket)))
                denied.Add(person.Email.Trim());
        }
        if (denied.Count == 0) return email;

        var recipients = email.Recipients.Where(address => !denied.Contains(address.Trim())).ToArray();
        if (recipients.Length == 0) return null;
        return email with
        {
            Recipients = recipients,
            Cc = (email.Cc ?? []).Where(address => !denied.Contains(address.Trim())).ToArray(),
            Bcc = email.Bcc.Where(address => !denied.Contains(address.Trim())).ToArray()
        };
    }

    private Task<Ticket?> LoadTicketAsync(string id, CancellationToken ct) =>
        db.Tickets.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);

    private async Task<bool> CanReadAsync(Ticket ticket, string userId, CancellationToken ct) =>
        await AssigneeQueries.ResolveAccountAccessAsync(db, accessService, userId, ct) is { } access &&
        TicketAccessPolicy.CanReadStaffContent(access, ticket);

    private Task<bool> HasRuleAsync(string organizationId, string groupId, CancellationToken ct) =>
        db.SupportNotificationSubscriptions.AnyAsync(rule => rule.IsEnabled && rule.CustomerOrganizationId == organizationId &&
            rule.RecipientType == SupportNotificationRecipientType.SupportGroup && rule.RecipientId == groupId &&
            rule.EventType == SupportNotificationEventType.TicketAssignedToSupportGroup && rule.Channel == SupportNotificationChannel.Email, ct);

    private async Task<bool> IsCurrentAsync(Ticket ticket, TicketQueueTransition transition, CancellationToken ct) =>
        ticket.Id == transition.TicketId && ticket.OrganizationId == transition.OrganizationId &&
        ticket.AssignedSupportGroupId == transition.AssignedSupportGroupId && TicketAccessPolicy.Module(ticket) == transition.TicketType &&
        !await db.TicketQueueTransitions.AnyAsync(row => row.TicketId == ticket.Id && row.AssignmentVersion > transition.AssignmentVersion, ct);
}
