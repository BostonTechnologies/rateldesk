using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

public sealed record MailboxSenderSelection(string Status, string? ErrorCode, string? OrganizationId,
    EmailInboxSettings? Mailbox, MailboxOutgoingSettings? Outgoing);

/// <summary>Resolves a sender from authoritative business context, never recipient addresses.</summary>
public sealed class MailboxSenderResolver(HelpdeskDbContext db)
{
    public Task<bool> AnyConfiguredSenderAsync(CancellationToken ct) =>
        (from outgoing in db.Set<MailboxOutgoingSettings>().AsNoTracking()
         join mailbox in db.EmailInboxSettings.AsNoTracking() on outgoing.MailboxId equals mailbox.Id
         where outgoing.Enabled && mailbox.Enabled && !mailbox.Archived
         select outgoing.MailboxId).AnyAsync(ct);

    public async Task<MailboxSenderSelection> ResolveAsync(string? ticketId, string? organizationId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(ticketId))
        {
            // A new ticket may be tracked but not flushed yet in the ingress transaction.
            var ticketOrganization = db.ChangeTracker.Entries<Ticket>()
                .Where(x => x.Entity.Id == ticketId && x.State != Microsoft.EntityFrameworkCore.EntityState.Deleted)
                .Select(x => x.Entity.OrganizationId).SingleOrDefault()
                ?? await db.Tickets.IgnoreQueryFilters()
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
        return await CheckMailboxAsync(mailbox, organizationId, ct);
    }

    public async Task<MailboxSenderSelection> ResolveExplicitAsync(Guid mailboxId, CancellationToken ct)
    {
        var mailbox = await db.EmailInboxSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mailboxId, ct);
        if (mailbox is null) return new("Blocked", "MailboxNotFound", null, null, null);
        if (mailbox.Archived) return new("Blocked", "MailboxArchived", mailbox.OrganizationId, mailbox, null);
        return await CheckMailboxAsync(mailbox, mailbox.OrganizationId, ct);
    }

    private async Task<MailboxSenderSelection> CheckMailboxAsync(EmailInboxSettings? mailbox,
        string? organizationId, CancellationToken ct)
    {
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
