using System.Text.Json;
using Helpdesk.Application.Events;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Notifications;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.Workflow;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Html;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Services;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class WorklogMailDeliveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Manual_task_failure_keeps_parent_blocked_and_one_truthful_delivery_without_a_sender(
        bool postgres, bool transactional)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        foreach (var disabled in new[] { false, true })
        {
            await using var db = fixture.Open();
            if (disabled) await fixture.ConfigureSenderAsync(db, port: 465, enabled: false);
            var request = await fixture.SeedRequestAsync(db);
            var task = new RequestTask
            {
                RequestId = request.Id, OrganizationId = request.OrganizationId,
                TrackingId = $"TASK-{Guid.NewGuid():N}", Name = "Synthetic manual task",
                Type = RequestTaskType.Manual, Status = RequestTaskStatus.InProgress,
                FailurePolicy = "BlockRequest"
            };
            db.RequestTasks.Add(task);
            await db.SaveChangesAsync();
            var context = new IngressEffectContext();
            var handler = fixture.Handler(db, context);
            var requestSender = Substitute.For<IRequestSender>();
            requestSender.Send(Arg.Any<IRequest<WorkLog>>(), Arg.Any<CancellationToken>())
                .Returns(call => handler.Handle((CreateWorkLogCommand)call.Arg<IRequest<WorkLog>>(), call.Arg<CancellationToken>()));
            var events = Substitute.For<IDomainEventPublisher>();
            var correlation = Substitute.For<ICorrelationContext>();
            var policy = new FailurePolicyEngine(new EfRepository<Request>(db), new EfRepository<RequestTask>(db),
                events, correlation, NullLogger<FailurePolicyEngine>.Instance,
                new EfRepository<TicketTimelineEvent>(db), requestSender);
            var lifecycle = new RequestTaskLifecycleService(new EfRepository<RequestTask>(db), new EfRepository<Request>(db),
                Substitute.For<IOrchestrationConnectivityService>(), Substitute.For<IAutomationBindingService>(),
                Substitute.For<IRequestTaskPayloadBuilder>(), Substitute.For<IOrchestrationInternalClient>(),
                policy, events, correlation, NullLogger<RequestTaskLifecycleService>.Instance);

            if (transactional)
            {
                using var capture = context.Begin(Guid.NewGuid());
                context.OrganizationId = request.OrganizationId;
                await using var transaction = await db.Database.BeginTransactionAsync();
                await lifecycle.FailAsync(task.Id, "Synthetic failure", default);
                await new MailboxOutboxStore(db, context, TimeProvider.System).FlushAsync();
                await transaction.CommitAsync();
            }
            else
                await lifecycle.FailAsync(task.Id, "Synthetic failure", default);

            db.ChangeTracker.Clear();
            var worklog = await db.WorkLogs.SingleAsync(x => x.TicketId == request.Id);
            var (effect, delivery, email) = await ReadDeliveryAsync(db, request.Id);
            Assert.Equal(worklog.Id, delivery.WorkLogId);
            Assert.Equal(worklog.Id, email.WorkLogId);
            Assert.Equal(request.OrganizationId, email.OrganizationId);
            Assert.Equal(EmailDeliveryStatus.Pending, delivery.EmailStatus);
            Assert.Equal(MailboxEffectState.Pending, effect.State);
            Assert.Null(effect.RecipientOutcomeJson);
            Assert.Equal(0, effect.Attempts);
            Assert.Equal(RequestTaskStatus.Failed, (await db.RequestTasks.SingleAsync(x => x.Id == task.Id)).Status);
            var blocked = await db.Requests.SingleAsync(x => x.Id == request.Id);
            Assert.Equal("Blocked", blocked.WorkflowStatus);
            Assert.Equal(TicketState.OnHold, blocked.State);

            await fixture.DispatchAsync();

            db.ChangeTracker.Clear();
            var (held, history, payload) = await ReadDeliveryAsync(db, request.Id);
            Assert.Equal(effect.Id, held.Id);
            Assert.Equal(delivery.Id, history.Id);
            Assert.Equal(worklog.Id, history.WorkLogId);
            Assert.Equal(effect.Payload, held.Payload);
            Assert.Equal(email.WorkLogId, payload.WorkLogId);
            Assert.Equal(MailboxEffectState.NeedsReview, held.State);
            Assert.Equal(disabled ? "OutgoingDisabled" : "NoEffectiveMailbox", held.LastErrorCode);
            Assert.Equal(EmailDeliveryStatus.Failed, history.EmailStatus);
            Assert.Equal(held.LastErrorCode, history.RetryError);
            Assert.Null(held.RecipientOutcomeJson);
            Assert.DoesNotContain("successfully sent", history.MessageText);
            Assert.False((await db.Set<MailboxWorkerControl>().SingleAsync()).Running);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Captured_submission_updates_only_the_original_worklog_delivery_for_each_outcome(
        bool postgres, bool transactional)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        foreach (var outcome in new[] { "accepted", "failed", "partial", "uncertain" })
        {
            await using var server = new SmtpMailboxSenderTests.SmtpFixture(
                rejectRecipient: outcome == "partial" ? "copy@example.test" : null,
                authStatus: outcome == "failed" ? 535 : null,
                disconnectAfterData: outcome == "uncertain");
            await using var db = fixture.Open();
            await fixture.ConfigureSenderAsync(db, server.Port);
            var request = await fixture.SeedRequestAsync(db);
            var worklog = await fixture.AddWorklogAsync(db, request, transactional);
            db.ChangeTracker.Clear();
            var (queued, pending, email) = await ReadDeliveryAsync(db, request.Id);
            Assert.Equal(EmailDeliveryStatus.Pending, pending.EmailStatus);
            Assert.Equal(worklog.Id, email.WorkLogId);
            Assert.Empty(server.Commands);

            await fixture.DispatchAsync();
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            db.ChangeTracker.Clear();
            var (completed, history, dispatched) = await ReadDeliveryAsync(db, request.Id);
            Assert.Equal(queued.Id, completed.Id);
            Assert.Equal(pending.Id, history.Id);
            Assert.Equal(worklog.Id, history.WorkLogId);
            Assert.Equal(email.WorkLogId, dispatched.WorkLogId);
            Assert.Equal(queued.Payload, completed.Payload);
            Assert.Equal(1, completed.Attempts);
            Assert.Equal(request.OrganizationId, dispatched.OrganizationId);
            Assert.Equal(outcome == "accepted" ? EmailDeliveryStatus.Delivered : EmailDeliveryStatus.Failed, history.EmailStatus);
            Assert.Equal(outcome == "accepted" ? MailboxEffectState.Completed : MailboxEffectState.NeedsReview, completed.State);
            Assert.Equal(outcome switch
            {
                "failed" => "SmtpAuthenticationFailed",
                "partial" => "SmtpPartialRecipientAcceptance",
                "uncertain" => "SubmissionOutcomeUnknown",
                _ => null
            }, completed.LastErrorCode);
            if (outcome is "accepted" or "partial")
            {
                var recipients = JsonSerializer.Deserialize<MailboxRecipientOutcome>(completed.RecipientOutcomeJson!)!;
                Assert.Contains("requester@example.test", recipients.AcceptedRecipients);
                if (outcome == "partial") Assert.Contains("copy@example.test", recipients.RejectedRecipients);
            }
            else
                Assert.Null(completed.RecipientOutcomeJson);
            if (outcome != "failed")
            {
                Assert.Contains($"{completed.Id:N}@example.test", server.Message);
                Assert.Contains("Synthetic public update", server.Message);
            }
            Assert.False(await new MailboxOutboxStore(db, new IngressEffectContext(), TimeProvider.System)
                .TryBeginSubmissionAsync(completed, default));
            if (outcome is "accepted" or "partial" or "uncertain")
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new MailboxOutboxStore(db, new IngressEffectContext(), TimeProvider.System)
                        .RetryForTimelineAsync(history.Id, default));
            var commands = server.Commands.Count;
            await fixture.DispatchAsync();
            Assert.Equal(commands, server.Commands.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmed_sender_repair_retries_the_same_worklog_effect_and_captured_content(bool postgres)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        await using var rejected = new SmtpMailboxSenderTests.SmtpFixture(authStatus: 535);
        await using var db = fixture.Open();
        await fixture.ConfigureSenderAsync(db, rejected.Port);
        var request = await fixture.SeedRequestAsync(db);
        var worklog = await fixture.AddWorklogAsync(db, request, transactional: false);
        await fixture.DispatchAsync();
        await rejected.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        db.ChangeTracker.Clear();
        var (failed, history, original) = await ReadDeliveryAsync(db, request.Id);
        Assert.Equal(MailboxEffectState.NeedsReview, failed.State);
        Assert.Equal("SmtpAuthenticationFailed", failed.LastErrorCode);
        Assert.Empty(rejected.Message);

        await using var accepted = new SmtpMailboxSenderTests.SmtpFixture();
        await fixture.ConfigureSenderAsync(db, accepted.Port);
        var retry = new MailboxOutgoingRetryService(db, new MailboxSenderResolver(db), TimeProvider.System);
        var preview = await retry.PreviewAsync(history.Id, default);
        Assert.True(preview.CanRetry);
        var sender = await db.Set<MailboxOutgoingSettings>().AsNoTracking().SingleAsync();
        var mailbox = await db.EmailInboxSettings.AsNoTracking().SingleAsync();
        var result = await retry.RetryAsync(history.Id, new ConfirmMailboxOutgoingRetryRequest(sender.Version, true)
        {
            ExpectedMailboxId = mailbox.Id, ExpectedMailboxVersion = mailbox.Version, ExpectedFence = preview.Fence
        }, "synthetic-operator", default);
        Assert.Equal("Queued", result.Status);
        db.ChangeTracker.Clear();
        var (queued, pending, replay) = await ReadDeliveryAsync(db, request.Id);
        Assert.Equal(failed.Id, queued.Id);
        Assert.Equal(history.Id, pending.Id);
        Assert.Equal(EmailDeliveryStatus.Pending, pending.EmailStatus);
        Assert.Equal(worklog.Id, replay.WorkLogId);
        Assert.Equal(original.Html, replay.Html);
        Assert.Equal(original.Recipients, replay.Recipients);
        Assert.Equal(original.Cc, replay.Cc);
        Assert.Equal(original.OrganizationId, replay.OrganizationId);

        await fixture.DispatchAsync();
        await accepted.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        db.ChangeTracker.Clear();
        var (completed, delivered, _) = await ReadDeliveryAsync(db, request.Id);
        Assert.Equal(failed.Id, completed.Id);
        Assert.Equal(history.Id, delivered.Id);
        Assert.Equal(worklog.Id, delivered.WorkLogId);
        Assert.Null(completed.LastErrorCode);
        Assert.Equal(EmailDeliveryStatus.Delivered, delivered.EmailStatus);
        Assert.Equal(MailboxEffectState.Completed, completed.State);
        Assert.Contains($"{completed.Id:N}@example.test", accepted.Message);
        Assert.Contains("Synthetic public update", accepted.Message);
        Assert.Equal(1, accepted.Commands.Count(command => command == "DATA"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_template_repair_replays_the_exact_public_worklog_instead_of_delivery_status_text(bool postgres)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        await using var db = fixture.Open();
        await db.EmailTemplates.ExecuteDeleteAsync();
        var request = await fixture.SeedRequestAsync(db);
        var worklog = await fixture.AddWorklogAsync(db, request, transactional: false);
        db.ChangeTracker.Clear();
        var history = await db.TicketTimelineEvents.SingleAsync(x => x.EventType == TimelineEventType.EmailDelivery);
        Assert.Equal(EmailDeliveryStatus.Failed, history.EmailStatus);
        Assert.Equal(worklog.Id, history.WorkLogId);
        Assert.Contains("email template was not found", history.MessageText);
        Assert.False(await db.Set<MailboxOutboxEffect>().AnyAsync(x => x.Kind == MailboxEffectKind.Email));

        await using var server = new SmtpMailboxSenderTests.SmtpFixture();
        await fixture.ConfigureSenderAsync(db, server.Port);
        db.EmailTemplates.Add(new EmailTemplate { Name = "TicketUpdated", Subject = "Synthetic retry", HtmlContent = "{{UpdateMessageHtml}}" });
        await db.SaveChangesAsync();
        await fixture.RetryLegacyAsync(db, history.Id);
        db.ChangeTracker.Clear();
        var (queued, pending, original) = await ReadDeliveryAsync(db, request.Id);
        Assert.Equal(history.Id, pending.Id);
        Assert.Equal(EmailDeliveryStatus.Pending, pending.EmailStatus);
        Assert.Equal(worklog.Id, original.WorkLogId);
        Assert.Contains("Synthetic public update", original.Html);
        Assert.DoesNotContain("email template was not found", original.Html);

        await fixture.DispatchAsync();
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        db.ChangeTracker.Clear();
        var (completed, delivered, _) = await ReadDeliveryAsync(db, request.Id);
        Assert.Equal(queued.Id, completed.Id);
        Assert.Equal(history.Id, delivered.Id);
        Assert.Equal(EmailDeliveryStatus.Delivered, delivered.EmailStatus);
        Assert.Contains("Synthetic public update", server.Message);
        Assert.DoesNotContain("email template was not found", server.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ownerless_retry_rejects_missing_foreign_internal_or_unavailable_worklog_content(bool postgres)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        await using var db = fixture.Open();
        var request = await fixture.SeedRequestAsync(db);
        var foreign = new WorkLog { TicketId = "foreign-ticket", NotesHtml = "<p>Foreign</p>" };
        var internalNote = new WorkLog { TicketId = request.Id, IsInternalNote = true, NotesHtml = "<p>Internal</p>" };
        db.WorkLogs.AddRange(foreign, internalNote);
        await db.SaveChangesAsync();
        foreach (var worklogId in new[] { foreign.Id, internalNote.Id, "missing-worklog", null })
        {
            var history = new TicketTimelineEvent
            {
                TicketId = request.Id, WorkLogId = worklogId, EventType = TimelineEventType.EmailDelivery,
                EmailStatus = EmailDeliveryStatus.Failed, EmailRecipient = "requester@example.test",
                MessageText = "Synthetic delivery failure"
            };
            db.TicketTimelineEvents.Add(history);
            await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RetryLegacyAsync(db, history.Id));
            Assert.False(await db.Set<MailboxOutboxEffect>().AnyAsync(x => x.Kind == MailboxEffectKind.Email));
            await db.Entry(history).ReloadAsync();
            Assert.Equal(EmailDeliveryStatus.Failed, history.EmailStatus);
            Assert.Equal(worklogId, history.WorkLogId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_queue_acceptance_keeps_the_worklog_and_records_failure_without_an_orphan_effect(bool postgres)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        foreach (var transactional in new[] { false, true })
        {
            await using var db = fixture.Open();
            var request = await fixture.SeedRequestAsync(db);
            var context = new IngressEffectContext();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var command = new CreateWorkLogCommand(request.Id, 0, "<p>Retained update</p>", "synthetic-operator", "Synthetic operator",
                AuthorizedTicket: request);
            WorkLog worklog;
            if (transactional)
            {
                using var capture = context.Begin(Guid.NewGuid());
                await using var transaction = await db.Database.BeginTransactionAsync();
                worklog = await fixture.Handler(db, context).Handle(command, cancelled.Token);
                await new MailboxOutboxStore(db, context, TimeProvider.System).FlushAsync();
                await transaction.CommitAsync();
            }
            else
                worklog = await fixture.Handler(db, context).Handle(command, cancelled.Token);
            db.ChangeTracker.Clear();
            Assert.True(await db.WorkLogs.AnyAsync(x => x.Id == worklog.Id && x.TicketId == request.Id));
            var history = await db.TicketTimelineEvents.SingleAsync(x => x.TicketId == request.Id && x.EventType == TimelineEventType.EmailDelivery);
            Assert.Equal(worklog.Id, history.WorkLogId);
            Assert.Equal(EmailDeliveryStatus.Failed, history.EmailStatus);
            Assert.False(await db.Set<MailboxOutboxEffect>().AnyAsync(x => x.Kind == MailboxEffectKind.Email));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_duplicate_binding_and_internal_notes_preserve_history_without_submission(bool postgres)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        await using var db = fixture.Open();
        var request = await fixture.SeedRequestAsync(db);
        await fixture.AddWorklogAsync(db, request, transactional: false);
        db.ChangeTracker.Clear();
        var (queued, history, email) = await ReadDeliveryAsync(db, request.Id);
        var store = new MailboxOutboxStore(db, new IngressEffectContext(), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueueDirectAsync(email, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueueDirectAsync(email with { WorkLogId = null }, default));
        Assert.Equal(1, await db.Set<MailboxOutboxEffect>().CountAsync(x => x.Kind == MailboxEffectKind.Email));

        var wrongTicket = new TicketTimelineEvent
        {
            TicketId = "different-ticket", EventType = TimelineEventType.EmailDelivery,
            EmailStatus = EmailDeliveryStatus.Pending
        };
        db.TicketTimelineEvents.Add(wrongTicket);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueueDirectAsync(
            email with { TimelineDeliveryId = wrongTicket.Id }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueueDirectAsync(
            email with { TimelineDeliveryId = Guid.NewGuid() }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueueDirectAsync(
            email with { TimelineDeliveryId = null }, default));

        var action = new PendingEmailActions(db, store, TimeProvider.System);
        Assert.Equal("Cancelled", (await action.ApplyAsync(history.Id, "cancel", "synthetic-operator", "owned fixture", default)).Outcome);
        Assert.Null(await store.TryClaimAsync(queued.Id, "worker", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueueDirectAsync(email, default));
        await fixture.AddWorklogAsync(db, request, transactional: false, internalNote: true);
        await fixture.AddWorklogAsync(db, request, transactional: true, internalNote: true);
        db.ChangeTracker.Clear();
        var (cancelled, retained, _) = await ReadDeliveryAsync(db, request.Id);
        Assert.Equal(queued.Payload, cancelled.Payload);
        Assert.Equal(queued.Id, cancelled.Id);
        Assert.Equal(history.Id, retained.Id);
        Assert.Equal(EmailDeliveryStatus.Cancelled, retained.EmailStatus);
        Assert.Equal(MailboxEffectState.Cancelled, cancelled.State);
        Assert.Equal(2, await db.WorkLogs.CountAsync(x => x.TicketId == request.Id && x.IsInternalNote));
        Assert.Equal(1, await db.Set<MailboxOutboxEffect>().CountAsync(x => x.Kind == MailboxEffectKind.Email));
        var internalWorklog = await db.WorkLogs.FirstAsync(x => x.TicketId == request.Id && x.IsInternalNote);
        var unowned = new TicketTimelineEvent
        {
            TicketId = request.Id, WorkLogId = internalWorklog.Id, EventType = TimelineEventType.EmailDelivery,
            EmailStatus = EmailDeliveryStatus.Pending
        };
        db.TicketTimelineEvents.Add(unowned);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueueDirectAsync(email with
        {
            TimelineDeliveryId = unowned.Id, WorkLogId = internalWorklog.Id
        }, default));

        foreach (var invalid in new[]
        {
            email with { WorkLogId = internalWorklog.Id },
            email with { TicketId = wrongTicket.TicketId },
            email with { WorkLogId = Guid.NewGuid().ToString() }
        })
        {
            var withoutHistory = invalid with { TimelineDeliveryId = null, SuppressTimeline = false };
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueueDirectAsync(withoutHistory, default));
            var context = new IngressEffectContext();
            using var capture = context.Begin(Guid.NewGuid());
            context.Capture(MailboxEffectKind.Email, withoutHistory);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new MailboxOutboxStore(db, context, TimeProvider.System).FlushAsync());
        }
        Assert.Equal(1, await db.Set<MailboxOutboxEffect>().CountAsync(x => x.Kind == MailboxEffectKind.Email));
        Assert.Equal(3, await db.TicketTimelineEvents.CountAsync(x => x.EventType == TimelineEventType.EmailDelivery));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Exception_after_queue_acceptance_reconciles_the_owner_without_overwriting_dispatch_evidence(
        bool postgres, bool transactional)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        foreach (var cancellation in new[] { false, true })
        {
            await using var server = new SmtpMailboxSenderTests.SmtpFixture();
            await using var db = fixture.Open();
            await fixture.ConfigureSenderAsync(db, server.Port);
            var request = await fixture.SeedRequestAsync(db);
            var context = new IngressEffectContext();
            var handler = fixture.Handler(db, context, inner => new InterruptedEmailService(inner,
                transactional ? () => Task.CompletedTask : fixture.DispatchAsync, cancellation));
            var command = new CreateWorkLogCommand(request.Id, 0.5, "<p>Accepted update</p>", "synthetic-operator", "Synthetic operator",
                AuthorizedTicket: request);
            WorkLog worklog;
            if (transactional)
            {
                using var capture = context.Begin(Guid.NewGuid());
                await using var transaction = await db.Database.BeginTransactionAsync();
                worklog = await handler.Handle(command, default);
                await new MailboxOutboxStore(db, context, TimeProvider.System).FlushAsync();
                await transaction.CommitAsync();
            }
            else
                worklog = await handler.Handle(command, default);
            db.ChangeTracker.Clear();
            var (owned, retained, _) = await ReadDeliveryAsync(db, request.Id);
            Assert.Equal(worklog.Id, retained.WorkLogId);
            Assert.Equal(transactional ? EmailDeliveryStatus.Pending : EmailDeliveryStatus.Delivered, retained.EmailStatus);
            Assert.Equal(transactional ? MailboxEffectState.Pending : MailboxEffectState.Completed, owned.State);
            if (transactional) await fixture.DispatchAsync();
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            db.ChangeTracker.Clear();
            var (completed, delivered, _) = await ReadDeliveryAsync(db, request.Id);
            Assert.Equal(owned.Id, completed.Id);
            Assert.Equal(retained.Id, delivered.Id);
            Assert.Equal(EmailDeliveryStatus.Delivered, delivered.EmailStatus);
            Assert.Equal(1, completed.Attempts);
            Assert.Contains("Accepted update", server.Message);
            Assert.Equal(1, server.Commands.Count(command => command == "DATA"));
        }
    }

    private static async Task<(MailboxOutboxEffect Effect, TicketTimelineEvent Delivery, IngressEmailEffect Email)> ReadDeliveryAsync(
        HelpdeskDbContext db, string ticketId)
    {
        var delivery = await db.TicketTimelineEvents.AsNoTracking()
            .SingleAsync(x => x.TicketId == ticketId && x.EventType == TimelineEventType.EmailDelivery);
        var effect = await db.Set<MailboxOutboxEffect>().AsNoTracking().SingleAsync(x => x.DeliveryEventId == delivery.Id);
        var email = MailboxOutboxStore.Deserialize<IngressEmailEffect>(effect.Payload);
        Assert.Equal(delivery.Id, email.TimelineDeliveryId);
        Assert.Equal(ticketId, email.TicketId);
        Assert.Equal(MailboxEffectKind.Email, effect.Kind);
        return (effect, delivery, email);
    }

    private sealed class Database : IAsyncDisposable
    {
        private PostgreSqlContainer? postgres;
        private string? path;
        private DbContextOptions<HelpdeskDbContext> options = null!;
        private readonly IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["EmailSending:AllowedPrivateHosts:0"] = "localhost" }).Build();
        private readonly MailboxOutgoingCredentialProtector protection = new(new EphemeralDataProtectionProvider());
        private readonly TimelineEventBus timeline = new();

        public static async Task<Database> CreateAsync(bool postgres)
        {
            var fixture = new Database();
            var builder = new DbContextOptionsBuilder<HelpdeskDbContext>();
            if (postgres)
            {
                fixture.postgres = new PostgreSqlBuilder("postgres:16").Build();
                await fixture.postgres.StartAsync();
                builder.UseNpgsql(fixture.postgres.GetConnectionString());
            }
            else
            {
                fixture.path = Path.Combine(Path.GetTempPath(), $"worklog-mail-{Guid.NewGuid():N}.db");
                builder.UseSqlite(new SqliteConnectionStringBuilder { DataSource = fixture.path, Pooling = false }.ToString(),
                    x => x.MigrationsAssembly("Helpdesk.Infrastructure.SqliteMigrations"));
            }
            fixture.options = builder.Options;
            await using var db = fixture.Open();
            await db.Database.MigrateAsync();
            db.Organizations.Add(new Organization { Id = "synthetic-tenant", Name = "Synthetic tenant" });
            db.EmailTemplates.Add(new EmailTemplate { Name = "TicketUpdated", Subject = "Update {{TicketRef}}", HtmlContent = "{{UpdateMessageHtml}}" });
            db.Set<MailboxWorkerControl>().Add(new MailboxWorkerControl { Running = false });
            await db.SaveChangesAsync();
            return fixture;
        }

        public HelpdeskDbContext Open()
        {
            var tenant = Substitute.For<ITenantContext>();
            tenant.IsHelpdeskAdmin.Returns(true);
            return new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
        }

        public async Task<Request> SeedRequestAsync(HelpdeskDbContext db)
        {
            var request = new Request
            {
                OrganizationId = "synthetic-tenant", TrackingId = $"REQ-{Guid.NewGuid():N}",
                Title = "Synthetic request", RequesterEmail = "requester@example.test", CcRecipients = ["copy@example.test"]
            };
            db.Requests.Add(request);
            await db.SaveChangesAsync();
            return request;
        }

        public async Task ConfigureSenderAsync(HelpdeskDbContext db, int port, bool enabled = true)
        {
            var mailbox = await db.EmailInboxSettings.SingleOrDefaultAsync();
            if (mailbox is null)
            {
                mailbox = new EmailInboxSettings
                {
                    Id = Guid.NewGuid(), Scope = MailboxScope.Global, MailboxAddress = "sender@example.test",
                    Enabled = true, SourceKey = "synthetic-capture", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
                };
                db.EmailInboxSettings.Add(mailbox);
            }
            var outgoing = await db.Set<MailboxOutgoingSettings>().SingleOrDefaultAsync();
            if (outgoing is null)
            {
                outgoing = new MailboxOutgoingSettings { MailboxId = mailbox.Id };
                db.Set<MailboxOutgoingSettings>().Add(outgoing);
            }
            outgoing.Enabled = enabled;
            outgoing.Transport = MailboxOutgoingTransport.Smtp;
            outgoing.SmtpHost = "localhost";
            outgoing.SmtpPort = port;
            outgoing.SmtpTlsMode = MailboxTlsMode.TlsOnConnect;
            outgoing.SmtpUsername = mailbox.MailboxAddress;
            outgoing.ProtectedSmtpPassword = protection.Protect(outgoing, "synthetic-password");
            outgoing.Version++;
            await db.SaveChangesAsync();
        }

        private MailboxEmailService Sender(HelpdeskDbContext db, IIngressEffectContext context) => new(
            new MailboxSenderResolver(db), new SmtpMailboxSender(new MailboxDestinationPolicy(config), protection, new HtmlToPlainTextConverter()),
            new GraphMailboxSender(_ => throw new InvalidOperationException("Only isolated SMTP capture is configured.")),
            new MailboxOutboxStore(db, context, TimeProvider.System), context, NullLogger<MailboxEmailService>.Instance);

        public CreateWorkLogCommandHandler Handler(HelpdeskDbContext db, IngressEffectContext context,
            Func<IEmailService, IEmailService>? decorate = null)
        {
            var images = Substitute.For<IWorklogImageStorageService>();
            images.ExtractAndStoreImagesAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(call => call.ArgAt<string>(1));
            var renderer = Substitute.For<IEmailTemplateRenderer>();
            renderer.Render(Arg.Any<string>(), Arg.Any<EmailTemplateContext>()).Returns(call => call.Arg<EmailTemplateContext>().UpdateMessageHtml);
            var branding = Substitute.For<ITenantBrandingResolver>();
            branding.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new TenantBrandingResolved());
            IEmailService email = new IngressEmailService(Sender(db, context), new MailboxSenderResolver(db), context);
            if (decorate is not null) email = decorate(email);
            return new(new EfRepository<WorkLog>(db), new EfRepository<Incident>(db), new EfRepository<ActivityLog>(db),
                Substitute.For<INotificationService>(), email,
                new EfRepository<EmailTemplate>(db), new EfRepository<Ticket>(db), new EfRepository<Customer>(db),
                new EfRepository<TicketTimelineEvent>(db), new IngressTimelineEventBus(timeline, context),
                new HtmlSanitizerService(), images, new HtmlToPlainTextConverter(), renderer,
                Substitute.For<IEmailLayoutResolver>(), branding, Substitute.For<IPublicTicketLinkSigner>(), config,
                ingressEffects: context);
        }

        public async Task<WorkLog> AddWorklogAsync(HelpdeskDbContext db, Request request, bool transactional, bool internalNote = false)
        {
            var context = new IngressEffectContext();
            var handler = Handler(db, context);
            var command = new CreateWorkLogCommand(request.Id, 0.25, "<p>Synthetic public update</p>", "synthetic-operator", "Synthetic operator",
                IsInternalNote: internalNote, AuthorizedTicket: request);
            if (!transactional) return await handler.Handle(command, default);
            using var capture = context.Begin(Guid.NewGuid());
            context.OrganizationId = request.OrganizationId;
            await using var transaction = await db.Database.BeginTransactionAsync();
            var log = await handler.Handle(command, default);
            await new MailboxOutboxStore(db, context, TimeProvider.System).FlushAsync();
            await transaction.CommitAsync();
            return log;
        }

        public async Task DispatchAsync()
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => Open());
            services.AddScoped<IIngressEffectContext, IngressEffectContext>();
            services.AddScoped(p => new MailboxOutboxStore(p.GetRequiredService<HelpdeskDbContext>(),
                p.GetRequiredService<IIngressEffectContext>(), TimeProvider.System));
            services.AddScoped(p => Sender(p.GetRequiredService<HelpdeskDbContext>(), p.GetRequiredService<IIngressEffectContext>()));
            services.AddSingleton<ITimelineEventBus>(timeline);
            await using var provider = services.BuildServiceProvider();
            using var dispatcher = new MailboxOutboxDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<MailboxOutboxDispatcher>.Instance);
            await dispatcher.DispatchBatchAsync();
        }

        public Task RetryLegacyAsync(HelpdeskDbContext db, Guid deliveryId)
        {
            var context = new IngressEffectContext();
            var renderer = Substitute.For<IEmailTemplateRenderer>();
            renderer.Render(Arg.Any<string>(), Arg.Any<EmailTemplateContext>()).Returns(call => call.Arg<EmailTemplateContext>().UpdateMessageHtml);
            var branding = Substitute.For<ITenantBrandingResolver>();
            branding.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new TenantBrandingResolved());
            var service = new TimelineService(db, Sender(db, context), renderer, Substitute.For<IEmailLayoutResolver>(), branding,
                Substitute.For<IPublicTicketLinkSigner>(), timeline, config,
                new MailboxOutboxStore(db, context, TimeProvider.System));
            return service.RetryEmailAsync(deliveryId, "synthetic-operator", default);
        }

        public async ValueTask DisposeAsync()
        {
            if (postgres is not null) await postgres.DisposeAsync();
            if (path is not null) { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
        }
    }

    private sealed class InterruptedEmailService(IEmailService inner, Func<Task> afterAcceptance, bool cancellation)
        : IEmailService, IDurableEmailService
    {
        public bool QueuesDelivery => true;
        public Task<bool?> OwnsDeliveryHistoryAsync(Guid deliveryEventId, CancellationToken ct = default) =>
            ((IDurableEmailService)inner).OwnsDeliveryHistoryAsync(deliveryEventId, ct);
        public Task<bool> TestApiConnectionAsync() => inner.TestApiConnectionAsync();
        public async Task<bool> SendEmailAsync(EmailSendRequest request, CancellationToken ct = default)
        {
            if (!await inner.SendEmailAsync(request, ct)) return false;
            await afterAcceptance();
            if (cancellation) throw new OperationCanceledException("Synthetic interruption after queue acceptance.");
            throw new IOException("Synthetic failure after queue acceptance.");
        }
        public Task<bool> SendEmailAsync(string recipient, string subject, string htmlMessage,
            IEnumerable<string>? cc = null, CancellationToken ct = default, string? ticketId = null,
            IEnumerable<EmailAttachmentData>? attachments = null, string? fromName = null, string? replyTo = null,
            bool suppressTimeline = false) => SendEmailAsync([recipient], subject, htmlMessage, cc, ct, ticketId,
                attachments, fromName, replyTo, suppressTimeline);
        public Task<bool> SendEmailAsync(IEnumerable<string> recipients, string subject, string htmlMessage,
            IEnumerable<string>? cc = null, CancellationToken ct = default, string? ticketId = null,
            IEnumerable<EmailAttachmentData>? attachments = null, string? fromName = null, string? replyTo = null,
            bool suppressTimeline = false) => SendEmailAsync(new EmailSendRequest(recipients.ToArray(), subject, htmlMessage)
            {
                Cc = cc?.ToArray() ?? [], TicketId = ticketId, Attachments = attachments?.ToArray() ?? [],
                FromName = fromName, ReplyTo = replyTo, SuppressTimeline = suppressTimeline
            }, ct);
    }
}
