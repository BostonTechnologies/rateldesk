using Helpdesk.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Services.IntegrationCredentials;

/// <summary>Durable removal through the existing credential authority and recovery storage.</summary>
public sealed class IntegrationCredentialLifecycle(RatelDeskIdentityDbContext identityDb)
{
    public async Task<bool> DeleteOwnedAsync(string ownerUserId, Guid credentialId, CancellationToken ct)
    {
        var credential = await identityDb.IntegrationCredentials.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == credentialId && candidate.OwnerUserId == ownerUserId, ct);
        // Missing and another owner's records have the same response, including an idempotent retry.
        if (credential is null) return false;

        await using var transaction = await identityDb.Database.BeginTransactionAsync(ct);
        // Fence in-flight delivery/acknowledgement saves before removing authentication material.
        // Pairing rows are non-authenticating recovery tombstones, not credential foreign keys.
        await identityDb.IntegrationCredentialPairings.Where(pair => pair.CredentialId == credentialId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(pair => pair.State, "cancelled")
                .SetProperty(pair => pair.ProtectedApproval, (string?)null)
                .SetProperty(pair => pair.ProtectedDelivery, (string?)null)
                .SetProperty(pair => pair.Revision, pair => pair.Revision + 1), ct);
        var removed = await identityDb.IntegrationCredentials
            .Where(candidate => candidate.Id == credentialId && candidate.OwnerUserId == ownerUserId)
            .ExecuteDeleteAsync(ct);
        if (removed > 0)
        {
            var audit = new Helpdesk.Shared.Models.ActivityLog
            {
                UserId = ownerUserId, RelatedEntityId = credentialId.ToString("N"),
                Message = $"Integration credential revoked and deleted. Prefix={credential.Prefix}; Purpose={credential.Purpose}; OrganizationId={credential.OrganizationId}; DeletedAtUtc={DateTimeOffset.UtcNow:O}."
            };
            // Both contexts use the same database. Write the existing, non-authenticating
            // audit table on this connection so attribution commits with deletion on both providers.
            await identityDb.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ActivityLogs" ("Id", "RelatedEntityId", "Timestamp", "UserId", "Message")
                VALUES ({audit.Id}, {audit.RelatedEntityId}, {audit.Timestamp}, {audit.UserId}, {audit.Message})
                """, ct);
        }
        await transaction.CommitAsync(ct);
        return true;
    }
}
