using Helpdesk.Application.Events;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Workflow;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Helpdesk.Tests.Application.RequestTasks;

public sealed class RequestTaskLifecycleServiceTests
{
    [Theory]
    [InlineData(AutomationBindingSyncState.Drifted)]
    [InlineData(AutomationBindingSyncState.ImportPending)]
    public async Task StartAsync_AutomationTaskWithNonBrokenBinding_SubmitsToOrchestration(AutomationBindingSyncState syncState)
    {
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var requests = Substitute.For<IRepository<Request>>();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        var bindings = Substitute.For<IAutomationBindingService>();
        var payloadBuilder = Substitute.For<IRequestTaskPayloadBuilder>();
        var orchestrationClient = Substitute.For<IOrchestrationInternalClient>();
        var failurePolicy = Substitute.For<IFailurePolicyEngine>();
        var domainEvents = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        correlation.GetCorrelationId().Returns("corr-test");

        const string taskId = "019cd87fde1079559e9e17caaa5356d0";
        tasks.GetAsync(taskId).Returns(new RequestTask
        {
            Id = taskId,
            RequestId = "req-1",
            TemplateId = "22222222-2222-2222-2222-222222222222",
            Title = "Provision mailbox",
            Type = RequestTaskType.Automation,
            Status = RequestTaskStatus.Pending,
            OrganizationId = "tenant-1",
            ExpectedRuntimeSeconds = 1800,
            GraceSeconds = 600,
            HardTimeoutSeconds = 2400
        });
        requests.GetAsync("req-1").Returns(new Request
        {
            Id = "req-1",
            RequestFormId = "form-1"
        });
        bindings.GetByTaskTemplateAsync("form-1", Guid.Parse("22222222-2222-2222-2222-222222222222"), Arg.Any<CancellationToken>())
            .Returns(new AutomationBindingDto
            {
                Id = "binding-1",
                RequestFormId = "form-1",
                TaskTemplateId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                OrchestrationRequestDefinitionId = "orchestration-req-1",
                OrchestrationRequestDefinitionName = "Provision mailbox",
                Enabled = true,
                SyncState = syncState
            });
        connectivity.GetResolvedOrchestrationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new OrchestrationResolvedSettings { Enabled = true, BaseUrl = "https://orchestration.local" });
        payloadBuilder.BuildAsync(Arg.Any<RequestTask>(), "corr-test", Arg.Any<CancellationToken>())
            .Returns(RequestTaskPayloadBuildResult.Succeeded(
                "Provision mailbox",
                """{"input":{"Path":"c:\\"}}""",
                "binding-1",
                "orchestration-req-1",
                "orchestration-job-1"));
        orchestrationClient.IngestAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<OrchestrationIngestRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OrchestrationIngestResult
            {
                RequestId = "orchestration-request-1",
                RunId = "orchestration-run-1",
                ExecutionId = "orchestration-execution-1",
                Status = "Submitted"
            });

        var service = new RequestTaskLifecycleService(
            tasks,
            requests,
            connectivity,
            bindings,
            payloadBuilder,
            orchestrationClient,
            failurePolicy,
            domainEvents,
            correlation,
            NullLogger<RequestTaskLifecycleService>.Instance);

        var started = await service.StartAsync(taskId, CancellationToken.None);

        Assert.Equal(RequestTaskStatus.InProgress, started.Status);
        Assert.Equal("orchestration-request-1", started.OrchestrationExternalRequestId);
        Assert.Equal("orchestration-run-1", started.OrchestrationExternalRunId);
        await payloadBuilder.Received(1).BuildAsync(Arg.Any<RequestTask>(), "corr-test", Arg.Any<CancellationToken>());
        await orchestrationClient.Received(1).IngestAsync(
            Arg.Any<OrchestrationResolvedSettings>(),
            Arg.Is<OrchestrationIngestRequest>(x =>
                x.PayloadJson.Contains("\"Path\"", StringComparison.Ordinal) &&
                x.ExpectedRuntimeSeconds == 1800 &&
                x.GraceSeconds == 600 &&
                x.HardTimeoutSeconds == 2400),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_AutomationTaskWithBrokenBinding_ThrowsBeforeStarting()
    {
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var requests = Substitute.For<IRepository<Request>>();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        var bindings = Substitute.For<IAutomationBindingService>();
        var payloadBuilder = Substitute.For<IRequestTaskPayloadBuilder>();
        var orchestrationClient = Substitute.For<IOrchestrationInternalClient>();
        var failurePolicy = Substitute.For<IFailurePolicyEngine>();
        var domainEvents = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        correlation.GetCorrelationId().Returns("corr-test");

        const string taskId = "019cd87fde1079559e9e17caaa5356d0";
        tasks.GetAsync(taskId).Returns(new RequestTask
        {
            Id = taskId,
            RequestId = "req-1",
            TemplateId = "22222222-2222-2222-2222-222222222222",
            Title = "Provision mailbox",
            Type = RequestTaskType.Automation,
            Status = RequestTaskStatus.Pending,
            OrganizationId = "tenant-1"
        });
        requests.GetAsync("req-1").Returns(new Request
        {
            Id = "req-1",
            RequestFormId = "form-1"
        });
        bindings.GetByTaskTemplateAsync("form-1", Guid.Parse("22222222-2222-2222-2222-222222222222"), Arg.Any<CancellationToken>())
            .Returns(new AutomationBindingDto
            {
                Id = "binding-1",
                RequestFormId = "form-1",
                TaskTemplateId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                OrchestrationRequestDefinitionId = "orchestration-req-1",
                OrchestrationRequestDefinitionName = "Provision mailbox",
                Enabled = true,
                SyncState = AutomationBindingSyncState.Broken
            });

        var service = new RequestTaskLifecycleService(
            tasks,
            requests,
            connectivity,
            bindings,
            payloadBuilder,
            orchestrationClient,
            failurePolicy,
            domainEvents,
            correlation,
            NullLogger<RequestTaskLifecycleService>.Instance);

        var ex = await Assert.ThrowsAsync<AutomationBindingNotReadyException>(() => service.StartAsync(taskId, CancellationToken.None));

        Assert.Contains("broken", ex.Message, StringComparison.OrdinalIgnoreCase);
        await tasks.DidNotReceive().UpdateAsync(Arg.Any<RequestTask>());
        await payloadBuilder.DidNotReceive().BuildAsync(Arg.Any<RequestTask>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await orchestrationClient.DidNotReceive().IngestAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<OrchestrationIngestRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_AutomationTaskAfterPendingApproval_ThrowsBeforeSubmitting()
    {
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var requests = Substitute.For<IRepository<Request>>();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        var bindings = Substitute.For<IAutomationBindingService>();
        var payloadBuilder = Substitute.For<IRequestTaskPayloadBuilder>();
        var orchestrationClient = Substitute.For<IOrchestrationInternalClient>();
        var failurePolicy = Substitute.For<IFailurePolicyEngine>();
        var domainEvents = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        correlation.GetCorrelationId().Returns("corr-test");

        const string requestId = "019e8d57a1be7d74a20b26f2d6723400";
        const string approvalTaskId = "019e8d57a1be7d74a20b26f2d6723401";
        const string automationTaskId = "019e8d57a1be7d74a20b26f2d6723402";
        var approvalTask = new RequestTask
        {
            Id = approvalTaskId,
            RequestId = requestId,
            Title = "Approval",
            Type = RequestTaskType.Approval,
            Status = RequestTaskStatus.PendingApproval,
            Order = 1,
            OrganizationId = "tenant-1"
        };
        var automationTask = new RequestTask
        {
            Id = automationTaskId,
            RequestId = requestId,
            TemplateId = "22222222-2222-2222-2222-222222222222",
            Title = "Provision mailbox",
            Type = RequestTaskType.Automation,
            Status = RequestTaskStatus.Pending,
            Order = 2,
            OrganizationId = "tenant-1"
        };

        tasks.GetAsync(automationTaskId).Returns(automationTask);
        tasks.GetAllAsync().Returns([approvalTask, automationTask]);

        var service = new RequestTaskLifecycleService(
            tasks,
            requests,
            connectivity,
            bindings,
            payloadBuilder,
            orchestrationClient,
            failurePolicy,
            domainEvents,
            correlation,
            NullLogger<RequestTaskLifecycleService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(automationTaskId, CancellationToken.None));

        Assert.Contains("earlier approval", ex.Message, StringComparison.OrdinalIgnoreCase);
        await bindings.DidNotReceive().GetByTaskTemplateAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await payloadBuilder.DidNotReceive().BuildAsync(Arg.Any<RequestTask>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await orchestrationClient.DidNotReceive().IngestAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<OrchestrationIngestRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_OrchestrationSubmitFailure_MarksManualRetry_AndDoesNotScheduleRetry()
    {
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var requests = Substitute.For<IRepository<Request>>();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        var bindings = Substitute.For<IAutomationBindingService>();
        var payloadBuilder = Substitute.For<IRequestTaskPayloadBuilder>();
        var orchestrationClient = Substitute.For<IOrchestrationInternalClient>();
        var failurePolicy = Substitute.For<IFailurePolicyEngine>();
        var domainEvents = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        correlation.GetCorrelationId().Returns("corr-submit");

        const string taskId = "019e20bb13717583bef964579cbcf339";
        var task = new RequestTask
        {
            Id = taskId,
            RequestId = "019e20bb112a7685aac9294e5c6db8e0",
            Title = "Test LS on NIX",
            Type = RequestTaskType.Automation,
            Status = RequestTaskStatus.Pending,
            MaxRetries = 3,
            RetryDelayMinutes = 0,
            OrganizationId = "tenant-1"
        };

        tasks.GetAsync(taskId).Returns(task);
        connectivity.GetResolvedOrchestrationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new OrchestrationResolvedSettings { Enabled = true, BaseUrl = "https://orchestration.local" });
        payloadBuilder.BuildAsync(task, "corr-submit", Arg.Any<CancellationToken>())
            .Returns(RequestTaskPayloadBuildResult.Succeeded(
                "Test Job (Linux)",
                "{}",
                "binding-1",
                "2",
                "2"));
        orchestrationClient.IngestAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<OrchestrationIngestRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<OrchestrationIngestResult>>(_ => throw new InvalidOperationException("provider failed with client_secret=synthetic-secret and Bearer synthetic-token"));

        var service = new RequestTaskLifecycleService(
            tasks,
            requests,
            connectivity,
            bindings,
            payloadBuilder,
            orchestrationClient,
            failurePolicy,
            domainEvents,
            correlation,
            NullLogger<RequestTaskLifecycleService>.Instance);

        var failed = await service.StartAsync(taskId, CancellationToken.None);

        Assert.Equal(RequestTaskStatus.Failed, failed.Status);
        Assert.Equal(AutomationTaskStatuses.SubmitFailedManualRetry, failed.LastAutomationStatus);
        Assert.Null(failed.NextRetryAt);
        Assert.Equal("External orchestration submission failed. Check provider status before retrying.", failed.FailureReason);
        Assert.DoesNotContain("synthetic-secret", failed.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token", failed.FailureReason, StringComparison.Ordinal);
        await failurePolicy.Received(1).OnTaskFailedAsync(task.RequestId, task.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_UncertainOrchestrationSubmit_MarksManualReconciliation_AndDoesNotRetry()
    {
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var requests = Substitute.For<IRepository<Request>>();
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        var bindings = Substitute.For<IAutomationBindingService>();
        var payloadBuilder = Substitute.For<IRequestTaskPayloadBuilder>();
        var orchestrationClient = Substitute.For<IOrchestrationInternalClient>();
        var failurePolicy = Substitute.For<IFailurePolicyEngine>();
        var domainEvents = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        correlation.GetCorrelationId().Returns("corr-uncertain");

        const string taskId = "019e20bb13717583bef964579cbcf340";
        var task = new RequestTask
        {
            Id = taskId,
            RequestId = "019e20bb112a7685aac9294e5c6db8e1",
            Title = "Submit uncertain job",
            Type = RequestTaskType.Automation,
            Status = RequestTaskStatus.Pending,
            MaxRetries = 3,
            RetryDelayMinutes = 0,
            OrganizationId = "tenant-1"
        };

        tasks.GetAsync(taskId).Returns(task);
        connectivity.GetResolvedOrchestrationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new OrchestrationResolvedSettings { Enabled = true, BaseUrl = "https://orchestration.local" });
        payloadBuilder.BuildAsync(task, "corr-uncertain", Arg.Any<CancellationToken>())
            .Returns(RequestTaskPayloadBuildResult.Succeeded("Uncertain job", "{}", "binding-1", "2", "2"));
        orchestrationClient.IngestAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<OrchestrationIngestRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<OrchestrationIngestResult>>(_ => throw new OrchestrationSubmissionUncertainException("The submission acknowledgement could not be confirmed."));

        var service = new RequestTaskLifecycleService(
            tasks,
            requests,
            connectivity,
            bindings,
            payloadBuilder,
            orchestrationClient,
            failurePolicy,
            domainEvents,
            correlation,
            NullLogger<RequestTaskLifecycleService>.Instance);

        var failed = await service.StartAsync(taskId, CancellationToken.None);

        Assert.Equal(RequestTaskStatus.Failed, failed.Status);
        Assert.Equal(AutomationTaskStatuses.SubmitUncertainManualReconcile, failed.LastAutomationStatus);
        Assert.Null(failed.NextRetryAt);
        Assert.Contains("acknowledgement", failed.FailureReason, StringComparison.OrdinalIgnoreCase);
        await failurePolicy.DidNotReceive().OnTaskFailedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await domainEvents.Received(1).PublishAsync(Arg.Is<RequestTaskAutomationSubmitUncertainEvent>(x => x.TaskId == taskId), Arg.Any<CancellationToken>());
    }
}
