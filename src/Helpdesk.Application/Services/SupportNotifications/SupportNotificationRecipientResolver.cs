using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.Services.SupportNotifications;

public sealed class SupportNotificationRecipientResolver(
    IRepository<User> users,
    IRepository<SupportGroup> supportGroups,
    IRepository<SupportGroupMember> supportGroupMembers,
    IRepository<OrganizationSupportCoverage> coverages,
    IRepository<SupportNotificationSubscription> subscriptions,
    IRepository<UserSupportNotificationPreference> preferences,
    ILogger<SupportNotificationRecipientResolver> logger) : ISupportNotificationRecipientResolver
{
    public Task<IReadOnlyList<SupportNotificationRecipient>> ResolveTicketEventRecipientsAsync(
        Ticket ticket,
        SupportNotificationEventType eventType,
        SupportNotificationChannel channel,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticket.OrganizationId))
        {
            return Task.FromResult<IReadOnlyList<SupportNotificationRecipient>>([]);
        }

        return ResolveOrganizationEventRecipientsAsync(ticket.OrganizationId, eventType, channel, ct);
    }

    public async Task<IReadOnlyList<SupportNotificationRecipient>> ResolveOrganizationEventRecipientsAsync(
        string customerOrganizationId,
        SupportNotificationEventType eventType,
        SupportNotificationChannel channel,
        CancellationToken ct = default)
    {
        _ = ct;

        if (string.IsNullOrWhiteSpace(customerOrganizationId) || channel != SupportNotificationChannel.Email)
        {
            return [];
        }

        var activeCoverages = (await coverages.GetAllAsync())
            .Where(x => x.IsEnabled && string.Equals(x.CustomerOrganizationId, customerOrganizationId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (activeCoverages.Count == 0)
        {
            return [];
        }

        var groups = (await supportGroups.GetAllAsync()).Where(x => x.IsEnabled).ToList();
        var activeGroupById = groups.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var coveredGroupIds = activeCoverages
            .Where(x => activeGroupById.ContainsKey(x.SupportGroupId))
            .Select(x => x.SupportGroupId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (coveredGroupIds.Count == 0)
        {
            return [];
        }

        var activeSubscriptions = (await subscriptions.GetAllAsync())
            .Where(x =>
                x.IsEnabled &&
                x.EventType == eventType &&
                x.Channel == channel &&
                string.Equals(x.CustomerOrganizationId, customerOrganizationId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (activeSubscriptions.Count == 0)
        {
            return [];
        }

        var activeMembers = (await supportGroupMembers.GetAllAsync())
            .Where(x => x.IsEnabled && coveredGroupIds.Contains(x.SupportGroupId))
            .ToList();
        var activeMemberLookup = activeMembers
            .GroupBy(x => x.SupportGroupId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        var activeUsers = (await users.GetAllAsync())
            .Where(x => !x.IsDeleted && !string.IsNullOrWhiteSpace(x.Email))
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var userPreferences = (await preferences.GetAllAsync())
            .Where(x => x.EventType == eventType && x.Channel == channel)
            .ToDictionary(x => x.UserId, StringComparer.OrdinalIgnoreCase);

        var recipients = new List<SupportNotificationRecipient>();
        foreach (var subscription in activeSubscriptions)
        {
            if (subscription.RecipientType == SupportNotificationRecipientType.SupportGroup)
            {
                if (!coveredGroupIds.Contains(subscription.RecipientId) ||
                    !activeGroupById.TryGetValue(subscription.RecipientId, out var group))
                {
                    logger.LogWarning(
                        "Skipping support notification subscription with invalid support group recipient. SubscriptionId={SubscriptionId} RecipientId={RecipientId}",
                        subscription.Id,
                        subscription.RecipientId);
                    continue;
                }

                if (!activeMemberLookup.TryGetValue(group.Id, out var members))
                {
                    continue;
                }

                foreach (var member in members)
                {
                    AddRecipientIfEnabled(recipients, activeUsers, userPreferences, member.UserId, group);
                }
                continue;
            }

            if (!activeMembers.Any(x => string.Equals(x.UserId, subscription.RecipientId, StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogWarning(
                    "Skipping support notification subscription with user recipient outside active coverage. SubscriptionId={SubscriptionId} RecipientId={RecipientId}",
                    subscription.Id,
                    subscription.RecipientId);
                continue;
            }

            AddRecipientIfEnabled(recipients, activeUsers, userPreferences, subscription.RecipientId, null);
        }

        return Deduplicate(recipients);
    }

    public async Task<IReadOnlyList<SupportNotificationRecipient>> ResolveAssignmentRecipientsAsync(
        Ticket ticket,
        string newAssignedUserId,
        SupportNotificationChannel channel,
        CancellationToken ct = default)
    {
        _ = ticket;
        _ = ct;

        if (channel != SupportNotificationChannel.Email || string.IsNullOrWhiteSpace(newAssignedUserId))
        {
            return [];
        }

        var user = await users.GetAsync(newAssignedUserId);
        if (user is null || string.IsNullOrWhiteSpace(user.Email))
        {
            return [];
        }

        var preference = (await preferences.GetAllAsync()).FirstOrDefault(x =>
            x.EventType == SupportNotificationEventType.TicketAssigned &&
            x.Channel == channel &&
            string.Equals(x.UserId, user.Id, StringComparison.OrdinalIgnoreCase));
        if (preference is { IsEnabled: false })
        {
            return [];
        }

        return [new SupportNotificationRecipient(user.Id, user.Name, user.Email.Trim())];
    }

    private static void AddRecipientIfEnabled(
        List<SupportNotificationRecipient> recipients,
        IReadOnlyDictionary<string, User> activeUsers,
        IReadOnlyDictionary<string, UserSupportNotificationPreference> preferencesByUserId,
        string userId,
        SupportGroup? group)
    {
        if (!activeUsers.TryGetValue(userId, out var user))
        {
            return;
        }

        if (preferencesByUserId.TryGetValue(user.Id, out var preference) && !preference.IsEnabled)
        {
            return;
        }

        recipients.Add(new SupportNotificationRecipient(
            user.Id,
            user.Name,
            user.Email.Trim(),
            group?.Id,
            group?.Name));
    }

    private static IReadOnlyList<SupportNotificationRecipient> Deduplicate(IEnumerable<SupportNotificationRecipient> recipients)
    {
        var byUser = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byEmail = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SupportNotificationRecipient>();

        foreach (var recipient in recipients)
        {
            if (!string.IsNullOrWhiteSpace(recipient.UserId) && !byUser.Add(recipient.UserId))
            {
                continue;
            }

            if (!byEmail.Add(recipient.Email))
            {
                continue;
            }

            result.Add(recipient);
        }

        return result;
    }
}
