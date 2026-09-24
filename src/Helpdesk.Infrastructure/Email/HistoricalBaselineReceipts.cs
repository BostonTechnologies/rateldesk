using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Recognizes the receipt shape written by the beta.2 initial-skip producer.</summary>
public static class HistoricalBaselineReceipts
{
    public static async Task<IQueryable<InboundMessageReceipt>> EligibleAsync(
        HelpdeskDbContext db, EmailInboxSettings mailbox, CancellationToken ct)
    {
        var state = await db.Set<MailboxIngestionState>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.MailboxId == mailbox.Id, ct);
        var legacySourceInitialized = state is { Initialized: true } && state.SourceKey == mailbox.SourceKey;
        return db.Set<InboundMessageReceipt>().Where(x => x.MailboxId == mailbox.Id &&
            x.SourceKey == mailbox.SourceKey && x.Outcome == InboundReceiptOutcome.Ignored &&
            (x.Reason == "InitialBaselineSkipped" ||
             legacySourceInitialized && x.Reason == null && x.Acknowledged &&
             x.AcknowledgmentStatus == InboundAcknowledgmentStatus.NotRequired &&
             x.AcknowledgmentAttempts == 0 && x.Attempts == 0 &&
             x.ProtectedEnvelope == "" && x.InternetMessageId == null &&
             x.OrganizationId == null && x.TicketId == null));
    }

    public static bool IsLegacySourceIdentity(EmailInboxSettings mailbox, string key) =>
        mailbox.Provider switch
        {
            InboundMailboxProvider.Imap => TryImapIdentity(key),
            InboundMailboxProvider.Pop3 => key.Length is > 0 and <= 1024,
            InboundMailboxProvider.Graph => key.Length is > 0 and <= 2048,
            _ => false
        };

    private static bool TryImapIdentity(string key)
    {
        var parts = key.Split(':');
        return parts.Length == 2 && uint.TryParse(parts[0], out var epoch) && epoch > 0 &&
            uint.TryParse(parts[1], out var uid) && uid > 0;
    }
}
