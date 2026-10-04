using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Helpdesk.API.Authentication;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Connectivity;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed class ServiceIdentityMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Beta8_provider_and_human_credentials_and_receiver_receipts_survive_service_upgrade(bool postgres)
    {
        await using var fixture = await DatabaseFixture.CreateAsync(postgres);
        var profileId = Guid.NewGuid();
        const string secret = "synthetic-preserved-beta8-outbound-secret";
        var protection = fixture.Protection();
        var protectedSecret = new IntegrationProviderSecretProtector(protection).Protect(secret);
        var fingerprint = IntegrationProviderSecretBinding.Fingerprint("Orchestrator", "https://peer.example.test/api", "https://issuer.example.test",
            "https://peer.example.test/connect/token", "netratel.api", "netratel.api", "legacy-outbound-client");
        var source = new IncidentReceiverSource { SourceNamespaceId = Guid.NewGuid(), SourceInstanceId = Guid.NewGuid(), OrganizationId = "upgrade-service-org",
            CustomerId = "upgrade-service-customer", CreatedBy = "original-admin", UpdatedBy = "original-admin", Revision = 7,
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        var credentialId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        const string accepted = "{\"id\":\"existing-incident\",\"trackingId\":\"INC-EXISTING\",\"integrationReceipt\":{\"outcome\":\"committed\"}}";

        await using (var identity = fixture.Identity())
        {
            await identity.Database.MigrateAsync();
            identity.Users.Add(new ApplicationUser { Id = "upgrade-human", UserName = "upgrade-human", Email = "human@example.test", IsEnabled = true, IsInstanceAdministrator = true });
            identity.IntegrationCredentials.Add(new() { Id = credentialId, OwnerUserId = "upgrade-human", Name = "Existing API credential", Prefix = "rdk_existing",
                SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-original-rdk-secret"))), Purpose = "api", OrganizationId = source.OrganizationId,
                Permissions = "Incident.Read Incident.Write", CreatedAtUtc = DateTimeOffset.UtcNow, CreatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30) });
            await identity.SaveChangesAsync();
        }
        await using (var db = fixture.Open())
        {
            var publishedBeta8 = postgres ? "20260926160353_AddNetclawProviderProfileBinding" : "20260926160417_AddNetclawProviderProfileBinding";
            await db.GetService<IMigrator>().MigrateAsync(publishedBeta8);
            db.Organizations.Add(new() { Id = source.OrganizationId, Name = "Preserved organization" });
            db.Customers.Add(new() { Id = source.CustomerId, OrganizationId = source.OrganizationId, Name = "Preserved customer", Email = "customer@example.test" });
            await db.SaveChangesAsync();
            // Use the beta.8 schema rather than the current entity, whose added link columns do not exist yet.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "M2MConnectivitySettings"
                ("Id", "ProviderKey", "Enabled", "RemoteBaseUrl", "RemoteAudience", "RemoteSystemName", "RemoteTokenEndpoint", "RemoteAuthority", "RemoteScope", "ClientId",
                 "ProtectedClientSecret", "SecretBindingFingerprint", "SecretBindingRevision", "ProfileFingerprint", "AllowPrivateHttp", "HealthPath", "IngestPath", "CatalogPath", "Revision", "UpdatedAtUtc")
                VALUES ({profileId}, {"Orchestrator"}, {true}, {"https://peer.example.test/api"}, {"netratel.api"}, {"Legacy NetRatel"}, {"https://peer.example.test/connect/token"},
                    {"https://issuer.example.test"}, {"netratel.api"}, {"legacy-outbound-client"}, {protectedSecret}, {fingerprint}, {12}, {fingerprint}, {false},
                    {"/internal/health"}, {"/internal/ingest"}, {"/internal/catalog"}, {12}, {DateTimeOffset.UtcNow});
                """);
            var receiverMigration = db.Database.GetMigrations().Single(x => x.EndsWith("_AddIncidentReceiver", StringComparison.Ordinal));
            await db.GetService<IMigrator>().MigrateAsync(receiverMigration);
            db.IncidentReceiverSources.Add(source);
            db.IncidentReceiverPrincipalBindings.Add(new() { SourceNamespaceId = source.SourceNamespaceId, PrincipalKind = "api_credential", PrincipalId = credentialId.ToString("N"), IsEnabled = true });
            db.IncidentCreateReceipts.Add(new() { Id = receiptId, SourceNamespaceId = source.SourceNamespaceId, Key = "preserved-replay-key", Fingerprint = new string('a', 64),
                IncidentId = "existing-incident", OrganizationId = source.OrganizationId, CustomerId = source.CustomerId, Location = "/api/v1/incidents/existing-incident",
                AcceptedJson = accepted, CommittedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await db.Database.MigrateAsync();
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }

        // Reopen both the database and the actual persisted key ring, as another container/API replica would.
        var restoredProtection = fixture.Protection();
        Assert.NotEmpty(Directory.GetFiles(fixture.KeyDirectory, "key-*.xml"));
        await using (var db = fixture.Open())
        {
            var profile = Assert.Single(await db.M2MConnectivitySettings.AsNoTracking().ToListAsync());
            Assert.Equal(profileId, profile.Id);
            Assert.Equal(12, profile.Revision);
            Assert.Equal(12, profile.SecretBindingRevision);
            Assert.Equal(fingerprint, profile.SecretBindingFingerprint);
            Assert.Equal(protectedSecret, profile.ProtectedClientSecret);
            Assert.Null(profile.LinkId);
            Assert.True(profile.ManagedSenderEnabled);
            var provider = new IntegrationProviderSettingsService(db, new ConfigurationBuilder().Build(), new IntegrationProviderSecretProtector(restoredProtection),
                Options.Create(new AiAssistantChatOptions()), new DatabaseOptions { Provider = postgres ? "PostgreSql" : "Sqlite" }, TimeProvider.System,
                NullLogger<IntegrationProviderSettingsService>.Instance);
            var effective = await provider.GetResolvedOrchestratorSettingsAsync();
            Assert.Equal("database", effective.Source);
            Assert.False(effective.SecretUnavailable);
            Assert.Equal(secret, effective.ClientSecret);
            Assert.Equal("netratel.api", effective.Scope);
            var preserved = await db.IncidentCreateReceipts.AsNoTracking().SingleAsync(x => x.Id == receiptId);
            Assert.Equal(source.SourceNamespaceId, preserved.SourceNamespaceId);
            Assert.Equal(accepted, preserved.AcceptedJson);
            Assert.Equal("preserved-replay-key", preserved.Key);
            Assert.Equal(7, (await db.IncidentReceiverSources.AsNoTracking().SingleAsync()).Revision);
            Assert.Equal(credentialId.ToString("N"), (await db.IncidentReceiverPrincipalBindings.SingleAsync()).PrincipalId);
        }
        await using (var identity = fixture.Identity())
        {
            await identity.Database.MigrateAsync();
            var human = await identity.Users.AsNoTracking().SingleAsync(x => x.Id == "upgrade-human");
            var original = await identity.IntegrationCredentials.AsNoTracking().SingleAsync(x => x.Id == credentialId);
            Assert.True(human.IsEnabled);
            Assert.Equal(human.Id, original.OwnerUserId);
            Assert.Equal("api", original.Purpose);
            Assert.Equal("Incident.Read Incident.Write", original.Permissions);
            Assert.Null(original.RevokedAtUtc);
        }
        await AssertServiceSubjectCannotBecomeAccountAsync(fixture, source.CustomerId);
        await AssertRegistryConstraintsAsync(fixture, source.OrganizationId, postgres);
    }

    private static async Task AssertServiceSubjectCannotBecomeAccountAsync(DatabaseFixture fixture, string customer)
    {
        await using var db = fixture.Open();
        await using var identity = fixture.Identity();
        const string subject = "service:accidental-external-subject-match";
        db.CustomerAuthLinks.Add(new() { CustomerId = customer, LocalAccountId = "upgrade-human", AuthProviderType = "Oidc", OidcIssuer = "https://rateldesk.example/services",
            OidcSubject = subject, InviteStatus = CustomerInviteStatus.Active });
        await db.SaveChangesAsync();
        var machine = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("iss", "https://rateldesk.example/services"), new Claim("sub", subject),
            new Claim("auth_mode", "service"), new Claim("token_use", ServiceIdentityClaims.Purpose) }, ServiceIdentityAuthenticationHandler.SchemeName));
        Assert.Null(await new IntegrationCredentialOwnerResolver(db, identity).ResolveAsync(machine));
        var access = new CurrentUserAccessService(db, identity);
        var machineAccess = await access.ResolveAsync(machine);
        Assert.False(machineAccess.IsHelpdeskAdmin);
        Assert.Empty(machineAccess.Permissions);
        var human = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("auth_mode", "local"), new Claim(ClaimTypes.NameIdentifier, "upgrade-human") }, "Local"));
        Assert.True((await access.ResolveAsync(human)).IsHelpdeskAdmin);
        Assert.Equal("upgrade-human", (await new IntegrationCredentialOwnerResolver(db, identity).ResolveAsync(human))!.UserId);
    }

    private static async Task AssertRegistryConstraintsAsync(DatabaseFixture fixture, string organization, bool postgres)
    {
        var principal = new ServicePrincipalRegistration { ClientId = "unique-client", NormalizedClientId = "UNIQUE-CLIENT", Name = "Unique service", OrganizationId = organization,
            PeerInstanceId = "approved-peer", PeerTenantId = "17", Status = "active", CreatedBy = "admin", ApprovedBy = "admin", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        await using (var db = fixture.Open()) { db.Set<ServicePrincipalRegistration>().Add(principal); await db.SaveChangesAsync(); }
        await using (var competing = fixture.Open())
        {
            competing.Set<ServicePrincipalRegistration>().Add(new() { ClientId = "Unique-Client", NormalizedClientId = "UNIQUE-CLIENT", OrganizationId = organization });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => competing.SaveChangesAsync());
            AssertUnique(error, postgres);
        }
        var results = await Task.WhenAll(new[] { 2L, 3L }.Select(async revision =>
        {
            await using var db = fixture.Open();
            db.Set<ServicePrincipalSecret>().Add(new() { ServicePrincipalId = principal.Id, CredentialRevision = revision, Status = "pending",
                Salt = new string('b', 64), SecretHash = new string('a', 64), CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(90) });
            try { await db.SaveChangesAsync(); return (Exception?)null; }
            catch (DbUpdateException error) { return error; }
        }));
        Assert.Single(results, x => x is null);
        AssertUnique((DbUpdateException)Assert.Single(results, x => x is not null)!, postgres);
        await using var clean = fixture.Open();
        Assert.Single(await clean.Set<ServicePrincipalSecret>().Where(x => x.ServicePrincipalId == principal.Id && x.Status == "pending").ToListAsync());
    }

    private static void AssertUnique(DbUpdateException error, bool postgres)
    {
        if (postgres) Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        else Assert.Equal(19, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "rateldesk-service-upgrade-" + Guid.NewGuid().ToString("N"));
        private PostgreSqlContainer? container;
        private DbContextOptions<HelpdeskDbContext> options = null!;
        private DbContextOptions<RatelDeskIdentityDbContext> identityOptions = null!;
        public string KeyDirectory => Path.Combine(root, "keys");
        public IDataProtectionProvider Protection() => DataProtectionProvider.Create(new DirectoryInfo(KeyDirectory), builder => builder.SetApplicationName("RatelDesk.ServiceUpgradeTests"));
        public static async Task<DatabaseFixture> CreateAsync(bool postgres)
        {
            var fixture = new DatabaseFixture();
            Directory.CreateDirectory(fixture.root); Directory.CreateDirectory(fixture.KeyDirectory);
            var app = new DbContextOptionsBuilder<HelpdeskDbContext>();
            var identity = new DbContextOptionsBuilder<RatelDeskIdentityDbContext>();
            if (postgres)
            {
                fixture.container = new PostgreSqlBuilder("postgres:16").Build(); await fixture.container.StartAsync();
                app.UseNpgsql(fixture.container.GetConnectionString()); identity.UseNpgsql(fixture.container.GetConnectionString());
            }
            else
            {
                var connection = $"Data Source={Path.Combine(fixture.root, "upgrade.db")};Default Timeout=30";
                app.UseSqlite(connection, x => x.MigrationsAssembly("Helpdesk.Infrastructure.SqliteMigrations"));
                identity.UseSqlite(connection, x => x.MigrationsAssembly("Helpdesk.Infrastructure.SqliteMigrations"));
            }
            fixture.options = app.Options; fixture.identityOptions = identity.Options; return fixture;
        }
        public HelpdeskDbContext Open() => new(options, new AdminTenant(), new HttpContextAccessor());
        public RatelDeskIdentityDbContext Identity() => new(identityOptions);
        public async ValueTask DisposeAsync()
        {
            if (container is not null) await container.DisposeAsync();
            SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }
    private sealed class AdminTenant : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }
}
