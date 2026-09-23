using Helpdesk.Application.Notifications;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Events;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Services;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class MailboxOutboxTests
{
    [Fact]
    public async Task Direct_ticket_mail_has_one_durable_owner_and_changed_sender_retry_stays_held()
    {
        await using var fixture = await OutboxDatabase.CreateAsync();
        await using var db = fixture.Open();
        var store = fixture.Store(db);
        var mailboxId = Guid.NewGuid();
        var email = new IngressEmailEffect(["requester@tenant-a.example.test"], "Ticket update", "<p>Body</p>",
            [], "ticket-a", [], null, null, false, null, null)
        {
            MailboxId = mailboxId, OrganizationId = "tenant-a",
            MailboxConfigurationVersion = 3, OutgoingConfigurationVersion = 4
        };
        var queued = await store.QueueDirectAsync(email, default);
        Assert.Null(queued.ReceiptId);
        Assert.NotNull(queued.DeliveryEventId);
        var claim = Assert.IsType<MailboxOutboxEffect>(await store.TryClaimAsync(queued.Id, "worker", default));
        Assert.True(await store.CompleteAsync(claim, false, "SenderConfigurationChanged", default,
            requiresReview: true));
        Assert.Equal(EmailDeliveryStatus.Failed,
            (await db.TicketTimelineEvents.SingleAsync(x => x.Id == queued.DeliveryEventId)).EmailStatus);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RetryForTimelineAsync(queued.DeliveryEventId!.Value, default));
        Assert.Null(await store.TryClaimAsync(queued.Id, "worker", default));
        var held = await db.Set<MailboxOutboxEffect>().AsNoTracking().SingleAsync(x => x.Id == queued.Id);
        var replay = MailboxOutboxStore.Deserialize<IngressEmailEffect>(held.Payload);
        Assert.Equal(mailboxId, replay.MailboxId);
        Assert.Equal("tenant-a", replay.OrganizationId);
        Assert.Equal(3, replay.MailboxConfigurationVersion);
        Assert.Equal(4, replay.OutgoingConfigurationVersion);
        Assert.Equal(1, await db.Set<MailboxOutboxEffect>().CountAsync(x => x.Kind == MailboxEffectKind.Email));
    }

    [Fact]
    public async Task Concurrent_dispatchers_claim_one_effect_only_once()
    {
        await using var fixture = await OutboxDatabase.CreateAsync();
        var id = await fixture.CaptureEmailAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<MailboxOutboxEffect?> Claim(string owner, TaskCompletionSource ready) => Task.Run(async () =>
        {
            await using var db = fixture.Open();
            ready.SetResult();
            await start.Task;
            return await fixture.Store(db).TryClaimAsync(id, owner, default);
        });
        var first = Claim("a", firstReady);
        var second = Claim("b", secondReady);
        await Task.WhenAll(firstReady.Task, secondReady.Task).WaitAsync(TimeSpan.FromSeconds(10));
        start.SetResult();
        var claims = await Task.WhenAll(first, second);
        Assert.Single(claims.OfType<MailboxOutboxEffect>());
        await using var verify = fixture.Open();
        Assert.Equal(1, (await verify.Set<MailboxOutboxEffect>().SingleAsync(x => x.Id == id)).Attempts);
    }

    [Theory]
    [InlineData("SmtpPartialRecipientAcceptance")]
    [InlineData("SocketException")]
    public async Task Bulk_and_single_retry_cannot_requeue_partial_or_uncertain_delivery(string errorCode)
    {
        await using var fixture = await OutboxDatabase.CreateAsync();
        await using var db = fixture.Open();
        var store = fixture.Store(db);
        var queued = await store.QueueDirectAsync(new IngressEmailEffect(["requester@example.test"],
            "Subject", "<p>Body</p>", [], "ticket-a", [], null, null, false, null, null), default);
        var claim = Assert.IsType<MailboxOutboxEffect>(await store.TryClaimAsync(queued.Id, "worker", default));
        Assert.True(await store.CompleteAsync(claim, false, errorCode, default, requiresReview: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RetryForTimelineAsync(queued.DeliveryEventId!.Value, default));
        Assert.Null(await store.TryClaimAsync(queued.Id, "worker", default));
        Assert.Equal(MailboxEffectState.NeedsReview,
            (await db.Set<MailboxOutboxEffect>().AsNoTracking().SingleAsync(x => x.Id == queued.Id)).State);
    }

    [Fact]
    public async Task Rollback_discards_durable_effects_and_does_not_publish_notifications()
    {
        await using var fixture = await OutboxDatabase.CreateAsync();
        await using var db = fixture.Open();
        var context = new IngressEffectContext();
        var inner = new NotificationEventBus();
        var bus = new IngressNotificationEventBus(inner, context);
        var reader = inner.Subscribe("tenant-a");
        using (context.Begin(Guid.NewGuid()))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            bus.Publish(new NotificationDto { Id = Guid.NewGuid(), TenantId = "tenant-a", Title = "Created", Message = "Test" });
            Assert.False(reader.TryRead(out _));
            await new MailboxOutboxStore(db, context, fixture.Clock).FlushAsync();
            Assert.Equal(1, await db.Set<MailboxOutboxEffect>().CountAsync());
            await transaction.RollbackAsync();
        }
        await using var verify = fixture.Open();
        Assert.Empty(await verify.Set<MailboxOutboxEffect>().ToListAsync());
        Assert.False(reader.TryRead(out _));
        inner.Unsubscribe("tenant-a", reader);
    }

    [Fact]
    public async Task Legacy_email_without_authoritative_sender_binding_is_held_for_review()
    {
        await using var fixture = await OutboxDatabase.CreateAsync();
        await fixture.CaptureEmailAsync();
        var services = new ServiceCollection();
        services.AddScoped<HelpdeskDbContext>(_ => fixture.Open());
        services.AddSingleton<TimeProvider>(fixture.Clock);
        services.AddScoped<IIngressEffectContext, IngressEffectContext>();
        services.AddScoped<MailboxOutboxStore>();
        services.AddSingleton<ITimelineEventBus, TimelineEventBus>();
        services.AddSingleton<INotificationEventBus, NotificationEventBus>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = new MailboxOutboxDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MailboxOutboxDispatcher>.Instance);
        await dispatcher.DispatchBatchAsync();
        await dispatcher.DispatchBatchAsync();
        await using var verify = fixture.Open();
        Assert.Equal(EmailDeliveryStatus.Failed, (await verify.TicketTimelineEvents.SingleAsync()).EmailStatus);
        Assert.Equal(MailboxEffectState.NeedsReview,
            (await verify.Set<MailboxOutboxEffect>().SingleAsync(x => x.Kind == MailboxEffectKind.Email)).State);
        Assert.Equal("LegacySenderBindingMissing",
            (await verify.Set<MailboxOutboxEffect>().SingleAsync(x => x.Kind == MailboxEffectKind.Email)).LastErrorCode);
    }

    [Fact]
    public async Task Unexpected_email_dispatch_failure_is_held_instead_of_automatically_retried()
    {
        await using var fixture = await OutboxDatabase.CreateAsync();
        var id = await fixture.CaptureEmailAsync();
        await using (var setup = fixture.Open())
            await setup.Set<MailboxOutboxEffect>().Where(x => x.Id == id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Payload, "invalid-json"));
        var services = new ServiceCollection();
        services.AddScoped<HelpdeskDbContext>(_ => fixture.Open());
        services.AddSingleton<TimeProvider>(fixture.Clock);
        services.AddScoped<IIngressEffectContext, IngressEffectContext>();
        services.AddScoped<MailboxOutboxStore>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = new MailboxOutboxDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MailboxOutboxDispatcher>.Instance);

        await dispatcher.DispatchBatchAsync();

        await using var verify = fixture.Open();
        var held = await verify.Set<MailboxOutboxEffect>().SingleAsync(x => x.Id == id);
        Assert.Equal(MailboxEffectState.NeedsReview, held.State);
        Assert.Equal("DispatchOutcomeUnknown", held.LastErrorCode);
        Assert.DoesNotContain(id, await fixture.Store(verify).GetCandidatesAsync(16, default));
    }

    [Fact]
    public async Task Expired_email_claim_requires_review_without_a_second_submission()
    {
        await using var fixture = await OutboxDatabase.CreateAsync();
        var id = await fixture.CaptureEmailAsync();
        await using var firstDb = fixture.Open();
        await using var secondDb = fixture.Open();
        var first = fixture.Store(firstDb);
        var second = fixture.Store(secondDb);
        var stale = Assert.IsType<MailboxOutboxEffect>(await first.TryClaimAsync(id, "first", default));
        Assert.Null(await second.TryClaimAsync(id, "second", default));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(await second.TryClaimAsync(id, "second", default));
        Assert.False(await first.CompleteAsync(stale, true, null, default));
        Assert.False(await first.CompleteAsync(stale, false, "OldFailure", default));
        await using var verify = fixture.Open();
        var held = await verify.Set<MailboxOutboxEffect>().SingleAsync(x => x.Id == id);
        Assert.Equal(MailboxEffectState.NeedsReview, held.State);
        Assert.Equal("DispatchOutcomeUnknown", held.LastErrorCode);
        Assert.Equal(stale.Attempts, held.Attempts);
        var timeline = await verify.TicketTimelineEvents.SingleAsync();
        Assert.Equal(EmailDeliveryStatus.Failed, timeline.EmailStatus);
        Assert.False(timeline.IsRetryable);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store(verify).RetryForTimelineAsync(timeline.Id, default));
    }

    [Fact]
    public async Task Retries_are_bounded_and_manual_retry_preserves_the_original_payload()
    {
        await using var fixture = await OutboxDatabase.CreateAsync();
        var id = await fixture.CaptureEmailAsync();
        for (var attempt = 1; attempt <= MailboxOutboxStore.MaximumAttempts; attempt++)
        {
            await using var db = fixture.Open();
            var store = fixture.Store(db);
            var claim = Assert.IsType<MailboxOutboxEffect>(await store.TryClaimAsync(id, "worker", default));
            Assert.Equal(attempt, claim.Attempts);
            Assert.True(await store.CompleteAsync(claim, false, "DeliveryRejected", default));
            fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        }
        await using var verify = fixture.Open();
        var final = await verify.Set<MailboxOutboxEffect>().AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(MailboxEffectState.Exhausted, final.State);
        Assert.Equal(EmailDeliveryStatus.Failed, (await verify.TicketTimelineEvents.AsNoTracking().SingleAsync()).EmailStatus);
        var retryStore = fixture.Store(verify);
        Assert.Null(await retryStore.TryClaimAsync(id, "worker", default));
        Assert.True(await retryStore.RetryForTimelineAsync(final.DeliveryEventId!.Value, default));
        var retried = Assert.IsType<MailboxOutboxEffect>(await retryStore.TryClaimAsync(id, "worker", default));
        Assert.Equal(final.Payload, retried.Payload);
        Assert.Equal(final.DeliveryEventId, retried.DeliveryEventId);
        Assert.Equal(1, retried.Attempts);
    }

    [Fact]
    public async Task Worker_crash_on_first_email_attempt_becomes_visible_unknown_outcome()
    {
        await using var fixture = await OutboxDatabase.CreateAsync();
        var id = await fixture.CaptureEmailAsync();
        await using (var worker = fixture.Open())
            Assert.NotNull(await fixture.Store(worker).TryClaimAsync(id, "worker", default));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await using var verify = fixture.Open();
        Assert.Null(await fixture.Store(verify).TryClaimAsync(id, "worker", default));
        var held = await verify.Set<MailboxOutboxEffect>().SingleAsync(x => x.Id == id);
        Assert.Equal(MailboxEffectState.NeedsReview, held.State);
        Assert.Equal("DispatchOutcomeUnknown", held.LastErrorCode);
        Assert.Equal(1, held.Attempts);
        Assert.Equal(EmailDeliveryStatus.Failed, (await verify.TicketTimelineEvents.SingleAsync()).EmailStatus);
    }

    [Fact]
    public async Task Expired_non_email_claim_can_be_reclaimed()
    {
        await using var fixture = await OutboxDatabase.CreateAsync();
        Guid id;
        await using (var setup = fixture.Open())
        {
            var row = new MailboxOutboxEffect
            {
                Kind = MailboxEffectKind.Notification, EffectKey = "notification:reclaim",
                Payload = "{}", AvailableUnixMilliseconds = fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds()
            };
            setup.Set<MailboxOutboxEffect>().Add(row);
            await setup.SaveChangesAsync();
            id = row.Id;
        }
        await using var firstDb = fixture.Open();
        await using var secondDb = fixture.Open();
        var first = Assert.IsType<MailboxOutboxEffect>(await fixture.Store(firstDb).TryClaimAsync(id, "first", default));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = Assert.IsType<MailboxOutboxEffect>(await fixture.Store(secondDb).TryClaimAsync(id, "second", default));
        Assert.Equal(first.Fence + 1, second.Fence);
        Assert.Equal(2, second.Attempts);
        Assert.False(await fixture.Store(firstDb).CompleteAsync(first, true, null, default));
    }

    private sealed class OutboxDatabase : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"mailbox-outbox-{Guid.NewGuid():N}.db");
        public ManualClock Clock { get; } = new();
        public static async Task<OutboxDatabase> CreateAsync()
        {
            var fixture = new OutboxDatabase();
            await using var db = fixture.Open();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }
        public HelpdeskDbContext Open() => new OutboxTestContext(new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()).Options);
        public MailboxOutboxStore Store(HelpdeskDbContext db) => new(db, new IngressEffectContext(), Clock);

        public async Task<Guid> CaptureEmailAsync()
        {
            await using var db = Open();
            var context = new IngressEffectContext();
            using var active = context.Begin(Guid.NewGuid());
            await using var transaction = await db.Database.BeginTransactionAsync();
            context.Capture(MailboxEffectKind.Email, new IngressEmailEffect(["requester@example.com"],
                "A subject", "<p>Body</p>", [], "ticket-a", [], null, null, false, null, null));
            await new MailboxOutboxStore(db, context, Clock).FlushAsync();
            var id = await db.Set<MailboxOutboxEffect>().Where(x => x.Kind == MailboxEffectKind.Email).Select(x => x.Id).SingleAsync();
            await transaction.CommitAsync();
            return id;
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OutboxTestContext(DbContextOptions<HelpdeskDbContext> options)
        : HelpdeskDbContext(options, new AdminTenantContext(), new HttpContextAccessor())
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var type in modelBuilder.Model.GetEntityTypes().Select(x => x.ClrType).ToArray())
                modelBuilder.Ignore(type);
            modelBuilder.Entity<MailboxOutboxEffect>().HasKey(x => x.Id);
            modelBuilder.Entity<MailboxOutboxEffect>().HasIndex(x => new { x.ReceiptId, x.EffectKey }).IsUnique();
            modelBuilder.Entity<MailboxOutboxEffect>().HasIndex(x => x.DeliveryEventId).IsUnique();
            modelBuilder.Entity<TicketTimelineEvent>().HasKey(x => x.Id);
            modelBuilder.Entity<SupportNotificationDelivery>().HasKey(x => x.Id);
        }
    }

    private sealed class AdminTenantContext : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long milliseconds = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref milliseconds));
        public void Advance(TimeSpan duration) => Interlocked.Add(ref milliseconds, (long)duration.TotalMilliseconds);
    }
}
