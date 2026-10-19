using System.Text.Json;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.RequestTasks;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Helpdesk.Tests.Application.RequestTasks;

public sealed partial class RequestTaskApprovalServiceTests
{
    [Fact]
    public async Task StartApprovalAsync_CreatesApprovalRows_AndAttemptsEmailSend()
    {
        await using var db = CreateDbContext();
        var notificationService = Substitute.For<ITicketNotificationService>();
        notificationService.SendRequestApprovalRequiredAsync(
                Arg.Any<Request>(),
                Arg.Any<RequestTask>(),
                Arg.Any<RequestTaskApproval>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
        var logger = new CapturingLogger<RequestTaskApprovalService>();
        var service = CreateService(db, notificationService, logger: logger);
        var (request, task) = await SeedApprovalRequestAsync(db);

        var started = await service.StartApprovalAsync(task, CancellationToken.None);

        Assert.Equal(RequestTaskStatus.PendingApproval, started.Status);
        Assert.Equal(TicketState.PendingApproval, request.State);
        var approval = await db.RequestTaskApprovals.SingleAsync();
        Assert.Equal("approver@example.com", approval.ApproverEmail);
        Assert.Null(approval.TokenSentAtUtc);
        await notificationService.Received(1).SendRequestApprovalRequiredAsync(
            Arg.Is<Request>(x => x.Id == request.Id),
            Arg.Is<RequestTask>(x => x.Id == task.Id),
            Arg.Is<RequestTaskApproval>(x => x.Id == approval.Id),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
        Assert.Contains(logger.Entries, x =>
            x.Level == LogLevel.Warning &&
            x.Message.Contains("Request approval email send failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetryUnsentApprovalEmailsAsync_SendsPendingUnsentApprovals_AndStampsTokenSentAt()
    {
        await using var db = CreateDbContext();
        var notificationService = Substitute.For<ITicketNotificationService>();
        notificationService.SendRequestApprovalRequiredAsync(
                Arg.Any<Request>(),
                Arg.Any<RequestTask>(),
                Arg.Any<RequestTaskApproval>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(db, notificationService);
        var (request, task) = await SeedPendingApprovalAsync(db, RequestTaskStatus.PendingApproval);

        var sent = await service.RetryUnsentApprovalEmailsAsync(CancellationToken.None);

        Assert.Equal(1, sent);
        var approval = await db.RequestTaskApprovals.SingleAsync();
        Assert.NotNull(approval.TokenSentAtUtc);
        await notificationService.Received(1).SendRequestApprovalRequiredAsync(
            Arg.Is<Request>(x => x.Id == request.Id),
            Arg.Is<RequestTask>(x => x.Id == task.Id),
            Arg.Is<RequestTaskApproval>(x => x.Id == approval.Id),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryUnsentApprovalEmailsAsync_SkipsApprovalsWhoseTaskIsNoLongerPendingApproval()
    {
        await using var db = CreateDbContext();
        var notificationService = Substitute.For<ITicketNotificationService>();
        var service = CreateService(db, notificationService);
        await SeedPendingApprovalAsync(db, RequestTaskStatus.Completed);

        var sent = await service.RetryUnsentApprovalEmailsAsync(CancellationToken.None);

        Assert.Equal(0, sent);
        var approval = await db.RequestTaskApprovals.SingleAsync();
        Assert.Null(approval.TokenSentAtUtc);
        await notificationService.DidNotReceive().SendRequestApprovalRequiredAsync(
            Arg.Any<Request>(),
            Arg.Any<RequestTask>(),
            Arg.Any<RequestTaskApproval>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartApprovalAsync_WhenAlreadyPendingApprovalWithoutRows_CreatesRowsAndSendsEmail()
    {
        await using var db = CreateDbContext();
        var notificationService = Substitute.For<ITicketNotificationService>();
        notificationService.SendRequestApprovalRequiredAsync(
                Arg.Any<Request>(),
                Arg.Any<RequestTask>(),
                Arg.Any<RequestTaskApproval>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        var logger = new CapturingLogger<RequestTaskApprovalService>();
        var service = CreateService(db, notificationService, logger: logger);
        var (_, task) = await SeedApprovalRequestAsync(db);
        task.Status = RequestTaskStatus.PendingApproval;
        task.State = TicketState.PendingApproval;
        await db.SaveChangesAsync();

        await service.StartApprovalAsync(task, CancellationToken.None);

        var approval = await db.RequestTaskApprovals.SingleAsync();
        Assert.Equal("approver@example.com", approval.ApproverEmail);
        Assert.NotNull(approval.TokenSentAtUtc);
        await notificationService.Received(1).SendRequestApprovalRequiredAsync(
            Arg.Any<Request>(),
            Arg.Is<RequestTask>(x => x.Id == task.Id),
            Arg.Is<RequestTaskApproval>(x => x.Id == approval.Id),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
        Assert.Contains(logger.Entries, x =>
            x.Level == LogLevel.Warning &&
            x.Message.Contains("Recovered missing request approval row", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetryUnsentApprovalEmailsAsync_RecoversPendingApprovalTasksWithoutRows()
    {
        await using var db = CreateDbContext();
        var notificationService = Substitute.For<ITicketNotificationService>();
        notificationService.SendRequestApprovalRequiredAsync(
                Arg.Any<Request>(),
                Arg.Any<RequestTask>(),
                Arg.Any<RequestTaskApproval>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(db, notificationService);
        var (_, task) = await SeedApprovalRequestAsync(db);
        task.Status = RequestTaskStatus.PendingApproval;
        task.State = TicketState.PendingApproval;
        await db.SaveChangesAsync();

        var sent = await service.RetryUnsentApprovalEmailsAsync(CancellationToken.None);

        Assert.Equal(0, sent);
        var approval = await db.RequestTaskApprovals.SingleAsync();
        Assert.Equal("approver@example.com", approval.ApproverEmail);
        Assert.NotNull(approval.TokenSentAtUtc);
        await notificationService.Received(1).SendRequestApprovalRequiredAsync(
            Arg.Any<Request>(),
            Arg.Is<RequestTask>(x => x.Id == task.Id),
            Arg.Is<RequestTaskApproval>(x => x.Id == approval.Id),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPublicApprovalAsync_FirstView_AddsTimelineEvent()
    {
        await using var db = CreateDbContext();
        var signer = Substitute.For<IPublicTicketLinkSigner>();
        signer.ValidateToken("token", "INC-PENDING:task-pending-approval", "approver@example.com").Returns(true);
        var service = CreateService(db, Substitute.For<ITicketNotificationService>(), signer: signer);
        await SeedPendingApprovalAsync(db, RequestTaskStatus.PendingApproval);

        var approval = await service.GetPublicApprovalAsync("INC-PENDING", "task-pending-approval", "approver@example.com", "token", CancellationToken.None);

        Assert.NotNull(approval);
        var timeline = await db.TicketTimelineEvents.SingleAsync();
        Assert.Equal(TimelineEventType.SystemNotification, timeline.EventType);
        Assert.Contains("Approval link viewed", timeline.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApproveAsync_AddsTimelineEvent()
    {
        await using var db = CreateDbContext();
        var signer = Substitute.For<IPublicTicketLinkSigner>();
        signer.ValidateToken("token", "INC-PENDING:task-pending-approval", "approver@example.com").Returns(true);
        var service = CreateService(db, Substitute.For<ITicketNotificationService>(), signer: signer);
        await SeedPendingApprovalAsync(db, RequestTaskStatus.PendingApproval);

        var result = await service.ApproveAsync("INC-PENDING", "task-pending-approval", "approver@example.com", "token", CancellationToken.None);

        Assert.True(result.Success);
        var timeline = await db.TicketTimelineEvents.SingleAsync();
        Assert.Equal(TimelineEventType.SystemNotification, timeline.EventType);
        Assert.Contains("Approval granted", timeline.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectAsync_AddsTimelineEvent()
    {
        await using var db = CreateDbContext();
        var signer = Substitute.For<IPublicTicketLinkSigner>();
        signer.ValidateToken("token", "INC-PENDING:task-pending-approval", "approver@example.com").Returns(true);
        var notificationService = Substitute.For<ITicketNotificationService>();
        notificationService.SendRequestApprovalDeclinedAsync(
                Arg.Any<Request>(),
                Arg.Any<RequestTask>(),
                Arg.Any<RequestTaskApproval>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(db, notificationService, signer: signer);
        await SeedPendingApprovalAsync(db, RequestTaskStatus.PendingApproval);

        var result = await service.RejectAsync(
            "INC-PENDING",
            "task-pending-approval",
            "approver@example.com",
            "token",
            "Too risky for production.",
            CancellationToken.None);

        Assert.True(result.Success);
        var timeline = await db.TicketTimelineEvents.SingleAsync();
        Assert.Equal(TimelineEventType.SystemNotification, timeline.EventType);
        Assert.Contains("Approval rejected", timeline.MessageText, StringComparison.Ordinal);
        Assert.Contains("Too risky for production.", timeline.MessageText, StringComparison.Ordinal);
        await notificationService.Received(1).SendRequestApprovalDeclinedAsync(
            Arg.Is<Request>(x => x.Id == "request-pending-approval"),
            Arg.Is<RequestTask>(x => x.Id == "task-pending-approval"),
            Arg.Is<RequestTaskApproval>(x => x.Id == "approval-pending"),
            "Too risky for production.",
            Arg.Any<string>(),
            "requester@example.com",
            "requester@example.com",
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RejectAsync_WithoutReason_DoesNotMutate()
    {
        await using var db = CreateDbContext();
        var signer = Substitute.For<IPublicTicketLinkSigner>();
        signer.ValidateToken("token", "INC-PENDING:task-pending-approval", "approver@example.com").Returns(true);
        var notificationService = Substitute.For<ITicketNotificationService>();
        var service = CreateService(db, notificationService, signer: signer);
        await SeedPendingApprovalAsync(db, RequestTaskStatus.PendingApproval);

        var result = await service.RejectAsync(
            "INC-PENDING",
            "task-pending-approval",
            "approver@example.com",
            "token",
            " ",
            CancellationToken.None);

        Assert.False(result.Success);
        var approval = await db.RequestTaskApprovals.SingleAsync();
        var task = await db.RequestTasks.SingleAsync();
        Assert.Equal(RequestTaskApprovalStatus.Pending, approval.Status);
        Assert.Equal(RequestTaskStatus.PendingApproval, task.Status);
        Assert.Empty(await db.TicketTimelineEvents.ToListAsync());
        await notificationService.DidNotReceive().SendRequestApprovalDeclinedAsync(
            Arg.Any<Request>(),
            Arg.Any<RequestTask>(),
            Arg.Any<RequestTaskApproval>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RejectAsync_CancelsApprovalAndIncompleteSiblingTasks()
    {
        await using var db = CreateDbContext();
        var signer = Substitute.For<IPublicTicketLinkSigner>();
        signer.ValidateToken("token", "INC-PENDING:task-pending-approval", "approver@example.com").Returns(true);
        var service = CreateService(db, Substitute.For<ITicketNotificationService>(), signer: signer);
        var (request, _) = await SeedPendingApprovalAsync(db, RequestTaskStatus.PendingApproval);
        db.RequestTasks.AddRange(
            new RequestTask
            {
                Id = "task-downstream-pending",
                RequestId = request.Id,
                Title = "Downstream pending",
                Type = RequestTaskType.Automation,
                Status = RequestTaskStatus.Pending,
                Order = 2,
                OrganizationId = "tenant-1"
            },
            new RequestTask
            {
                Id = "task-completed",
                RequestId = request.Id,
                Title = "Already completed",
                Type = RequestTaskType.Manual,
                Status = RequestTaskStatus.Completed,
                Order = 3,
                OrganizationId = "tenant-1"
            },
            new RequestTask
            {
                Id = "task-skipped",
                RequestId = request.Id,
                Title = "Already skipped",
                Type = RequestTaskType.Manual,
                Status = RequestTaskStatus.Skipped,
                Order = 4,
                OrganizationId = "tenant-1"
            });
        await db.SaveChangesAsync();

        var result = await service.RejectAsync(
            "INC-PENDING",
            "task-pending-approval",
            "approver@example.com",
            "token",
            "Insufficient details.",
            CancellationToken.None);

        Assert.True(result.Success);
        var tasks = await db.RequestTasks.ToDictionaryAsync(x => x.Id);
        Assert.Equal(RequestTaskStatus.Cancelled, tasks["task-pending-approval"].Status);
        Assert.Equal(RequestTaskStatus.Cancelled, tasks["task-downstream-pending"].Status);
        Assert.Equal(RequestTaskStatus.Completed, tasks["task-completed"].Status);
        Assert.Equal(RequestTaskStatus.Skipped, tasks["task-skipped"].Status);
        Assert.Equal(TicketState.OnHold, request.State);
        Assert.Equal("Cancelled", request.WorkflowStatus);
        Assert.Contains("Insufficient details.", request.WorkflowBlockReason, StringComparison.Ordinal);
        Assert.Contains("Insufficient details.", tasks["task-pending-approval"].ResultJson, StringComparison.Ordinal);
    }

    private static RequestTaskApprovalService CreateService(
        HelpdeskDbContext db,
        ITicketNotificationService notificationService,
        IPublicTicketLinkSigner? signer = null,
        ITimelineEventBus? timelineEventBus = null,
        ILogger<RequestTaskApprovalService>? logger = null)
        => new(
            db,
            new RequestFormSchemaParser(),
            notificationService,
            signer ?? Substitute.For<IPublicTicketLinkSigner>(),
            timelineEventBus ?? Substitute.For<ITimelineEventBus>(),
            logger ?? Substitute.For<ILogger<RequestTaskApprovalService>>());

    private static HelpdeskDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new HelpdeskDbContext(options, new TestTenantContext(), new HttpContextAccessor());
    }

    private static async Task<(Request Request, RequestTask Task)> SeedApprovalRequestAsync(HelpdeskDbContext db)
    {
        const string formId = "form-approval";
        const string requestId = "request-approval";
        const string taskId = "task-approval";
        const string templateId = "77777777-7777-7777-7777-777777777777";
        db.RequestForms.Add(new RequestForm
        {
            Id = formId,
            JsonSchema = JsonDocument.Parse($$"""
            {
              "fields": [],
              "tasks": [
                {
                  "id": "{{templateId}}",
                  "name": "Approval gate",
                  "order": 1,
                  "type": "approval",
                  "approvalAllowedDays": 7,
                  "approvalApprovers": [
                    { "source": "Customer", "id": "cust-1", "name": "Approver", "email": "approver@example.com" }
                  ]
                }
              ]
            }
            """)
        });
        var request = new Request
        {
            Id = requestId,
            TrackingId = "INC-APPROVAL",
            Title = "Approval request",
            RequestFormId = formId,
            OrganizationId = "tenant-1",
            CustomerId = "customer-1",
            PayloadJson = """{"Server":"srv-01"}"""
        };
        var task = new RequestTask
        {
            Id = taskId,
            RequestId = requestId,
            TemplateId = templateId,
            Title = "Approval gate",
            Type = RequestTaskType.Approval,
            Status = RequestTaskStatus.Pending,
            Order = 1,
            OrganizationId = "tenant-1"
        };
        db.Requests.Add(request);
        db.RequestTasks.Add(task);
        await db.SaveChangesAsync();
        return (request, task);
    }

    private static async Task<(Request Request, RequestTask Task)> SeedPendingApprovalAsync(
        HelpdeskDbContext db,
        RequestTaskStatus taskStatus)
    {
        var request = new Request
        {
            Id = "request-pending-approval",
            TrackingId = "INC-PENDING",
            Title = "Pending approval request",
            RequestFormId = "form-pending",
            OrganizationId = "tenant-1",
            RequesterEmail = "requester@example.com",
            PayloadJson = """{"Server":"srv-01"}"""
        };
        var task = new RequestTask
        {
            Id = "task-pending-approval",
            RequestId = request.Id,
            TemplateId = "88888888-8888-8888-8888-888888888888",
            Title = "Approval gate",
            Type = RequestTaskType.Approval,
            Status = taskStatus,
            Order = 1,
            OrganizationId = "tenant-1"
        };
        var approval = new RequestTaskApproval
        {
            Id = "approval-pending",
            RequestId = request.Id,
            RequestTaskId = task.Id,
            ApproverSource = "Customer",
            ApproverId = "cust-1",
            ApproverName = "Approver",
            ApproverEmail = "approver@example.com",
            Status = RequestTaskApprovalStatus.Pending
        };
        db.Requests.Add(request);
        db.RequestTasks.Add(task);
        db.RequestTaskApprovals.Add(approval);
        await db.SaveChangesAsync();
        return (request, task);
    }

    private sealed class TestTenantContext : ITenantContext
    {
        public string? TenantId => "tenant-1";
        public string? UserId => "user-1";
        public bool IsHelpdeskAdmin => true;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
