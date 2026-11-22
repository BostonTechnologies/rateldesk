using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Sla;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.RequestTasks;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Tests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.RequestTasks;

public sealed class AdministrativeClosureProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_and_unknown_work_preserves_attempts_with_one_audit_per_closure(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using var db = fixture.Open();
        await db.Database.MigrateAsync();
        var parent = new Request { Id = "request", TrackingId = "REQ-1", State = TicketState.OnHold, WorkflowStatus = "Blocked" };
        var failed = new RequestTask
        {
            Id = "failed", TrackingId = "TASK-1", RequestId = parent.Id, Status = RequestTaskStatus.Failed,
            Type = RequestTaskType.Automation, FailureReason = "Provider returned an error", ResultJson = "{\"failed\":true}",
            AutomationAttemptCorrelationId = "attempt", OrchestrationExternalRunId = "run", NextRetryAt = DateTimeOffset.UtcNow
        };
        var unknown = new RequestTask
        {
            Id = "unknown", TrackingId = "TASK-2", RequestId = parent.Id, Status = RequestTaskStatus.InProgress,
            Type = RequestTaskType.Automation, LastAutomationStatus = AutomationTaskStatuses.ExecutionUnknownManualReconcile,
            AutomationNextCheckUtcTicks = 12345
        };
        var approval = new RequestTask { Id = "approval", TrackingId = "TASK-3", RequestId = parent.Id,
            Type = RequestTaskType.Approval, Status = RequestTaskStatus.PendingApproval };
        db.Requests.Add(parent);
        db.RequestTasks.AddRange(failed, unknown, approval);
        db.RequestTaskApprovals.Add(new RequestTaskApproval { RequestId = parent.Id, RequestTaskId = approval.Id });
        await db.SaveChangesAsync();
        var effects = new IngressEffectContext();
        var command = new CloseRequestWork(db, effects, new MailboxOutboxStore(db, effects, TimeProvider.System),
            Substitute.For<ITicketSlaCompletionService>(), TimeProvider.System);
        var empty = new HashSet<string>();
        var admin = new CurrentUserAccessProfile(true, "Operator", null, null, null, null, true, empty, empty, empty, empty);
        var select = new CloseWorkSelection(failed.Id, failed.AutomationStateVersion);
        Assert.Equal("unavailable", (await command.ExecuteAsync(select, false, "cleanup", "actor",
            admin with { IsHelpdeskAdmin = false }, default)).Outcome);
        Assert.Equal("changed", (await command.ExecuteAsync(select with { StateVersion = -1 }, false, "cleanup", "actor", admin, default)).Outcome);
        Assert.Equal(new CloseWorkResult(failed.Id, "closed", 1, 2),
            await command.ExecuteAsync(select, false, "acknowledged failure", "actor", admin, default));
        parent = await db.Requests.SingleAsync();
        Assert.Equal(new CloseWorkResult(parent.Id, "closed", 2, 0),
            await command.ExecuteAsync(new(parent.Id, parent.WorkflowStateVersion), true, "test finished", "actor", admin, default));
        Assert.Equal("already_closed", (await command.ExecuteAsync(select, false, "later", "another", admin, default)).Outcome);

        await using var verify = fixture.Open();
        Assert.Equal(TicketState.Resolved, (await verify.Requests.SingleAsync()).State);
        failed = await verify.RequestTasks.SingleAsync(x => x.Id == "failed");
        Assert.Equal(RequestTaskStatus.Failed, failed.Status);
        Assert.Equal("Provider returned an error", failed.FailureReason);
        Assert.Equal("{\"failed\":true}", failed.ResultJson);
        Assert.Equal("attempt", failed.AutomationAttemptCorrelationId);
        Assert.Equal("run", failed.OrchestrationExternalRunId);
        Assert.Equal("acknowledged failure", failed.AdministrativeClosureReason);
        Assert.Null(failed.NextRetryAt);
        unknown = await verify.RequestTasks.SingleAsync(x => x.Id == "unknown");
        Assert.Equal(AutomationTaskStatuses.ExecutionUnknownManualReconcile, unknown.LastAutomationStatus);
        Assert.Null(unknown.AutomationNextCheckUtcTicks);
        Assert.Equal(RequestTaskApprovalStatus.Superseded, (await verify.RequestTaskApprovals.SingleAsync()).Status);
        Assert.Equal(2, await verify.TicketTimelineEvents.CountAsync());
        Assert.Equal(2, await verify.Set<MailboxOutboxEffect>().CountAsync());
        Assert.Equal(2, await verify.ActivityLogs.CountAsync());
    }
}
