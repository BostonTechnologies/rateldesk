using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Tickets;
using Helpdesk.Infrastructure.RequestTasks;
using Helpdesk.Shared.Models;
using Helpdesk.Tests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.RequestTasks;

public sealed class RequestTaskApprovalStartupProviderTests
{
    [Theory]
    [InlineData(false, false)][InlineData(false, true)]
    [InlineData(true, false)][InlineData(true, true)]
    public async Task Approval_start_rechecks_a_hold_committed_after_its_template_was_loaded(bool postgres, bool operatorHold)
    {
        await using var database = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        var templateId = Guid.NewGuid();
        await using (var seed = database.Open())
        {
            await seed.Database.MigrateAsync();
            seed.RequestForms.Add(new RequestForm { Id = "form", JsonSchema = JsonDocument.Parse($$"""
                {"tasks":[{"id":"{{templateId}}","name":"Review","type":"approval",
                "approvalApprovers":[{"source":"Customer","id":"customer","name":"Approver","email":"approver@example.test"}]}]}
                """) });
            seed.Requests.Add(new Request { Id = "request", RequestFormId = "form", WorkflowStatus = "InProgress", State = TicketState.InProgress });
            seed.RequestTasks.Add(new RequestTask { Id = "approval", RequestId = "request", TemplateId = templateId.ToString(), Type = RequestTaskType.Approval, Title = "Review" });
            await seed.SaveChangesAsync();
        }
        var barrier = new HeldApprovalLookup();
        await using var starting = database.Open(barrier);
        var notifications = Substitute.For<ITicketNotificationService>();
        var service = new RequestTaskApprovalService(starting, new RequestFormSchemaParser(), notifications,
            Substitute.For<IPublicTicketLinkSigner>(), NullLogger<RequestTaskApprovalService>.Instance);
        var snapshot = await starting.RequestTasks.AsNoTracking().SingleAsync();
        var start = service.StartApprovalAsync(snapshot, default);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await using var competing = database.Open();
            var parent = await competing.Requests.SingleAsync();
            parent.State = TicketState.OnHold;
            parent.OperatorHold = operatorHold;
            parent.WorkflowStatus = operatorHold ? RequestHoldPolicy.OperatorStatus : "NeedsReconciliation";
            parent.WorkflowBlockReason = operatorHold ? RequestHoldPolicy.OperatorReason : AutomationTaskStatuses.ReconciliationHoldReason;
            if (!operatorHold) competing.RequestTasks.Add(new RequestTask { Id = "uncertain", RequestId = parent.Id,
                Type = RequestTaskType.Automation, Status = RequestTaskStatus.Failed,
                LastAutomationStatus = AutomationTaskStatuses.ExecutionUnknownManualReconcile });
            await competing.SaveChangesAsync();
        }
        finally { barrier.Release.TrySetResult(); }
        await Assert.ThrowsAsync<RequestProgressionHeldException>(() => start);
        await starting.SaveChangesAsync();
        await using var readback = database.Open();
        Assert.Equal(RequestTaskStatus.Pending, (await readback.RequestTasks.SingleAsync(row => row.Id == "approval")).Status);
        Assert.Empty(await readback.RequestTaskApprovals.ToListAsync());
        Assert.Equal(TicketState.OnHold, (await readback.Requests.SingleAsync()).State);
        Assert.Empty(notifications.ReceivedCalls());
    }

    private sealed class HeldApprovalLookup : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("RequestTaskApprovals", StringComparison.Ordinal) && Entered.TrySetResult())
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Approval_starts_from_detached_workflow_snapshot_after_request_or_predecessor_commit(bool postgres, bool predecessor)
    {
        await using var database = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using var db = database.Open();
        await db.Database.MigrateAsync();
        var templateId = Guid.NewGuid();
        db.RequestForms.Add(new RequestForm { Id = "form", JsonSchema = JsonDocument.Parse($$"""
            {"fields":[],"tasks":[{"id":"{{templateId}}","name":"Review","type":"approval","order":1,
            "approvalApprovers":[{"source":"Customer","id":"customer","name":"Approver","email":"approver@example.test"}]}]}
            """) });
        var request = new Request { Id = "request", TrackingId = "REQ-STARTUP", RequestFormId = "form", Title = "Approval regression" };
        var task = new RequestTask { Id = "approval", RequestId = request.Id, TemplateId = templateId.ToString(), Type = RequestTaskType.Approval, Title = "Review" };
        db.Requests.Add(request);
        db.RequestTasks.Add(task);
        if (predecessor) db.RequestTasks.Add(new RequestTask { Id = "manual", RequestId = request.Id, Title = "Manual predecessor", Status = RequestTaskStatus.Completed });
        await db.SaveChangesAsync();
        // WorkflowEngine reads task snapshots through IRepository.GetAllAsync;
        // the creating/completing HTTP scope still tracks the committed graph.
        var snapshot = await db.RequestTasks.AsNoTracking().SingleAsync(x => x.Id == task.Id);
        var notifications = Substitute.For<ITicketNotificationService>();
        var service = new RequestTaskApprovalService(db, new RequestFormSchemaParser(), notifications,
            Substitute.For<IPublicTicketLinkSigner>(), NullLogger<RequestTaskApprovalService>.Instance);
        await service.StartApprovalAsync(snapshot, CancellationToken.None);
        await service.StartApprovalAsync(snapshot, CancellationToken.None);
        await using var readback = database.Open();
        Assert.Equal(RequestTaskStatus.PendingApproval, (await readback.RequestTasks.SingleAsync(x => x.Id == "approval")).Status);
        Assert.Equal("PendingApproval", (await readback.Requests.SingleAsync()).WorkflowStatus);
        Assert.Single(await readback.RequestTaskApprovals.ToListAsync());
        Assert.Equal(predecessor ? 2 : 1, await readback.RequestTasks.CountAsync());
        Assert.Single(await readback.Requests.ToListAsync());
    }
}
