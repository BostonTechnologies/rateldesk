using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

public sealed record MailboxSenderSelection(string Status, string? ErrorCode, string? OrganizationId,
    EmailInboxSettings? Mailbox, MailboxOutgoingSettings? Outgoing);

/// <summary>Resolves a sender from authoritative business context, never recipient addresses.</summary>
public sealed class MailboxSenderResolver(HelpdeskDbContext db)
{
    public async Task<MailboxSenderSelection> ResolveAsync(string? ticketId, string? organizationId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(ticketId))
        {
            var ticketOrganization = await db.Tickets.IgnoreQueryFilters()
                .Where(x => x.Id == ticketId).Select(x => x.OrganizationId).SingleOrDefaultAsync(ct);
            if (ticketOrganization is null)
                return new("Blocked", "TicketNotFound", organizationId, null, null);
            if (organizationId is not null && !string.Equals(ticketOrganization, organizationId, StringComparison.Ordinal))
                return new("Blocked", "TicketOrganizationMismatch", organizationId, null, null);
            organizationId = ticketOrganization;
        }

        var assignments = await db.EmailInboxSettings.AsNoTracking().Where(x => !x.Archived &&
            (x.Scope == MailboxScope.Global ||
             organizationId != null && x.Scope == MailboxScope.Organization && x.OrganizationId == organizationId))
            .ToListAsync(ct);
        var mailbox = organizationId is null ? assignments.SingleOrDefault(x => x.Scope == MailboxScope.Global)
            : assignments.SingleOrDefault(x => x.Scope == MailboxScope.Organization) ??
              assignments.SingleOrDefault(x => x.Scope == MailboxScope.Global);
        if (mailbox is null)
            return new("Blocked", "NoEffectiveMailbox", organizationId, null, null);
        if (!mailbox.Enabled)
            return new("Blocked", "MailboxDisabled", organizationId, mailbox, null);
        var outgoing = await db.Set<MailboxOutgoingSettings>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.MailboxId == mailbox.Id, ct);
        if (outgoing is null)
            return new("Blocked", "OutgoingNotConfigured", organizationId, mailbox, null);
        if (!outgoing.Enabled)
            return new("Blocked", "OutgoingDisabled", organizationId, mailbox, outgoing);
        if (outgoing.Transport == MailboxOutgoingTransport.Smtp && outgoing.ProtectedSmtpPassword.Length == 0)
            return new("Blocked", "SmtpCredentialMissing", organizationId, mailbox, outgoing);
        if (outgoing.Transport == MailboxOutgoingTransport.Graph &&
            (mailbox.Authentication != MailboxAuthentication.MicrosoftApplication || mailbox.ClientSecret.Length == 0))
            return new("Blocked", "GraphCredentialMissing", organizationId, mailbox, outgoing);
        return new("Ready", null, organizationId, mailbox, outgoing);
    }
}
