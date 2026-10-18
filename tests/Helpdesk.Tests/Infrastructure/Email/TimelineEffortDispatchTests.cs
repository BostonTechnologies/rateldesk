using System.Text.Json;
using System.Threading.Channels;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Tests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class TimelineEffortDispatchTests
{
    [Theory]
    [InlineData(TimelineEventType.Worklog, 0.625)]
    [InlineData(TimelineEventType.InternalNote, 0.625)]
    [InlineData(TimelineEventType.Worklog, 0d)]
    [InlineData(TimelineEventType.InternalNote, 0d)]
    [InlineData(TimelineEventType.Worklog, null)]
    [InlineData(TimelineEventType.InternalNote, null)]
    public async Task Retained_publication_uses_exact_durable_effort_and_preserves_other_metadata(TimelineEventType type, double? hours)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(false);
        var id = Guid.NewGuid();
        var payload = LegacyPayload(id, "ticket-effort", type);
        await using (var db = fixture.Open())
        {
            await db.Database.EnsureCreatedAsync();
            db.TicketTimelineEvents.Add(new TicketTimelineEvent { Id = id, TicketId = "ticket-effort", EventType = type,
                Hours = hours, WorkLogId = hours is null ? null : "exact-worklog", MessageText = "Durable note" });
            db.Set<MailboxOutboxEffect>().Add(new MailboxOutboxEffect { Kind = MailboxEffectKind.Timeline,
                EffectKey = "0000:Timeline", ReceiptId = Guid.NewGuid(), Payload = payload });
            await db.SaveChangesAsync();
        }
        var bus = new CapturingBus();
        await using var services = Services(fixture, bus);
        var dispatcher = new MailboxOutboxDispatcher(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<MailboxOutboxDispatcher>.Instance);

        Assert.Equal(1, await dispatcher.DispatchBatchAsync());

        var published = Assert.Single(bus.Events);
        Assert.Equal(hours ?? 0, published.Hours);
        Assert.Equal(hours, published.RecordedHours);
        Assert.Equal(hours is null ? null : "exact-worklog", published.WorkLogId);
        Assert.Equal(id, published.Id);
        Assert.Equal("Original technician", published.TechnicianName);
        Assert.Equal("Queued note", published.MessageText);
        Assert.NotNull(published.Recipients);
        Assert.NotNull(published.CcRecipients);
        Assert.Equal(["customer@example.test"], published.Recipients);
        Assert.Equal(["cc@example.test"], published.CcRecipients);
        await using var reopened = fixture.Open();
        Assert.Equal(payload, (await reopened.Set<MailboxOutboxEffect>().AsNoTracking().SingleAsync()).Payload);
        Assert.Equal(hours, (await reopened.TicketTimelineEvents.AsNoTracking().SingleAsync()).Hours);
    }

    [Theory]
    [InlineData("ticket")]
    [InlineData("type")]
    [InlineData("missing")]
    public async Task Publication_does_not_enrich_from_a_different_or_missing_durable_event(string mismatch)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(false);
        var id = Guid.NewGuid();
        await using (var db = fixture.Open())
        {
            await db.Database.EnsureCreatedAsync();
            if (mismatch != "missing")
                db.TicketTimelineEvents.Add(new TicketTimelineEvent { Id = id, TicketId = mismatch == "ticket" ? "other-ticket" : "ticket-effort",
                    EventType = mismatch == "type" ? TimelineEventType.InternalNote : TimelineEventType.Worklog, Hours = 0.625, WorkLogId = "private-worklog" });
            db.Set<MailboxOutboxEffect>().Add(new MailboxOutboxEffect { Kind = MailboxEffectKind.Timeline,
                EffectKey = "0000:Timeline", ReceiptId = Guid.NewGuid(), Payload = LegacyPayload(id, "ticket-effort", TimelineEventType.Worklog) });
            await db.SaveChangesAsync();
        }
        var bus = new CapturingBus();
        await using var services = Services(fixture, bus);
        var dispatcher = new MailboxOutboxDispatcher(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<MailboxOutboxDispatcher>.Instance);

        Assert.Equal(1, await dispatcher.DispatchBatchAsync());

        var published = Assert.Single(bus.Events);
        Assert.Equal(99, published.Hours);
        Assert.Null(published.RecordedHours);
        Assert.Null(published.WorkLogId);
    }

    private static string LegacyPayload(Guid id, string ticketId, TimelineEventType type) => JsonSerializer.Serialize(new
    {
        Id = id, TicketId = ticketId, EventType = type, Hours = 99,
        TechnicianName = "Original technician", MessageText = "Queued note",
        Recipients = new[] { "customer@example.test" }, CcRecipients = new[] { "cc@example.test" }
    });

    private static ServiceProvider Services(ServiceIdentityMigrationTests.DatabaseFixture fixture, CapturingBus bus)
    {
        var services = new ServiceCollection();
        services.AddScoped<HelpdeskDbContext>(_ => fixture.Open());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IIngressEffectContext, IngressEffectContext>();
        services.AddScoped<MailboxOutboxStore>();
        services.AddSingleton<ITimelineEventBus>(bus);
        return services.BuildServiceProvider();
    }

    private sealed class CapturingBus : ITimelineEventBus
    {
        public List<TicketTimelineEventDto> Events { get; } = [];
        public ValueTask PublishAsync(TicketTimelineEventDto evt) { Events.Add(evt); return ValueTask.CompletedTask; }
        public ChannelReader<TicketTimelineEventDto> Subscribe(string ticketId) => throw new NotSupportedException();
        public void Unsubscribe(string ticketId, ChannelReader<TicketTimelineEventDto> reader) => throw new NotSupportedException();
    }
}
