using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Infrastructure.Email;

/// <summary>One deployment and persisted operator policy for every ingestion entry point.</summary>
public sealed class MailboxWorkerPolicy(HelpdeskDbContext db, IConfiguration configuration, TimeProvider clock)
{
    private static readonly long ProcessStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private const long HeartbeatGraceMilliseconds = 30000;
    public bool DeploymentPermitsIngestion =>
        !bool.TryParse(configuration["EmailIngestion:Enabled"], out var enabled) || enabled;

    public async Task<MailboxWorkerStatus> GetStatusAsync(CancellationToken ct)
    {
        var control = await db.Set<MailboxWorkerControl>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
        // Explicit true from a previous deployment retains its run intent. A fresh
        // installation has no persisted activation and remains paused until an admin starts it.
        var running = control?.Running ??
            bool.TryParse(configuration["EmailIngestion:Enabled"], out var legacyEnabled) && legacyEnabled;
        if (!DeploymentPermitsIngestion)
            return new(false, false, "Disabled by deployment", "EmailIngestion:Enabled=false", control?.Version,
                control?.LastHeartbeatUnixMilliseconds);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var lastSeen = control?.LastHeartbeatUnixMilliseconds;
        var unavailable = running && now - (lastSeen ?? control?.UpdatedUnixMilliseconds ?? ProcessStartedUnixMilliseconds)
            > HeartbeatGraceMilliseconds;
        return running
            ? new(true, true, unavailable ? "Worker unavailable" : "Instance enabled", null, control?.Version, lastSeen)
            : new(true, false, "Instance paused", null, control?.Version, lastSeen);
    }

    public async Task RecordHeartbeatAsync(CancellationToken ct)
    {
        if (!DeploymentPermitsIngestion) return;
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var updated = await db.Set<MailboxWorkerControl>().Where(x => x.Id == 1 && x.Running)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.LastHeartbeatUnixMilliseconds, now), ct);
        if (updated != 0 || !bool.TryParse(configuration["EmailIngestion:Enabled"], out var legacyEnabled) || !legacyEnabled)
            return;
        // Legacy explicit activation may predate the persisted run-control row.
        var control = new MailboxWorkerControl { Running = true, UpdatedUnixMilliseconds = now,
            LastHeartbeatUnixMilliseconds = now };
        db.Set<MailboxWorkerControl>().Add(control);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(control).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            await db.Set<MailboxWorkerControl>().Where(x => x.Id == 1 && x.Running)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.LastHeartbeatUnixMilliseconds, now), ct);
        }
    }

    public async Task<MailboxWorkerStatus> SetRunningAsync(bool running, CancellationToken ct)
    {
        if (running && !DeploymentPermitsIngestion)
            return await GetStatusAsync(ct);
        var control = await db.Set<MailboxWorkerControl>().SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (control is null)
        {
            control = new MailboxWorkerControl { Running = running,
                UpdatedUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                LastHeartbeatUnixMilliseconds = null };
            db.Set<MailboxWorkerControl>().Add(control);
        }
        else
        {
            control.Running = running;
            control.Version++;
            control.UpdatedUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds();
            control.LastHeartbeatUnixMilliseconds = null;
        }
        await db.SaveChangesAsync(ct);
        return await GetStatusAsync(ct);
    }

    public async Task<MailboxEffectiveStatusDto?> GetMailboxStatusAsync(Guid mailboxId, CancellationToken ct)
    {
        var mailbox = await db.EmailInboxSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mailboxId, ct);
        if (mailbox is null) return null;
        var instance = await GetStatusAsync(ct);
        var state = await db.Set<MailboxIngestionState>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.MailboxId == mailboxId, ct);
        var lease = await db.Set<MailboxLease>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.MailboxId == mailboxId, ct);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var active = lease?.Owner is not null && lease.ExpiresUnixMilliseconds > now;
        var receipts = db.Set<InboundMessageReceipt>().AsNoTracking().Where(x => x.MailboxId == mailboxId);
        var captured = await receipts.CountAsync(ct);
        var skipped = await receipts.CountAsync(x => x.Reason == "InitialBaselineSkipped", ct);
        var held = await receipts.CountAsync(x => x.Outcome == InboundReceiptOutcome.NeedsReview, ct);
        var status = !instance.DeploymentPermitsIngestion ? "Disabled by deployment"
            : !instance.InstanceRunning ? "Instance paused"
            : instance.State == "Worker unavailable" ? "Worker unavailable"
            : mailbox.Archived || !mailbox.Enabled ? "Mailbox disabled"
            : !mailbox.BackgroundSyncEnabled ? "Incoming paused"
            : state?.ErrorCode is not null && state.NextRetryUnixMilliseconds > now ? "Retry scheduled"
            : held > 0 ? "Needs review"
            : active && state?.Initialized != true ? "Initializing baseline"
            : active ? "Running"
            : state?.Initialized != true ? "Waiting for baseline"
            : "Waiting for next poll";
        return new(mailboxId, status, instance.BlockedBy, instance.DeploymentPermitsIngestion,
            instance.InstanceRunning, mailbox.Enabled && !mailbox.Archived, mailbox.BackgroundSyncEnabled,
            state?.Initialized == true, active, state?.LastSyncUnixMilliseconds,
            state?.NextRetryUnixMilliseconds, captured, skipped, held);
    }
}
