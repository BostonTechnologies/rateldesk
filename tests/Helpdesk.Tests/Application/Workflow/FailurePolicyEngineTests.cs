using Helpdesk.Application.Events;
using Helpdesk.Application.Incidents;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Application.Workflow;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Helpdesk.Tests.Application.Workflow;

public sealed class FailurePolicyEngineTests
{
    [Fact]
    public async Task OnTaskFailedAsync_ManualRetrySubmitFailure_BlocksRequestWithoutSchedulingRetry()
    {
        var requests = Substitute.For<IRepository<Request>>();
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var events = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        var logger = Substitute.For<ILogger<FailurePolicyEngine>>();
        const string requestId = "019e20bb112a7685aac9294e5c6db8e0";
        const string taskId = "019e20bb13717583bef964579cbcf339";

        var request = new Request
        {
            Id = requestId,
            OrganizationId = "tenant-1"
        };
        var task = new RequestTask
        {
            Id = taskId,
            RequestId = requestId,
            Title = "Test LS on NIX",
            Type = RequestTaskType.Automation,
            Status = RequestTaskStatus.Failed,
            MaxRetries = 3,
            RetryCount = 0,
            LastAutomationStatus = AutomationTaskStatuses.SubmitFailedManualRetry,
            OrganizationId = "tenant-1"
        };

        requests.GetAsync(requestId).Returns(request);
        tasks.GetAsync(taskId).Returns(task);

        var engine = new FailurePolicyEngine(requests, tasks, events, correlation, logger);

        var result = await engine.OnTaskFailedAsync(requestId, taskId, CancellationToken.None);

        Assert.False(result.RetryScheduled);
        Assert.Null(task.NextRetryAt);
        Assert.Equal("BlockRequest", result.Decision);
        Assert.Equal("Blocked", request.WorkflowStatus);
        await tasks.DidNotReceive().UpdateAsync(task);
        await requests.Received(1).UpdateAsync(request);
    }

    [Fact]
    public async Task OnTaskFailedAsync_BlockedRequest_SendsSystemTimelineNotification()
    {
        var requests = Substitute.For<IRepository<Request>>();
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var events = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        var logger = Substitute.For<ILogger<FailurePolicyEngine>>();
        var timelineEvents = Substitute.For<IRepository<TicketTimelineEvent>>();
        var sender = Substitute.For<IRequestSender>();
        const string requestId = "req-1";
        const string taskId = "task-1";

        requests.GetAsync(requestId).Returns(new Request
        {
            Id = requestId,
            OrganizationId = "tenant-1"
        });
        tasks.GetAsync(taskId).Returns(new RequestTask
        {
            Id = taskId,
            RequestId = requestId,
            Name = "Test LS on NIX",
            Type = RequestTaskType.Automation,
            Status = RequestTaskStatus.Failed,
            OrganizationId = "tenant-1"
        });
        timelineEvents.GetAllAsync().Returns(Array.Empty<TicketTimelineEvent>());
        sender.Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>())
            .Returns(new WorkLog());

        var engine = new FailurePolicyEngine(requests, tasks, events, correlation, logger, timelineEvents, sender);

        await engine.OnTaskFailedAsync(requestId, taskId, CancellationToken.None);

        await sender.Received(1).Send(
            Arg.Is<CreateWorkLogCommand>(x =>
                x.TicketId == requestId &&
                x.NotifyCustomer &&
                x.EventType == TimelineEventType.SystemNotification &&
                x.Notes != null &&
                x.Notes.Contains("Workflow: Blocked") &&
                x.Notes.Contains("Task 'Test LS on NIX' failed.")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTaskFailedAsync_DoesNotDuplicateExistingFailureTimelineNotification()
    {
        var requests = Substitute.For<IRepository<Request>>();
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var events = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        var logger = Substitute.For<ILogger<FailurePolicyEngine>>();
        var timelineEvents = Substitute.For<IRepository<TicketTimelineEvent>>();
        var sender = Substitute.For<IRequestSender>();
        const string requestId = "req-1";
        const string taskId = "task-1";

        requests.GetAsync(requestId).Returns(new Request
        {
            Id = requestId,
            OrganizationId = "tenant-1"
        });
        tasks.GetAsync(taskId).Returns(new RequestTask
        {
            Id = taskId,
            RequestId = requestId,
            Name = "Test LS on NIX",
            Type = RequestTaskType.Automation,
            Status = RequestTaskStatus.Failed,
            OrganizationId = "tenant-1"
        });
        timelineEvents.GetAllAsync().Returns(new[]
        {
            new TicketTimelineEvent
            {
                TicketId = requestId,
                EventType = TimelineEventType.SystemNotification,
                MessageText = "Workflow: Blocked\nTask 'Test LS on NIX' failed."
            }
        });

        var engine = new FailurePolicyEngine(requests, tasks, events, correlation, logger, timelineEvents, sender);

        await engine.OnTaskFailedAsync(requestId, taskId, CancellationToken.None);

        await sender.DidNotReceive().Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnTaskFailedAsync_SelfServiceFailure_CreatesInvestigationIncidentAndSendsFailureEmail(bool preDispatch)
    {
        var requests = Substitute.For<IRepository<Request>>();
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var incidents = Substitute.For<IRepository<Incident>>();
        var events = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        var logger = Substitute.For<ILogger<FailurePolicyEngine>>();
        var timelineEvents = Substitute.For<IRepository<TicketTimelineEvent>>();
        var sender = Substitute.For<IRequestSender>();
        var notifications = Substitute.For<ITicketNotificationService>();
        const string requestId = "req-ss-1";
        const string taskId = "task-ss-1";
        CreateIncidentCommand? capturedIncident = null;
        var request = new Request
        {
            Id = requestId,
            TrackingId = "REQ-SS-1",
            Title = "Disk capacity report",
            RequestFormId = "form-1",
            RequesterEmail = "customer@example.com",
            CustomerId = "customer-1",
            OrganizationId = "tenant-1",
            Priority = TicketPriority.Low
        };
        var task = new RequestTask
        {
            Id = taskId,
            RequestId = requestId,
            Name = "Run - Disk capacity report",
            Type = RequestTaskType.Automation,
            Status = RequestTaskStatus.Failed,
            FailureReason = "External orchestration run failed",
            OrganizationId = "tenant-1"
        };
        var incident = new Incident
        {
            Id = "inc-1",
            TrackingId = "INC-1",
            RequesterEmail = "customer@example.com"
        };

        requests.GetAsync(requestId).Returns(request);
        tasks.GetAsync(taskId).Returns(task);
        timelineEvents.GetAllAsync().Returns(Array.Empty<TicketTimelineEvent>());
        incidents.GetAllAsync().Returns(Array.Empty<Incident>());
        sender.Send(Arg.Do<CreateIncidentCommand>(x => capturedIncident = x), Arg.Any<CancellationToken>())
            .Returns(incident);
        sender.Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>())
            .Returns(new WorkLog());

        var engine = new FailurePolicyEngine(
            requests,
            tasks,
            events,
            correlation,
            logger,
            timelineEvents,
            sender,
            incidents,
            notifications);

        var diagnostic = preDispatch ? new Helpdesk.Shared.DTOs.Orchestration.AutomationPreDispatchFailure(
            "capabilities", "authorization", "access_denied", 403, false, "attempt-1", "provider-1", "trace-1", null) : null;
        await engine.OnTaskFailedAsync(requestId, taskId, CancellationToken.None, diagnostic);

        Assert.NotNull(capturedIncident);
        Assert.Equal(TicketPriority.Low, capturedIncident.Priority);
        Assert.Equal("customer@example.com", capturedIncident.RequesterEmail);
        Assert.Equal("tenant-1", capturedIncident.OrganizationId);
        Assert.Contains("Source self-service request: req-ss-1", capturedIncident.Description);
        if (preDispatch)
        {
            Assert.Contains("phase=capabilities, layer=authorization, code=access_denied, HTTP status=403, retryable=false", capturedIncident.Description);
            Assert.Contains("Attempt: attempt-1. Connection: unavailable. Provider reference: provider-1. Trace: trace-1.", capturedIncident.Description);
        }
        else Assert.DoesNotContain("Pre-dispatch diagnostic:", capturedIncident.Description);
        await notifications.Received(1).SendSelfServiceRequestFailedAsync(
            request,
            incident,
            "External orchestration run failed",
            "customer@example.com",
            "customer@example.com",
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>());
    }
}
