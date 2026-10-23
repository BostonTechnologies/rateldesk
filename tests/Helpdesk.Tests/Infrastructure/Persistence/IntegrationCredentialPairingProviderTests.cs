using System.Security.Claims;
using Helpdesk.API.Authentication;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Tests.Infrastructure.Persistence;

/// <summary>Owning provider regression: migration upgrade plus concurrent/restarted issuance on both databases.</summary>
public sealed class IntegrationCredentialPairingProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_provider_migrations_preserve_credentials_and_atomic_single_issuance(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using (var domain = fixture.Open()) await domain.Database.MigrateAsync();
        var clock = new Clock();
        await using (var identity = fixture.Identity())
        {
            var preceding = identity.Database.GetMigrations().Last(m => !m.EndsWith("AddIntegrationCredentialPairing", StringComparison.Ordinal));
            await identity.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(preceding);
            identity.Users.Add(new() { Id = "pairing-owner", UserName = "pairing-owner", IsEnabled = true, IsInstanceAdministrator = true });
            identity.IntegrationCredentials.Add(new() { Id = Guid.NewGuid(), OwnerUserId = "pairing-owner", Name = "existing profile",
                Prefix = "rdk_preserved", SecretHash = "existing-verifier", Purpose = "api", OrganizationId = "pairing-org", Permissions = "Incident.Read",
                CreatedAtUtc = clock.GetUtcNow(), CreatedAtUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds(), ExpiresAtUtc = clock.GetUtcNow().AddDays(30) });
            await identity.SaveChangesAsync(); await identity.Database.MigrateAsync();
            Assert.Empty(await identity.Database.GetPendingMigrationsAsync());
            Assert.Equal("existing-verifier", (await identity.IntegrationCredentials.SingleAsync()).SecretHash);
        }
        await using (var domain = fixture.Open())
        {
            domain.Organizations.Add(new() { Id = "pairing-org", Name = "Pairing organization", IsEnabled = true }); await domain.SaveChangesAsync();
        }
        IntegrationPairingStarted started;
        await using (var identity = fixture.Identity())
        await using (var domain = fixture.Open())
        {
            var service = Service(identity, domain, fixture, clock);
            started = await service.StartAsync(new("provider client", "api"), "test", default);
            Assert.True(await service.ApproveAsync(new(started.HumanCode, "pairing-org", ["Incident.Read"], 30, true),
                new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "pairing-owner"), new("auth_mode", "local")], "Test")), "test", default));
        }
        var poll = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var identity = fixture.Identity(); await using var domain = fixture.Open();
            return await Service(identity, domain, fixture, clock).PollAsync(started.DeviceCode, default);
        }));
        var delivered = Assert.Single(poll, p => p.Credential is not null).Credential!;
        clock.Advance();
        await using (var identity = fixture.Identity())
        await using (var domain = fixture.Open())
        {
            var service = Service(identity, domain, fixture, clock);
            var repeated = await service.PollAsync(started.DeviceCode, default);
            Assert.True(delivered.Secret == repeated.Credential!.Secret, "Restarted delivery must return the same credential.");
            Assert.Equal(2, await identity.IntegrationCredentials.CountAsync());
            Assert.True(await service.AckAsync(started.DeviceCode, delivered.Id, default));
            await service.CancelAsync(started.DeviceCode, default);
            Assert.All(await identity.IntegrationCredentials.ToListAsync(), c => Assert.Null(c.RevokedAtUtc));
        }
    }
    private static IntegrationCredentialPairingService Service(RatelDeskIdentityDbContext identity, HelpdeskDbContext domain,
        ServiceIdentityMigrationTests.DatabaseFixture fixture, Clock clock)
    {
        var owner = new IntegrationCredentialOwnerResolver(domain, identity);
        var access = new CurrentUserAccessService(domain, identity);
        return new(identity, domain, new(owner, access, domain, identity), owner, access, fixture.Protection(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["IntegrationCredentials:Pairing:PublicOrigin"] = "https://desk.example" }).Build(), clock);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now = now.AddSeconds(5);
    }
}
