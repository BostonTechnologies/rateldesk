using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Identity;

/// <summary>Owner-bound setup receipt. Only a short-lived, protected secret delivery is recoverable.</summary>
public sealed class IntegrationCredentialSetupAttempt
{
    public Guid RequestId { get; set; }
    public string OwnerUserId { get; set; } = "";
    public Guid CredentialId { get; set; }
    public string RequestHash { get; set; } = "";
    public string State { get; set; } = "Delivered";
    public long DeliveryExpiresAtUnixMilliseconds { get; set; }
    public string? ProtectedDelivery { get; set; }
    public long Revision { get; set; }
}

public static class IntegrationCredentialSetupAuthorization
{
    public static async Task<bool> IsUsableAsync(RatelDeskIdentityDbContext db, Guid credentialId,
        DateTimeOffset now, CancellationToken ct = default)
    {
        var deadline = now.ToUnixTimeMilliseconds();
        return !await db.IntegrationCredentialSetupAttempts.AsNoTracking().AnyAsync(attempt =>
            attempt.CredentialId == credentialId && attempt.State != "Acknowledged" &&
            (attempt.State != "Delivered" || attempt.DeliveryExpiresAtUnixMilliseconds <= deadline), ct);
    }
}

public static class IntegrationCredentialSetupMaintenance
{
    public static async Task ExpireAsync(RatelDeskIdentityDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var deadline = now.ToUnixTimeMilliseconds();
        var expired = await db.IntegrationCredentialSetupAttempts.AsNoTracking()
            .Where(x => x.State == "Delivered" && x.DeliveryExpiresAtUnixMilliseconds <= deadline)
            .OrderBy(x => x.DeliveryExpiresAtUnixMilliseconds).Take(100).ToListAsync(ct);
        foreach (var attempt in expired)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.IntegrationCredentialSetupAttempts
                .Where(x => x.RequestId == attempt.RequestId && x.Revision == attempt.Revision && x.State == "Delivered")
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.State, "Expired")
                    .SetProperty(x => x.ProtectedDelivery, (string?)null).SetProperty(x => x.Revision, x => x.Revision + 1), ct);
            if (changed == 1)
                await db.IntegrationCredentials.Where(x => x.Id == attempt.CredentialId && x.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAtUtc, now), ct);
            await transaction.CommitAsync(ct);
        }
    }
}
