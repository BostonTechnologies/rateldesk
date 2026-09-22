using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

public sealed record MailboxLeaseToken(Guid MailboxId, string Owner, long Fence);

/// <summary>
/// Database-coordinated mailbox ownership. Each concurrent operation needs its own DbContext.
/// A lease token is valid only while unexpired; renewal cannot resurrect an expired token.
/// </summary>
public sealed class MailboxLeaseStore(HelpdeskDbContext db, TimeProvider timeProvider)
{
    public async Task<MailboxLeaseToken?> TryAcquireAsync(
        Guid mailboxId, string owner, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var expires = Expiry(now, duration);
        var candidate = await db.Set<MailboxLease>().AsNoTracking()
            .Where(x => x.MailboxId == mailboxId && x.ExpiresUnixMilliseconds <= now)
            .Select(x => new { x.Fence })
            .SingleOrDefaultAsync(cancellationToken);
        if (candidate is null)
            return null;

        var nextFence = checked(candidate.Fence + 1);
        var changed = await db.Set<MailboxLease>()
            .Where(x => x.MailboxId == mailboxId && x.Fence == candidate.Fence && x.ExpiresUnixMilliseconds <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Owner, owner)
                .SetProperty(x => x.Fence, nextFence)
                .SetProperty(x => x.ExpiresUnixMilliseconds, expires), cancellationToken);
        return changed == 1 ? new MailboxLeaseToken(mailboxId, owner, nextFence) : null;
    }

    public async Task<bool> RenewAsync(
        MailboxLeaseToken token, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var expires = Expiry(now, duration);
        return await Owned(token, now).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.ExpiresUnixMilliseconds,
                x => x.ExpiresUnixMilliseconds > expires ? x.ExpiresUnixMilliseconds : expires), cancellationToken) == 1;
    }

    public async Task ReleaseAsync(MailboxLeaseToken token, CancellationToken cancellationToken = default)
    {
        await db.Set<MailboxLease>()
            .Where(x => x.MailboxId == token.MailboxId && x.Owner == token.Owner && x.Fence == token.Fence)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Owner, (string?)null)
                .SetProperty(x => x.ExpiresUnixMilliseconds, 0L), cancellationToken);
    }

    /// <summary>
    /// Locks current ownership until the caller commits or rolls back its business transaction.
    /// Call before business writes and abort the transaction when false is returned.
    /// </summary>
    public async Task<bool> FenceAsync(MailboxLeaseToken token, CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Mailbox fencing requires the caller's business transaction.");

        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        // An UPDATE (even unchanged values) acquires the database write lock. A SELECT
        // would allow another replica to take ownership between validation and commit.
        return await Owned(token, now).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.ExpiresUnixMilliseconds, x => x.ExpiresUnixMilliseconds), cancellationToken) == 1;
    }

    private IQueryable<MailboxLease> Owned(MailboxLeaseToken token, long now) => db.Set<MailboxLease>()
        .Where(x => x.MailboxId == token.MailboxId && x.Owner == token.Owner && x.Fence == token.Fence
            && x.ExpiresUnixMilliseconds > now);

    private static long Expiry(long now, TimeSpan duration)
    {
        if (duration < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be at least one millisecond.");
        return checked(now + (long)duration.TotalMilliseconds);
    }
}
