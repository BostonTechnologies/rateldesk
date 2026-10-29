using System.Data.Common;
using System.Security.Claims;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.McpPolicies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed class IntegrationCredentialSetupProviderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Setup_commits_one_policy_replays_same_secret_and_erases_delivery_on_acknowledgement(bool postgres, bool administration)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        var clock = new Clock();
        var request = Request(clock, administration);
        IntegrationCredentialSetupDelivery first;
        await using (var identity = fixture.Identity())
        await using (var domain = fixture.Open())
            first = await Service(identity, domain, fixture, clock).CreateAsync(request, Principal(), default);
        await using (var identity = fixture.Identity())
        await using (var domain = fixture.Open())
        {
            var service = Service(identity, domain, fixture, clock);
            var repeated = await service.CreateAsync(request, Principal(), default);
            Assert.Equal(first.Credential.Id, repeated.Credential.Id);
            Assert.True(first.Credential.Secret == repeated.Credential.Secret, "A delivery retry must recover the original credential.");
            Assert.Equal(request.Credential.ExpiresAtUtc, repeated.Credential.ExpiresAtUtc);
            Assert.Equal(first.HandoffExpiresAtUtc, repeated.HandoffExpiresAtUtc);
            var policy = await domain.Set<McpAccessPolicy>().SingleAsync();
            Assert.Equal(first.Credential.Id, policy.CredentialId);
            Assert.Equal(1, policy.Revision);
            Assert.Equal(administration ? "" : "setup-org", policy.OrganizationId);
            Assert.Equal(1, await domain.Set<McpAccessPolicyAudit>().CountAsync());
            Assert.Equal(1, await identity.IntegrationCredentials.CountAsync());
            var receipt = await identity.IntegrationCredentialSetupAttempts.AsNoTracking().SingleAsync();
            Assert.NotNull(receipt.ProtectedDelivery);
            Assert.False(receipt.ProtectedDelivery.Contains(first.Credential.Secret, StringComparison.Ordinal), "The durable delivery must be protected.");
            Assert.True(await IntegrationCredentialSetupAuthorization.IsUsableAsync(identity, first.Credential.Id, clock.GetUtcNow()));
            await service.AcknowledgeAsync(request.RequestId, Principal(), default);
            await service.AcknowledgeAsync(request.RequestId, Principal(), default);
            var acknowledged = await identity.IntegrationCredentialSetupAttempts.AsNoTracking().SingleAsync();
            Assert.Equal("Acknowledged", acknowledged.State);
            Assert.Null(acknowledged.ProtectedDelivery);
            clock.Advance(TimeSpan.FromMinutes(6));
            Assert.True(await IntegrationCredentialSetupAuthorization.IsUsableAsync(identity, first.Credential.Id, clock.GetUtcNow()));
            var closed = await Assert.ThrowsAsync<IntegrationSetupException>(() => service.CreateAsync(request, Principal(), default));
            Assert.Equal("setup_acknowledged", closed.Code);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Policy_write_failure_rolls_back_credential_receipt_and_audits_then_same_request_retries(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        var clock = new Clock();
        var request = Request(clock);
        await using (var domain = fixture.Open())
        {
            if (postgres)
                await domain.Database.ExecuteSqlRawAsync("""
                    CREATE FUNCTION reject_setup_policy() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic policy failure'; END; $$;
                    CREATE TRIGGER reject_setup_policy BEFORE INSERT ON "McpAccessPolicy" FOR EACH ROW EXECUTE FUNCTION reject_setup_policy();
                    """);
            else
                await domain.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER reject_setup_policy BEFORE INSERT ON "McpAccessPolicy" BEGIN SELECT RAISE(ABORT, 'synthetic policy failure'); END;
                    """);
        }
        await using (var identity = fixture.Identity())
        await using (var domain = fixture.Open())
            await Assert.ThrowsAnyAsync<DbException>(() => Service(identity, domain, fixture, clock).CreateAsync(request, Principal(), default));
        await using (var identity = fixture.Identity())
        await using (var domain = fixture.Open())
        {
            Assert.Empty(await identity.IntegrationCredentials.ToListAsync());
            Assert.Empty(await identity.IntegrationCredentialSetupAttempts.ToListAsync());
            Assert.Empty(await domain.Set<McpAccessPolicy>().ToListAsync());
            Assert.Empty(await domain.Set<McpAccessPolicyAudit>().ToListAsync());
            Assert.Empty(await domain.ActivityLogs.ToListAsync());
            if (postgres)
                await domain.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_setup_policy ON \"McpAccessPolicy\"; DROP FUNCTION reject_setup_policy();");
            else
                await domain.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_setup_policy;");
            var delivered = await Service(identity, domain, fixture, clock).CreateAsync(request, Principal(), default);
            Assert.NotEqual(Guid.Empty, delivered.Credential.Id);
            Assert.Equal(1, await identity.IntegrationCredentials.CountAsync());
            Assert.Equal(1, await domain.Set<McpAccessPolicy>().CountAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_unacknowledged_delivery_denies_auth_without_cleanup_and_is_revoked_on_retry(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        var clock = new Clock();
        var request = Request(clock);
        await using var identity = fixture.Identity();
        await using var domain = fixture.Open();
        var service = Service(identity, domain, fixture, clock);
        var delivered = await service.CreateAsync(request, Principal(), default);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(await IntegrationCredentialSetupAuthorization.IsUsableAsync(identity, delivered.Credential.Id, clock.GetUtcNow()));
        Assert.Null((await identity.IntegrationCredentials.AsNoTracking().SingleAsync()).RevokedAtUtc);
        var expired = await Assert.ThrowsAsync<IntegrationSetupException>(() => service.CreateAsync(request, Principal(), default));
        Assert.Equal("setup_delivery_expired", expired.Code);
        Assert.NotNull((await identity.IntegrationCredentials.AsNoTracking().SingleAsync()).RevokedAtUtc);
        var receipt = await identity.IntegrationCredentialSetupAttempts.AsNoTracking().SingleAsync();
        Assert.Equal("Expired", receipt.State);
        Assert.Null(receipt.ProtectedDelivery);
        Assert.Equal(1, await identity.IntegrationCredentials.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_creates_and_restarted_retries_preserve_single_issuance(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        var clock = new Clock();
        var request = Request(clock);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var identity = fixture.Identity();
            await using var domain = fixture.Open();
            try { return (Delivery: await Service(identity, domain, fixture, clock).CreateAsync(request, Principal(), default), Contention: false); }
            catch (DbUpdateException) { return (Delivery: (IntegrationCredentialSetupDelivery?)null, Contention: true); }
            catch (DbException) { return (Delivery: (IntegrationCredentialSetupDelivery?)null, Contention: true); }
        }));
        var completed = results.Where(x => x.Delivery is not null).Select(x => x.Delivery!).ToArray();
        Assert.NotEmpty(completed);
        Assert.All(completed, result => Assert.Equal(completed[0].Credential.Id, result.Credential.Id));
        Assert.True(results.All(x => x.Delivery is not null || x.Contention));
        await using var verifyIdentity = fixture.Identity();
        await using var verifyDomain = fixture.Open();
        var recovered = await Service(verifyIdentity, verifyDomain, fixture, clock).CreateAsync(request, Principal(), default);
        Assert.Equal(completed[0].Credential.Id, recovered.Credential.Id);
        Assert.Equal(1, await verifyIdentity.IntegrationCredentials.CountAsync());
        Assert.Equal(1, await verifyIdentity.IntegrationCredentialSetupAttempts.CountAsync());
        Assert.Equal(1, await verifyDomain.Set<McpAccessPolicyAudit>().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migrations_preserve_existing_credentials_and_enforce_request_identity_and_scope(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using (var domain = fixture.Open()) await domain.Database.MigrateAsync();
        await using (var identity = fixture.Identity())
        {
            var preceding = identity.Database.GetMigrations().Last(x => !x.EndsWith("AddIntegrationSetupAttempts", StringComparison.Ordinal));
            await identity.GetService<IMigrator>().MigrateAsync(preceding);
            identity.IntegrationCredentials.Add(new IntegrationCredential { Id = Guid.NewGuid(), Name = "Existing", OwnerUserId = "setup-owner", Prefix = "rdk_existing", SecretHash = "retained-verifier", Purpose = "api", OrganizationId = "setup-org", Permissions = "Incident.Read", CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30) });
            await identity.SaveChangesAsync();
            await identity.Database.MigrateAsync();
            Assert.False(identity.Database.HasPendingModelChanges());
            Assert.Equal("retained-verifier", (await identity.IntegrationCredentials.SingleAsync()).SecretHash);
            Assert.True(await IntegrationCredentialSetupAuthorization.IsUsableAsync(identity, (await identity.IntegrationCredentials.SingleAsync()).Id, DateTimeOffset.UtcNow));
        }
        await SeedAsync(fixture);
        var clock = new Clock();
        var request = Request(clock);
        await using var currentIdentity = fixture.Identity();
        await using var currentDomain = fixture.Open();
        var service = Service(currentIdentity, currentDomain, fixture, clock);
        await service.CreateAsync(request, Principal(), default);
        var conflict = await Assert.ThrowsAsync<IntegrationSetupException>(() => service.CreateAsync(request with { Credential = request.Credential with { Name = "Changed" } }, Principal(), default));
        Assert.Equal("setup_request_conflict", conflict.Code);
        var foreign = await Assert.ThrowsAsync<IntegrationSetupException>(() => service.CreateAsync(request, Principal("other-owner"), default));
        Assert.Equal(404, foreign.StatusCode);
        var invalid = request with { RequestId = Guid.NewGuid(), Policies = [request.Policies[0] with { OrganizationId = "foreign-org" }] };
        Assert.Equal("setup_policy_invalid", (await Assert.ThrowsAsync<IntegrationSetupException>(() => service.CreateAsync(invalid, Principal(), default))).Code);
        Assert.Equal(2, await currentIdentity.IntegrationCredentials.CountAsync());
    }

    private static async Task<ServiceIdentityMigrationTests.DatabaseFixture> CreateFixtureAsync(bool postgres)
    {
        var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using (var domain = fixture.Open()) await domain.Database.MigrateAsync();
        await using (var identity = fixture.Identity()) await identity.Database.MigrateAsync();
        await SeedAsync(fixture);
        return fixture;
    }

    private static async Task SeedAsync(ServiceIdentityMigrationTests.DatabaseFixture fixture)
    {
        await using var identity = fixture.Identity();
        identity.Users.AddRange(new ApplicationUser { Id = "setup-owner", UserName = "setup-owner", IsEnabled = true, IsInstanceAdministrator = true },
            new ApplicationUser { Id = "other-owner", UserName = "other-owner", IsEnabled = true, IsInstanceAdministrator = true });
        await identity.SaveChangesAsync();
        await using var domain = fixture.Open();
        domain.Organizations.Add(new() { Id = "setup-org", Name = "Setup organization", IsEnabled = true });
        await domain.SaveChangesAsync();
    }

    private static IntegrationCredentialSetupRequest Request(Clock clock, bool administration = false)
    {
        var expiry = new DateTimeOffset(clock.GetUtcNow().AddDays(30).Ticks / 10 * 10, TimeSpan.Zero);
        string[] permissions = administration ? [HelpdeskPermissions.HelpdeskAdmin] : [HelpdeskPermissions.IncidentRead];
        return new(Guid.NewGuid(), new("Reviewed integration", "mcp", "setup-org", permissions, null)
        {
            ExpiresAtUtc = expiry, McpResourceUri = "https://desk.example.test/mcp",
            GrantKind = administration ? "owner-administration" : "ordinary", OwnerAdministrationConsent = administration
        }, [new("Reviewed base", administration ? "server" : "organization", administration ? null : "setup-org", "Allow",
            permissions, ["helpdesk_incidents.list"], expiry.UtcDateTime, [])], true);
    }

    private static ClaimsPrincipal Principal(string user = "setup-owner") => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, user), new("auth_mode", "local")], "Test"));
    private static IntegrationCredentialSetupService Service(RatelDeskIdentityDbContext identity, HelpdeskDbContext domain,
        ServiceIdentityMigrationTests.DatabaseFixture fixture, Clock clock) => new(new IntegrationCredentialOwnerResolver(domain, identity),
            new CurrentUserAccessService(domain, identity), domain, identity, fixture.Protection(), clock);
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
