using System.Text.Json;
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
