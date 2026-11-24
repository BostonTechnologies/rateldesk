using System.Net;
using System.Net.Http.Json;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence.Entities;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

public sealed partial class ChangeStateAndAuthorityHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_and_failed_follow_the_matrix_keep_the_first_audit_and_leave_active(bool postgres)
    {
        await using var h = await CreateAsync(postgres);
        await SeedChangesAsync(h);
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            var credential = await identity.IntegrationCredentials.SingleAsync(item => item.Id == h.CredentialId);
            credential.Permissions += " Change.Read";
            await identity.SaveChangesAsync();
        }
        using var readerOptions = await BoundedAsync(h, HttpMethod.Get, "/api/v1/changes/draft/lifecycle-options");
        Assert.Empty((await readerOptions.Content.ReadFromJsonAsync<ChangeLifecycleOptionsDto>())!.Choices);
        using var readerCancel = await BoundedAsync(h, HttpMethod.Post, "/api/v1/changes/draft/lifecycle",
            new ChangeLifecycleRequest(ChangeLifecycleState.Cancelled));
        Assert.Equal(HttpStatusCode.Forbidden, readerCancel.StatusCode);
        await GrantChangeWriterAsync(h);
        await h.EditAsync(async db =>
        {
            var approved = await db.Changes.SingleAsync(change => change.Id == "approved");
            db.Changes.AddRange(
                new Change { Id = "legacy", TrackingId = "CHG-LEGACY", OrganizationId = h.OrganizationId, LifecycleState = null },
                new Change { Id = "unscoped", TrackingId = "CHG-UNSCOPED", LifecycleState = null },
                new Change { Id = "submitted", TrackingId = "CHG-SUBMITTED", OrganizationId = h.OrganizationId, LifecycleState = ChangeLifecycleState.Submitted },
                new Change { Id = "started", TrackingId = "CHG-STARTED", OrganizationId = h.OrganizationId,
                    LifecycleState = ChangeLifecycleState.ImplementationInProgress, ChangeType = approved.ChangeType,
                    ChangeTemplateJson = approved.ChangeTemplateJson },
                new Change { Id = "approved-cancel", TrackingId = "CHG-STOP", OrganizationId = h.OrganizationId,
                    LifecycleState = ChangeLifecycleState.ApprovedForImplementation });
        });

        var before = await SnapshotAsync(h);
        foreach (var (id, request) in new[]
        {
            ("pending", new ChangeLifecycleRequest(ChangeLifecycleState.Cancelled, "  ")),
            ("submitted", new ChangeLifecycleRequest(ChangeLifecycleState.Cancelled)),
            ("incomplete", new ChangeLifecycleRequest(ChangeLifecycleState.Cancelled)),
            ("approved", new ChangeLifecycleRequest(ChangeLifecycleState.ImplementedFailed)),
            ("incomplete", new ChangeLifecycleRequest(ChangeLifecycleState.ImplementedFailed, "Incomplete template")),
            ("legacy", new ChangeLifecycleRequest(ChangeLifecycleState.ImplementedFailed, "Wrong predecessor")),
            ("pending", new ChangeLifecycleRequest(ChangeLifecycleState.ImplementedFailed, "Wrong predecessor")),
            ("submitted", new ChangeLifecycleRequest(ChangeLifecycleState.ImplementedFailed, "Wrong predecessor")),
            ("legacy", new ChangeLifecycleRequest(ChangeLifecycleState.Cancelled, new string('x', 1001))),
            ("legacy", new ChangeLifecycleRequest(ChangeLifecycleState.Cancelled, new string(' ', 1001))),
            ("implemented", new ChangeLifecycleRequest(ChangeLifecycleState.Cancelled, "Terminal")),
        })
        {
            using var invalid = await BoundedAsync(h, HttpMethod.Post, $"/api/v1/changes/{id}/lifecycle", request);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var foreign = await BoundedAsync(h, HttpMethod.Post, "/api/v1/changes/foreign/lifecycle",
            new ChangeLifecycleRequest(ChangeLifecycleState.Cancelled));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(before, await SnapshotAsync(h));

        using var detail = await BoundedAsync(h, HttpMethod.Put, "/api/v1/changes/legacy",
            new UpdateChangeDto { LifecycleState = ChangeLifecycleState.Cancelled, ExpectedLifecycleState = ChangeLifecycleState.Draft });
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var unscoped = await h.AdminAsync(HttpMethod.Post, "/api/v1/changes/unscoped/lifecycle",
            new ChangeLifecycleRequest(ChangeLifecycleState.Cancelled, ExpectedLifecycleState: ChangeLifecycleState.Draft));
        Assert.Equal(HttpStatusCode.OK, unscoped.StatusCode);
        foreach (var id in new[] { "pending", "submitted", "incomplete", "approved-cancel" })
        {
            using var cancelled = await BoundedAsync(h, HttpMethod.Post, $"/api/v1/changes/{id}/lifecycle",
                new ChangeLifecycleRequest(ChangeLifecycleState.Cancelled, "  Work discontinued  "));
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        }
        foreach (var id in new[] { "approved", "started" })
        {
            using var failed = await BoundedAsync(h, HttpMethod.Post, $"/api/v1/changes/{id}/lifecycle",
                new ChangeLifecycleRequest(ChangeLifecycleState.ImplementedFailed, "  Verification failed  "));
            Assert.True(failed.IsSuccessStatusCode, await failed.Content.ReadAsStringAsync());
            Assert.Equal(ChangeLifecycleState.ImplementedFailed, (await failed.Content.ReadFromJsonAsync<ChangeDto>())!.LifecycleState);
        }

        var committed = await SnapshotAsync(h);
        using var repeat = await BoundedAsync(h, HttpMethod.Post, "/api/v1/changes/approved/lifecycle",
            new ChangeLifecycleRequest(ChangeLifecycleState.ImplementedFailed, "Replacement", ChangeLifecycleState.ApprovedForImplementation));
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        var replacement = await repeat.Content.ReadFromJsonAsync<ChangeDto>();
        Assert.False(replacement!.LifecycleChanged);
        Assert.Equal("Verification failed", replacement.LifecycleExplanation);
        using var stale = await BoundedAsync(h, HttpMethod.Put, "/api/v1/changes/legacy",
            new UpdateChangeDto { LifecycleState = ChangeLifecycleState.Draft, ExpectedLifecycleState = ChangeLifecycleState.Draft });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var reopen = await h.AdminAsync(HttpMethod.Post, "/api/v1/changes/bulk/state",
            new { ids = new[] { "legacy", "approved" }, newState = TicketState.New });
        Assert.Equal(HttpStatusCode.BadRequest, reopen.StatusCode);
        Assert.Equal(committed, await SnapshotAsync(h));

        await h.EditAsync(async db =>
        {
            var closed = await db.Changes.IgnoreQueryFilters().SingleAsync(change => change.Id == "approved");
            Assert.Equal(TicketState.Resolved, closed.State);
            Assert.NotNull(closed.ClosedAt);
            Assert.Equal(SlaStatus.Completed, (await db.TicketSlaStates.SingleAsync(state => state.TicketId == closed.Id)).Status);
            Assert.Equal(ChangeApprovalStatus.Pending, (await db.ChangeApprovals.SingleAsync(approval => approval.ChangeId == "pending")).Status);
            var timeline = await db.TicketTimelineEvents.Where(item => item.TicketId == "approved" && item.MessageText!.Contains("Verification failed")).ToListAsync();
            var audit = Assert.Single(timeline);
            Assert.Equal(closed.ClosedAt, audit.CreatedUtc);
            Assert.Contains("Approved for Implementation to Implemented - Failed", audit.MessageText);
            Assert.False(string.IsNullOrWhiteSpace(audit.CreatedByUserId));
            Assert.Equal(audit.CreatedByUserId, (await db.ActivityLogs.SingleAsync(log =>
                log.TicketId == closed.Id && log.Message.Contains("Verification failed"))).UserId);
            var cancellation = await db.Changes.IgnoreQueryFilters().SingleAsync(change => change.Id == "legacy");
            Assert.Null(cancellation.ImplementationStartAt);
            Assert.Null(cancellation.ImplementationEndAt);
            Assert.Equal(ChangeLifecycleState.Cancelled, cancellation.LifecycleState);
            Assert.Single(await db.Set<MailboxOutboxEffect>().Where(effect =>
                effect.DispatchGroup == "change-approval:legacy" && effect.Kind == MailboxEffectKind.Timeline).ToListAsync());
        });

        using var active = await BoundedAsync(h, HttpMethod.Get, "/api/v1/changes/?view=active");
        Assert.Equal("draft", Assert.Single((await active.Content.ReadFromJsonAsync<PagedResponse<ChangeDto>>())!.Items).Id);
        using var history = await BoundedAsync(h, HttpMethod.Get, "/api/v1/changes/?view=historic&stateValues=ImplementedFailed");
        Assert.Equal(2, (await history.Content.ReadFromJsonAsync<PagedResponse<ChangeDto>>())!.TotalCount);
        using var cancelledHistory = await BoundedAsync(h, HttpMethod.Get, "/api/v1/changes/?view=historic&stateValues=Cancelled");
        Assert.Equal(5, (await cancelledHistory.Content.ReadFromJsonAsync<PagedResponse<ChangeDto>>())!.TotalCount);
        using var all = await BoundedAsync(h, HttpMethod.Get, "/api/v1/changes/?view=all");
        Assert.Equal(9, (await all.Content.ReadFromJsonAsync<PagedResponse<ChangeDto>>())!.TotalCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bulk_cancellation_bounds_the_selection_and_reports_each_record_without_replaying(bool postgres)
    {
        await using var h = await CreateAsync(postgres);
        await SeedChangesAsync(h);
        await GrantChangeWriterAsync(h);
        var page = Enumerable.Range(1, 25).Select(i => new ChangeCancellationSelection($"cleanup-{i}", ChangeLifecycleState.Draft)).ToArray();
        await h.EditAsync(db =>
        {
            db.Changes.AddRange(page.Select(item => new Change
            {
                Id = item.Id, TrackingId = item.Id, Title = "Synthetic abandoned draft",
                OrganizationId = h.OrganizationId, LifecycleState = null, State = TicketState.InProgress
            }));
            return Task.CompletedTask;
        });
        using var denied = await BoundedAsync(h, HttpMethod.Post, "/api/v1/changes/bulk/cancel",
            new BulkChangeCancellationRequest(page));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var withoutReason = await h.AdminAsync(HttpMethod.Post, "/api/v1/changes/bulk/cancel",
            new BulkChangeCancellationRequest([page[0], new("pending", ChangeLifecycleState.PendingApproval)]));
        Assert.Equal(HttpStatusCode.BadRequest, withoutReason.StatusCode);

        using var initial = await h.AdminAsync(HttpMethod.Post, "/api/v1/changes/bulk/cancel",
            new BulkChangeCancellationRequest([.. page, page[0], new("pending", ChangeLifecycleState.Draft)]));
        Assert.True(initial.IsSuccessStatusCode, await initial.Content.ReadAsStringAsync());
        var outcomes = (await initial.Content.ReadFromJsonAsync<List<BulkChangeCancellationResult>>())!;
        Assert.Equal(26, outcomes.Count);
        Assert.All(outcomes.Take(25), result => Assert.Equal("cancelled", result.Outcome));
        Assert.Equal(new BulkChangeCancellationResult("pending", "conflict"), outcomes[^1]);

        using var mixed = await h.AdminAsync(HttpMethod.Post, "/api/v1/changes/bulk/cancel", new BulkChangeCancellationRequest(
            [page[0], new("implemented", ChangeLifecycleState.ImplementedSuccess), new("pending", ChangeLifecycleState.Draft),
                new("missing", ChangeLifecycleState.Draft), new("approved", ChangeLifecycleState.ApprovedForImplementation)], "Synthetic cleanup"));
        Assert.Equal(HttpStatusCode.OK, mixed.StatusCode);
        var results = (await mixed.Content.ReadFromJsonAsync<List<BulkChangeCancellationResult>>())!;
        Assert.Equal(["already-cancelled", "ineligible", "conflict", "not-found", "cancelled"], results.Select(item => item.Outcome));
        await h.EditAsync(async db =>
        {
            Assert.Equal(ChangeLifecycleState.Draft, (await db.Changes.IgnoreQueryFilters().SingleAsync(change => change.Id == "draft")).LifecycleState);
            Assert.Equal(ChangeLifecycleState.PendingApproval, (await db.Changes.IgnoreQueryFilters().SingleAsync(change => change.Id == "pending")).LifecycleState);
            Assert.Equal(25, await db.TicketTimelineEvents.CountAsync(item => item.TicketId.StartsWith("cleanup-")));
        });
    }
}
