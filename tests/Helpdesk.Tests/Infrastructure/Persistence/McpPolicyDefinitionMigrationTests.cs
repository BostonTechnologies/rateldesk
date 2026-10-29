using System.Security.Claims;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed class McpPolicyDefinitionMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upgrade_preserves_legacy_policy_bounds_revisions_and_audit_on_both_providers(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        var credentialId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var expiry = new DateTime(DateTime.UtcNow.AddDays(1).Ticks / 10 * 10, DateTimeKind.Utc);
        await using (var previous = fixture.Open())
        {
            await previous.Database.MigrateAsync();
            previous.Set<McpAccessPolicy>().Add(new()
            {
                CredentialId = credentialId, OrganizationId = "migration-organization", PermissionsJson = "[\"Incident.Read\"]",
                State = "Disabled", ExpiresUtc = expiry, Revision = 7
            });
            previous.Set<McpAccessPolicyAudit>().Add(new()
            {
                Id = auditId, CredentialId = credentialId, Revision = 7, Actor = "synthetic-admin",
                DefinitionJson = "{\"State\":\"Disabled\",\"legacy\":true}", OccurredUtc = expiry.AddDays(-1)
            });
            await previous.SaveChangesAsync();
            var migrations = previous.Database.GetMigrations().ToArray();
            var migrationIndex = Array.FindIndex(migrations, value => value.EndsWith("_AddMcpPolicyDefinitions", StringComparison.Ordinal));
            Assert.True(migrationIndex > 0);
            await previous.GetService<IMigrator>().MigrateAsync(migrations[migrationIndex - 1]);
        }

        await using var upgraded = fixture.Open();
        await upgraded.Database.MigrateAsync();
        await upgraded.Database.MigrateAsync();
        var policy = await upgraded.Set<McpAccessPolicy>().AsNoTracking().SingleAsync();
        Assert.Equal(credentialId, policy.CredentialId);
        Assert.Equal("migration-organization", policy.OrganizationId);
        Assert.Equal("[\"Incident.Read\"]", policy.PermissionsJson);
        Assert.Equal("Disabled", policy.State);
        Assert.Equal(expiry.Ticks, policy.ExpiresUtc.Ticks);
        Assert.Equal(7, policy.Revision);
        Assert.Equal("", policy.DefinitionJson);
        var audit = await upgraded.Set<McpAccessPolicyAudit>().AsNoTracking().SingleAsync();
        Assert.Equal(auditId, audit.Id);
        Assert.Equal(7, audit.Revision);
        Assert.Equal("{\"State\":\"Disabled\",\"legacy\":true}", audit.DefinitionJson);

        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("auth_mode", "integration"), new Claim("integration_credential_id", credentialId.ToString("N"))], "test"));
        var profile = new CurrentUserAccessProfile(true, "Synthetic", null, "migration-organization", "Synthetic organization", null, true,
            new HashSet<string>(), new HashSet<string> { HelpdeskPermissions.HelpdeskAdmin },
            new HashSet<string> { "migration-organization" }, new HashSet<string>());
        var constrained = await McpAccessPolicyEnforcement.ApplyAsync(upgraded, profile, principal, default);
        Assert.False(constrained.IsHelpdeskAdmin);
        Assert.Empty(constrained.Permissions);
        Assert.Empty(constrained.AllowedOrganizationIds);
        Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync());
    }
}
