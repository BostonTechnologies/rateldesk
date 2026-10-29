using System.Data.Common;
using System.Security.Claims;
using Helpdesk.API.Authentication;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Shared.DTOs.Auth;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed class IntegrationCredentialAccessProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reviewed_grants_and_audit_commit_together_and_survive_restart_on_each_provider(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        var created = DateTimeOffset.UtcNow;
        var credential = new IntegrationCredential
        {
            Id = Guid.NewGuid(), OwnerUserId = "owner", Name = "Existing client", Prefix = "rdk_synthetic",
            SecretHash = "unchanged-synthetic-secret-verifier", Purpose = "mcp", OrganizationId = "org-a",
            McpResourceUri = "https://desk.example/mcp", Permissions = "Incident.Read",
            CreatedAtUtc = created, CreatedAtUnixMilliseconds = created.ToUnixTimeMilliseconds(), ExpiresAtUtc = created.AddDays(30)
        };
        await using (var identity = fixture.Identity())
        {
            await identity.Database.MigrateAsync();
            identity.Users.Add(new() { Id = "owner", UserName = "owner", IsEnabled = true, IsInstanceAdministrator = true });
            identity.IntegrationCredentials.Add(credential);
            await identity.SaveChangesAsync();
        }
        await using (var domain = fixture.Open())
        {
            await domain.Database.MigrateAsync();
            domain.Organizations.Add(new() { Id = "org-a", Name = "Organization A", IsEnabled = true });
            await domain.SaveChangesAsync();
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim("auth_mode", "local")], "Local"));
        IntegrationCredentialAccessReview approved;
        IntegrationCredential persisted;
        await using (var identity = fixture.Identity())
        await using (var domain = fixture.Open())
        {
            persisted = await identity.IntegrationCredentials.AsNoTracking().SingleAsync();
            var service = new IntegrationCredentialAccessService(new IntegrationCredentialOwnerResolver(domain, identity),
                new CurrentUserAccessService(domain, identity), domain, identity, fixture.Protection(), TimeProvider.System);
            var current = await service.GetAsync(credential.Id, principal, default);
            approved = await service.ReviewAsync(credential.Id,
                new IntegrationCredentialAccessDraft(["Incident.Read", "Incident.Write"], current.Version), principal, default);
            var saved = await service.ApplyAsync(credential.Id, new(approved.Confirmation, true), principal, default);
            Assert.Equal(["Incident.Read", "Incident.Write"], saved.EffectivePermissions);
        }
        // A different scope and Data Protection instance can replay a confirmed review without
        // rewriting grants or issuing a new secret, including PostgreSQL timestamp precision.
        await using (var identity = fixture.Identity())
        await using (var domain = fixture.Open())
        {
            var service = new IntegrationCredentialAccessService(new IntegrationCredentialOwnerResolver(domain, identity),
                new CurrentUserAccessService(domain, identity), domain, identity, fixture.Protection(), TimeProvider.System);
            var replay = await service.ApplyAsync(credential.Id, new(approved.Confirmation, true), principal, default);
            Assert.Equal(["Incident.Read", "Incident.Write"], replay.Permissions);
            var retained = await identity.IntegrationCredentials.AsNoTracking().SingleAsync();
            Assert.Equal(persisted.SecretHash, retained.SecretHash);
            Assert.Equal(persisted.ExpiresAtUtc, retained.ExpiresAtUtc);
            Assert.Equal(persisted.McpResourceUri, retained.McpResourceUri);
            var audit = await domain.ActivityLogs.SingleAsync(x => x.RelatedEntityId == credential.Id.ToString("N"));
            Assert.Equal("owner", audit.UserId);
            Assert.DoesNotContain(persisted.SecretHash, audit.Message);
            var nextReview = await service.ReviewAsync(credential.Id,
                new IntegrationCredentialAccessDraft(["Incident.Read"], replay.Version), principal, default);
            await identity.Database.ExecuteSqlRawAsync("ALTER TABLE \"ActivityLogs\" RENAME TO \"UnavailableActivityLogs\"");
            try
            {
                await Assert.ThrowsAnyAsync<DbException>(() => service.ApplyAsync(credential.Id,
                    new(nextReview.Confirmation, true), principal, default));
                Assert.Equal("Incident.Read Incident.Write", (await identity.IntegrationCredentials.AsNoTracking().SingleAsync()).Permissions);
            }
            finally
            {
                await identity.Database.ExecuteSqlRawAsync("ALTER TABLE \"UnavailableActivityLogs\" RENAME TO \"ActivityLogs\"");
            }
        }
    }
}
