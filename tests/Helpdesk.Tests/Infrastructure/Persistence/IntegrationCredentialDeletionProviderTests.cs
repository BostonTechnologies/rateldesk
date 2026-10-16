using System.Data.Common;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed class IntegrationCredentialDeletionProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_commits_audit_and_delivery_cancellation_and_fences_stale_pairing_after_restart(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using (var domain = fixture.Open()) await domain.Database.MigrateAsync();
        var credential = Credential("selected");
        var unrelated = Credential("unrelated");
        var pair = new IntegrationCredentialPairing
        {
            Id = Guid.NewGuid(), DeviceHash = new string('a', 64), HumanHash = new string('b', 64), Name = "selected delivery",
            Purpose = "api", State = "issued", CredentialId = credential.Id, OwnerUserId = "owner",
            ProtectedApproval = "synthetic protected approval", ProtectedDelivery = "synthetic protected delivery",
            ExpiresAtUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds()
        };
        await using (var identity = fixture.Identity())
        {
            await identity.Database.MigrateAsync();
            identity.IntegrationCredentials.AddRange(credential, unrelated);
            identity.IntegrationCredentialPairings.Add(pair);
            await identity.SaveChangesAsync();
        }
        await using var stale = fixture.Identity();
        var stalePair = await stale.IntegrationCredentialPairings.SingleAsync();
        await using (var identity = fixture.Identity())
        {
            var lifecycle = new IntegrationCredentialLifecycle(identity);
            Assert.False(await lifecycle.DeleteOwnedAsync("other", credential.Id, default));
            Assert.True(await lifecycle.DeleteOwnedAsync("owner", credential.Id, default));
            Assert.False(await lifecycle.DeleteOwnedAsync("owner", credential.Id, default));
        }
        stalePair.State = "issued";
        stalePair.Revision++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        await using (var restarted = fixture.Identity())
        {
            Assert.Equal(unrelated.Id, (await restarted.IntegrationCredentials.SingleAsync()).Id);
            var cancelled = await restarted.IntegrationCredentialPairings.SingleAsync();
            Assert.Equal("cancelled", cancelled.State);
            Assert.Null(cancelled.ProtectedDelivery);
            Assert.Null(cancelled.ProtectedApproval);
        }
        await using var audit = fixture.Open();
        var entry = await audit.ActivityLogs.SingleAsync(a => a.RelatedEntityId == credential.Id.ToString("N"));
        Assert.Equal("owner", entry.UserId);
        Assert.Contains("revoked and deleted", entry.Message);
        Assert.DoesNotContain(credential.SecretHash, entry.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_failure_rolls_back_authentication_removal_on_each_provider(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using (var domain = fixture.Open()) await domain.Database.MigrateAsync();
        var credential = Credential("audit failure");
        await using (var identity = fixture.Identity())
        {
            await identity.Database.MigrateAsync();
            identity.IntegrationCredentials.Add(credential);
            await identity.SaveChangesAsync();
            await identity.Database.ExecuteSqlRawAsync("ALTER TABLE \"ActivityLogs\" RENAME TO \"UnavailableActivityLogs\"");
            await Assert.ThrowsAnyAsync<DbException>(() => new IntegrationCredentialLifecycle(identity).DeleteOwnedAsync("owner", credential.Id, default));
            Assert.True(await identity.IntegrationCredentials.AsNoTracking().AnyAsync(c => c.Id == credential.Id));
            await identity.Database.ExecuteSqlRawAsync("ALTER TABLE \"UnavailableActivityLogs\" RENAME TO \"ActivityLogs\"");
        }
    }

    private static IntegrationCredential Credential(string name) => new()
    {
        Id = Guid.NewGuid(), OwnerUserId = "owner", Name = name, Prefix = "rdk_synthetic", SecretHash = "synthetic-non-secret-verifier",
        Purpose = "api", OrganizationId = "organization", Permissions = "Incident.Read", CreatedAtUtc = DateTimeOffset.UtcNow,
        CreatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30)
    };
}
