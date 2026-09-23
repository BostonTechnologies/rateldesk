using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class MailboxConfigurationTests
{
    [Fact]
    public async Task Worker_policy_distinguishes_fresh_pause_operator_stop_and_persisted_activation()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(false);
        await using var db = fixture.Open();
        await db.Database.MigrateAsync();
        var fresh = new MailboxWorkerPolicy(db, new ConfigurationBuilder().Build(), TimeProvider.System);
        Assert.Equal("Instance paused", (await fresh.GetStatusAsync(default)).State);
        Assert.False((await fresh.GetStatusAsync(default)).InstanceRunning);

        var stoppedConfiguration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["EmailIngestion:Enabled"] = "false" }).Build();
        var stopped = new MailboxWorkerPolicy(db, stoppedConfiguration, TimeProvider.System);
        Assert.Equal("Disabled by deployment", (await stopped.SetRunningAsync(true, default)).State);
        Assert.Empty(await db.Set<MailboxWorkerControl>().ToListAsync());

        var permitted = await fresh.SetRunningAsync(true, default);
        Assert.True(permitted.InstanceRunning);
        Assert.Single(await db.Set<MailboxWorkerControl>().ToListAsync());
        Assert.Equal("Disabled by deployment", (await stopped.GetStatusAsync(default)).State);
        await fresh.SetRunningAsync(false, default);
        var legacyTrue = new MailboxWorkerPolicy(db, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["EmailIngestion:Enabled"] = "true" }).Build(), TimeProvider.System);
        Assert.False((await legacyTrue.GetStatusAsync(default)).InstanceRunning);
        db.Set<MailboxWorkerControl>().Remove(await db.Set<MailboxWorkerControl>().SingleAsync());
        await db.SaveChangesAsync();
        Assert.True((await legacyTrue.GetStatusAsync(default)).InstanceRunning);
    }

    [Fact]
    public async Task Effective_status_never_calls_an_enabled_source_running_without_a_live_lease()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(false);
        await using var db = fixture.Open();
        await db.Database.MigrateAsync();
        var mailbox = new EmailInboxSettings
        {
            Id = Guid.NewGuid(), MailboxAddress = "support@tenant-a.example.test",
            SourceKey = "synthetic-source", Enabled = true, BackgroundSyncEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.EmailInboxSettings.Add(mailbox);
        db.Set<MailboxIngestionState>().Add(new() { MailboxId = mailbox.Id, SourceKey = mailbox.SourceKey });
        db.Set<MailboxLease>().Add(new() { MailboxId = mailbox.Id });
        await db.SaveChangesAsync();
        var policy = new MailboxWorkerPolicy(db, new ConfigurationBuilder().Build(), TimeProvider.System);
        Assert.Equal("Instance paused", (await policy.GetMailboxStatusAsync(mailbox.Id, default))?.State);
        await policy.SetRunningAsync(true, default);
        Assert.Equal("Waiting for baseline", (await policy.GetMailboxStatusAsync(mailbox.Id, default))?.State);
        var lease = await db.Set<MailboxLease>().SingleAsync();
        lease.Owner = "synthetic-worker";
        lease.ExpiresUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();
        Assert.Equal("Initializing baseline", (await policy.GetMailboxStatusAsync(mailbox.Id, default))?.State);
        var ingestion = await db.Set<MailboxIngestionState>().SingleAsync();
        ingestion.Initialized = true;
        await db.SaveChangesAsync();
        Assert.Equal("Running", (await policy.GetMailboxStatusAsync(mailbox.Id, default))?.State);
    }

    [Fact]
    public async Task Worker_heartbeat_reports_unavailable_after_stale_process_and_recovers_on_reconcile()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(false);
        await using var db = fixture.Open();
        await db.Database.MigrateAsync();
        var policy = new MailboxWorkerPolicy(db, new ConfigurationBuilder().Build(), TimeProvider.System);
        await policy.SetRunningAsync(true, default);
        await db.Set<MailboxWorkerControl>().ExecuteUpdateAsync(update => update
            .SetProperty(x => x.LastHeartbeatUnixMilliseconds,
                DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()));
        var stale = await policy.GetStatusAsync(default);
        Assert.True(stale.InstanceRunning);
        Assert.Equal("Worker unavailable", stale.State);

        await policy.RecordHeartbeatAsync(default);

        var active = await policy.GetStatusAsync(default);
        Assert.Equal("Instance enabled", active.State);
        Assert.NotNull(active.LastHeartbeatUnixMilliseconds);
    }

    [Fact]
    public async Task Smtp_configuration_is_separate_redacted_and_destination_bound()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(false);
        await using var db = fixture.Open();
        await db.Database.MigrateAsync();
        var mailbox = new EmailInboxSettings
        {
            Id = Guid.NewGuid(), Provider = InboundMailboxProvider.Imap,
            Authentication = MailboxAuthentication.Password,
            MailboxAddress = "support@tenant-a.example.test", SourceKey = "immutable-inbound-source",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.EmailInboxSettings.Add(mailbox);
        await db.SaveChangesAsync();
        var outgoing = new MailboxOutgoingSettingsService(db,
            new MailboxOutgoingCredentialProtector(new EphemeralDataProtectionProvider()));
        var request = new MailboxOutgoingSettingsRequest(0, true, MailboxOutgoingTransport.Smtp,
            "Tenant A support", "smtp.tenant-a.example.test", 587, MailboxTlsMode.StartTls,
            "support@tenant-a.example.test", "synthetic-secret", false);
        var saved = await outgoing.SaveAsync(mailbox.Id, request, default);
        Assert.True(saved.HasSmtpPassword);
        Assert.Equal(mailbox.MailboxAddress, saved.MailboxAddress);
        Assert.DoesNotContain("synthetic-secret", System.Text.Json.JsonSerializer.Serialize(saved));
        Assert.DoesNotContain("synthetic-secret", (await db.Set<MailboxOutgoingSettings>().SingleAsync()).ProtectedSmtpPassword);
        Assert.Equal("immutable-inbound-source", (await db.EmailInboxSettings.SingleAsync()).SourceKey);

        var changedHost = request with { Version = saved.Version,
            SmtpHost = "different.tenant-a.example.test", SmtpPassword = string.Empty };
        await Assert.ThrowsAsync<ArgumentException>(() => outgoing.SaveAsync(mailbox.Id, changedHost, default));
        var retained = await outgoing.SaveAsync(mailbox.Id,
            request with { Version = saved.Version, SmtpPassword = string.Empty }, default);
        Assert.True(retained.HasSmtpPassword);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => outgoing.SaveAsync(mailbox.Id, request, default));
    }

    [Fact]
    public async Task Sender_resolver_keeps_dedicated_identity_and_never_falls_back_when_its_sender_is_missing()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(false);
        await using var db = fixture.Open();
        await db.Database.MigrateAsync();
        db.Organizations.AddRange(new Organization { Id = "tenant-a", Name = "Tenant A" },
            new Organization { Id = "tenant-b", Name = "Tenant B" });
        var global = new EmailInboxSettings { Id = Guid.NewGuid(), Scope = MailboxScope.Global,
            MailboxAddress = "global@example.test", SourceKey = "global-source", Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var dedicated = new EmailInboxSettings { Id = Guid.NewGuid(), Scope = MailboxScope.Organization,
            OrganizationId = "tenant-a", MailboxAddress = "support@tenant-a.example.test",
            SourceKey = "tenant-a-source", Enabled = true, BackgroundSyncEnabled = false,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.EmailInboxSettings.AddRange(global, dedicated);
        db.Set<MailboxOutgoingSettings>().Add(new MailboxOutgoingSettings
        {
            MailboxId = global.Id, Enabled = true, Transport = MailboxOutgoingTransport.Smtp,
            SmtpHost = "smtp.example.test", SmtpUsername = global.MailboxAddress,
            ProtectedSmtpPassword = "synthetic-protected"
        });
        db.Incidents.AddRange(new Incident { Id = "ticket-a", OrganizationId = "tenant-a", TrackingId = "INC-A" },
            new Incident { Id = "ticket-b", OrganizationId = "tenant-b", TrackingId = "INC-B" });
        await db.SaveChangesAsync();
        var resolver = new MailboxSenderResolver(db);
        Assert.Equal("OutgoingNotConfigured", (await resolver.ResolveAsync("ticket-a", null, default)).ErrorCode);
        Assert.Equal(dedicated.Id, (await resolver.ResolveAsync("ticket-a", null, default)).Mailbox?.Id);
        Assert.Equal(global.Id, (await resolver.ResolveAsync("ticket-b", null, default)).Mailbox?.Id);
        Assert.Equal("TicketOrganizationMismatch",
            (await resolver.ResolveAsync("ticket-a", "tenant-b", default)).ErrorCode);
        Assert.Equal(global.Id, (await resolver.ResolveAsync(null, null, default)).Mailbox?.Id);
        // Ingress can capture delivery before a newly created ticket is flushed.
        db.Incidents.Add(new Incident { Id = "new-ticket", OrganizationId = "tenant-a", TrackingId = "INC-NEW" });
        Assert.Equal(dedicated.Id, (await resolver.ResolveAsync("new-ticket", "tenant-a", default)).Mailbox?.Id);
        Assert.Equal("TicketOrganizationMismatch",
            (await resolver.ResolveAsync("new-ticket", "tenant-b", default)).ErrorCode);
        db.Set<MailboxOutgoingSettings>().Add(new MailboxOutgoingSettings
        {
            MailboxId = dedicated.Id, Enabled = true, Transport = MailboxOutgoingTransport.Smtp,
            SmtpHost = "smtp.tenant-a.example.test", SmtpUsername = dedicated.MailboxAddress,
            ProtectedSmtpPassword = "synthetic-protected"
        });
        await db.SaveChangesAsync();
        Assert.Equal("Ready", (await resolver.ResolveAsync("ticket-a", null, default)).Status);
        // A dedicated source must remain usable when no global assignment exists.
        global.Archived = true;
        await db.SaveChangesAsync();
        Assert.Equal(dedicated.Id, (await resolver.ResolveAsync("ticket-a", null, default)).Mailbox?.Id);
        Assert.Equal("NoEffectiveMailbox", (await resolver.ResolveAsync("ticket-b", null, default)).ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_schema_enforces_four_assignments_for_thirteen_organizations_and_keeps_disabled_overrides(bool postgres)
    {
        await using var fixture = await DatabaseFixture.CreateAsync(postgres);
        await using var db = fixture.Open();
        await db.Database.MigrateAsync();
        for (var i = 1; i <= 13; i++) db.Organizations.Add(new Organization { Id = $"tenant-{i:00}", Name = $"Tenant {i:00}" });
        await db.SaveChangesAsync();
        var service = new MailboxSettingsService(db, fixture.Secrets);
        var global = await service.PrepareAsync(Request("support@example.test"), null, default);
        db.EmailInboxSettings.Add(global);
        for (var i = 11; i <= 13; i++)
        {
            var request = Request($"support@tenant{i}.example.test");
            request.Scope = MailboxScope.Organization; request.OrganizationId = $"tenant-{i:00}";
            request.Provider = i == 11 ? InboundMailboxProvider.Imap : i == 12 ? InboundMailboxProvider.Pop3 : InboundMailboxProvider.Graph;
            request.MarkReadAfterSuccess = request.Provider != InboundMailboxProvider.Pop3;
            db.EmailInboxSettings.Add(await service.PrepareAsync(request, null, default));
        }
        await db.SaveChangesAsync();
        Assert.Equal(4, await db.EmailInboxSettings.CountAsync());
        var dedicated = await db.EmailInboxSettings.Where(x => x.OrganizationId != null).Select(x => x.OrganizationId).ToListAsync();
        Assert.Equal(10, await db.Organizations.CountAsync(x => !dedicated.Contains(x.Id)));
        db.EmailInboxSettings.Add(await service.PrepareAsync(Request("second@example.test"), null, default));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var duplicate = Request("duplicate@example.test"); duplicate.Scope = MailboxScope.Organization; duplicate.OrganizationId = "tenant-11";
        db.EmailInboxSettings.Add(await service.PrepareAsync(duplicate, null, default));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upgrade_preserves_effective_guid_protects_credentials_and_does_not_reimport_on_restart(bool postgres)
    {
        await using var fixture = await DatabaseFixture.CreateAsync(postgres);
        await using var db = fixture.Open();
        var migrations = db.Database.GetMigrations().ToArray();
        var mailboxMigration = Array.FindIndex(migrations,
            migration => migration.EndsWith("_MultiProviderMailboxes", StringComparison.Ordinal));
        Assert.True(mailboxMigration > 0);
        await db.GetService<IMigrator>().MigrateAsync(migrations[mailboxMigration - 1]);
        var id = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "EmailInboxSettings" ("Id", "MailHost", "Port", "UseSsl", "MailboxAddress", "TenantId", "ClientId", "ClientSecret", "MailboxFolder", "Enabled", "BackgroundSyncEnabled", "CreatedAt", "UpdatedAt")
            VALUES ({id}, {"outlook.office365.com"}, {993}, {true}, {"support@example.test"}, {"11111111-1111-4111-8111-111111111111"}, {"22222222-2222-4222-8222-222222222222"}, {"synthetic-secret with spaces "}, {"inbox"}, {false}, {true}, {created}, {created});
            """);
        await db.Database.MigrateAsync();
        var migrator = new MailboxConfigurationMigration(db, fixture.Secrets, new ConfigurationBuilder().Build());
        await migrator.RunAsync(default);
        await migrator.RunAsync(default);
        db.ChangeTracker.Clear();
        var migrated = await db.EmailInboxSettings.SingleAsync();
        Assert.Equal(id, migrated.Id);
        Assert.False(migrated.Archived);
        Assert.False(migrated.Enabled);
        Assert.True(migrated.BackgroundSyncEnabled);
        Assert.Equal(InitialMailImport.ExistingUnread, migrated.InitialImport);
        Assert.Equal("synthetic-secret with spaces ", fixture.Secrets.Unprotect(migrated, migrated.ClientSecret));
        Assert.DoesNotContain("synthetic-secret", migrated.ClientSecret);
        Assert.Single(await db.Set<MailboxMigrationState>().ToListAsync());
        Assert.Single(await db.Set<MailboxLease>().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multiple_historic_sources_keep_one_effective_global_and_freeze_sender(bool postgres)
    {
        await using var fixture = await DatabaseFixture.CreateAsync(postgres);
        await using var db = fixture.Open();
        await db.Database.MigrateAsync();
        var older = new EmailInboxSettings { Id = Guid.NewGuid(), MailboxAddress = "old@example.test", MailHost = "outlook.office365.com", TenantId = "fixture-directory", ClientId = "fixture-app", Enabled = true,
            Archived = true, BackgroundSyncEnabled = false, ClientSecret = "fixture-old", CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        var effective = new EmailInboxSettings { Id = Guid.NewGuid(), MailboxAddress = "effective@example.test", MailHost = "outlook.office365.com", TenantId = "fixture-directory", ClientId = "fixture-app", Enabled = true,
            Archived = true, BackgroundSyncEnabled = true, ClientSecret = "fixture-current", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        db.EmailInboxSettings.AddRange(older, effective);
        await db.SaveChangesAsync();
        var migration = new MailboxConfigurationMigration(db, fixture.Secrets, new ConfigurationBuilder().Build());
        await migration.RunAsync(default);
        await migration.RunAsync(default);
        db.ChangeTracker.Clear();
        Assert.Equal(effective.Id, (await db.EmailInboxSettings.SingleAsync(x => !x.Archived)).Id);
        Assert.Equal("effective@example.test", (await db.Set<MailboxMigrationState>().SingleAsync()).OutboundMailboxAddress);
        Assert.Equal(2, await db.Set<MailboxLease>().CountAsync());
        Assert.True((await db.EmailInboxSettings.SingleAsync(x => x.Id == older.Id)).Enabled);
    }

    [Fact]
    public async Task Changed_draft_destination_cannot_reuse_saved_secret_and_secret_loss_never_falls_back_to_plaintext()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(false);
        await using var db = fixture.Open(); await db.Database.MigrateAsync();
        var service = new MailboxSettingsService(db, fixture.Secrets);
        var request = Request("support@example.test");
        var saved = await service.PrepareAsync(request, null, default);
        request.Id = saved.Id; request.Version = saved.Version; request.ClientSecret = string.Empty;
        var retained = await service.PrepareAsync(request, saved, default);
        Assert.Equal(saved.ClientSecret, retained.ClientSecret);
        request.MailHost = "attacker.example.test";
        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync(request, saved, default));
        var otherKeys = new MailboxCredentialProtector(new EphemeralDataProtectionProvider());
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => otherKeys.Unprotect(saved, saved.ClientSecret));
    }

    private static MailboxSettingsRequest Request(string address) => new()
    {
        DisplayName = address, MailboxAddress = address, MailHost = "mail.example.test",
        TenantId = "11111111-1111-4111-8111-111111111111", ClientId = "22222222-2222-4222-8222-222222222222",
        ClientSecret = "synthetic-secret", MailboxFolder = "inbox"
    };

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private PostgreSqlContainer? container;
        private SqliteConnection? sqlite;
        private DbContextOptions<HelpdeskDbContext> options = null!;
        public MailboxCredentialProtector Secrets { get; } = new(new EphemeralDataProtectionProvider());
        public static async Task<DatabaseFixture> CreateAsync(bool postgres)
        {
            var fixture = new DatabaseFixture();
            var builder = new DbContextOptionsBuilder<HelpdeskDbContext>();
            if (postgres)
            {
                fixture.container = new PostgreSqlBuilder("postgres:16").Build();
                await fixture.container.StartAsync(); builder.UseNpgsql(fixture.container.GetConnectionString());
            }
            else
            {
                fixture.sqlite = new SqliteConnection("Data Source=:memory:"); await fixture.sqlite.OpenAsync();
                builder.UseSqlite(fixture.sqlite, x => x.MigrationsAssembly("Helpdesk.Infrastructure.SqliteMigrations"));
            }
            fixture.options = builder.Options; return fixture;
        }
        public HelpdeskDbContext Open() => new(options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
        public async ValueTask DisposeAsync()
        {
            if (container is not null) await container.DisposeAsync();
            if (sqlite is not null) await sqlite.DisposeAsync();
        }
    }
}
