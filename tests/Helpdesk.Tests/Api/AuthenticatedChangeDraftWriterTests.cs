using System.Net;
using System.Net.Http.Json;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Changes;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed partial class AuthenticatedChangeApprovalTests
{
    public enum DraftWriter { Assignee, BulkAssign, State, BulkState }

    [Theory]
    [InlineData(false, DraftWriter.Assignee, true)]
    [InlineData(false, DraftWriter.BulkAssign, true)]
    [InlineData(false, DraftWriter.State, true)]
    [InlineData(false, DraftWriter.BulkState, true)]
    [InlineData(true, DraftWriter.Assignee, true)]
    [InlineData(true, DraftWriter.BulkAssign, true)]
    [InlineData(true, DraftWriter.State, true)]
    [InlineData(true, DraftWriter.BulkState, true)]
    [InlineData(false, DraftWriter.Assignee, false)]
    [InlineData(false, DraftWriter.BulkAssign, false)]
    [InlineData(false, DraftWriter.State, false)]
    [InlineData(false, DraftWriter.BulkState, false)]
    [InlineData(true, DraftWriter.Assignee, false)]
    [InlineData(true, DraftWriter.BulkAssign, false)]
    [InlineData(true, DraftWriter.State, false)]
    [InlineData(true, DraftWriter.BulkState, false)]
    public async Task Draft_side_writers_and_submission_preserve_the_committed_winner_and_notification_intent(
        bool postgres, DraftWriter writer, bool submissionWins)
    {
        await using var fixture = await Fixture.StartAsync(postgres);
        await PrepareDraftWriterChangesAsync(fixture);
        using var submitter = fixture.Client("owner-a");
        using var editor = fixture.Client("owner-a");
        fixture.Commits.HoldNext = true;
        var first = Task.Run(() => submissionWins ? SubmitAsync(submitter) : WriteDraftAsync(editor, writer));
        Task<HttpResponseMessage> competing;
        try
        {
            await fixture.Commits.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            // A separate connection sees neither the held write nor its queued effects.
            await AssertDraftWriterStateAsync(fixture, writer, changed: false, submitted: false);
            Assert.Empty(fixture.Timeline.ReceivedCalls());
            Assert.Empty(fixture.DomainEvents.ReceivedCalls());
            fixture.Commits.ObserveCompetitor = true;
            competing = Task.Run(() => submissionWins ? WriteDraftAsync(editor, writer) : SubmitAsync(submitter));
            await (postgres ? fixture.Commits.CompetingRead.Task : fixture.Commits.CompetingStart.Task)
                .WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            fixture.Commits.Release.TrySetResult();
        }

        using var firstResponse = await first;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        using var competingResponse = await competing;
        if (submissionWins)
        {
            Assert.Contains(competingResponse.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict });
            await AssertDraftWriterStateAsync(fixture, writer, changed: false, submitted: true);
            Assert.Single(fixture.DomainEvents.ReceivedCalls());
        }
        else
        {
            Assert.Contains(competingResponse.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            if (competingResponse.StatusCode == HttpStatusCode.Conflict)
            {
                // The competing submission made no partial approval/mail writes.
                await AssertDraftWriterStateAsync(fixture, writer, changed: true, submitted: false);
                using var retry = await SubmitAsync(submitter);
                Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            }
            await AssertDraftWriterStateAsync(fixture, writer, changed: true, submitted: true);
            Assert.Equal(writer == DraftWriter.State ? 2 : 1, fixture.DomainEvents.ReceivedCalls().Count());
        }
        Assert.Empty(fixture.Timeline.ReceivedCalls());
    }

    [Theory]
    [InlineData(false, DraftWriter.Assignee)]
    [InlineData(false, DraftWriter.BulkAssign)]
    [InlineData(false, DraftWriter.State)]
    [InlineData(false, DraftWriter.BulkState)]
    [InlineData(true, DraftWriter.Assignee)]
    [InlineData(true, DraftWriter.BulkAssign)]
    [InlineData(true, DraftWriter.State)]
    [InlineData(true, DraftWriter.BulkState)]
    public async Task A_failed_draft_side_writer_commit_leaves_no_partial_batch_or_published_effect(
        bool postgres, DraftWriter writer)
    {
        await using var fixture = await Fixture.StartAsync(postgres);
        await PrepareDraftWriterChangesAsync(fixture);
        using var editor = fixture.Client("owner-a");
        fixture.Commits.Fail = true;
        using var failed = await WriteDraftAsync(editor, writer);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await AssertDraftWriterStateAsync(fixture, writer, changed: false, submitted: false);
        Assert.Empty(fixture.Timeline.ReceivedCalls());
        Assert.Empty(fixture.DomainEvents.ReceivedCalls());
        fixture.Commits.Fail = false;
        using var retry = await WriteDraftAsync(editor, writer);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await AssertDraftWriterStateAsync(fixture, writer, changed: true, submitted: false);
        Assert.Empty(fixture.Timeline.ReceivedCalls());
        Assert.Equal(writer == DraftWriter.State ? 1 : 0, fixture.DomainEvents.ReceivedCalls().Count());
    }

    [Theory]
    [InlineData(false, DraftWriter.BulkAssign)]
    [InlineData(false, DraftWriter.BulkState)]
    [InlineData(true, DraftWriter.BulkAssign)]
    [InlineData(true, DraftWriter.BulkState)]
    public async Task A_locked_bulk_target_rejects_the_whole_draft_side_write_in_either_id_order(
        bool postgres, DraftWriter writer)
    {
        await using var fixture = await Fixture.StartAsync(postgres);
        await PrepareDraftWriterChangesAsync(fixture);
        using var editor = fixture.Client("owner-a");
        using var submitted = await SubmitAsync(editor);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        foreach (var reverse in new[] { false, true })
        {
            using var rejected = await WriteDraftAsync(editor, writer, reverse);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            await AssertDraftWriterStateAsync(fixture, writer, changed: false, submitted: true);
        }
        Assert.Single(fixture.DomainEvents.ReceivedCalls());
        Assert.Empty(fixture.Timeline.ReceivedCalls());
    }

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client) => client.PostAsJsonAsync(
        "/api/v1/changes/change/lifecycle", new { LifecycleState = ChangeLifecycleState.Submitted });

    private static Task<HttpResponseMessage> WriteDraftAsync(HttpClient client, DraftWriter writer, bool reverse = false)
    {
        string[] ids = reverse ? ["a-companion", "change"] : ["change", "a-companion"];
        return writer switch
        {
            DraftWriter.Assignee => client.PostAsJsonAsync("/api/v1/changes/change/assignee", new { ImplementorUserId = "owner-b" }),
            DraftWriter.BulkAssign => client.PostAsJsonAsync("/api/v1/changes/bulk/assign", new { Ids = ids, AssignedToId = "owner-b" }),
            DraftWriter.State => client.PostAsJsonAsync("/api/v1/changes/change/state", new { NewState = TicketState.InProgress }),
            DraftWriter.BulkState => client.PostAsJsonAsync("/api/v1/changes/bulk/state", new { Ids = ids, NewState = TicketState.InProgress }),
            _ => throw new ArgumentOutOfRangeException(nameof(writer))
        };
    }

    private static Task PrepareDraftWriterChangesAsync(Fixture fixture) => fixture.WithDbAsync(async db =>
    {
        var change = await db.Changes.SingleAsync();
        change.LifecycleState = ChangeLifecycleState.Draft;
        change.State = TicketState.New;
        change.ImplementorUserId = change.AssignedToId = change.RequestedForUserId = "owner-a";
        change.ChangeType = "Standard";
        change.ChangeTemplateJson = new ChangeTemplateService().SerializeTemplate(new ChangeTemplateDto
        {
            IsPreApproved = true, ExistingRunbookReference = "Synthetic reviewed runbook", ScopeOfChange = "Synthetic review",
            AffectedSystems = ["Isolated fixture"], ImplementationSteps = ["Apply reviewed fixture"],
            ValidationSteps = ["Verify fixture"], RollbackReference = "Restore fixture"
        });
        foreach (var actor in await db.Users.ToListAsync()) actor.Role = "Technician";
        db.Changes.Add(new Change
        {
            Id = "a-companion", TrackingId = "CHG-COMPANION", OrganizationId = "org-a", Title = "Bulk companion",
            LifecycleState = ChangeLifecycleState.Draft, State = TicketState.New,
            RequestedForUserId = "owner-a", ImplementorUserId = "owner-a", AssignedToId = "owner-a",
            ApproverUserIds = ["owner-a", "owner-b"], ChangeType = change.ChangeType, ChangeTemplateJson = change.ChangeTemplateJson
        });
        await db.SaveChangesAsync();
    });

    private static Task AssertDraftWriterStateAsync(Fixture fixture, DraftWriter writer, bool changed, bool submitted) =>
        fixture.WithDbAsync(async db =>
        {
            var assignmentChanged = changed && writer is DraftWriter.Assignee or DraftWriter.BulkAssign;
            var stateChanged = changed && writer is DraftWriter.State or DraftWriter.BulkState;
            var changes = await db.Changes.AsNoTracking().ToListAsync();
            Assert.Equal(2, changes.Count);
            var change = Assert.Single(changes, item => item.Id == "change");
            Assert.Equal(submitted ? ChangeLifecycleState.PendingApproval : ChangeLifecycleState.Draft, change.LifecycleState);
            Assert.Equal(stateChanged ? TicketState.InProgress : TicketState.New, change.State);
            Assert.Equal(assignmentChanged ? "owner-b" : "owner-a", change.ImplementorUserId);
            Assert.Equal(change.ImplementorUserId, change.AssignedToId);
            Assert.Equal("owner-a", change.RequestedForUserId);
            Assert.Equal(new[] { "owner-a", "owner-b" }, change.ApproverUserIds.Order(StringComparer.Ordinal));
            var companion = Assert.Single(changes, item => item.Id == "a-companion");
            Assert.Equal(ChangeLifecycleState.Draft, companion.LifecycleState);
            Assert.Equal(changed && writer == DraftWriter.BulkState ? TicketState.InProgress : TicketState.New, companion.State);
            Assert.Equal(changed && writer == DraftWriter.BulkAssign ? "owner-b" : "owner-a", companion.ImplementorUserId);
            Assert.Equal(companion.ImplementorUserId, companion.AssignedToId);
            var approvals = await db.ChangeApprovals.AsNoTracking().ToArrayAsync();
            Assert.Equal(2, approvals.Length);
            Assert.All(approvals, approval =>
            {
                Assert.Equal("change", approval.ChangeId);
                Assert.Equal(ChangeApprovalStatus.Pending, approval.Status);
                Assert.Null(approval.ReviewedAtUtc);
                if (submitted) Assert.NotNull(approval.TokenSentAtUtc);
                else Assert.Null(approval.TokenSentAtUtc);
            });
            var emailEffects = await db.Set<MailboxOutboxEffect>().AsNoTracking()
                .Where(effect => effect.Kind == MailboxEffectKind.Email).ToArrayAsync();
            Assert.Equal(submitted ? assignmentChanged ? 4 : 3 : 0, emailEffects.Length);
            Assert.All(emailEffects, effect => Assert.Equal(MailboxEffectState.Pending, effect.State));
            var emails = emailEffects.Select(effect => MailboxOutboxStore.Deserialize<IngressEmailEffect>(effect.Payload)).ToArray();
            var submitRecipients = emails.Where(email => email.Subject.StartsWith("ChangeSubmitted", StringComparison.Ordinal))
                .SelectMany(email => email.Recipients).Order(StringComparer.Ordinal).ToArray();
            string[] expectedSubmitted = submitted
                ? assignmentChanged ? ["owner-a@example.test", "owner-b@example.test"] : ["owner-a@example.test"] : [];
            Assert.Equal(expectedSubmitted, submitRecipients);
            var approvalRecipients = emails.Where(email => email.Subject.StartsWith("ChangeApprovalRequired", StringComparison.Ordinal))
                .SelectMany(email => email.Recipients).Order(StringComparer.Ordinal).ToArray();
            string[] expectedApprovals = submitted ? ["owner-a@example.test", "owner-b@example.test"] : [];
            Assert.Equal(expectedApprovals, approvalRecipients);
            var timelines = (submitted ? 3 : 0) + (changed && writer == DraftWriter.State ? 1 : 0);
            Assert.Equal(timelines, await db.Set<MailboxOutboxEffect>().CountAsync(effect => effect.Kind == MailboxEffectKind.Timeline));
            Assert.Equal(timelines + emailEffects.Length, await db.TicketTimelineEvents.CountAsync());
            Assert.Equal(0, await db.ActivityLogs.CountAsync());
        });
}
