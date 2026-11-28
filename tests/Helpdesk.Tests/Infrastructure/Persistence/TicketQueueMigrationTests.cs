using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed class TicketQueueMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queue_upgrade_preserves_legacy_tickets_and_enforces_group_references(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        const string organizationId = "managed-customer";
        const string groupId = "desktop";
        const string assignedUserId = "msp-operator";
        var dueDate = new DateTime(2026, 11, 30, 10, 0, 0, DateTimeKind.Utc);
        var legacyTickets = new Ticket[]
        {
            new Incident { Id = "incident", TrackingId = "INC-LEGACY", AssignedToId = assignedUserId, DueDate = dueDate },
            new Request
            {
                Id = "request", TrackingId = "REQ-LEGACY", WorkflowStatus = "Blocked",
                WorkflowFailureTaskId = "failed-task", WorkflowFailureAttemptId = "rejected-attempt",
                WorkflowFailureCategory = RequestFailureCategory.SubmissionRejected
            },
            new Change { Id = "change", TrackingId = "CHG-LEGACY", AssignedToId = assignedUserId, ImplementorUserId = assignedUserId }
        };
        string precedingMigration;
        await using (var db = fixture.Open())
        {
            var migrations = db.Database.GetMigrations().ToArray();
            var migrationIndex = Array.FindIndex(migrations, name => name.EndsWith("_AddTicketQueueAssignment", StringComparison.Ordinal));
            Assert.True(migrationIndex > 0);
            precedingMigration = migrations[migrationIndex - 1];
            Assert.EndsWith("_OwnRequestFailureHolds", precedingMigration);
            await db.GetService<IMigrator>().MigrateAsync(precedingMigration);
            await using var seed = new BeforeQueueAssignmentContext(db);
            seed.Organizations.Add(new Organization { Id = organizationId, Name = "Managed Customer" });
            seed.SupportGroups.Add(new SupportGroup { Id = groupId, OwningOrganizationId = organizationId, Name = "Desktop" });
            foreach (var ticket in legacyTickets)
            {
                ticket.OrganizationId = organizationId;
                ticket.State = TicketState.InProgress;
                seed.Tickets.Add(ticket);
            }
            seed.TicketTimelineEvents.Add(new TicketTimelineEvent
            {
                TicketId = "request", MessageText = "Preserved workflow note", EventType = TimelineEventType.InternalNote,
                CreatedByUserId = assignedUserId, CreatedByUserName = "MSP Operator"
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.Open())
        {
            await db.Database.MigrateAsync();
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            var hydrated = await db.Tickets.ToDictionaryAsync(ticket => ticket.Id);
            Assert.Equal(legacyTickets.Length, hydrated.Count);
            foreach (var legacy in legacyTickets)
            {
                var current = hydrated[legacy.Id];
                Assert.Null(current.AssignedSupportGroupId);
                Assert.Equal(0, current.QueueAssignmentVersion);
                Assert.Equal(legacy.TrackingId, current.TrackingId);
                Assert.Equal(legacy.AssignedToId, current.AssignedToId);
                Assert.Equal(legacy.State, current.State);
                Assert.Equal(legacy.DueDate, current.DueDate);
            }
            AssertRetainedHold(Assert.IsType<Request>(hydrated["request"]));
            Assert.Equal(assignedUserId, Assert.IsType<Change>(hydrated["change"]).ImplementorUserId);
            Assert.Equal("Preserved workflow note", (await db.TicketTimelineEvents.SingleAsync()).MessageText);
            Assert.Empty(await db.TicketQueueTransitions.ToListAsync());

            hydrated["incident"].AssignedSupportGroupId = groupId;
            await db.SaveChangesAsync();
            Assert.Equal(1, hydrated["incident"].QueueAssignmentVersion);
            hydrated["incident"].AssignedSupportGroupId = "missing-group";
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using (var db = fixture.Open())
        {
            db.SupportGroups.Remove(await db.SupportGroups.SingleAsync());
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using (var db = fixture.Open())
        {
            await db.GetService<IMigrator>().MigrateAsync(precedingMigration);
            await using var previous = new BeforeQueueAssignmentContext(db);
            Assert.Equal(legacyTickets.Length, await previous.Tickets.CountAsync());
            Assert.Equal(assignedUserId, (await previous.Changes.SingleAsync()).ImplementorUserId);
            AssertRetainedHold(await previous.Requests.SingleAsync());
            Assert.Equal("Preserved workflow note", (await previous.TicketTimelineEvents.SingleAsync()).MessageText);
        }
    }

    private static void AssertRetainedHold(Request request)
    {
        Assert.Equal("Blocked", request.WorkflowStatus);
        Assert.Equal("failed-task", request.WorkflowFailureTaskId);
        Assert.Equal("rejected-attempt", request.WorkflowFailureAttemptId);
        Assert.Equal(RequestFailureCategory.SubmissionRejected, request.WorkflowFailureCategory);
    }

    private sealed class BeforeQueueAssignmentContext(HelpdeskDbContext current) : HelpdeskDbContext(
        (DbContextOptions<HelpdeskDbContext>)current.GetService<IDbContextOptions>(), new MigrationTenant(), new HttpContextAccessor())
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            PublishedCampaignSeedModel.OmitTicketQueueAssignment(builder);
        }
    }

    private sealed class MigrationTenant : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }
}
