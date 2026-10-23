using System.Text.Json;
using Helpdesk.Application.Tickets;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Tests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Persistence;

public sealed class EfRepositoryProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_polymorphic_seen_update_preserves_the_lifecycle_committed_after_its_tracked_read(bool postgres)
    {
        await using var fixture = await CreateAsync(postgres);
        await using var seenDb = fixture.Open();
        IRepository<Ticket> repository = new EfRepository<Ticket>(seenDb);
        var stale = Assert.IsType<Change>(await repository.GetAsync("change"));
        Assert.Equal(ChangeLifecycleState.Draft, stale.LifecycleState);
        await CommitLifecycleAsync(fixture);

        // The handler reuses the already-tracked old Change while applying its
        // single metadata update through IRepository<Ticket>.
        var handler = new MarkTicketAsSeenCommandHandler(repository, new EfRepository<WorkLog>(seenDb));
        Assert.True(await handler.Handle(new MarkTicketAsSeenCommand("change"), CancellationToken.None));
        await using var readback = fixture.Open();
        var saved = await readback.Changes.SingleAsync();
        Assert.NotNull(saved.LastViewedByCustomerAt);
        AssertCommittedLifecycle(saved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_in_place_listener_edit_and_a_concurrent_lifecycle_both_survive_tracked_repository_save(bool postgres)
    {
        await using var fixture = await CreateAsync(postgres);
        await using var listenerDb = fixture.Open();
        IRepository<Ticket> repository = new EfRepository<Ticket>(listenerDb);
        var change = Assert.IsType<Change>(await repository.GetAsync("change"));
        change.CcRecipients.Add("added@example.test");
        await CommitLifecycleAsync(fixture);
        await repository.UpdateAsync(change);

        await using var readback = fixture.Open();
        var saved = await readback.Changes.SingleAsync();
        Assert.Equal(new[] { "original@example.test", "added@example.test" }, saved.CcRecipients);
        AssertCommittedLifecycle(saved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_explicit_detached_polymorphic_repository_update_still_persists_its_supplied_values(bool postgres)
    {
        await using var fixture = await CreateAsync(postgres);
        Change detached;
        await using (var source = fixture.Open()) detached = await source.Changes.AsNoTracking().SingleAsync();
        detached.Title = "Explicit detached edit";
        detached.State = TicketState.InProgress;
        detached.ImplementorUserId = "detached-implementor";
        detached.CcRecipients.Add("detached@example.test");
        await using (var writer = fixture.Open())
        {
            IRepository<Ticket> repository = new EfRepository<Ticket>(writer);
            await repository.UpdateAsync(detached);
        }
        await using var readback = fixture.Open();
        var saved = await readback.Changes.SingleAsync();
        Assert.Equal("Explicit detached edit", saved.Title);
        Assert.Equal(TicketState.InProgress, saved.State);
        Assert.Equal("detached-implementor", saved.ImplementorUserId);
        Assert.Equal(new[] { "original@example.test", "detached@example.test" }, saved.CcRecipients);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nested_calendar_edits_use_independent_snapshots_and_preserve_a_concurrent_name_change(bool postgres)
    {
        await using var fixture = await CreateAsync(postgres);
        await using (var seed = fixture.Open())
        {
            seed.WorkingCalendars.Add(new WorkingCalendar
            {
                Id = "calendar", Name = "Original calendar",
                WeeklyRules = [new WorkingDayRule { DayOfWeek = 1, IsWorkingDay = true,
                    WorkingHours = [new TimeRange { Start = TimeSpan.FromHours(8), End = TimeSpan.FromHours(17) }] }],
                Exceptions = [new CalendarException { Date = new DateOnly(2026, 10, 20), IsWorkingDayOverride = true,
                    WorkingHoursOverride = [new TimeRange { Start = TimeSpan.FromHours(9), End = TimeSpan.FromHours(12) }] }]
            });
            await seed.SaveChangesAsync();
        }
        await using var editor = fixture.Open();
        IRepository<WorkingCalendar> repository = new EfRepository<WorkingCalendar>(editor);
        var calendar = (await repository.GetAsync("calendar"))!;
        calendar.WeeklyRules[0].WorkingHours[0].End = TimeSpan.FromHours(18);
        calendar.Exceptions[0].WorkingHoursOverride[0].Start = TimeSpan.FromHours(10);
        await using (var concurrent = fixture.Open())
        {
            var renamed = await concurrent.WorkingCalendars.SingleAsync();
            renamed.Name = "Concurrently renamed calendar";
            await concurrent.SaveChangesAsync();
        }
        await repository.UpdateAsync(calendar);
        await using var readback = fixture.Open();
        var saved = await readback.WorkingCalendars.SingleAsync();
        Assert.Equal("Concurrently renamed calendar", saved.Name);
        Assert.Equal(TimeSpan.FromHours(18), saved.WeeklyRules[0].WorkingHours[0].End);
        Assert.Equal(TimeSpan.FromHours(10), saved.Exceptions[0].WorkingHoursOverride[0].Start);
    }

    private static async Task<ServiceIdentityMigrationTests.DatabaseFixture> CreateAsync(bool postgres)
    {
        var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using var db = fixture.Open();
        await db.Database.EnsureCreatedAsync();
        db.Changes.Add(new Change { Id = "change", TrackingId = "CHG-TRACKED", Title = "Original change",
            LifecycleState = ChangeLifecycleState.Draft, State = TicketState.New, CcRecipients = ["original@example.test"] });
        await db.SaveChangesAsync();
        return fixture;
    }

    private static async Task CommitLifecycleAsync(ServiceIdentityMigrationTests.DatabaseFixture fixture)
    {
        await using var concurrent = fixture.Open();
        var change = await concurrent.Changes.SingleAsync();
        change.LifecycleState = ChangeLifecycleState.ApprovedForImplementation;
        change.State = TicketState.PendingApproval;
        change.ImplementorUserId = change.AssignedToId = "reviewed-implementor";
        change.ApproverUserIds = ["reviewed-owner"];
        change.ChangeTemplateJson = "{\"scopeOfChange\":\"reviewed scope\"}";
        await concurrent.SaveChangesAsync();
    }

    private static void AssertCommittedLifecycle(Change saved)
    {
        Assert.Equal(ChangeLifecycleState.ApprovedForImplementation, saved.LifecycleState);
        Assert.Equal(TicketState.PendingApproval, saved.State);
        Assert.Equal("reviewed-implementor", saved.ImplementorUserId);
        Assert.Equal(saved.ImplementorUserId, saved.AssignedToId);
        Assert.Equal(new[] { "reviewed-owner" }, saved.ApproverUserIds);
        using var template = JsonDocument.Parse(saved.ChangeTemplateJson);
        Assert.Equal(JsonValueKind.Object, template.RootElement.ValueKind);
        var property = Assert.Single(template.RootElement.EnumerateObject());
        Assert.Equal("scopeOfChange", property.Name);
        Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
        Assert.Equal("reviewed scope", property.Value.GetString());
    }
}
