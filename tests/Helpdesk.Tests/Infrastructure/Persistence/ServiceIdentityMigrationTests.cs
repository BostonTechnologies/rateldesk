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
    public async Task Current_service_schema_preserves_account_isolation_and_unique_credentials(bool postgres)
    {
        await using var fixture = await DatabaseFixture.CreateAsync(postgres);
        await using (var identity = fixture.Identity())
        {
            await identity.Database.MigrateAsync();
            identity.Users.Add(new ApplicationUser { Id = "upgrade-human", UserName = "upgrade-human", Email = "human@example.test", IsEnabled = true, IsInstanceAdministrator = true });
            await identity.SaveChangesAsync();
        }
        await using (var db = fixture.Open())
        {
            await db.Database.MigrateAsync();
            db.Organizations.Add(new() { Id = "upgrade-service-org", Name = "Preserved organization" });
            db.Customers.Add(new() { Id = "upgrade-service-customer", OrganizationId = "upgrade-service-org", Name = "Preserved customer", Email = "customer@example.test" });
            await db.SaveChangesAsync();
        }
        await AssertServiceSubjectCannotBecomeAccountAsync(fixture, "upgrade-service-customer");
        await AssertRegistryConstraintsAsync(fixture, "upgrade-service-org", postgres);
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

    internal sealed class DatabaseFixture : IAsyncDisposable
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
