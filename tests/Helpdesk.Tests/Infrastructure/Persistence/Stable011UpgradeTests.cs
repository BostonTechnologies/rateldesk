using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed class Stable011UpgradeTests
{
    // These are the last migrations in the published beta.22 tag, not a moving
    // "previous migration" inferred from the candidate's migration list.
    private const string PostgresBaseline = "20261009075713_ReplaceServiceLinksWithPairingCodes";
    private const string SqliteBaseline = "20261009082938_StorePairingTimestampsAsUtcTicks";
    private const string PostgresIdentityBaseline = "20261014175710_AddIntegrationCredentialPairing";
    private const string SqliteIdentityBaseline = "20261014175823_AddIntegrationCredentialPairing";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Beta22_upgrade_and_restart_preserve_settings_identity_tickets_categories_roles_and_delivery(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        var category = Guid.NewGuid();
        var delivery = Guid.NewGuid();
        var effect = Guid.NewGuid();
        var instance = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 14, 12, 30, 0, TimeSpan.Zero);
        Dictionary<string, TableSnapshot> snapshots;

        await using (var identity = fixture.Identity())
        {
            await identity.GetService<IMigrator>().MigrateAsync(postgres ? PostgresIdentityBaseline : SqliteIdentityBaseline);
            identity.Users.Add(new() { Id = "upgrade-owner", UserName = "upgrade-owner", Email = "owner@example.test", IsEnabled = true, IsInstanceAdministrator = true });
            identity.IntegrationCredentials.Add(new() { Id = Guid.NewGuid(), OwnerUserId = "upgrade-owner", Name = "Preserved API credential", Prefix = "rdk_upgrade",
                SecretHash = new string('a', 64), Purpose = "api", OrganizationId = "upgrade-org", Permissions = "Incident.Read", CreatedAtUtc = now,
                CreatedAtUnixMilliseconds = now.ToUnixTimeMilliseconds(), ExpiresAtUtc = now.AddDays(30) });
            await identity.SaveChangesAsync();
        }
        await using (var db = fixture.Open())
        {
            await db.GetService<IMigrator>().MigrateAsync(postgres ? PostgresBaseline : SqliteBaseline);
            db.Organizations.Add(new() { Id = "upgrade-org", Name = "Synthetic upgrade organization" });
            db.Customers.Add(new() { Id = "upgrade-customer", OrganizationId = "upgrade-org", Name = "Synthetic customer", Email = "customer@example.test" });
            db.InstanceInitializations.Add(new() { InstanceId = instance, OperationId = Guid.NewGuid(), SetupVersion = "0.1.1-beta.22", CompletedAtUtc = now });
            db.InstanceBrandings.Add(new() { ApplicationName = "Synthetic helpdesk", ApplicationUrl = "https://desk.example.test", UpdatedUtc = now.UtcDateTime });
            db.WorkingCalendars.Add(new() { Id = "upgrade-calendar", Name = "Synthetic business calendar", TimeZoneId = "Africa/Johannesburg" });
            db.TenantSlaSettings.Add(new() { TenantId = "upgrade-org", CalendarId = "upgrade-calendar", UseBusinessHours = true });
            db.Roles.Add(new() { Id = "upgrade-role", Key = "upgrade-custom", Name = "Custom analyst", Scope = RoleScopeKind.Tenant, OwnerOrganizationId = "upgrade-org",
                Permissions = [new() { Permission = "Incident.Read" }] });
            db.ScopedRoleAssignments.Add(new() { Id = "upgrade-assignment", UserId = "upgrade-owner", RoleKey = "upgrade-custom", OrganizationId = "upgrade-org" });
            db.TicketCategories.Add(new() { Id = category, Name = "Synthetic service category", Type = TicketCategoryType.Service, CreatedUtc = now.UtcDateTime });
            db.Incidents.Add(new() { Id = "upgrade-incident", TrackingId = "INC-UPGRADE", Title = "Preserved incident", OrganizationId = "upgrade-org", CustomerId = "upgrade-customer",
                OriginalEmailHtml = "<p style=\"color:#111;background:#fff\">Preserved original</p>", OriginalEmailText = "Preserved original",
                CategoryLinks = [new() { TicketCategoryId = category }] });
            db.Requests.Add(new() { Id = "upgrade-request", TrackingId = "REQ-UPGRADE", Title = "Preserved request", OrganizationId = "upgrade-org", CustomerId = "upgrade-customer",
                CategoryLinks = [new() { TicketCategoryId = category }] });
            db.Set<Change>().Add(new() { Id = "upgrade-change", TrackingId = "CHG-UPGRADE", Title = "Preserved change", OrganizationId = "upgrade-org",
                RequestedForUserId = "upgrade-owner", CategoryLinks = [new() { TicketCategoryId = category }] });
            db.WorkLogs.Add(new() { Id = "upgrade-worklog", TicketId = "upgrade-incident", NotesHtml = "<p>Preserved work</p>", NotesText = "Preserved work", Hours = 0.5,
                TechnicianId = "upgrade-owner", LoggedAt = now.UtcDateTime });
            db.Attachments.Add(new() { Id = Guid.NewGuid(), TicketId = "upgrade-incident", FileName = "synthetic.txt", FilePath = "fixture/synthetic.txt",
                ContentType = "text/plain", SizeBytes = 4, CreatedAt = now.UtcDateTime, UploadedById = "upgrade-owner" });
            db.Set<MailboxOutboxEffect>().Add(new() { Id = effect, DeliveryEventId = delivery, EffectKey = "upgrade-delivery", DispatchGroup = "upgrade-group", Kind = MailboxEffectKind.Email,
                State = MailboxEffectState.NeedsReview, Attempts = 2, Fence = 7, LastErrorCode = "SubmissionOutcomeUnknown", Payload = "{\"synthetic\":true}",
                RecipientOutcomeJson = "{\"AcceptedRecipients\":[],\"RejectedRecipients\":[\"recipient@example.test\"]}" });
            await db.SaveChangesAsync();
            // Insert through the shipped schema: a candidate may add computed
            // sort columns, so its current EF INSERT/RETURNING is not the baseline.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "TicketTimelineEvents"
                    ("Id", "TicketId", "CreatedUtc", "CreatedByUserId", "CreatedByUserName", "EventType", "MessageHtml", "MessageText", "EmailStatus", "EmailRecipient", "RetryCount", "RetryError")
                VALUES ({delivery}, {"upgrade-incident"}, {now}, {"upgrade-owner"}, {"Synthetic owner"}, {1}, {"<p>Preserved delivery history</p>"}, {"Preserved delivery history"}, {3}, {"recipient@example.test"}, {2}, {"SubmissionOutcomeUnknown"})
                """);
            snapshots = new();
            foreach (var table in new[] { "Organizations", "Customers", "InstanceInitializations", "InstanceBrandings", "WorkingCalendars", "TenantSlaSettings",
                         "Roles", "RolePermissions", "ScopedRoleAssignments", "Tickets", "WorkLogs", "Attachments", "TicketCategories", "IncidentCategoryLinks", "RequestCategoryLinks", "ChangeCategoryLinks", "MailboxOutboxEffect", "TicketTimelineEvents" })
            {
                // The outbox uses the entity's mapped table, not a DbSet name.
                var actual = table == "MailboxOutboxEffect" ? db.Model.FindEntityType(typeof(MailboxOutboxEffect))!.GetTableName()! : table;
                snapshots[actual] = await SnapshotAsync(db, actual);
            }
        }

        // Reopen each context, migrate twice, and compare every original column.
        // This catches destructive transforms, accidental resets and restart drift.
        for (var restart = 0; restart < 2; restart++)
        {
            await using var db = fixture.Open();
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            foreach (var (table, previous) in snapshots)
            {
                var current = await SnapshotAsync(db, table, previous.Columns);
                Assert.Equal(previous.Rows, current.Rows);
            }
            Assert.Equal(instance, (await db.InstanceInitializations.SingleAsync()).InstanceId);
            Assert.Equal(category, (await db.IncidentCategoryLinks.SingleAsync()).TicketCategoryId);
            Assert.Equal(category, (await db.RequestCategoryLinks.SingleAsync()).TicketCategoryId);
            Assert.Equal(category, (await db.ChangeCategoryLinks.SingleAsync()).TicketCategoryId);
            var pending = await db.Set<MailboxOutboxEffect>().SingleAsync(x => x.Id == effect);
            Assert.Equal(MailboxEffectState.NeedsReview, pending.State);
            Assert.Equal(7, pending.Fence);
            Assert.Equal(delivery, pending.DeliveryEventId);
            await using var identity = fixture.Identity();
            await identity.Database.MigrateAsync();
            Assert.Empty(await identity.Database.GetPendingMigrationsAsync());
            var owner = await identity.Users.SingleAsync();
            Assert.True(owner.IsEnabled && owner.IsInstanceAdministrator);
            Assert.Equal("owner@example.test", owner.Email);
            var credential = await identity.IntegrationCredentials.SingleAsync();
            Assert.Equal(new string('a', 64), credential.SecretHash);
            Assert.Equal("upgrade-owner", credential.OwnerUserId);
            Assert.Equal("api", credential.Purpose);
            Assert.Equal("Incident.Read", credential.Permissions);
            Assert.Equal(now.AddDays(30), credential.ExpiresAtUtc);
        }
    }

    private sealed record TableSnapshot(string[] Columns, string[] Rows);

    private static async Task<TableSnapshot> SnapshotAsync(DbContext db, string table, string[]? columns = null)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        // Identifiers come only from the fixed table inventory/model metadata.
        command.CommandText = $"SELECT {(columns is null ? "*" : string.Join(",", columns.Select(x => '"' + x + '"')))} FROM \"{table}\"";
        await using var reader = await command.ExecuteReaderAsync();
        columns ??= reader.GetColumnSchema().Select(x => x.ColumnName!).ToArray();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var values = new string?[columns.Length];
            for (var i = 0; i < values.Length; i++)
                values[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            rows.Add(JsonSerializer.Serialize(values));
        }
        return new(columns, rows.Order(StringComparer.Ordinal).ToArray());
    }
}
