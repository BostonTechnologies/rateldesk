using Helpdesk.Application.Messaging;
using Helpdesk.Application.Notifications;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.Tenants;
using Helpdesk.Application.Services.Tickets;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Tests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TicketRequest = Helpdesk.Shared.Models.Request;

namespace Helpdesk.Tests.Application.WorkLogs;

public sealed class ChangeActivityPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notes_and_authenticated_replies_preserve_every_change_lifecycle_and_its_ticket_state(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await InitializeAsync(fixture);
        foreach (var lifecycle in Enum.GetValues<ChangeLifecycleState>())
        {
            var id = $"change-{lifecycle}";
            var original = await SeedChangeAsync(fixture, id, lifecycle);
            await using (var db = fixture.Open())
            {
                var handler = Worklogs(db);
                await handler.Handle(new CreateWorkLogCommand(id, 1, "Public note", "technician", "Technician", NotifyCustomer: false), default);
                await handler.Handle(new CreateWorkLogCommand(id, 0.5, "Internal note", "technician", "Technician", NotifyCustomer: false, IsInternalNote: true), default);
                await handler.Handle(new CreateWorkLogCommand(id, 0, "System note", "technician", "System", NotifyCustomer: false, EventType: TimelineEventType.SystemNotification), default);
                await InboundReplyAsync(db, id);
            }

            await using var readback = fixture.Open();
            var stored = await readback.Changes.AsNoTracking().SingleAsync(change => change.Id == id);
            Assert.Equal(lifecycle, stored.LifecycleState);
            Assert.Equal(original.State, stored.State);
            Assert.Equal(original.ClosedAt, stored.ClosedAt);
            Assert.Equal(3, stored.Replies);
            Assert.Equal(1.5, stored.TimeSpentHours);
            Assert.Equal("Requester", stored.LastReplierName);
            Assert.Contains("observer@example.test", stored.CcRecipients);
            Assert.Equal(4, await readback.WorkLogs.CountAsync(log => log.TicketId == id));
            var events = await readback.TicketTimelineEvents.Where(item => item.TicketId == id).ToListAsync();
            Assert.Equal(4, events.Count);
            Assert.Single(events, item => item.EventType == TimelineEventType.InternalNote);
            Assert.Single(events, item => item.EventType == TimelineEventType.SystemNotification);
            Assert.Single(events, item => item.EventType == TimelineEventType.CustomerReply);
            Assert.Equal(original.Approvals.Single().Status,
                (await readback.ChangeApprovals.SingleAsync(approval => approval.ChangeId == id)).Status);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incident_and_request_reply_state_behavior_remains_unchanged(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await InitializeAsync(fixture);
        foreach (var ticket in new Ticket[] { new Incident { Id = "incident" }, new TicketRequest { Id = "request" } })
        {
            ticket.OrganizationId = "org";
            ticket.State = TicketState.InProgress;
            ticket.RequesterEmail = "requester@example.test";
            await using (var seed = fixture.Open())
            {
                seed.Tickets.Add(ticket);
                await seed.SaveChangesAsync();
            }

            await using (var db = fixture.Open())
                await Worklogs(db).Handle(new CreateWorkLogCommand(ticket.Id, 1, "Note", "technician", "Technician", NotifyCustomer: false), default);
            await using (var readback = fixture.Open())
                Assert.Equal(TicketState.Replied, (await readback.Tickets.SingleAsync(item => item.Id == ticket.Id)).State);

            await using (var db = fixture.Open()) await InboundReplyAsync(db, ticket.Id);
            await using (var readback = fixture.Open())
            {
                var stored = await readback.Tickets.SingleAsync(item => item.Id == ticket.Id);
                Assert.Equal(TicketState.WaitingReply, stored.State);
                Assert.Equal(2, stored.Replies);
                Assert.Equal(1, stored.TimeSpentHours);
                Assert.Equal(2, await readback.WorkLogs.CountAsync(log => log.TicketId == ticket.Id));
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Activity_started_before_a_concurrent_lifecycle_commit_preserves_the_committed_decision(bool postgres, bool inbound)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await InitializeAsync(fixture);
        foreach (var completion in new[] { false, true })
        {
            var id = completion ? "completing-change" : "reviewed-change";
            var initial = completion ? ChangeLifecycleState.ImplementationInProgress : ChangeLifecycleState.PendingApproval;
            var target = completion ? ChangeLifecycleState.ImplementedSuccess : ChangeLifecycleState.ApprovedForImplementation;
            await SeedChangeAsync(fixture, id, initial);
            var gate = new HoldActivitySave(id);
            await using var activityDb = fixture.Open(gate);
            Task activity = inbound
                ? InboundReplyAsync(activityDb, id)
                : Worklogs(activityDb).Handle(new CreateWorkLogCommand(id, 1, "Concurrent note", "technician", "Technician", NotifyCustomer: false), default);
            var reviewedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await using var lifecycleDb = fixture.Open();
                var decision = await lifecycleDb.Changes.Include(change => change.Approvals).SingleAsync(change => change.Id == id);
                decision.LifecycleState = target;
                if (completion)
                {
                    decision.State = TicketState.Resolved;
                    decision.ClosedAt = reviewedAt;
                }
                decision.Approvals.Single().Status = ChangeApprovalStatus.Reviewed;
                decision.Approvals.Single().ReviewedAtUtc = reviewedAt;
                await lifecycleDb.SaveChangesAsync();
            }
            finally
            {
                gate.Release.TrySetResult();
            }
            await activity.WaitAsync(TimeSpan.FromSeconds(20));

            await using var readback = fixture.Open();
            var stored = await readback.Changes.AsNoTracking().SingleAsync(change => change.Id == id);
            Assert.Equal(target, stored.LifecycleState);
            Assert.Equal(completion ? TicketState.Resolved : TicketState.InProgress, stored.State);
            Assert.Equal(completion ? reviewedAt : (DateTimeOffset?)null, stored.ClosedAt);
            Assert.Equal("implementor", stored.ImplementorUserId);
            Assert.Equal(new[] { "approver" }, stored.ApproverUserIds);
            Assert.Equal(1, stored.Replies);
            Assert.Equal(inbound ? 0 : 1, stored.TimeSpentHours);
            Assert.Single(await readback.WorkLogs.Where(log => log.TicketId == id).ToListAsync());
            Assert.Single(await readback.TicketTimelineEvents.Where(item => item.TicketId == id).ToListAsync());
            var approval = await readback.ChangeApprovals.SingleAsync(item => item.ChangeId == id);
            Assert.Equal(ChangeApprovalStatus.Reviewed, approval.Status);
            Assert.Equal(reviewedAt, approval.ReviewedAtUtc);
        }
    }

    private static async Task InitializeAsync(ServiceIdentityMigrationTests.DatabaseFixture fixture)
    {
        await using var db = fixture.Open();
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "org", Name = "Activity test organization" });
        await db.SaveChangesAsync();
    }

    private static async Task<Change> SeedChangeAsync(ServiceIdentityMigrationTests.DatabaseFixture fixture, string id, ChangeLifecycleState lifecycle)
    {
        var implemented = lifecycle is ChangeLifecycleState.ImplementedSuccess or ChangeLifecycleState.ImplementedBackedOut;
        var change = new Change
        {
            Id = id, OrganizationId = "org", Title = "Activity test change", TrackingId = $"CHG-{id}",
            LifecycleState = lifecycle, State = implemented ? TicketState.Resolved : TicketState.InProgress,
            ClosedAt = implemented ? new DateTimeOffset(2025, 12, 1, 12, 0, 0, TimeSpan.Zero) : null,
            RequesterEmail = "requester@example.test", ImplementorUserId = "implementor", ApproverUserIds = ["approver"],
            Approvals = [new ChangeApproval
            {
                ApproverId = "approver", ApproverName = "Approver", ApproverEmail = "approver@example.test",
                Status = lifecycle >= ChangeLifecycleState.ApprovedForImplementation ? ChangeApprovalStatus.Reviewed : ChangeApprovalStatus.Pending
            }]
        };
        await using var db = fixture.Open();
        db.Changes.Add(change);
        await db.SaveChangesAsync();
        return change;
    }

    private static CreateWorkLogCommandHandler Worklogs(HelpdeskDbContext db)
    {
        var sanitizer = Substitute.For<IHtmlSanitizerService>();
        sanitizer.Sanitize(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var images = Substitute.For<IWorklogImageStorageService>();
        images.ExtractAndStoreImagesAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(1));
        var converter = Substitute.For<IHtmlToPlainTextConverter>();
        converter.Convert(Arg.Any<string>()).Returns(call => call.Arg<string>());
        return new CreateWorkLogCommandHandler(new EfRepository<WorkLog>(db), new EfRepository<Incident>(db),
            new EfRepository<ActivityLog>(db), Substitute.For<INotificationService>(), Substitute.For<IEmailService>(),
            new EfRepository<EmailTemplate>(db), new EfRepository<Ticket>(db), new EfRepository<Customer>(db),
            new EfRepository<TicketTimelineEvent>(db), Substitute.For<ITimelineEventBus>(), sanitizer, images, converter,
            Substitute.For<IEmailTemplateRenderer>(), Substitute.For<IEmailLayoutResolver>(),
            Substitute.For<ITenantBrandingResolver>(), Substitute.For<IPublicTicketLinkSigner>(), new ConfigurationBuilder().Build());
    }

    private static Task<Ticket?> InboundReplyAsync(HelpdeskDbContext db, string id)
    {
        var sender = Substitute.For<IRequestSender>();
        sender.Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => Worklogs(db).Handle(call.Arg<CreateWorkLogCommand>(), call.Arg<CancellationToken>()));
        var processor = new InboundTicketProcessor(sender, NullLogger.Instance,
            Substitute.For<ITenantProvisioningService>(), Substitute.For<ITicketNotificationService>(),
            new EfRepository<BlockedEntity>(db), Substitute.For<IEmailService>(), new EfRepository<EmailTemplate>(db),
            new EfRepository<Incident>(db), new EfRepository<Ticket>(db), new EfRepository<TicketTimelineEvent>(db),
            Substitute.For<ITicketAttachmentService>(),
            new InboundInlineImageResolver(Substitute.For<IInlineImageStorageService>(), NullLogger<InboundInlineImageResolver>.Instance),
            Substitute.For<IEmailTemplateRenderer>(), Substitute.For<IEmailLayoutResolver>(), Substitute.For<ITenantBrandingResolver>(), db: db);
        var messageId = $"activity-{id}@example.test";
        var message = new InboundEmailContext(messageId, null, null, "org", "mailbox@example.test",
            "requester@example.test", "Requester", ["mailbox@example.test"], ["observer@example.test"],
            "Authenticated reply", "Customer response", "Customer response", DateTimeOffset.UtcNow, new Dictionary<string, string>(), []);
        return processor.ProcessTicketEmailAsync(message, [],
            new Customer { Id = "requester", OrganizationId = "org", Name = "Requester", Email = "requester@example.test" },
            default, messageId, authorizedTicketId: id);
    }

    private sealed class HoldActivitySave(string changeId) : SaveChangesInterceptor
    {
        private int held;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Change>().Any(entry => entry.Entity.Id == changeId &&
                    entry.State == Microsoft.EntityFrameworkCore.EntityState.Modified) && Interlocked.Exchange(ref held, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }
}
