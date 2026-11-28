using Helpdesk.Shared.Models;

namespace Helpdesk.Application.Services.SupportNotifications;

public interface ISupportNotificationService
{
    Task NotifyTicketCreatedUnassignedAsync(Ticket ticket, CancellationToken ct = default);

    Task NotifyTicketAssignedAsync(
        Ticket ticket,
        string? previousAssignedToId,
        string? newAssignedToId,
        CancellationToken ct = default);

    Task NotifyTicketAssignedToSupportGroupAsync(Ticket ticket, string transitionId,
        IReadOnlyList<SupportNotificationRecipient> recipients, CancellationToken ct = default);
}
