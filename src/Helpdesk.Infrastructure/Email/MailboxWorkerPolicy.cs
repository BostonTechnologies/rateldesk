using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Infrastructure.Email;

public sealed record MailboxWorkerStatus(bool DeploymentPermitsIngestion, bool InstanceRunning,
    string State, string? BlockedBy, long? ControlVersion);

/// <summary>One deployment and persisted operator policy for every ingestion entry point.</summary>
public sealed class MailboxWorkerPolicy(HelpdeskDbContext db, IConfiguration configuration, TimeProvider clock)
{
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
            return new(false, false, "Disabled by deployment", "EmailIngestion:Enabled=false", control?.Version);
        return running
            ? new(true, true, "Instance enabled", null, control?.Version)
            : new(true, false, "Instance paused", null, control?.Version);
    }

    public async Task<MailboxWorkerStatus> SetRunningAsync(bool running, CancellationToken ct)
    {
        if (running && !DeploymentPermitsIngestion)
            return await GetStatusAsync(ct);
        var control = await db.Set<MailboxWorkerControl>().SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (control is null)
        {
            control = new MailboxWorkerControl { Running = running,
                UpdatedUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds() };
            db.Set<MailboxWorkerControl>().Add(control);
        }
        else
        {
            control.Running = running;
            control.Version++;
            control.UpdatedUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds();
        }
        await db.SaveChangesAsync(ct);
        return await GetStatusAsync(ct);
    }
}
