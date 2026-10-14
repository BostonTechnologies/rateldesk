using System.Security.Claims;
using System.Security.Cryptography;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed class IntegrationCredentialPairingTests
{
    private static readonly ClaimsPrincipal Owner = new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim("auth_mode", "local")], "Test"));

    [Fact]
    public async Task Lost_delivery_and_ack_responses_restart_to_the_same_ordinary_credential()
    {
        await using var fixture = await Fixture.CreateAsync();
        var start = await fixture.StartApprovedAsync();
        IntegrationPairingPoll first;
        await using (var session = fixture.Session()) first = await session.Service.PollAsync(start.DeviceCode, default);
        Assert.Equal("issued", first.Status);
        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(2), first.DeliveryExpiresAtUtc);
        Assert.Equal("org-a", first.Credential!.OrganizationId);
        Assert.Equal(["Incident.Read"], first.Credential.Permissions);
        Assert.Equal(fixture.Clock.GetUtcNow().AddDays(30), first.Credential.ExpiresAtUtc);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await using (var session = fixture.Session())
        {
            var replay = await session.Service.PollAsync(start.DeviceCode, default);
            Assert.True(first.Credential.Secret == replay.Credential!.Secret, "A delivery retry must return the same credential.");
            Assert.Single(await session.Identity.IntegrationCredentials.ToListAsync());
            var persisted = await session.Identity.IntegrationCredentialPairings.SingleAsync();
            Assert.False(persisted.ProtectedDelivery!.Contains(first.Credential.Secret, StringComparison.Ordinal), "Delivery must be protected at rest.");
            Assert.DoesNotContain(start.DeviceCode, persisted.DeviceHash);
            Assert.DoesNotContain(start.HumanCode.Replace("-", ""), persisted.HumanHash);
            Assert.True(await session.Service.AckAsync(start.DeviceCode, first.Credential.Id, default));
        }
        await using (var restarted = fixture.Session())
        {
            Assert.True(await restarted.Service.AckAsync(start.DeviceCode, first.Credential.Id, default));
            Assert.Equal("consumed", (await restarted.Service.PollAsync(start.DeviceCode, default)).Status);
            await restarted.Service.CancelAsync(start.DeviceCode, default);
            Assert.Null((await restarted.Identity.IntegrationCredentials.SingleAsync()).RevokedAtUtc);
            var pair = await restarted.Identity.IntegrationCredentialPairings.SingleAsync();
            Assert.Null(pair.ProtectedDelivery); Assert.Null(pair.ProtectedApproval);
            Assert.Single(await restarted.Domain.ActivityLogs.ToListAsync());
        }
    }

    [Fact]
    public async Task Failed_delivery_protection_does_not_track_issue_or_return_an_unprotected_credential()
    {
        await using var fixture = await Fixture.CreateAsync();
        var start = await fixture.StartApprovedAsync();
        await using var session = fixture.Session(new FailingDeliveryProtection(fixture.Protection()));
        var denied = await session.Service.PollAsync(start.DeviceCode, default);
        Assert.Equal("denied", denied.Status); Assert.Null(denied.Credential);
        Assert.False(System.Text.Json.JsonSerializer.Serialize(denied).Contains("rdk_", StringComparison.Ordinal), "Denied delivery must not include a credential.");
        Assert.Empty(await session.Identity.IntegrationCredentials.ToListAsync());
        var pair = await session.Identity.IntegrationCredentialPairings.SingleAsync();
        Assert.Null(pair.CredentialId); Assert.Null(pair.ProtectedDelivery); Assert.Null(pair.ProtectedApproval);
    }

    private sealed class FailingDeliveryProtection(IDataProtectionProvider inner) : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new FailingProtector(inner.CreateProtector(purpose));
        private sealed class FailingProtector(IDataProtector inner) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => new FailingProtector(inner.CreateProtector(purpose));
            public byte[] Protect(byte[] plaintext) => throw new CryptographicException("Synthetic unavailable protection key.");
            public byte[] Unprotect(byte[] protectedData) => inner.Unprotect(protectedData);
        }
    }

    [Fact]
    public async Task Concurrent_redemption_creates_one_record_and_replays_only_the_same_secret()
    {
        await using var fixture = await Fixture.CreateAsync();
        var start = await fixture.StartApprovedAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var session = fixture.Session(); return await session.Service.PollAsync(start.DeviceCode, default);
        }));
        var delivered = Assert.Single(results, r => r.Credential is not null).Credential!;
        Assert.All(results, r => Assert.Contains(r.Status, new[] { "issued", "slow_down" }));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await using var verify = fixture.Session();
        Assert.Single(await verify.Identity.IntegrationCredentials.ToListAsync());
        Assert.True(delivered.Secret == (await verify.Service.PollAsync(start.DeviceCode, default)).Credential!.Secret, "Concurrent retries must return the same credential.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_scoped_users_choose_only_current_grants_and_lost_roles_block_delivery(bool afterIssue)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var session = fixture.Session();
        (await session.Identity.Users.SingleAsync()).IsInstanceAdministrator = false;
        session.Domain.Users.Add(new() { Id = "owner", Name = "Owner", OrganizationId = "org-a" });
        session.Domain.Organizations.Add(new() { Id = "org-b", Name = "Organization B", IsEnabled = true });
        session.Domain.ScopedRoleAssignments.Add(new() { Id = "reader", UserId = "owner", OrganizationId = "org-a", RoleKey = ScopedRoleCatalog.IncidentReader });
        await session.Identity.SaveChangesAsync(); await session.Domain.SaveChangesAsync();
        var started = await session.Service.StartAsync(new("reader client", "api"), "ip", default);
        var review = await session.Service.ReviewAsync(started.HumanCode, Owner, "ip", default);
        Assert.Equal("org-a", Assert.Single(review!.Organizations).Id);
        Assert.Equal(["Incident.Read"], review.Organizations[0].Permissions);
        Assert.Null((await Assert.ThrowsAsync<CredentialGrantException>(() => session.Service.ApproveAsync(
            new(started.HumanCode, "org-a", ["Incident.Write"], 30, true), Owner, "ip", default))).Errors);
        Assert.Null((await Assert.ThrowsAsync<CredentialGrantException>(() => session.Service.ApproveAsync(
            new(started.HumanCode, "org-b", ["Incident.Read"], 30, true), Owner, "ip", default))).Errors);
        Assert.True(await session.Service.ApproveAsync(new(started.HumanCode, "org-a", ["Incident.Read"], 30, true), Owner, "ip", default));
        if (afterIssue)
        {
            Assert.Equal("issued", (await session.Service.PollAsync(started.DeviceCode, default)).Status);
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        }
        session.Domain.ScopedRoleAssignments.Remove(await session.Domain.ScopedRoleAssignments.SingleAsync());
        await session.Domain.SaveChangesAsync();
        var refused = await session.Service.PollAsync(started.DeviceCode, default);
        Assert.Equal("denied", refused.Status); Assert.Null(refused.Credential);
        if (afterIssue) Assert.NotNull((await session.Identity.IntegrationCredentials.SingleAsync()).RevokedAtUtc);
        else Assert.Empty(await session.Identity.IntegrationCredentials.ToListAsync());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("grant")]
    [InlineData("organization")]
    public async Task Authority_changes_after_approval_prevent_issuance(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        var start = await fixture.StartApprovedAsync();
        await using var session = fixture.Session();
        if (change == "organization") (await session.Domain.Organizations.SingleAsync()).IsEnabled = false;
        else
        {
            var owner = await session.Identity.Users.SingleAsync();
            if (change == "account") owner.IsEnabled = false; else owner.IsInstanceAdministrator = false;
        }
        await session.Domain.SaveChangesAsync(); await session.Identity.SaveChangesAsync();
        Assert.Equal("denied", (await session.Service.PollAsync(start.DeviceCode, default)).Status);
        Assert.Empty(await session.Identity.IntegrationCredentials.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Linked_oidc_owner_changes_cannot_issue_into_a_different_account(bool remap)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var session = fixture.Session();
        session.Identity.Users.Add(new() { Id = "other", UserName = "other", IsEnabled = true, IsInstanceAdministrator = true });
        session.Domain.Customers.Add(new() { Id = "customer", Name = "Customer", OrganizationId = "org-a", IsEnabled = true });
        session.Domain.CustomerAuthLinks.Add(new() { Id = "link", CustomerId = "customer", LocalAccountId = "owner", OidcIssuer = "https://id.example", OidcSubject = "external-subject" });
        await session.Identity.SaveChangesAsync(); await session.Domain.SaveChangesAsync();
        var external = new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", "https://id.example"), new Claim("sub", "external-subject")], "Oidc"));
        var start = await session.Service.StartAsync(new("linked client", "api"), "ip", default);
        Assert.True(await session.Service.ApproveAsync(new(start.HumanCode, "org-a", ["Incident.Read"], 30, true), external, "ip", default));
        var link = await session.Domain.CustomerAuthLinks.SingleAsync();
        if (remap) link.LocalAccountId = "other"; else session.Domain.CustomerAuthLinks.Remove(link);
        await session.Domain.SaveChangesAsync();
        Assert.Equal("denied", (await session.Service.PollAsync(start.DeviceCode, default)).Status);
        Assert.Empty(await session.Identity.IntegrationCredentials.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_unacknowledged_delivery_expiry_revokes_only_new_credential(bool expire)
    {
        await using var fixture = await Fixture.CreateAsync();
        var start = await fixture.StartApprovedAsync();
        await using var session = fixture.Session();
        var older = new IntegrationCredential { Id = Guid.NewGuid(), OwnerUserId = "owner", Name = "old working profile", Prefix = "rdk_old", SecretHash = "old-verifier", Purpose = "api", Permissions = "Incident.Read", OrganizationId = "org-a", ExpiresAtUtc = fixture.Clock.GetUtcNow().AddDays(10) };
        session.Identity.IntegrationCredentials.Add(older); await session.Identity.SaveChangesAsync();
        var issued = (await session.Service.PollAsync(start.DeviceCode, default)).Credential!;
        if (expire) { fixture.Clock.Advance(TimeSpan.FromMinutes(3)); await session.Service.CleanupAsync(default); }
        else await session.Service.CancelAsync(start.DeviceCode, default);
        Assert.False(await session.Service.AckAsync(start.DeviceCode, issued.Id, default));
        Assert.NotNull((await session.Identity.IntegrationCredentials.SingleAsync(c => c.Id == issued.Id)).RevokedAtUtc);
        Assert.Null((await session.Identity.IntegrationCredentials.SingleAsync(c => c.Id == older.Id)).RevokedAtUtc);
        var pair = await session.Identity.IntegrationCredentialPairings.SingleAsync();
        Assert.Null(pair.ProtectedDelivery); Assert.Null(pair.ProtectedApproval);
    }

    [Fact]
    public async Task Pending_denied_expired_rate_limits_and_explicit_consent_are_bounded()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var session = fixture.Session();
        var start = await session.Service.StartAsync(new("laptop", "api"), "ip", default);
        Assert.Equal("pending", (await session.Service.PollAsync(start.DeviceCode, default)).Status);
        Assert.Equal("slow_down", (await session.Service.PollAsync(start.DeviceCode, default)).Status);
        Assert.Null(await session.Service.ReviewAsync("INVALID", Owner, "ip", default));
        var review = await session.Service.ReviewAsync(start.HumanCode, Owner, "ip", default);
        Assert.Equal("Organization A", Assert.Single(review!.Organizations).Name);
        await Assert.ThrowsAsync<CredentialGrantException>(() => session.Service.ApproveAsync(new(start.HumanCode, "org-a", ["Incident.Read"], 30, false), Owner, "ip", default));
        Assert.True(await session.Service.DenyAsync(start.HumanCode, Owner, "ip", default));
        Assert.Equal("denied", (await session.Service.PollAsync(start.DeviceCode, default)).Status);
        for (var i = 0; i < 119; i++) await session.Service.StartAsync(new("laptop", "api"), "ip", default);
        await Assert.ThrowsAsync<PairingRateLimitException>(() => session.Service.StartAsync(new("laptop", "api"), "ip", default));
        for (var i = 0; i < 27; i++) await session.Service.ReviewAsync("INVALID", Owner, "ip", default);
        await Assert.ThrowsAsync<PairingRateLimitException>(() => session.Service.ReviewAsync("INVALID", Owner, "ip", default));
        fixture.Clock.Advance(TimeSpan.FromMinutes(11)); await session.Service.CleanupAsync(default); await session.Service.CleanupAsync(default);
        Assert.All(await session.Identity.IntegrationCredentialPairings.ToListAsync(), p => Assert.Contains(p.State, new[] { "denied", "expired" }));
        Assert.Empty(await session.Identity.IntegrationCredentials.ToListAsync());
    }

    [Fact]
    public async Task Purpose_resource_and_lifetime_follow_existing_issuance_rules()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var session = fixture.Session();
        await Assert.ThrowsAsync<CredentialGrantException>(() => session.Service.StartAsync(new("laptop", "mcp", "https://attacker.example/mcp"), "ip", default));
        var mcp = await session.Service.StartAsync(new("laptop", "mcp", "https://desk.example/mcp"), "ip", default);
        await Assert.ThrowsAsync<CredentialGrantException>(() => session.Service.ApproveAsync(new(mcp.HumanCode, "org-a", ["Incident.Read"], 91, true), Owner, "ip", default));
        await Assert.ThrowsAsync<CredentialGrantException>(() => session.Service.ApproveAsync(new(mcp.HumanCode, "org-a", ["HelpdeskAdmin"], 90, true), Owner, "ip", default));
        Assert.True(await session.Service.ApproveAsync(new(mcp.HumanCode, "org-a", ["Incident.Read"], 90, true), Owner, "ip", default));
        var result = await session.Service.PollAsync(mcp.DeviceCode, default);
        Assert.Equal("mcp", result.Credential!.Purpose); Assert.Equal("https://desk.example/mcp", result.Credential.McpResourceUri);
        Assert.Equal(fixture.Clock.GetUtcNow().AddDays(90), result.Credential.ExpiresAtUtc);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "rdk-pairing-" + Guid.NewGuid().ToString("N"));
        public TestClock Clock { get; } = new();
        private DbContextOptions<RatelDeskIdentityDbContext> identityOptions = null!;
        private DbContextOptions<HelpdeskDbContext> domainOptions = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(); Directory.CreateDirectory(fixture.root);
            fixture.identityOptions = new DbContextOptionsBuilder<RatelDeskIdentityDbContext>().UseSqlite($"Data Source={fixture.root}/identity.db;Default Timeout=30").Options;
            fixture.domainOptions = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite($"Data Source={fixture.root}/domain.db;Default Timeout=30").Options;
            await using var session = fixture.Session();
            await session.Identity.Database.EnsureCreatedAsync(); await session.Domain.Database.EnsureCreatedAsync();
            session.Identity.Users.Add(new() { Id = "owner", UserName = "owner", IsEnabled = true, IsInstanceAdministrator = true });
            session.Domain.Organizations.Add(new() { Id = "org-a", Name = "Organization A", IsEnabled = true });
            await session.Identity.SaveChangesAsync(); await session.Domain.SaveChangesAsync();
            return fixture;
        }
        public IDataProtectionProvider Protection() => DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
        public ScopeSession Session(IDataProtectionProvider? protection = null) => new(this, protection);
        public async Task<IntegrationPairingStarted> StartApprovedAsync()
        {
            await using var session = Session();
            var start = await session.Service.StartAsync(new("laptop", "api"), "ip", default);
            Assert.True(await session.Service.ApproveAsync(new(start.HumanCode, "org-a", ["Incident.Read"], null, true), Owner, "ip", default));
            return start;
        }
        public ValueTask DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); return default; }
        public sealed class ScopeSession : IAsyncDisposable
        {
            public RatelDeskIdentityDbContext Identity { get; }
            public HelpdeskDbContext Domain { get; }
            public IntegrationCredentialPairingService Service { get; }
            public ScopeSession(Fixture fixture, IDataProtectionProvider? protection = null)
            {
                Identity = new(fixture.identityOptions);
                var tenant = Substitute.For<ITenantContext>(); tenant.IsHelpdeskAdmin.Returns(true);
                Domain = new(fixture.domainOptions, tenant, new HttpContextAccessor());
                var owners = new IntegrationCredentialOwnerResolver(Domain, Identity);
                var access = new CurrentUserAccessService(Domain, Identity);
                Service = new(Identity, Domain, new(owners, access, Domain, Identity), owners, access,
                    protection ?? fixture.Protection(),
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["IntegrationCredentials:Pairing:PublicOrigin"] = "https://desk.example" }).Build(), fixture.Clock);
            }
            public async ValueTask DisposeAsync() { await Identity.DisposeAsync(); await Domain.DisposeAsync(); }
        }
    }
}
