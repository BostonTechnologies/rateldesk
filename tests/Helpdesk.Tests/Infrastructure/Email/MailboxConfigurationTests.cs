using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Enums;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Due_mailbox_backlog_does_not_hide_another_mailbox_or_notification(bool postgres)
    {
        await using var fixture = await DatabaseFixture.CreateAsync(postgres);
        await using var db = fixture.Open();
        await db.Database.MigrateAsync();
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        for (var index = 0; index < 20; index++)
            db.Set<MailboxOutboxEffect>().Add(new MailboxOutboxEffect
            {
                Kind = MailboxEffectKind.Email, EffectKey = $"backlog:{index:D2}",
                DispatchGroup = "mailbox:backlog", Payload = "{}", AvailableUnixMilliseconds = now - 2
            });
        var otherMailbox = new MailboxOutboxEffect
        {
            Kind = MailboxEffectKind.Email, EffectKey = "other:00", DispatchGroup = "mailbox:other",
            Payload = "{}", AvailableUnixMilliseconds = now - 1
        };
        var notification = new MailboxOutboxEffect
        {
            Kind = MailboxEffectKind.Notification, EffectKey = "notification:00", Payload = "{}",
            AvailableUnixMilliseconds = now - 1
        };
        db.Set<MailboxOutboxEffect>().AddRange(otherMailbox, notification);
        await db.SaveChangesAsync();

        var candidates = await new MailboxOutboxStore(db, new IngressEffectContext(), TimeProvider.System)
            .GetCandidatesAsync(16, default);

        Assert.Equal(16, candidates.Count);
        Assert.Contains(otherMailbox.Id, candidates);
        Assert.Contains(notification.Id, candidates);
    }

    [Fact]
    public async Task Confirmed_outgoing_retry_rebinds_only_the_same_mailbox_after_a_safe_configuration_failure()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(false);
        await using var db = fixture.Open();
        await db.Database.MigrateAsync();
        db.Organizations.Add(new Organization { Id = "tenant-a", Name = "Tenant A" });
        var mailbox = new EmailInboxSettings
        {
            Id = Guid.NewGuid(), Scope = MailboxScope.Organization, OrganizationId = "tenant-a",
            MailboxAddress = "support@tenant-a.example.test", SourceKey = "retry-source",
            Enabled = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.EmailInboxSettings.Add(mailbox);
        var outgoing = new MailboxOutgoingSettings
        {
            MailboxId = mailbox.Id, Version = 1, Enabled = false, Transport = MailboxOutgoingTransport.Smtp,
            SmtpHost = "smtp.tenant-a.example.test", SmtpUsername = mailbox.MailboxAddress,
            ProtectedSmtpPassword = "synthetic-protected"
        };
        db.Set<MailboxOutgoingSettings>().Add(outgoing);
        db.Incidents.Add(new Incident { Id = "retry-ticket", OrganizationId = "tenant-a", TrackingId = "INC-RETRY" });
        await db.SaveChangesAsync();

        var store = new MailboxOutboxStore(db, new IngressEffectContext(), TimeProvider.System);
        var queued = await store.QueueDirectAsync(new IngressEmailEffect(
            ["requester@tenant-a.example.test"], "Confirmation", "<p>Body</p>", [],
            "retry-ticket", [], null, null, false, null, null)
        {
            MailboxId = mailbox.Id, OrganizationId = "tenant-a",
            MailboxConfigurationVersion = mailbox.Version, OutgoingConfigurationVersion = outgoing.Version
        }, default);
        var claim = Assert.IsType<MailboxOutboxEffect>(await store.TryClaimAsync(queued.Id, "worker", default));
        Assert.True(await store.CompleteAsync(claim, false, "OutgoingDisabled", default, requiresReview: true));
        var retry = new MailboxOutgoingRetryService(db, new MailboxSenderResolver(db), TimeProvider.System);
        Assert.Equal("OutgoingDisabled", (await retry.PreviewAsync(queued.DeliveryEventId!.Value, default)).Status);

        outgoing.Enabled = true;
        outgoing.Version++;
        await db.SaveChangesAsync();
        var preview = await retry.PreviewAsync(queued.DeliveryEventId.Value, default);
        Assert.True(preview.CanRetry);
        Assert.Equal(mailbox.Id, preview.MailboxId);
        Assert.Equal(mailbox.MailboxAddress, preview.MailboxAddress);
        Assert.Equal(2, preview.CurrentOutgoingVersion);
        Assert.Equal("OutgoingRevisionChanged", (await retry.RetryAsync(
            queued.DeliveryEventId.Value, 1, "admin", default)).Status);
        Assert.Equal("Queued", (await retry.RetryAsync(
            queued.DeliveryEventId.Value, 2, "admin", default)).Status);
        var rebound = await db.Set<MailboxOutboxEffect>().AsNoTracking().SingleAsync(x => x.Id == queued.Id);
        Assert.Equal(MailboxEffectState.Pending, rebound.State);
        var email = MailboxOutboxStore.Deserialize<IngressEmailEffect>(rebound.Payload);
        Assert.Equal(mailbox.Id, email.MailboxId);
        Assert.Equal("tenant-a", email.OrganizationId);
        Assert.Equal(2, email.OutgoingConfigurationVersion);
        Assert.Equal(EmailDeliveryStatus.Pending, (await db.TicketTimelineEvents.AsNoTracking()
            .SingleAsync(x => x.Id == queued.DeliveryEventId)).EmailStatus);
        Assert.Single(await db.ActivityLogs.Where(x => x.RelatedEntityId == mailbox.Id.ToString("D")).ToListAsync());

        await db.Set<MailboxOutboxEffect>().Where(x => x.Id == queued.Id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, MailboxEffectState.NeedsReview)
            .SetProperty(x => x.LastErrorCode, "SmtpPartialRecipientAcceptance"));
        await db.TicketTimelineEvents.Where(x => x.Id == queued.DeliveryEventId).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.EmailStatus, EmailDeliveryStatus.Failed));
        outgoing.Version++;
        await db.SaveChangesAsync();
        Assert.Equal("DeliveryOutcomeRequiresReview", (await retry.PreviewAsync(
            queued.DeliveryEventId.Value, default)).Status);

        await db.Set<MailboxOutboxEffect>().Where(x => x.Id == queued.Id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.LastErrorCode, "SmtpAuthenticationFailed"));
        Assert.True((await retry.PreviewAsync(queued.DeliveryEventId.Value, default)).CanRetry);
    }

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
    public async Task Beta2_upgrade_preserves_global_dedicated_receipts_and_failed_delivery_without_activation(bool postgres)
    {
        await using var fixture = await DatabaseFixture.CreateAsync(postgres);
        await using var db = fixture.Open();
        var beta2 = db.Database.GetMigrations().Single(x => x.EndsWith("_MailboxAcknowledgmentClaims", StringComparison.Ordinal));
        await db.GetService<IMigrator>().MigrateAsync(beta2);
        var globalId = Guid.NewGuid();
        var dedicatedId = Guid.NewGuid();
        var skippedId = Guid.NewGuid();
        var succeededId = Guid.NewGuid();
        var heldId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var protectedGlobal = fixture.Secrets.Protect(globalId, "synthetic-global-secret");
        var protectedDedicated = fixture.Secrets.Protect(dedicatedId, "synthetic-dedicated-secret");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Organizations" ("Id", "Name", "State", "IsEnabled")
            VALUES ({"tenant-a"}, {"Tenant A"}, {0}, {true});
            """);
        foreach (var (id, address, scope, organizationId, source, protectedSecret) in new[]
                 {
                     (globalId, "global@example.test", 0, (string?)null, "global-source", protectedGlobal),
                     (dedicatedId, "support@tenant-a.example.test", 1, "tenant-a", "dedicated-source", protectedDedicated)
                 })
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "EmailInboxSettings" ("Id", "MailHost", "Port", "UseSsl", "MailboxAddress",
                    "TenantId", "ClientId", "ClientSecret", "MailboxFolder", "Enabled", "BackgroundSyncEnabled",
                    "CreatedAt", "UpdatedAt", "Archived", "Scope", "OrganizationId", "SourceKey", "CredentialVersion",
                    "Authentication", "BatchSize", "DisplayName", "InitialImport", "LegacySource", "MarkReadAfterSuccess",
                    "Password", "PollIntervalSeconds", "Provider", "TlsMode", "Username", "Version")
                VALUES ({id}, {"mail.example.test"}, {993}, {true}, {address}, {"directory"}, {"application"},
                    {protectedSecret}, {"INBOX"}, {true}, {false}, {now}, {now}, {false}, {scope}, {organizationId}, {source}, {1},
                    {0}, {25}, {"Synthetic mailbox"}, {0}, {false}, {true}, {""}, {30}, {0}, {0}, {""}, {1L});
                """);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "InboundMessageReceipt" ("Id", "MailboxId", "SourceKey", "TransportKey",
                "ConfigurationVersion", "Outcome", "Reason", "Acknowledged", "Attempts",
                "CreatedUnixMilliseconds", "UpdatedUnixMilliseconds", "ProtectedEnvelope")
            VALUES ({skippedId}, {globalId}, {"global-source"}, {"old-uid"}, {1L},
                {(int)InboundReceiptOutcome.Ignored}, {"InitialBaselineSkipped"}, {true}, {0},
                {now.ToUnixTimeMilliseconds()}, {now.ToUnixTimeMilliseconds()}, {""});
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "InboundMessageReceipt" ("Id", "MailboxId", "SourceKey", "TransportKey",
                "ConfigurationVersion", "Outcome", "Acknowledged", "Attempts",
                "CreatedUnixMilliseconds", "UpdatedUnixMilliseconds", "ProtectedEnvelope")
            VALUES ({succeededId}, {dedicatedId}, {"dedicated-source"}, {"handled-uid"}, {1L},
                {(int)InboundReceiptOutcome.Succeeded}, {true}, {1},
                {now.ToUnixTimeMilliseconds()}, {now.ToUnixTimeMilliseconds()}, {""});
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "InboundMessageReceipt" ("Id", "MailboxId", "SourceKey", "TransportKey",
                "ConfigurationVersion", "Outcome", "Reason", "Acknowledged", "Attempts",
                "CreatedUnixMilliseconds", "UpdatedUnixMilliseconds", "ProtectedEnvelope")
            VALUES ({heldId}, {dedicatedId}, {"dedicated-source"}, {"held-uid"}, {1L},
                {(int)InboundReceiptOutcome.NeedsReview}, {"TenantResolutionAmbiguous"}, {false}, {2},
                {now.ToUnixTimeMilliseconds()}, {now.ToUnixTimeMilliseconds()}, {""});
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MailboxOutboxEffect" ("Id", "ReceiptId", "EffectKey", "Kind", "Payload", "State",
                "Attempts", "Fence", "AvailableUnixMilliseconds", "LeaseExpiresUnixMilliseconds")
            VALUES ({failedId}, {succeededId}, {"email:confirmation"}, {(int)MailboxEffectKind.Email}, {"{}"},
                {(int)MailboxEffectState.Exhausted}, {5}, {5L}, {now.ToUnixTimeMilliseconds()}, {0L});
            """);

        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var mailboxes = await db.EmailInboxSettings.AsNoTracking().ToListAsync();
        Assert.Equal(2, mailboxes.Count);
        Assert.Equal("synthetic-global-secret", fixture.Secrets.Unprotect(mailboxes.Single(x => x.Id == globalId),
            mailboxes.Single(x => x.Id == globalId).ClientSecret));
        Assert.Equal("synthetic-dedicated-secret", fixture.Secrets.Unprotect(mailboxes.Single(x => x.Id == dedicatedId),
            mailboxes.Single(x => x.Id == dedicatedId).ClientSecret));
        Assert.All(mailboxes, x => Assert.False(x.BackgroundSyncEnabled));
        var receipts = await db.Set<InboundMessageReceipt>().AsNoTracking().ToListAsync();
        Assert.Equal("InitialBaselineSkipped", receipts.Single(x => x.Id == skippedId).Reason);
        Assert.Equal(InboundReceiptOutcome.Succeeded, receipts.Single(x => x.Id == succeededId).Outcome);
        Assert.Equal("TenantResolutionAmbiguous", receipts.Single(x => x.Id == heldId).Reason);
        Assert.All(receipts, x => Assert.Null(x.HistoricalImportRequestId));
        var failedDelivery = await db.Set<MailboxOutboxEffect>().AsNoTracking().SingleAsync(x => x.Id == failedId);
        Assert.Equal(MailboxEffectState.Exhausted, failedDelivery.State);
        Assert.StartsWith("legacy:", failedDelivery.DispatchGroup);
        var policy = new MailboxWorkerPolicy(db, new ConfigurationBuilder().Build(), TimeProvider.System);
        Assert.Equal("Instance paused", (await policy.GetStatusAsync(default)).State);
        Assert.Empty(await db.Set<MailboxOutgoingSettings>().ToListAsync());
        Assert.Equal("Instance enabled", (await policy.SetRunningAsync(true, default)).State);
        Assert.Equal("Incoming paused", (await policy.GetMailboxStatusAsync(dedicatedId, default))!.State);
        await db.EmailInboxSettings.Where(x => x.Id == dedicatedId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.BackgroundSyncEnabled, true));
        Assert.Equal("Waiting for baseline", (await policy.GetMailboxStatusAsync(dedicatedId, default))!.State);
        Assert.Equal(3, await db.Set<InboundMessageReceipt>().CountAsync());
        Assert.Equal(MailboxEffectState.Exhausted,
            (await db.Set<MailboxOutboxEffect>().AsNoTracking().SingleAsync(x => x.Id == failedId)).State);
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
