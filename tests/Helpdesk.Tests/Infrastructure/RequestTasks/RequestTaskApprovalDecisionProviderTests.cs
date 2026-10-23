using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.RequestTasks;
using Helpdesk.Infrastructure.Security;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Tests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.RequestTasks;

public sealed class RequestTaskApprovalDecisionProviderTests
{
    private const string TaskId = "approval-task";
    private const string RequestId = "approval-request";
    private const string TrackingId = "REQ-APPROVAL-RACE";
    private const string OwnerId = "designated-owner";
    private const string OwnerEmail = "approver@example.test";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Signed_and_authenticated_decisions_share_one_terminal_outcome_and_committed_effects(bool postgres, bool authenticatedWins)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var commit = new HeldCommit();
        var competingStart = new ObservedTransactionStart();
        var competingRead = new ObservedApprovalRead();
        await using var firstDb = fixture.Database.Open(commit);
        await using var secondDb = fixture.Database.Open(competingStart, competingRead);
        var first = fixture.Service(firstDb);
        var second = fixture.Service(secondDb);
        var winner = Task.Run(() => fixture.ReviewAsync(first, authenticatedWins, true, deadline.Token), deadline.Token);
        Task<RequestTaskApprovalReviewResult> loser;
        try
        {
            await commit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), deadline.Token);
            // Uncommitted business state, actor attribution and delivery effects must
            // remain invisible to the dispatcher and an independent connection.
            await fixture.AssertStoredAsync(RequestTaskApprovalStatus.Pending, 0, 0, 0);
            Assert.Empty(fixture.TimelineBus.ReceivedCalls());
            loser = Task.Run(() => fixture.ReviewAsync(second, !authenticatedWins, false, deadline.Token), deadline.Token);
            await (postgres ? competingRead.Entered.Task : competingStart.Entered.Task)
                .WaitAsync(TimeSpan.FromSeconds(20), deadline.Token);
        }
        finally
        {
            commit.Release.TrySetResult();
        }
        Assert.True((await winner).Success);
        var losingDecision = await loser;
        Assert.False(losingDecision.Success);
        Assert.Contains(losingDecision.ErrorCode, new[] { "approval_changed", "approval_not_pending" });
        await fixture.AssertStoredAsync(RequestTaskApprovalStatus.Approved, 1, authenticatedWins ? 1 : 0, 0);

        // The same approver can retry either transport after a lost response. Neither
        // transport can reverse the decision or recreate notification/audit effects.
        await using var replayDb = fixture.Database.Open();
        var replay = fixture.Service(replayDb);
        Assert.True((await fixture.ReviewAsync(replay, false, true, deadline.Token)).Success);
        Assert.True((await fixture.ReviewAsync(replay, true, true, deadline.Token)).Success);
        Assert.False((await fixture.ReviewAsync(replay, true, false, deadline.Token)).Success);
        await fixture.AssertStoredAsync(RequestTaskApprovalStatus.Approved, 1, authenticatedWins ? 1 : 0, 0);
        Assert.Empty(fixture.TimelineBus.ReceivedCalls());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Failed_commit_rolls_back_decision_actor_timeline_and_queued_delivery_before_retry(bool postgres, bool approve)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        var failure = new FailedCommit();
        await using (var failingDb = fixture.Database.Open(failure))
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.ReviewAsync(fixture.Service(failingDb), true, approve, CancellationToken.None));
        }
        Assert.Equal(1, failure.Attempts);
        await fixture.AssertStoredAsync(RequestTaskApprovalStatus.Pending, 0, 0, 0);
        Assert.Empty(fixture.TimelineBus.ReceivedCalls());

        await using var retryDb = fixture.Database.Open();
        var retry = fixture.Service(retryDb);
        Assert.True((await fixture.ReviewAsync(retry, true, approve, CancellationToken.None)).Success);
        Assert.True((await fixture.ReviewAsync(retry, true, approve, CancellationToken.None)).Success);
        Assert.False((await fixture.ReviewAsync(retry, false, !approve, CancellationToken.None)).Success);
        await fixture.AssertStoredAsync(approve ? RequestTaskApprovalStatus.Approved : RequestTaskApprovalStatus.Rejected, 1, 1, approve ? 0 : 1);
        Assert.Empty(fixture.TimelineBus.ReceivedCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_rechecks_selected_candidates_after_a_concurrent_approved_decision(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres, overdue: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var start = new HeldTransactionStart();
        await using var timeoutDb = fixture.Database.Open(start);
        var sweep = fixture.Service(timeoutDb).ExpireOverdueApprovalsAsync(deadline.Token);
        try
        {
            await start.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), deadline.Token);
            await using var reviewDb = fixture.Database.Open();
            var task = await reviewDb.RequestTasks.SingleAsync(deadline.Token);
            task.DueAt = DateTimeOffset.UtcNow.AddHours(1);
            await reviewDb.SaveChangesAsync(deadline.Token);
            Assert.True((await fixture.ReviewAsync(fixture.Service(reviewDb), false, true, deadline.Token)).Success);
        }
        finally
        {
            start.Release.TrySetResult();
        }
        Assert.Equal(0, await sweep);
        await fixture.AssertStoredAsync(RequestTaskApprovalStatus.Approved, 1, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_approvals_reject_both_transports_and_cannot_reopen_after_timeout(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres, overdue: true);
        await using var db = fixture.Database.Open();
        var service = fixture.Service(db);
        Assert.False((await fixture.ReviewAsync(service, false, true, CancellationToken.None)).Success);
        Assert.False((await fixture.ReviewAsync(service, true, false, CancellationToken.None)).Success);
        await fixture.AssertStoredAsync(RequestTaskApprovalStatus.Pending, 0, 0, 0);
        Assert.Equal(1, await service.ExpireOverdueApprovalsAsync(CancellationToken.None));
        Assert.Equal(0, await service.ExpireOverdueApprovalsAsync(CancellationToken.None));
        Assert.False((await fixture.ReviewAsync(service, false, true, CancellationToken.None)).Success);
        Assert.False((await fixture.ReviewAsync(service, true, false, CancellationToken.None)).Success);
        await fixture.AssertStoredAsync(RequestTaskApprovalStatus.Expired, 0, 0, 0);
    }

    private sealed class HeldCommit : DbTransactionInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }

    private sealed class FailedCommit : DbTransactionInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new IOException("Synthetic failure before the database commit.");
        }
    }

    private sealed class ObservedTransactionStart : DbTransactionInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ObservedApprovalRead : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"RequestTaskApprovals\"", StringComparison.Ordinal)) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class HeldTransactionStart : DbTransactionInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }

    private sealed class Fixture(ServiceIdentityMigrationTests.DatabaseFixture database) : IAsyncDisposable
    {
        public ServiceIdentityMigrationTests.DatabaseFixture Database { get; } = database;
        public ITimelineEventBus TimelineBus { get; } = Substitute.For<ITimelineEventBus>();
        private readonly PublicTicketLinkSigner signer = new(Options.Create(new StorageOptions
        {
            ImageSigningSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
        }), NullLogger<PublicTicketLinkSigner>.Instance);
        private static readonly CurrentUserAccessProfile Access = new(true, "Designated approver", OwnerEmail, "org-approval", "Synthetic organization",
            null, true, new HashSet<string>(), new HashSet<string> { HelpdeskPermissions.HelpdeskAdmin },
            new HashSet<string> { "org-approval" }, new HashSet<string> { "org-approval" });

        public RequestTaskApprovalService Service(HelpdeskDbContext db)
        {
            var notifications = Substitute.For<ITicketNotificationService>();
            // Production notifications queue through the same scoped domain context.
            // This deterministic delivery adapter exercises that transactional contract
            // without submitting mail or depending on an external SMTP service.
            notifications.SendRequestApprovalDeclinedAsync(Arg.Any<Request>(), Arg.Any<RequestTask>(), Arg.Any<RequestTaskApproval>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    db.Set<MailboxOutboxEffect>().Add(new MailboxOutboxEffect
                    {
                        EffectKey = $"approval-declined:{Guid.NewGuid():N}", DispatchGroup = "approval-declined:fixture",
                        Kind = MailboxEffectKind.Email, Payload = "{\"recipient\":\"requester@example.test\"}"
                    });
                    await db.SaveChangesAsync(call.Arg<CancellationToken>());
                    return true;
                });
            return new RequestTaskApprovalService(db, new RequestFormSchemaParser(), notifications, signer, TimelineBus,
                NullLogger<RequestTaskApprovalService>.Instance);
        }

        public Task<RequestTaskApprovalReviewResult> ReviewAsync(RequestTaskApprovalService service, bool authenticated, bool approve, CancellationToken ct)
        {
            if (authenticated) return service.ReviewForActorAsync(TaskId, OwnerId, Access, approve, "Synthetic rejection reason.", ct);
            var token = signer.GenerateToken($"{TrackingId}:{TaskId}", OwnerEmail, DateTimeOffset.UtcNow.AddHours(1));
            return approve ? service.ApproveAsync(TrackingId, TaskId, OwnerEmail, token, ct)
                : service.RejectAsync(TrackingId, TaskId, OwnerEmail, token, "Synthetic rejection reason.", ct);
        }

        public static async Task<Fixture> CreateAsync(bool postgres, bool overdue = false)
        {
            var fixture = new Fixture(await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres));
            await using var db = fixture.Database.Open();
            await db.Database.MigrateAsync();
            db.Organizations.Add(new Organization { Id = "org-approval", Name = "Synthetic organization" });
            db.Requests.Add(new Request
            {
                Id = RequestId, TrackingId = TrackingId, Title = "Approval concurrency fixture", OrganizationId = "org-approval",
                State = TicketState.PendingApproval, WorkflowStatus = "PendingApproval", RequesterEmail = "requester@example.test"
            });
            db.RequestTasks.Add(new RequestTask
            {
                Id = TaskId, RequestId = RequestId, TrackingId = "TASK-APPROVAL-RACE", Title = "Approval decision", OrganizationId = "org-approval",
                Type = RequestTaskType.Approval, Status = RequestTaskStatus.PendingApproval,
                DueAt = DateTimeOffset.UtcNow.AddHours(overdue ? -1 : 1)
            });
            db.RequestTaskApprovals.AddRange(new RequestTaskApproval
            {
                Id = "designated-approval", RequestId = RequestId, RequestTaskId = TaskId, OrganizationId = "org-approval",
                ApproverSource = "User", ApproverId = OwnerId, ApproverName = "Designated approver", ApproverEmail = OwnerEmail
            }, new RequestTaskApproval
            {
                Id = "alternate-approval", RequestId = RequestId, RequestTaskId = TaskId, OrganizationId = "org-approval",
                ApproverSource = "User", ApproverId = "alternate-owner", ApproverName = "Alternate approver", ApproverEmail = "alternate@example.test"
            });
            await db.SaveChangesAsync();
            return fixture;
        }

        public async Task AssertStoredAsync(RequestTaskApprovalStatus status, int timelineCount, int auditCount, int emailCount)
        {
            await using var db = Database.Open();
            Assert.Equal(status, (await db.RequestTaskApprovals.SingleAsync(approval => approval.Id == "designated-approval")).Status);
            var alternate = status == RequestTaskApprovalStatus.Pending ? status
                : status == RequestTaskApprovalStatus.Expired ? status : RequestTaskApprovalStatus.Superseded;
            Assert.Equal(alternate, (await db.RequestTaskApprovals.SingleAsync(approval => approval.Id == "alternate-approval")).Status);
            var taskStatus = status == RequestTaskApprovalStatus.Pending ? RequestTaskStatus.PendingApproval
                : status == RequestTaskApprovalStatus.Approved ? RequestTaskStatus.Completed : RequestTaskStatus.Cancelled;
            Assert.Equal(taskStatus, (await db.RequestTasks.SingleAsync()).Status);
            var workflowStatus = status == RequestTaskApprovalStatus.Pending ? "PendingApproval"
                : status == RequestTaskApprovalStatus.Approved ? null : "Cancelled";
            Assert.Equal(workflowStatus, (await db.Requests.SingleAsync()).WorkflowStatus);
            var audit = await db.ActivityLogs.ToListAsync();
            Assert.Equal(auditCount, audit.Count);
            Assert.All(audit, item => { Assert.Equal(OwnerId, item.UserId); Assert.Equal(TaskId, item.RelatedEntityId); });
            var events = await db.TicketTimelineEvents.ToListAsync();
            Assert.Equal(timelineCount, events.Count);
            var outbox = await db.Set<MailboxOutboxEffect>().ToListAsync();
            Assert.Equal(emailCount, outbox.Count(effect => effect.Kind == MailboxEffectKind.Email));
            var publications = outbox.Where(effect => effect.Kind == MailboxEffectKind.Timeline).ToArray();
            Assert.Equal(timelineCount, publications.Length);
            foreach (var publication in publications)
            {
                Assert.Equal(MailboxEffectState.Pending, publication.State);
                var payload = JsonSerializer.Deserialize<TicketTimelineEventDto>(publication.Payload);
                Assert.NotNull(payload);
                Assert.Equal(RequestId, payload.TicketId);
                Assert.Equal(OwnerEmail, payload.PrimaryRecipient);
                Assert.Contains(events, item => item.Id == payload.Id && item.MessageText == payload.MessageText);
            }
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
