using Helpdesk.Shared.DTOs.PublicAccess;
using Helpdesk.Shared.Models;

namespace Helpdesk.Application.Tickets;

public interface IPublicLinkIssuer
{
    Task<string> IssueAsync(Ticket ticket, string recipient, PublicLinkPurpose purpose, string? applicationUrl,
        CancellationToken cancellationToken, string? taskId = null);
}
