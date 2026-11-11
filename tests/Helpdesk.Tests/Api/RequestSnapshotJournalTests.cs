extern alias NewWeb;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using Bunit;
using Bunit.TestDoubles;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.Application.Events;
using Helpdesk.Application.Notifications;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Tests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using NSubstitute;
using Xunit.Abstractions;
using RequestDetail = NewWeb::HelpDesk.NewWeb.Components.Pages.Requests.RequestDetail;
using Activity = NewWeb::HelpDesk.NewWeb.Components.Shared.TicketActivityTabs;
using TimelineStream = NewWeb::HelpDesk.NewWeb.Services.TimelineStreamService;

namespace Helpdesk.Tests.Api;

public sealed class RequestSnapshotJournalTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Committed_request_mutations_reach_gateway_and_canonical_render_when_best_effort_publication_fails(
        bool postgres, bool canceled)
    {
        var failure = new PublicationFailure(canceled);
        await using var harness = await RequestLiveProgressTests.Harness.CreateAsync(postgres, services =>
            services.AddScoped<INotificationService>(provider =>
            {
                var actual = new NotificationService(provider.GetRequiredService<INotificationRepository>());
                var substitute = Substitute.For<INotificationService>();
                substitute.CreateNotificationAsync(Arg.Any<CreateNotificationRequest>(), Arg.Any<CancellationToken>())
                    .Returns(call => failure.PublishAsync(actual, call.Arg<CreateNotificationRequest>(), call.Arg<CancellationToken>()));
                return substitute;
            }));
        failure.Database = harness.Database;
        await harness.WithDbAsync(async db =>
        {
            db.Requests.Add(new Request { Id = "second-request", TrackingId = "REQ-SECOND", Title = "Other selected request",
                OrganizationId = "live-org", CustomerId = "live-customer", State = TicketState.InProgress });
            await db.SaveChangesAsync();
        });
        var factory = await harness.ClientsAsync("operator");
        await using var context = Context(factory);
        context.ComponentFactories.AddStub<Activity>();
        var page = context.Render<RequestDetail>(parameters => parameters.Add(component => component.Id, "live-request"));
        await factory.Frames.Ready.Task.WaitAsync(Timeout);
        page.WaitForAssertion(() =>
        {
            Assert.True(factory.RequestSnapshots >= 2);
            Assert.Equal("false", page.Find("[data-testid=request-live-updates]").GetAttribute("aria-busy"));
        }, Timeout);
        using var client = factory.CreateClient("HelpdeskApi");
        var mutations = new[] { "priority", "quick-state", "bulk-state", "bulk-assign" };
        for (var index = 0; index < mutations.Length; index++)
        {
            var mutation = mutations[index];
            failure.CurrentMutation = mutation;
            var previousFrames = StateFrameCount(factory);
            var previousSnapshots = factory.RequestSnapshots;
            var started = System.Diagnostics.Stopwatch.StartNew();
            using var response = await MutateAsync(client, mutation);
            response.EnsureSuccessStatusCode();
            await using (var verify = harness.Database.Open())
            {
                var committed = await verify.Requests.AsNoTracking().SingleAsync(request => request.Id == "live-request");
                AssertMutation(committed, mutation);
                Assert.Equal(index + 1, await verify.Notifications.CountAsync(marker =>
                    marker.Category == RequestSnapshotJournal.Category && marker.Reference == "live-request"));
            }
            page.WaitForAssertion(() =>
            {
                Assert.True(StateFrameCount(factory) > previousFrames, "The committed marker must produce an actual gateway SSE frame.");
                Assert.True(factory.RequestSnapshots > previousSnapshots, "The frame must cause a canonical request read.");
                Assert.Equal("false", page.Find("[data-testid=request-live-updates]").GetAttribute("aria-busy"));
                Assert.Equal(TicketPriority.High, page.FindComponent<MudSelect<TicketPriority>>().Instance.GetState(select => select.Value));
                var expectedState = index switch { 0 => "In progress", 1 => "On hold", _ => "Waiting for reply" };
                Assert.Contains(expectedState, page.Find("[data-testid=request-committed-state]").TextContent);
            }, TimeSpan.FromSeconds(2));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"{mutation} convergence took {started.Elapsed}.");
            var canonical = await client.GetFromJsonAsync<RequestDto>("api/v1/requests/live-request");
            Assert.NotNull(canonical);
            if (mutation == "bulk-assign") Assert.Equal("requester", canonical.AssignedToId);
            output.WriteLine($"Provider={(postgres ? "PostgreSQL" : "SQLite")}; publication={(canceled ? "canceled" : "throwing")}; mutation={mutation}; committed marker → gateway SSE → canonical render={started.Elapsed.TotalMilliseconds:F0} ms");
        }
        Assert.True(factory.Frames.GatewayHeaderSeen);
        Assert.Contains("event: request-state", factory.Frames.Text);
        Assert.Equal(6, failure.Committed.Count);
        Assert.All(failure.Committed, committed => Assert.True(committed.HasMarker));
        Assert.All(failure.Committed, committed => AssertMutation(committed.Request, committed.Mutation));
        await using var final = harness.Database.Open();
        Assert.Equal(6, await final.Notifications.CountAsync(marker => marker.Category == RequestSnapshotJournal.Category));
        Assert.Equal(2, await final.Notifications.CountAsync(marker => marker.Category == RequestSnapshotJournal.Category && marker.Reference == "second-request"));
        Assert.False(await final.Notifications.AnyAsync(marker => marker.Source == "Request" && marker.IsGlobal));
        var inbox = await new NotificationRepository(final).GetNotificationsForUserAsync("operator", 0, 100, CancellationToken.None);
        Assert.DoesNotContain(inbox, item => item.Category == RequestSnapshotJournal.Category);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Healthy_publication_creates_one_visible_notification_and_one_private_marker(bool postgres)
    {
        await using var harness = await RequestLiveProgressTests.Harness.CreateAsync(postgres);
        var factory = await harness.ClientsAsync("operator");
        using var client = factory.CreateClient("HelpdeskApi");
        using var response = await MutateAsync(client, "priority");
        response.EnsureSuccessStatusCode();
        await using var db = harness.Database.Open();
        Assert.Equal(TicketPriority.High, (await db.Requests.AsNoTracking().SingleAsync()).Priority);
        var journal = await db.Notifications.Where(marker => marker.Source == "Request").ToListAsync();
        Assert.Equal(2, journal.Count);
        Assert.False(Assert.Single(journal, marker => marker.Category == RequestSnapshotJournal.Category).IsGlobal);
        Assert.True(Assert.Single(journal, marker => marker.Category == "DomainEvent.Request").IsGlobal);
        var inbox = await new NotificationRepository(db).GetNotificationsForUserAsync("operator", 0, 100, CancellationToken.None);
        Assert.Single(inbox, notification => notification.Source == "Request");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Markers_rollback_with_mutation_and_repeated_staging_is_idempotent_and_request_scoped(bool postgres)
    {
        await using var database = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using (var seed = database.Open())
        {
            await seed.Database.MigrateAsync();
            seed.Requests.AddRange(new Request { Id = "atomic-one", TrackingId = "REQ-ONE", Title = "Atomic first", State = TicketState.New },
                new Request { Id = "atomic-two", TrackingId = "REQ-TWO", Title = "Atomic second", State = TicketState.New });
            await seed.SaveChangesAsync();
        }
        var change = new RequestSnapshotChangedEvent("atomic-one", null, DateTimeOffset.UtcNow, "same-operation");
        await using (var writer = database.Open())
        {
            await using var transaction = await writer.Database.BeginTransactionAsync();
            (await writer.Requests.SingleAsync(request => request.Id == "atomic-one")).State = TicketState.OnHold;
            await RequestSnapshotJournal.StageAsync(writer, [change, change], CancellationToken.None);
            await RequestSnapshotJournal.StageAsync(writer, [change], CancellationToken.None);
            await writer.SaveChangesAsync();
            Assert.Single(await writer.Notifications.ToListAsync());
            await transaction.RollbackAsync();
        }
        await using (var verify = database.Open())
        {
            Assert.Equal(TicketState.New, (await verify.Requests.SingleAsync(request => request.Id == "atomic-one")).State);
            Assert.Empty(await verify.Notifications.ToListAsync());
        }
        await using (var writer = database.Open())
        {
            await using var transaction = await writer.Database.BeginTransactionAsync();
            (await writer.Requests.SingleAsync(request => request.Id == "atomic-one")).State = TicketState.OnHold;
            await RequestSnapshotJournal.StageAsync(writer, [change, change], CancellationToken.None);
            await writer.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await using var repeat = database.Open();
        await RequestSnapshotJournal.StageAsync(repeat, [change,
            new RequestSnapshotChangedEvent("atomic-two", null, change.Timestamp, change.CorrelationId!)], CancellationToken.None);
        await repeat.SaveChangesAsync();
        var markers = await repeat.Notifications.ToListAsync();
        Assert.Equal(2, markers.Count);
        Assert.Equal(["atomic-one", "atomic-two"], markers.Select(marker => marker.Reference).OrderBy(reference => reference));
        Assert.All(markers, marker => { Assert.Null(marker.UserId); Assert.False(marker.IsGlobal); });
    }

    private static BunitContext Context(IHttpClientFactory factory)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var authorization = context.AddAuthorization(); authorization.SetAuthorized("Synthetic operator"); authorization.SetRoles("HelpdeskAdmin");
        context.Services.AddSingleton(factory); context.Services.AddSingleton<TimelineStream>();
        return context;
    }

    private static int StateFrameCount(RequestLiveProgressTests.ClientFactory factory) =>
        factory.Frames.Text.Split("event: request-state", StringSplitOptions.None).Length - 1;

    private static Task<HttpResponseMessage> MutateAsync(HttpClient client, string mutation) => mutation switch
    {
        "priority" => client.PutAsJsonAsync("api/v1/requests/live-request", new UpdateRequestDto { Priority = TicketPriority.High }),
        "quick-state" => client.PostAsJsonAsync("api/v1/requests/live-request/state", new { NewState = TicketState.OnHold }),
        "bulk-state" => client.PostAsJsonAsync("api/v1/requests/bulk/state", new { Ids = new[] { "live-request", "second-request", "live-request" }, NewState = TicketState.WaitingReply }),
        "bulk-assign" => client.PostAsJsonAsync("api/v1/requests/bulk/assign", new { Ids = new[] { "live-request", "second-request" }, AssignedToId = "requester" }),
        _ => throw new ArgumentOutOfRangeException(nameof(mutation))
    };

    private static void AssertMutation(Request request, string mutation)
    {
        if (mutation == "priority") Assert.Equal(TicketPriority.High, request.Priority);
        else if (mutation == "quick-state") Assert.Equal(TicketState.OnHold, request.State);
        else if (mutation == "bulk-state") Assert.Equal(TicketState.WaitingReply, request.State);
        else if (mutation == "bulk-assign") Assert.Equal("requester", request.AssignedToId);
        else throw new ArgumentOutOfRangeException(nameof(mutation));
    }

    private sealed class PublicationFailure(bool canceled)
    {
        public ServiceIdentityMigrationTests.DatabaseFixture Database { get; set; } = null!;
        public string CurrentMutation { get; set; } = string.Empty;
        public ConcurrentQueue<(bool HasMarker, Request Request, string Mutation)> Committed { get; } = new();

        public async Task PublishAsync(INotificationService actual, CreateNotificationRequest request, CancellationToken ct)
        {
            if (request.Source != "Request") { await actual.CreateNotificationAsync(request, ct); return; }
            await using var verify = Database.Open();
            var committed = await verify.Requests.AsNoTracking().SingleAsync(candidate => candidate.Id == request.Reference);
            var markerCount = await verify.Notifications.CountAsync(marker => marker.Category == RequestSnapshotJournal.Category && marker.Reference == request.Reference);
            Committed.Enqueue((markerCount > 0, committed, CurrentMutation));
            if (canceled) throw new OperationCanceledException("Synthetic post-commit publication cancellation", new CancellationToken(true));
            throw new IOException("Synthetic post-commit publication failure");
        }
    }
}
