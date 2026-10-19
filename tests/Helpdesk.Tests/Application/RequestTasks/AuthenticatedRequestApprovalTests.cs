using Helpdesk.Application.Services.Notifications;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Helpdesk.Tests.Application.RequestTasks;

public sealed partial class RequestTaskApprovalServiceTests
{
    [Fact]
    public async Task Authenticated_designated_owner_approves_and_repeating_the_same_decision_is_idempotent()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, Substitute.For<ITicketNotificationService>());
        var (_, task) = await SeedPendingApprovalAsync(db, RequestTaskStatus.PendingApproval);
        var approval = await db.RequestTaskApprovals.SingleAsync();
        approval.ApproverSource = "User";
        approval.ApproverId = "owner-1";
        task.DueAt = DateTimeOffset.UtcNow.AddHours(1);
        await db.SaveChangesAsync();
        var access = ApprovalAccess(admin: true);

        var preview = await service.GetForActorAsync(task.Id, "owner-1", access, CancellationToken.None);
        Assert.NotNull(preview);
        Assert.True(preview.CanReview);
        Assert.Empty(await db.TicketTimelineEvents.ToListAsync());
        var accepted = await service.ReviewForActorAsync(task.Id, "owner-1", access, true, null, CancellationToken.None);
        var repeated = await service.ReviewForActorAsync(task.Id, "owner-1", access, true, null, CancellationToken.None);

        Assert.True(accepted.Success);
        Assert.True(repeated.Success);
        db.ChangeTracker.Clear();
        Assert.Equal(RequestTaskApprovalStatus.Approved, (await db.RequestTaskApprovals.SingleAsync()).Status);
        Assert.Equal(RequestTaskStatus.Completed, (await db.RequestTasks.SingleAsync()).Status);
        Assert.Single(await db.TicketTimelineEvents.ToListAsync());
    }

    [Theory]
    [InlineData("different-owner", "approver@example.com", "tenant-1", true, "Request.Manager")]
    [InlineData("owner-1", "different@example.com", "tenant-1", true, "Request.Manager")]
    [InlineData("owner-1", "approver@example.com", "other-tenant", false, "Request.Manager")]
    [InlineData("owner-1", "approver@example.com", "tenant-1", false, "Request.Read")]
    public async Task Authenticated_approval_does_not_allow_owner_impersonation_email_spoofing_foreign_scope_or_read_only_grants(
        string actorId, string email, string organization, bool admin, string permission)
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, Substitute.For<ITicketNotificationService>());
        var (_, task) = await SeedPendingApprovalAsync(db, RequestTaskStatus.PendingApproval);
        var approval = await db.RequestTaskApprovals.SingleAsync();
        approval.ApproverSource = "User";
        approval.ApproverId = "owner-1";
        await db.SaveChangesAsync();
        var access = ApprovalAccess(admin, email, organization, permission);

        Assert.Null(await service.GetForActorAsync(task.Id, actorId, access, CancellationToken.None));
        var result = await service.ReviewForActorAsync(task.Id, actorId, access, true, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(RequestTaskApprovalStatus.Pending, (await db.RequestTaskApprovals.SingleAsync()).Status);
        Assert.Equal(RequestTaskStatus.PendingApproval, (await db.RequestTasks.SingleAsync()).Status);
        Assert.Empty(await db.TicketTimelineEvents.ToListAsync());
    }

    [Fact]
    public async Task Authenticated_customer_approval_requires_the_actual_linked_customer_and_unexpired_deadline()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, Substitute.For<ITicketNotificationService>());
        var (_, task) = await SeedPendingApprovalAsync(db, RequestTaskStatus.PendingApproval);
        var access = ApprovalAccess(false, permission: HelpdeskPermissions.SelfServiceUser) with { CustomerId = "cust-1" };
        Assert.NotNull(await service.GetForActorAsync(task.Id, "local-customer-account", access, CancellationToken.None));
        Assert.Null(await service.GetForActorAsync(task.Id, "local-customer-account", access with { CustomerId = "different-customer" }, CancellationToken.None));
        task.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var before = await service.GetForActorAsync(task.Id, "local-customer-account", access, CancellationToken.None);
        Assert.False(before!.CanReview);
        var result = await service.ReviewForActorAsync(task.Id, "local-customer-account", access, true, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(RequestTaskApprovalStatus.Pending, (await db.RequestTaskApprovals.SingleAsync()).Status);
        Assert.Empty(await db.TicketTimelineEvents.ToListAsync());
    }

    private static CurrentUserAccessProfile ApprovalAccess(bool admin, string email = "approver@example.com", string organization = "tenant-1", string permission = HelpdeskPermissions.RequestManager)
        => new(true, "Designated operator", email, organization, "Synthetic tenant", null, admin,
            new HashSet<string>(), new HashSet<string> { permission }, new HashSet<string> { organization }, new HashSet<string> { organization });
}
