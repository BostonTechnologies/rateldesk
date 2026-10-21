using Helpdesk.Application.Events;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Workflow;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Helpdesk.Tests.Application.Workflow;

public sealed class WorkflowEngineTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task RunAsync_AutoStartPolicy_OnlyEnabledTasksAttemptExecution(bool? autoStart)
    {
        const string requestId = "019cd87fde1079559e9e17caaa5356c8";
        const string taskId = "019cd87fde1079559e9e17caaa5356d0";
        var dependencies = Substitute.For<IWorkflowDependencyEvaluator>();
        dependencies.EvaluateAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(new WorkflowDependencyEvaluationResult(0));
        var conditions = Substitute.For<IWorkflowConditionEvaluator>();
        var requests = Substitute.For<IRepository<Request>>();
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var approvalService = Substitute.For<IRequestTaskApprovalService>();
        var lifecycle = Substitute.For<IRequestTaskLifecycleService>();
        var stateService = Substitute.For<IRequestTaskStateService>();
        var events = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        var logger = Substitute.For<ILogger<WorkflowEngine>>();

        requests.GetAsync(requestId).Returns(new Request
        {
            Id = requestId,
            OrganizationId = "tenant-1"
        });
        tasks.GetAllAsync().Returns([
            new RequestTask
            {
                Id = taskId,
                RequestId = requestId,
                Title = "Provision mailbox",
                AutoStart = autoStart,
                Type = RequestTaskType.Automation,
                Status = RequestTaskStatus.Pending
            }
        ]);
        lifecycle.StartAsync(taskId, Arg.Any<CancellationToken>())
            .Returns<Task<RequestTask>>(_ => throw new AutomationBindingNotReadyException("binding drifted"));

        var engine = new WorkflowEngine(
            dependencies,
            conditions,
            approvalService,
            requests,
            tasks,
            lifecycle,
            stateService,
            events,
            correlation,
            logger);

        var result = await engine.RunAsync(requestId, WorkflowRunReason.RequestCreated, CancellationToken.None);

        Assert.Equal(0, result.AutoStartedCount);
        if (autoStart == false)
        {
            await lifecycle.DidNotReceive().StartAsync(taskId, Arg.Any<CancellationToken>());
            await lifecycle.DidNotReceive().FailAsync(taskId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        else await lifecycle.Received(1).FailAsync(taskId, "binding drifted", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ApprovalTask_StartsApprovalAndDoesNotStartLaterAutomation()
    {
        const string requestId = "019cd87fde1079559e9e17caaa5356e0";
        const string approvalTaskId = "019cd87fde1079559e9e17caaa5356e1";
        const string automationTaskId = "019cd87fde1079559e9e17caaa5356e2";
        var dependencies = Substitute.For<IWorkflowDependencyEvaluator>();
        dependencies.EvaluateAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(new WorkflowDependencyEvaluationResult(0));
        var conditions = Substitute.For<IWorkflowConditionEvaluator>();
        var requests = Substitute.For<IRepository<Request>>();
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var approvalService = Substitute.For<IRequestTaskApprovalService>();
        var lifecycle = Substitute.For<IRequestTaskLifecycleService>();
        var stateService = Substitute.For<IRequestTaskStateService>();
        var events = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        var logger = Substitute.For<ILogger<WorkflowEngine>>();
        var approvalTask = new RequestTask
        {
            Id = approvalTaskId,
            RequestId = requestId,
            Title = "Approval",
            Type = RequestTaskType.Approval,
            Status = RequestTaskStatus.Pending,
            Order = 1
        };

        requests.GetAsync(requestId).Returns(new Request
        {
            Id = requestId,
            OrganizationId = "tenant-1"
        });
        tasks.GetAllAsync().Returns([
            approvalTask,
            new RequestTask
            {
                Id = automationTaskId,
                RequestId = requestId,
                Title = "Automate",
                Type = RequestTaskType.Automation,
                Status = RequestTaskStatus.Pending,
                Order = 2
            }
        ]);
        approvalService.StartApprovalAsync(approvalTask, Arg.Any<CancellationToken>())
            .Returns(approvalTask);

        var engine = new WorkflowEngine(
            dependencies,
            conditions,
            approvalService,
            requests,
            tasks,
            lifecycle,
            stateService,
            events,
            correlation,
            logger);

        var result = await engine.RunAsync(requestId, WorkflowRunReason.RequestCreated, CancellationToken.None);

        Assert.Equal(1, result.AutoStartedCount);
        await approvalService.Received(1).StartApprovalAsync(approvalTask, Arg.Any<CancellationToken>());
        await lifecycle.DidNotReceive().StartAsync(automationTaskId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_PendingEarlierApproval_DoesNotStartLaterAutomation()
    {
        const string requestId = "019e8d53f4f077f1b494f973b219f1a0";
        const string approvalTaskId = "019e8d53f4f077f1b494f973b219f1a1";
        const string automationTaskId = "019e8d53f4f077f1b494f973b219f1a2";
        var dependencies = Substitute.For<IWorkflowDependencyEvaluator>();
        dependencies.EvaluateAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(new WorkflowDependencyEvaluationResult(0));
        var conditions = Substitute.For<IWorkflowConditionEvaluator>();
        var requests = Substitute.For<IRepository<Request>>();
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var approvalService = Substitute.For<IRequestTaskApprovalService>();
        var lifecycle = Substitute.For<IRequestTaskLifecycleService>();
        var stateService = Substitute.For<IRequestTaskStateService>();
        var events = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        var logger = Substitute.For<ILogger<WorkflowEngine>>();

        var pendingApprovalTask = new RequestTask
        {
            Id = approvalTaskId,
            RequestId = requestId,
            Title = "Approval",
            Type = RequestTaskType.Approval,
            Status = RequestTaskStatus.PendingApproval,
            Order = 1
        };
        requests.GetAsync(requestId).Returns(new Request
        {
            Id = requestId,
            OrganizationId = "tenant-1"
        });
        tasks.GetAllAsync().Returns([
            pendingApprovalTask,
            new RequestTask
            {
                Id = automationTaskId,
                RequestId = requestId,
                Title = "Automate",
                Type = RequestTaskType.Automation,
                Status = RequestTaskStatus.Pending,
                Order = 2
            }
        ]);
        approvalService.StartApprovalAsync(pendingApprovalTask, Arg.Any<CancellationToken>())
            .Returns(pendingApprovalTask);

        var engine = new WorkflowEngine(
            dependencies,
            conditions,
            approvalService,
            requests,
            tasks,
            lifecycle,
            stateService,
            events,
            correlation,
            logger);

        var result = await engine.RunAsync(requestId, WorkflowRunReason.RequestCreated, CancellationToken.None);

        Assert.Equal(0, result.AutoStartedCount);
        await approvalService.Received(1).StartApprovalAsync(pendingApprovalTask, Arg.Any<CancellationToken>());
        await lifecycle.DidNotReceive().StartAsync(automationTaskId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_CompletedEarlierApproval_StartsLaterAutomation()
    {
        const string requestId = "019e8d54a0a8743697e86be26e5d9500";
        const string approvalTaskId = "019e8d54a0a8743697e86be26e5d9501";
        const string automationTaskId = "019e8d54a0a8743697e86be26e5d9502";
        var dependencies = Substitute.For<IWorkflowDependencyEvaluator>();
        dependencies.EvaluateAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(new WorkflowDependencyEvaluationResult(0));
        var conditions = Substitute.For<IWorkflowConditionEvaluator>();
        var requests = Substitute.For<IRepository<Request>>();
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var approvalService = Substitute.For<IRequestTaskApprovalService>();
        var lifecycle = Substitute.For<IRequestTaskLifecycleService>();
        var stateService = Substitute.For<IRequestTaskStateService>();
        var events = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        var logger = Substitute.For<ILogger<WorkflowEngine>>();

        requests.GetAsync(requestId).Returns(new Request
        {
            Id = requestId,
            OrganizationId = "tenant-1"
        });
        tasks.GetAllAsync().Returns([
            new RequestTask
            {
                Id = approvalTaskId,
                RequestId = requestId,
                Title = "Approval",
                Type = RequestTaskType.Approval,
                Status = RequestTaskStatus.Completed,
                Order = 1
            },
            new RequestTask
            {
                Id = automationTaskId,
                RequestId = requestId,
                Title = "Automate",
                Type = RequestTaskType.Automation,
                Status = RequestTaskStatus.Pending,
                Order = 2
            }
        ]);

        var engine = new WorkflowEngine(
            dependencies,
            conditions,
            approvalService,
            requests,
            tasks,
            lifecycle,
            stateService,
            events,
            correlation,
            logger);

        var result = await engine.RunAsync(requestId, WorkflowRunReason.ApprovalReceived, CancellationToken.None);

        Assert.Equal(1, result.AutoStartedCount);
        await lifecycle.Received(1).StartAsync(automationTaskId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ReplayedApprovalPublishesNoDuplicateEvaluationAfterContinuationSettles()
    {
        const string requestId = "019e8d54a0a8743697e86be26e5d9510";
        var dependencies = Substitute.For<IWorkflowDependencyEvaluator>();
        dependencies.EvaluateAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(new WorkflowDependencyEvaluationResult(0));
        var conditions = Substitute.For<IWorkflowConditionEvaluator>();
        var requests = Substitute.For<IRepository<Request>>();
        requests.GetAsync(requestId).Returns(new Request { Id = requestId, OrganizationId = "tenant-1" });
        var tasks = Substitute.For<IRepository<RequestTask>>();
        tasks.GetAllAsync().Returns([]);
        var approvalService = Substitute.For<IRequestTaskApprovalService>();
        var lifecycle = Substitute.For<IRequestTaskLifecycleService>();
        var stateService = Substitute.For<IRequestTaskStateService>();
        stateService.EvaluateParentRequestState(requestId, Arg.Any<CancellationToken>())
            .Returns(true, false);
        var events = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        var logger = Substitute.For<ILogger<WorkflowEngine>>();
        var engine = new WorkflowEngine(dependencies, conditions, approvalService, requests, tasks,
            lifecycle, stateService, events, correlation, logger);

        var first = await engine.RunAsync(requestId, WorkflowRunReason.ApprovalReceived, CancellationToken.None);
        var replay = await engine.RunAsync(requestId, WorkflowRunReason.ApprovalReceived, CancellationToken.None);

        Assert.True(first.ParentStateChanged);
        Assert.False(replay.ParentStateChanged);
        await events.Received(1).PublishAsync(Arg.Any<WorkflowEvaluatedEvent>(), Arg.Any<CancellationToken>());
        await events.Received(1).PublishAsync(Arg.Any<WorkflowProgressedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NonApprovalNoopStillPublishesEvaluation()
    {
        const string requestId = "019e8d54a0a8743697e86be26e5d9511";
        var dependencies = Substitute.For<IWorkflowDependencyEvaluator>();
        dependencies.EvaluateAsync(requestId, Arg.Any<CancellationToken>())
            .Returns(new WorkflowDependencyEvaluationResult(0));
        var conditions = Substitute.For<IWorkflowConditionEvaluator>();
        var requests = Substitute.For<IRepository<Request>>();
        requests.GetAsync(requestId).Returns(new Request { Id = requestId, OrganizationId = "tenant-1" });
        var tasks = Substitute.For<IRepository<RequestTask>>();
        tasks.GetAllAsync().Returns([]);
        var approvalService = Substitute.For<IRequestTaskApprovalService>();
        var lifecycle = Substitute.For<IRequestTaskLifecycleService>();
        var stateService = Substitute.For<IRequestTaskStateService>();
        stateService.EvaluateParentRequestState(requestId, Arg.Any<CancellationToken>()).Returns(false);
        var events = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        var logger = Substitute.For<ILogger<WorkflowEngine>>();
        var engine = new WorkflowEngine(dependencies, conditions, approvalService, requests, tasks,
            lifecycle, stateService, events, correlation, logger);

        await engine.RunAsync(requestId, WorkflowRunReason.RequestCreated, CancellationToken.None);

        await events.Received(1).PublishAsync(Arg.Any<WorkflowEvaluatedEvent>(), Arg.Any<CancellationToken>());
        await events.DidNotReceive().PublishAsync(Arg.Any<WorkflowProgressedEvent>(), Arg.Any<CancellationToken>());
    }
}
