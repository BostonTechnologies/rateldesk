using System.Text.Json;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Email;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class PendingEmailQueueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_filters_counts_and_pages_the_entire_1205_row_dataset(bool postgres)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        await using var db = fixture.Open();
        var first = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        db.Organizations.AddRange(new Organization { Id = "org-a", Name = "Organization A" }, new Organization { Id = "org-b", Name = "Organization B" });
        db.Incidents.AddRange(new Incident { Id = "ticket-a", OrganizationId = "org-a", TrackingId = "INC-ALPHA", Title = "Synthetic printer" },
            new Incident { Id = "ticket-b", OrganizationId = "org-b", TrackingId = "INC-BETA", Title = "Synthetic network" });
        var mailbox = new EmailInboxSettings { Id = Guid.NewGuid(), MailboxAddress = "support@example.test", DisplayName = "Synthetic mailbox" };
        db.EmailInboxSettings.Add(mailbox);
        var ids = new List<Guid>();
        for (var i = 0; i < 1205; i++)
        {
            var delivery = new TicketTimelineEvent { TicketId = i % 2 == 0 ? "ticket-a" : "ticket-b",
                CreatedUtc = first.AddMinutes(i), EventType = TimelineEventType.EmailDelivery,
                EmailStatus = EmailDeliveryStatus.Failed, EmailRecipient = i % 2 == 0 ? "alpha@example.test" : "beta@example.test" };
            ids.Add(delivery.Id); db.TicketTimelineEvents.Add(delivery);
            db.Set<MailboxOutboxEffect>().Add(new() { DeliveryEventId = delivery.Id, Kind = MailboxEffectKind.Email,
                State = MailboxEffectState.Exhausted, EffectKey = $"synthetic-{i}", DispatchGroup = $"mailbox:{mailbox.Id:N}",
                LastErrorCode = i % 3 == 0 ? "SyntheticRejected" : "SyntheticUnavailable", Payload = "{}" });
        }
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var service = new PendingEmailQueryService(db);
        var page = await service.SearchAsync(new(), default);
        Assert.Equal(1205, page.Total); Assert.Equal(25, page.Items.Count);
        Assert.Equal(first.AddMinutes(1204), page.Items[0].CreatedUtc);
        var tail = await service.SearchAsync(new() { Page = 12, PageSize = 100 }, default);
        Assert.Equal(5, tail.Items.Count); Assert.Equal(1205, tail.Total);
        var filtered = await service.SearchAsync(new() { Search = "SYNTHETIC PRINTER", Recipient = "ALPHA", OrganizationId = "org-a",
            Sender = "SUPPORT", Reason = "rejected", From = first.AddMinutes(1000), To = first.AddMinutes(1200), Sort = "created", Descending = false }, default);
        Assert.Equal(34, filtered.Total); Assert.Equal(25, filtered.Items.Count);
        Assert.Equal(first.AddMinutes(1002), filtered.Items[0].CreatedUtc);
        Assert.All(filtered.Items, x => { Assert.Equal("INC-ALPHA", x.TrackingId); Assert.Equal("support@example.test", x.Sender); });
        var last = await service.SearchAsync(new() { Search = "INC-BETA", Page = 100, PageSize = 50 }, default);
        Assert.Equal(602, last.Total); Assert.Equal(12, last.Page); Assert.Equal(2, last.Items.Count);
        var seen = new HashSet<Guid>();
        for (var p = 0; p <= 12; p++)
            foreach (var item in (await service.SearchAsync(new() { Page = p, PageSize = 100, Sort = "status" }, default)).Items)
                Assert.True(seen.Add(item.Id));
        Assert.Equal(1205, seen.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new() { PageSize = 1000 }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new() { From = first.AddDays(1), To = first }, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_then_remove_is_idempotent_durable_and_preserves_all_history(bool postgres)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        var (id, timeline) = await fixture.SeedAsync();
        await using (var db = fixture.Open())
        {
            db.Incidents.Add(new Incident { Id = "ticket-a", TrackingId = "INC-HISTORY", Title = "Original title", Description = "Original message" });
            db.WorkLogs.Add(new WorkLog { TicketId = "ticket-a", NotesText = "Original worklog", Hours = 1 });
            db.Attachments.Add(new Attachment { TicketId = "ticket-a", FileName = "synthetic.txt", FilePath = "synthetic.txt", SizeBytes = 42, ContentType = "text/plain" });
            db.TicketTimelineEvents.Add(new() { TicketId = "ticket-a", EventType = TimelineEventType.TechnicianReply, MessageHtml = "<p>Original reply</p>", MessageText = "Original reply" });
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.Open())
        {
            var actions = fixture.Actions(db);
            Assert.Equal("Cancelled", (await actions.ApplyAsync(timeline, "cancel", "admin-a", "Requested by operator", default)).Outcome);
            Assert.Equal("AlreadyCancelled", (await actions.ApplyAsync(timeline, "cancel", "admin-b", "Repeat", default)).Outcome);
            Assert.Equal("Removed", (await actions.ApplyAsync(timeline, "remove", "admin-a", "Queue cleanup", default)).Outcome);
            Assert.Equal("AlreadyRemoved", (await actions.ApplyAsync(timeline, "remove", "admin-b", "Repeat", default)).Outcome);
            Assert.Equal("ReviewRequired", (await actions.ApplyAsync(timeline, "retry", "admin-a", "Retry", default)).Outcome);
        }
        // Fresh context is a worker restart; no in-memory cancellation token participates.
        await using var restarted = fixture.Open();
        Assert.Null(await fixture.Store(restarted).TryClaimAsync(id, "restarted-worker", default));
        Assert.DoesNotContain(id, await fixture.Store(restarted).GetCandidatesAsync(100, default));
        var durable = await restarted.Set<MailboxOutboxEffect>().SingleAsync(x => x.Id == id);
        Assert.Equal(MailboxEffectState.Removed, durable.State); Assert.Contains("Synthetic body", durable.Payload);
        Assert.Equal(2, await restarted.TicketTimelineEvents.CountAsync());
        Assert.Equal("Original reply", (await restarted.TicketTimelineEvents.SingleAsync(x => x.Id != timeline)).MessageText);
        Assert.Equal("Original title", (await restarted.Incidents.SingleAsync()).Title);
        Assert.Equal("Original message", (await restarted.Incidents.SingleAsync()).Description);
        Assert.Equal("Original worklog", (await restarted.WorkLogs.SingleAsync()).NotesText);
        Assert.Equal("synthetic.txt", (await restarted.Attachments.SingleAsync()).FileName);
        Assert.Equal("Email queued for delivery.", (await restarted.TicketTimelineEvents.SingleAsync(x => x.Id == timeline)).MessageText);
        Assert.Equal(2, await restarted.ActivityLogs.CountAsync());
        Assert.All(await restarted.ActivityLogs.ToListAsync(), x => Assert.Equal("admin-a", x.UserId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_fences_claimed_worker_and_submission_gate_has_one_winner(bool postgres)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        var (id, timeline) = await fixture.SeedAsync();
        await using var worker = fixture.Open();
        var claim = Assert.IsType<MailboxOutboxEffect>(await fixture.Store(worker).TryClaimAsync(id, "worker", default));
        await using (var control = fixture.Open())
            Assert.Equal("Cancelled", (await fixture.Actions(control).ApplyAsync(timeline, "cancel", "admin", "Stop", default)).Outcome);
        Assert.False(await fixture.Store(worker).TryBeginSubmissionAsync(claim, default));
        Assert.False(await fixture.Store(worker).CompleteAsync(claim, true, null, default));
        var next = await fixture.SeedAsync();
        var submitted = Assert.IsType<MailboxOutboxEffect>(await fixture.Store(worker).TryClaimAsync(next.Id, "worker", default));
        Assert.True(await fixture.Store(worker).TryBeginSubmissionAsync(submitted, default));
        await using var after = fixture.Open();
        Assert.Equal("Conflict", (await fixture.Actions(after).ApplyAsync(next.Timeline, "cancel", "admin", "Stop", default)).Outcome);
        Assert.Equal("Conflict", (await fixture.Actions(after).ApplyAsync(next.Timeline, "remove", "admin", "Remove", default)).Outcome);
        Assert.True(await fixture.Store(worker).CompleteAsync(submitted, true, null, default));
        Assert.Equal("ReviewRequired", (await fixture.Actions(after).ApplyAsync(next.Timeline, "cancel", "admin", "Stop", default)).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submission_crash_unknown_partial_acceptance_and_old_workers_are_never_reported_cancelled(bool postgres)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        foreach (var code in new[] { "DispatchOutcomeUnknown", "SubmissionOutcomeUnknown", "SmtpPartialRecipientAcceptance" })
        {
            var (id, timeline) = await fixture.SeedAsync();
            await using var db = fixture.Open();
            await db.Set<MailboxOutboxEffect>().Where(x => x.Id == id).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.State, MailboxEffectState.NeedsReview).SetProperty(x => x.LastErrorCode, code));
            Assert.Equal("ReviewRequired", (await fixture.Actions(db).ApplyAsync(timeline, "cancel", "admin", "Stop", default)).Outcome);
            Assert.Equal("ReviewRequired", (await fixture.Actions(db).ApplyAsync(timeline, "retry", "admin", "Retry", default)).Outcome);
            Assert.Equal("Removed", (await fixture.Actions(db).ApplyAsync(timeline, "remove", "admin", "Hide without recall", default)).Outcome);
            Assert.Equal(code, (await db.Set<MailboxOutboxEffect>().AsNoTracking().SingleAsync(x => x.Id == id)).LastErrorCode);
        }
        var crashed = await fixture.SeedAsync();
        await using var worker = fixture.Open();
        var claim = Assert.IsType<MailboxOutboxEffect>(await fixture.Store(worker).TryClaimAsync(crashed.Id, "worker", default));
        Assert.True(await fixture.Store(worker).TryBeginSubmissionAsync(claim, default));
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(6);
        await using var restart = fixture.Open();
        Assert.Null(await fixture.Store(restart).TryClaimAsync(crashed.Id, "restart", default));
        Assert.Equal("ReviewRequired", (await fixture.Actions(restart).ApplyAsync(crashed.Timeline, "cancel", "admin", "Stop", default)).Outcome);
        var old = await fixture.SeedAsync();
        await restart.Set<MailboxOutboxEffect>().Where(x => x.Id == old.Id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.State, MailboxEffectState.InFlight).SetProperty(x => x.Owner, "old-worker"));
        Assert.Equal("Conflict", (await fixture.Actions(restart).ApplyAsync(old.Timeline, "cancel", "admin", "Stop", default)).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_vs_removal_and_cancel_vs_submission_races_cannot_resurrect_delivery(bool postgres)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var (id, timeline) = await fixture.SeedAsync();
            await using (var seed = fixture.Open())
            {
                await seed.Set<MailboxOutboxEffect>().Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.State, MailboxEffectState.Exhausted));
                await seed.TicketTimelineEvents.Where(x => x.Id == timeline).ExecuteUpdateAsync(u => u.SetProperty(x => x.EmailStatus, EmailDeliveryStatus.Failed));
            }
            await using var retry = fixture.Open(); await using var removal = fixture.Open();
            var results = await Task.WhenAll(Task.Run(() => Capture(() => fixture.Actions(retry).ApplyAsync(timeline, "retry", "admin", "Retry", default))),
                Task.Run(() => Capture(() => fixture.Actions(removal).ApplyAsync(timeline, "remove", "admin", "Remove", default))));
            await using var verify = fixture.Open();
            // A serialization conflict is an explicit failed mutation, not success. A retried removal is safe.
            if (!results.Contains("Removed")) Assert.Equal("Removed", (await fixture.Actions(verify).ApplyAsync(timeline, "remove", "admin", "Retry cleanup", default)).Outcome);
            Assert.Null(await fixture.Store(verify).TryClaimAsync(id, "restarted", default));
            Assert.Equal(MailboxEffectState.Removed, (await verify.Set<MailboxOutboxEffect>().AsNoTracking().SingleAsync(x => x.Id == id)).State);
        }
        var pending = await fixture.SeedAsync();
        await using var worker = fixture.Open();
        var claim = Assert.IsType<MailboxOutboxEffect>(await fixture.Store(worker).TryClaimAsync(pending.Id, "worker", default));
        await using var cancel = fixture.Open();
        var cancelTask = Task.Run(() => Capture(() => fixture.Actions(cancel).ApplyAsync(pending.Timeline, "cancel", "admin", "Stop", default)));
        var gateTask = Task.Run(() => fixture.Store(worker).TryBeginSubmissionAsync(claim, default));
        var outcome = await cancelTask; var submitted = await gateTask;
        Assert.False(outcome == "Cancelled" && submitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_audit_write_rolls_back_queue_and_timeline_mutation(bool postgres)
    {
        await using var fixture = await Database.CreateAsync(postgres);
        var (id, timeline) = await fixture.SeedAsync();
        await using (var failing = fixture.Open(failAudit: true))
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Actions(failing).ApplyAsync(timeline, "cancel", "admin", "Synthetic failure", default));
        await using var verify = fixture.Open();
        Assert.Equal(MailboxEffectState.Pending, (await verify.Set<MailboxOutboxEffect>().SingleAsync(x => x.Id == id)).State);
        Assert.Equal(EmailDeliveryStatus.Pending, (await verify.TicketTimelineEvents.SingleAsync(x => x.Id == timeline)).EmailStatus);
        Assert.Empty(await verify.ActivityLogs.ToListAsync());
    }

    private sealed class FailAudit : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ActivityLog>().Any()) throw new InvalidOperationException("Synthetic audit write failure.");
            return ValueTask.FromResult(result);
        }
    }

    private static async Task<string> Capture(Func<Task<PendingEmailActionResult>> operation)
    {
        try { return (await operation()).Outcome; }
        catch (System.Data.Common.DbException) { return "Conflict"; }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Database : IAsyncDisposable
    {
        private PostgreSqlContainer? container;
        private string? path;
        private DbContextOptions<HelpdeskDbContext> options = null!;
        public ManualClock Clock { get; } = new();
        public static async Task<Database> CreateAsync(bool postgres)
        {
            var result = new Database(); var builder = new DbContextOptionsBuilder<HelpdeskDbContext>();
            if (postgres)
            {
                result.container = new PostgreSqlBuilder("postgres:16").Build(); await result.container.StartAsync();
                builder.UseNpgsql(result.container.GetConnectionString());
            }
            else
            {
                result.path = Path.Combine(Path.GetTempPath(), $"pending-mail-{Guid.NewGuid():N}.db");
                builder.UseSqlite(new SqliteConnectionStringBuilder { DataSource = result.path, Pooling = false }.ToString(),
                    x => x.MigrationsAssembly("Helpdesk.Infrastructure.SqliteMigrations"));
            }
            result.options = builder.Options;
            await using var db = result.Open(); await db.Database.MigrateAsync(); return result;
        }
        public HelpdeskDbContext Open(bool failAudit = false)
        {
            var tenant = Substitute.For<ITenantContext>(); tenant.IsHelpdeskAdmin.Returns(true);
            var selectedOptions = failAudit ? new DbContextOptionsBuilder<HelpdeskDbContext>(options).AddInterceptors(new FailAudit()).Options : options;
            return new(selectedOptions, tenant, new HttpContextAccessor());
        }
        public MailboxOutboxStore Store(HelpdeskDbContext db) => new(db, new IngressEffectContext(), Clock);
        public PendingEmailActions Actions(HelpdeskDbContext db) => new(db, Store(db), Clock);
        public async Task<(Guid Id, Guid Timeline)> SeedAsync()
        {
            await using var db = Open();
            var row = await Store(db).QueueDirectAsync(new IngressEmailEffect(["recipient@example.test"], "Synthetic subject", "<p>Synthetic body</p>",
                [], "ticket-a", [], null, null, false, null, null), default);
            return (row.Id, row.DeliveryEventId!.Value);
        }
        public async ValueTask DisposeAsync()
        {
            if (container is not null) await container.DisposeAsync();
            if (path is not null) { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
        }
    }
}
