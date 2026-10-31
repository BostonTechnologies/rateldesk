using Helpdesk.Application.Events;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Workflow;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Helpdesk.Tests.Application.RequestTasks;

public sealed class RequestTaskRetryProcessorTests
{
    [Fact]
    public async Task ProcessAsync_BindingNotReadyOnRetry_FailsTaskAndRunsWorkflow()
    {
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var lifecycle = Substitute.For<IRequestTaskLifecycleService>();
        var workflow = Substitute.For<IWorkflowEngine>();
        var events = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        var logger = Substitute.For<ILogger<RequestTaskRetryProcessor>>();
        const string requestId = "019cd87fde1079559e9e17caaa5356c8";
        const string taskId = "019cd87fde1079559e9e17caaa5356d0";

        tasks.GetAllAsync().Returns([
            new RequestTask
            {
                Id = taskId,
                RequestId = requestId,
                Title = "Provision mailbox",
                Type = RequestTaskType.Automation,
                Status = RequestTaskStatus.Failed,
                NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                OrganizationId = "tenant-1"
            }
        ]);
        lifecycle.StartAsync(taskId, Arg.Any<CancellationToken>())
            .Returns<Task<RequestTask>>(_ => throw new AutomationBindingNotReadyException("binding import pending"));

        var processor = new RequestTaskRetryProcessor(
            tasks,
            lifecycle,
            workflow,
            events,
            correlation,
            logger);

        await processor.ProcessAsync(CancellationToken.None);

        await lifecycle.Received(1).RetryAsync(taskId, Arg.Any<CancellationToken>());
        await lifecycle.Received(1).FailAsync(taskId, "binding import pending", Arg.Any<CancellationToken>());
        await workflow.Received(1).RunAsync(requestId, WorkflowRunReason.TaskFailed, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(AutomationTaskStatuses.SubmitFailedManualRetry)]
    [InlineData(AutomationTaskStatuses.SubmitUncertainManualReconcile)]
    [InlineData(AutomationTaskStatuses.ExecutionUnknownManualReconcile)]
    public async Task ProcessAsync_OperatorActionRequired_DoesNotAutoRetry(string status)
    {
        var tasks = Substitute.For<IRepository<RequestTask>>();
        var lifecycle = Substitute.For<IRequestTaskLifecycleService>();
        var workflow = Substitute.For<IWorkflowEngine>();
        var events = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        var logger = Substitute.For<ILogger<RequestTaskRetryProcessor>>();
        const string requestId = "019e20bb112a7685aac9294e5c6db8e0";
        const string taskId = "019e20bb13717583bef964579cbcf339";

        tasks.GetAllAsync().Returns([
            new RequestTask
            {
                Id = taskId,
                RequestId = requestId,
                Title = "Test LS on NIX",
                Type = RequestTaskType.Automation,
                Status = RequestTaskStatus.Failed,
                LastAutomationStatus = status,
                NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                OrganizationId = "tenant-1"
            }
        ]);

        var processor = new RequestTaskRetryProcessor(
            tasks,
            lifecycle,
            workflow,
            events,
            correlation,
            logger);

        await processor.ProcessAsync(CancellationToken.None);

        await lifecycle.DidNotReceive().RetryAsync(taskId, Arg.Any<CancellationToken>());
        await lifecycle.DidNotReceive().StartAsync(taskId, Arg.Any<CancellationToken>());
        await workflow.DidNotReceive().RunAsync(requestId, Arg.Any<WorkflowRunReason>(), Arg.Any<CancellationToken>());
    }
}
