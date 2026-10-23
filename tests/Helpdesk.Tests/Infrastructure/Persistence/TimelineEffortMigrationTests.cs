using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed class TimelineEffortMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upgrade_recovers_only_unambiguous_exact_captured_event_snapshots(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        var expectations = new Dictionary<Guid, (string Scenario, double? Hours)>();
        var effectCount = 0;
        string originalEffects;
        string originalReceipts;
        await using (var previous = fixture.Open())
        {
            var migrations = previous.Database.GetMigrations().ToArray();
            var effortIndex = Array.FindIndex(migrations, name => name.EndsWith("_PersistTimelineWorklogEffort", StringComparison.Ordinal));
            Assert.True(effortIndex > 0);
            await previous.GetService<IMigrator>().MigrateAsync(migrations[effortIndex - 1]);
            var mailboxId = Guid.NewGuid();
            previous.Organizations.AddRange(new Organization { Id = "upgrade-effort-org", Name = "Organization" },
                new Organization { Id = "other-effort-org", Name = "Other organization" });
            previous.Incidents.Add(new Incident { Id = "upgrade-effort-ticket", OrganizationId = "upgrade-effort-org",
                TrackingId = "INC-UPGRADE-EFFORT", Title = "Preserved ticket" });
            previous.Set<EmailInboxSettings>().Add(new EmailInboxSettings { Id = mailboxId, Scope = MailboxScope.Organization,
                OrganizationId = "upgrade-effort-org", SourceKey = "effort-source", MailboxAddress = "support@example.test" });
            await previous.SaveChangesAsync();
            foreach (var scenario in new[] { "fraction", "zero", "max-ordinal", "malformed", "duplicate-property", "overflow", "nonnumeric",
                "missing-hours", "missing-type", "conflict", "wrong-ticket", "wrong-type", "wrong-kind", "no-receipt", "wrong-key",
                "missing-receipt-row", "failed-receipt", "wrong-receipt-ticket", "wrong-receipt-org", "out-of-range-key",
                "valid-plus-missing", "valid-plus-duplicate", "valid-plus-overflow", "case-duplicate-hours", "case-duplicate-id",
                "case-duplicate-ticket", "case-duplicate-type", "valid-plus-case-duplicate" })
            {
                var id = Guid.NewGuid();
                var type = scenario is "zero" or "missing-type" ? TimelineEventType.Worklog : TimelineEventType.InternalNote;
                var hours = scenario == "zero" ? 0 : 0.625;
                expectations[id] = (scenario, scenario is "fraction" or "zero" or "max-ordinal" ? hours : null);
                await InsertLegacyEventAsync(previous, id, type);
                var snapshot = JsonSerializer.Serialize(new { Id = id, TicketId = scenario == "wrong-ticket" ? "other-ticket" : "upgrade-effort-ticket",
                    EventType = (int)(scenario == "wrong-type" ? TimelineEventType.Worklog : type), Hours = hours });
                var validSnapshot = snapshot;
                snapshot = scenario switch
                {
                    "malformed" => "{broken JSON",
                    "duplicate-property" or "valid-plus-duplicate" => snapshot.Replace("\"Hours\":0.625", "\"Hours\":0.625,\"Hours\":0.625"),
                    "overflow" or "valid-plus-overflow" => snapshot.Replace("\"Hours\":0.625", "\"Hours\":1e999"),
                    "nonnumeric" => snapshot.Replace("\"Hours\":0.625", "\"Hours\":\"0.625\""),
                    "missing-hours" or "valid-plus-missing" => snapshot.Replace(",\"Hours\":0.625", ""),
                    "missing-type" => snapshot.Replace("\"EventType\":0,", ""),
                    "case-duplicate-hours" or "valid-plus-case-duplicate" => snapshot.Replace("\"Hours\":0.625", "\"Hours\":0.625,\"hours\":0.625"),
                    "case-duplicate-id" => snapshot.Replace("\"Id\":", "\"id\":\"" + id + "\",\"Id\":"),
                    "case-duplicate-ticket" => snapshot.Replace("\"TicketId\":", "\"ticketid\":\"upgrade-effort-ticket\",\"TicketId\":"),
                    "case-duplicate-type" => snapshot.Replace("\"EventType\":7", "\"EventType\":7,\"eventtype\":7"),
                    _ => snapshot
                };
                var receipt = Guid.NewGuid();
                if (scenario is not ("no-receipt" or "missing-receipt-row"))
                    previous.Set<InboundMessageReceipt>().Add(new InboundMessageReceipt { Id = receipt, MailboxId = mailboxId,
                        SourceKey = "effort-source", TransportKey = "captured-" + scenario,
                        Outcome = scenario == "failed-receipt" ? InboundReceiptOutcome.RetryableFailure : InboundReceiptOutcome.Succeeded,
                        TicketId = scenario == "wrong-receipt-ticket" ? "other-ticket" : "upgrade-effort-ticket",
                        OrganizationId = scenario == "wrong-receipt-org" ? "other-effort-org" : "upgrade-effort-org" });
                previous.Set<MailboxOutboxEffect>().Add(new MailboxOutboxEffect { ReceiptId = scenario == "no-receipt" ? null : receipt,
                    EffectKey = scenario switch { "wrong-key" => "direct:unproven", "out-of-range-key" => "0256:Timeline", "max-ordinal" => "0255:Timeline", _ => "0000:Timeline" },
                    Kind = scenario == "wrong-kind" ? MailboxEffectKind.Notification : MailboxEffectKind.Timeline,
                    State = MailboxEffectState.Completed, Payload = snapshot });
                effectCount++;
                if (scenario == "conflict" || scenario.StartsWith("valid-plus-", StringComparison.Ordinal))
                {
                    previous.Set<MailboxOutboxEffect>().Add(new MailboxOutboxEffect { ReceiptId = receipt,
                        EffectKey = "0001:Timeline", Kind = MailboxEffectKind.Timeline, State = MailboxEffectState.Completed,
                        Payload = scenario == "conflict" ? validSnapshot.Replace("\"Hours\":0.625", "\"Hours\":0.25") : validSnapshot });
                    effectCount++;
                }
            }
            await previous.SaveChangesAsync();
            originalEffects = JsonSerializer.Serialize(await previous.Set<MailboxOutboxEffect>().AsNoTracking().OrderBy(item => item.Id).ToListAsync());
            originalReceipts = JsonSerializer.Serialize(await previous.Set<InboundMessageReceipt>().AsNoTracking().OrderBy(item => item.Id).ToListAsync());
        }

        await using (var upgrade = fixture.Open())
            await upgrade.Database.MigrateAsync();
        await using var reopened = fixture.Open();
        var events = await reopened.TicketTimelineEvents.AsNoTracking().ToListAsync();
        Assert.Equal(expectations.Count, events.Count);
        foreach (var item in events)
        {
            var expected = expectations[item.Id];
            Assert.True(expected.Hours == item.Hours, $"Scenario '{expected.Scenario}': expected {expected.Hours?.ToString() ?? "unavailable"}, actual {item.Hours?.ToString() ?? "unavailable"}.");
            Assert.Null(item.WorkLogId);
        }
        Assert.Empty(await reopened.Set<WorkLog>().ToListAsync());
        Assert.Equal(effectCount, await reopened.Set<MailboxOutboxEffect>().CountAsync());
        Assert.Equal(originalEffects, JsonSerializer.Serialize(await reopened.Set<MailboxOutboxEffect>().AsNoTracking().OrderBy(item => item.Id).ToListAsync()));
        Assert.Equal(originalReceipts, JsonSerializer.Serialize(await reopened.Set<InboundMessageReceipt>().AsNoTracking().OrderBy(item => item.Id).ToListAsync()));
    }

    private static Task InsertLegacyEventAsync(HelpdeskDbContext db, Guid id, TimelineEventType type) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "TicketTimelineEvents"
                ("Id", "TicketId", "CreatedUtc", "CreatedByUserId", "CreatedByUserName", "EventType", "MessageText", "RetryCount")
            VALUES ({id}, {"upgrade-effort-ticket"}, {new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero)}, {"technician-one"}, {"Technician One"}, {(int)type}, {"Identical note"}, {0})
            """);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Populated_upgrade_preserves_legacy_unknown_effort_and_roundtrips_new_snapshots(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        var legacyId = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
        string[] previousHistory;
        await using (var previous = fixture.Open())
        {
            var migrations = previous.Database.GetMigrations().ToArray();
            var effortMigration = Assert.Single(migrations, name => name.EndsWith("_PersistTimelineWorklogEffort", StringComparison.Ordinal));
            var effortIndex = Array.IndexOf(migrations, effortMigration);
            Assert.True(effortIndex > 0);
            await previous.GetService<IMigrator>().MigrateAsync(migrations[effortIndex - 1]);
            previousHistory = (await previous.Database.GetAppliedMigrationsAsync()).ToArray();
            previous.Organizations.Add(new Organization { Id = "upgrade-effort-org", Name = "Preserved organization" });
            previous.Incidents.Add(new Incident { Id = "upgrade-effort-ticket", OrganizationId = "upgrade-effort-org",
                TrackingId = "INC-UPGRADE-EFFORT", Title = "Preserved ticket", TimeSpentHours = 0.25 });
            previous.Set<WorkLog>().Add(new WorkLog { Id = "legacy-worklog", TicketId = "upgrade-effort-ticket", Hours = 0.25,
                NotesHtml = "Identical note", NotesText = "Identical note", TechnicianId = "technician-one",
                IsInternalNote = true, LoggedAt = timestamp.UtcDateTime });
            await previous.SaveChangesAsync();
            // Insert the actual pre-upgrade row shape, which has no link or effort
            // columns, despite matching the canonical worklog's note/author/time.
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "TicketTimelineEvents"
                    ("Id", "TicketId", "CreatedUtc", "CreatedByUserId", "CreatedByUserName", "EventType", "MessageHtml", "MessageText", "RetryCount")
                VALUES ({legacyId}, {"upgrade-effort-ticket"}, {timestamp}, {"technician-one"}, {"Technician One"}, {(int)TimelineEventType.InternalNote}, {"Identical note"}, {"Identical note"}, {0})
                """);
        }

        await using (var upgraded = fixture.Open())
        {
            await upgraded.Database.MigrateAsync();
            var applied = (await upgraded.Database.GetAppliedMigrationsAsync()).ToArray();
            Assert.Equal(previousHistory, applied.Take(previousHistory.Length));
            Assert.True(applied.Length > previousHistory.Length);
            Assert.Contains(applied, name => name.EndsWith("_PersistTimelineWorklogEffort", StringComparison.Ordinal));
            var legacy = await upgraded.TicketTimelineEvents.AsNoTracking().SingleAsync();
            Assert.Equal(legacyId, legacy.Id);
            Assert.Equal("Identical note", legacy.MessageText);
            Assert.Null(legacy.Hours);
            Assert.Null(legacy.WorkLogId);
            Assert.Equal(0.25, (await upgraded.Set<WorkLog>().AsNoTracking().SingleAsync()).Hours);
            Assert.Equal(0.25, (await upgraded.Incidents.AsNoTracking().SingleAsync()).TimeSpentHours);
            upgraded.TicketTimelineEvents.AddRange(
                new TicketTimelineEvent { TicketId = "upgrade-effort-ticket", EventType = TimelineEventType.InternalNote,
                    WorkLogId = "new-fraction", Hours = 0.625, MessageText = "Fraction" },
                new TicketTimelineEvent { TicketId = "upgrade-effort-ticket", EventType = TimelineEventType.Worklog,
                    WorkLogId = "new-zero", Hours = 0, MessageText = "Zero" },
                new TicketTimelineEvent { TicketId = "upgrade-effort-ticket", EventType = TimelineEventType.SystemNotification,
                    MessageText = "No effort" });
            await upgraded.SaveChangesAsync();
        }

        await using var reopened = fixture.Open();
        var events = await reopened.TicketTimelineEvents.AsNoTracking().ToListAsync();
        Assert.Equal(4, events.Count);
        Assert.Equal(0.625, Assert.Single(events, item => item.WorkLogId == "new-fraction").Hours);
        Assert.Equal(0, Assert.Single(events, item => item.WorkLogId == "new-zero").Hours);
        Assert.Null(Assert.Single(events, item => item.Id == legacyId).Hours);
        Assert.Null(Assert.Single(events, item => item.EventType == TimelineEventType.SystemNotification).Hours);
        Assert.Single(await reopened.Set<WorkLog>().AsNoTracking().ToListAsync());
    }
}
