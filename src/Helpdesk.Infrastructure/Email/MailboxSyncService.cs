using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

public sealed class MailboxSyncService(HelpdeskDbContext db, MailboxWorkerPolicy policy)
{
    public async Task<MailboxSyncRequestResult> RequestAsync(Guid mailboxId, CancellationToken ct)
    {
        var mailbox = await db.EmailInboxSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mailboxId, ct);
        if (mailbox is null) return new(mailboxId, 0, "Not configured");
        var worker = await policy.GetStatusAsync(ct);
        if (!worker.DeploymentPermitsIngestion) return new(mailboxId, 0, "Disabled by deployment");
        if (!worker.InstanceRunning) return new(mailboxId, 0, "Instance paused");
        if (mailbox.Archived || !mailbox.Enabled) return new(mailboxId, 0, "Mailbox disabled");
        if (!mailbox.BackgroundSyncEnabled) return new(mailboxId, 0, "Incoming paused");

        var changed = await db.Set<MailboxIngestionState>()
            .Where(x => x.MailboxId == mailboxId && x.SyncRequestedVersion == x.SyncCompletedVersion)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.SyncRequestedVersion,
                x => x.SyncRequestedVersion + 1), ct);
        var state = await db.Set<MailboxIngestionState>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.MailboxId == mailboxId, ct);
        return state is null ? new(mailboxId, 0, "Not configured")
            : new(mailboxId, state.SyncRequestedVersion, changed == 1 ? "Queued" : "Already queued");
    }
}
